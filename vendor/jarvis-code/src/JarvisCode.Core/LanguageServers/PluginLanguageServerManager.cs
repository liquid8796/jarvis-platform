using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.LanguageServers;

/// <summary>Lazy, headless language servers explicitly contributed by session plugins.</summary>
public sealed class PluginLanguageServerManager : IAsyncDisposable, IDisposable
{
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<Server> _servers;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    public IReadOnlyList<string> ConfigurationWarnings { get; private set; } = [];

    public PluginLanguageServerManager(IEnumerable<string> configFiles, string workingDirectory)
        : this(LoadConfigurations(configFiles), workingDirectory) { }

    private PluginLanguageServerManager((IReadOnlyList<LanguageServerConfiguration> Configurations, List<string> Warnings) loaded, string workingDirectory)
        : this(loaded.Configurations, workingDirectory) => ConfigurationWarnings = loaded.Warnings;

    private static (IReadOnlyList<LanguageServerConfiguration>, List<string>) LoadConfigurations(IEnumerable<string> files)
    {
        var warnings = new List<string>();
        return (LanguageServerConfiguration.LoadValid(files, warnings), warnings);
    }

    public PluginLanguageServerManager(IReadOnlyList<LanguageServerConfiguration> configurations, string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _servers = configurations.Select(config => new Server(config, _workingDirectory)).ToArray();
    }

    public bool HasServers => _servers.Count > 0;

    public IReadOnlyList<string> DrainDiagnostics() => _servers.SelectMany(server => server.DrainDiagnostics()).ToArray();

    public async Task<string?> SynchronizeDocumentAsync(string filePath, bool waitForDiagnostics, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var path = Path.GetFullPath(filePath, _workingDirectory);
        var server = _servers.FirstOrDefault(server => server.LanguageFor(path) is not null);
        if (server is null || !File.Exists(path)) return null;
        using var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await server.SynchronizeDocumentAsync(path, waitForDiagnostics, token.Token);
    }

    public async Task<JsonNode?> ExecuteAsync(string operation, string filePath, int line, int character, string? query,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (line < 1 || character < 1) throw new ArgumentException("line and character must be positive, one-based integers.");
        var path = Path.GetFullPath(filePath, _workingDirectory);
        if (!File.Exists(path)) throw new FileNotFoundException($"File does not exist: {path}", path);
        var server = _servers.FirstOrDefault(server => server.LanguageFor(path) is not null)
            ?? throw new InvalidOperationException($"No LSP server available for file type: {Path.GetExtension(path)}");
        using var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await server.ExecuteAsync(operation, path, line - 1, character - 1, query, token.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync();
        await Task.WhenAll(_servers.Select(server => server.DisposeAsync().AsTask()));
        _lifetime.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Server(LanguageServerConfiguration config, string workingDirectory) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1);
        private readonly Dictionary<string, (string Text, int Version)> _documents = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _documentVersions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, JsonNode> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _diagnosticWaiters = new(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder _stderr = new();
        private Process? _process;
        private LspJsonRpcConnection? _connection;
        private JsonObject _capabilities = new();
        private Task? _stderrReader;
        private bool _started;
        private bool _initialized;
        private int _restarts;
        private bool _disposed;
        private string Workspace => Path.GetFullPath(config.WorkspaceFolder ?? workingDirectory, workingDirectory);

        public string? LanguageFor(string path) => config.ExtensionToLanguage
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault(pair => path.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase)).Value;

        public async Task<string?> SynchronizeDocumentAsync(string path, bool waitForDiagnostics, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            var uri = FileUri(path);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await EnsureStartedAsync(token);
                var waiter = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (waitForDiagnostics && config.Diagnostics) _diagnosticWaiters[uri] = waiter;
                await SynchronizeAsync(path, token);
                if (!waitForDiagnostics || !config.Diagnostics) return null;
                JsonNode diagnostics;
                try { diagnostics = await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(500), token); }
                catch (TimeoutException) { return null; }
                if (diagnostics["version"]?.GetValue<int>() is { } version && version < _documents[uri].Version) return null;
                _diagnostics.TryRemove(new KeyValuePair<string, JsonNode>(uri, diagnostics));
                return FormatDiagnostics(uri, diagnostics);
            }
            finally { _diagnosticWaiters.TryRemove(uri, out _); _gate.Release(); }
        }

        public IEnumerable<string> DrainDiagnostics()
        {
            foreach (var entry in _diagnostics)
                if (_diagnostics.TryRemove(entry) &&
                    (entry.Value["version"]?.GetValue<int>() is not { } version || version >= _documentVersions.GetValueOrDefault(entry.Key)) &&
                    FormatDiagnostics(entry.Key, entry.Value) is { } text)
                    yield return "LSP diagnostics:\n" + text;
        }

        private static string? FormatDiagnostics(string uri, JsonNode diagnostics)
        {
            if (diagnostics["diagnostics"] is not JsonArray { Count: > 0 } items) return null;
            var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;
            return string.Join("\n", items.Select(item =>
                $"{path}:{(item?["range"]?["start"]?["line"]?.GetValue<int>() ?? 0) + 1}:{(item?["range"]?["start"]?["character"]?.GetValue<int>() ?? 0) + 1}: " +
                $"{(item?["severity"]?.GetValue<int>() switch { 1 => "error", 2 => "warning", 3 => "information", 4 => "hint", _ => "diagnostic" })}: {item?["message"]}"));
        }

        public async Task<JsonNode?> ExecuteAsync(string operation, string path, int line, int character, string? query, CancellationToken token)
        {
            // Document versions and each call hierarchy pair stay atomic per server. Separate servers remain concurrent.
            await _gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await EnsureStartedAsync(token);
                var (method, capability) = operation switch
                {
                    "goToDefinition" => ("textDocument/definition", "definitionProvider"),
                    "findReferences" => ("textDocument/references", "referencesProvider"),
                    "hover" => ("textDocument/hover", "hoverProvider"),
                    "documentSymbol" => ("textDocument/documentSymbol", "documentSymbolProvider"),
                    "workspaceSymbol" => ("workspace/symbol", "workspaceSymbolProvider"),
                    "goToImplementation" => ("textDocument/implementation", "implementationProvider"),
                    "prepareCallHierarchy" or "incomingCalls" or "outgoingCalls" => ("textDocument/prepareCallHierarchy", "callHierarchyProvider"),
                    _ => throw new ArgumentException($"Unknown LSP operation: {operation}"),
                };
                if (_capabilities[capability] is null || _capabilities[capability] is JsonValue value && value.TryGetValue<bool>(out var enabled) && !enabled)
                    throw new InvalidOperationException($"LSP server '{config.Name}' does not support {operation}.");
                await SynchronizeAsync(path, token);
                var parameters = operation == "workspaceSymbol" ? new JsonObject { ["query"] = query ?? "" }
                    : new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri(path) } };
                if (operation is not ("workspaceSymbol" or "documentSymbol"))
                    parameters["position"] = new JsonObject { ["line"] = line, ["character"] = character };
                if (operation == "findReferences") parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                var result = await _connection!.RequestAsync(method, parameters, timeout.Token);
                if (operation is "incomingCalls" or "outgoingCalls")
                {
                    if (result is not JsonArray items || items.Count == 0) return new JsonArray();
                    result = await _connection.RequestAsync("callHierarchy/" + operation,
                        new JsonObject { ["item"] = items[0]?.DeepClone() }, timeout.Token);
                }
                return result;
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
            {
                string details;
                lock (_stderr) details = _stderr.ToString();
                throw new IOException($"LSP server '{config.Name}': {error.Message}" +
                    (string.IsNullOrWhiteSpace(details) ? "" : "\nServer stderr: " + details.Trim()), error);
            }
            finally { _gate.Release(); }
        }

        private async Task EnsureStartedAsync(CancellationToken token)
        {
            if (_connection is { IsClosed: false } && _process is { HasExited: false }) return;
            if (_started)
            {
                if (!config.RestartOnCrash || _restarts >= config.MaxRestarts)
                    throw new IOException($"Language server exited; restart limit reached ({_restarts}).");
                _restarts++;
                await StopAsync();
            }
            _started = true;
            var command = config.Command;
            if (!Path.IsPathRooted(command) && (command.StartsWith("./") || command.StartsWith(".\\")))
                command = Path.GetFullPath(command, config.Environment.GetValueOrDefault("CLAUDE_PLUGIN_ROOT") ?? workingDirectory);
            var info = new ProcessStartInfo(command)
            {
                WorkingDirectory = Workspace, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var (name, value) in config.Environment) info.Environment[name] = value;
            ConfigureCommand(info, command, config.Arguments);
            _process = Process.Start(info) ?? throw new IOException("Could not start language server.");
            _stderrReader = DrainStderrAsync(_process.StandardError);
            _connection = new(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, HandleRequestAsync,
                (method, parameters) =>
                {
                    if (config.Diagnostics && method == "textDocument/publishDiagnostics" && parameters?["uri"]?.GetValue<string>() is { } uri)
                    {
                        _ = parameters["version"]?.GetValue<int>();
                        _ = FormatDiagnostics(uri, parameters);
                        var copy = parameters.DeepClone();
                        _diagnostics[uri] = copy;
                        if (_diagnosticWaiters.TryGetValue(uri, out var waiter)) waiter.TrySetResult(copy);
                    }
                });
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (config.StartupTimeout is { } startupTimeout) timeout.CancelAfter(startupTimeout);
                var initialized = await _connection.RequestAsync("initialize", new JsonObject
                {
                    ["processId"] = Environment.ProcessId,
                    ["clientInfo"] = new JsonObject { ["name"] = "Jarvis Code", ["version"] = "1.0" },
                    ["rootUri"] = FileUri(Workspace), ["rootPath"] = Workspace, ["workspaceFolders"] = WorkspaceFolders(),
                    ["initializationOptions"] = config.InitializationOptions?.DeepClone(),
                    ["capabilities"] = JsonNode.Parse("""
                        {"general":{"positionEncodings":["utf-16"]},"workspace":{"configuration":true,"workspaceFolders":true},"textDocument":{"synchronization":{"didSave":true},"hover":{"contentFormat":["markdown","plaintext"]},"definition":{"linkSupport":true},"implementation":{"linkSupport":true},"documentSymbol":{"hierarchicalDocumentSymbolSupport":true},"callHierarchy":{"dynamicRegistration":false}},"window":{"workDoneProgress":true}}
                        """),
                }, timeout.Token);
                _capabilities = initialized?["capabilities"]?.DeepClone() as JsonObject
                    ?? throw new InvalidDataException("Language server initialize response has no capabilities.");
                if (_capabilities["positionEncoding"]?.GetValue<string>() is { } encoding && encoding != "utf-16")
                    throw new InvalidDataException($"Language server selected unsupported position encoding: {encoding}");
                await _connection.NotifyAsync("initialized", new JsonObject(), timeout.Token);
                _initialized = true;
                if (config.Settings is not null)
                    await _connection.NotifyAsync("workspace/didChangeConfiguration", new JsonObject { ["settings"] = config.Settings.DeepClone() }, timeout.Token);
            }
            catch { await StopAsync(); throw; }
        }

        private async Task SynchronizeAsync(string path, CancellationToken token)
        {
            var uri = FileUri(path);
            if (new FileInfo(path).Length > 10_000_000) throw new IOException("LSP document exceeds 10 MB.");
            var text = await File.ReadAllTextAsync(path, token);
            var sync = _capabilities["textDocumentSync"];
            var syncKind = sync is JsonValue scalar ? scalar.GetValue<int>() : sync?["change"]?.GetValue<int>() ?? 0;
            var openClose = sync is JsonValue ? syncKind != 0 : sync?["openClose"]?.GetValue<bool>() ?? false;
            if (!_documents.TryGetValue(uri, out var previous))
            {
                _documentVersions[uri] = 1;
                if (openClose)
                {
                    await _connection!.NotifyAsync("textDocument/didOpen", new JsonObject
                    { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = LanguageFor(path), ["version"] = 1, ["text"] = text } }, token);
                    _openDocuments.Add(uri);
                }
                _documents[uri] = (text, 1);
            }
            else if (previous.Text != text)
            {
                _documentVersions[uri] = previous.Version + 1;
                if (syncKind != 0)
                {
                    var change = new JsonObject { ["text"] = text };
                    if (syncKind == 2)
                    {
                        var lastNewline = previous.Text.LastIndexOf('\n');
                        var lastLine = previous.Text[(lastNewline + 1)..].TrimEnd('\r');
                        change["range"] = new JsonObject
                        {
                            ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                            ["end"] = new JsonObject { ["line"] = previous.Text.Count(c => c == '\n'), ["character"] = lastLine.Length },
                        };
                    }
                    await _connection!.NotifyAsync("textDocument/didChange", new JsonObject
                    {
                        ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = previous.Version + 1 },
                        ["contentChanges"] = new JsonArray(change),
                    }, token);
                }
                if (sync is JsonObject && sync["save"] is { } save && !(save is JsonValue flag && flag.TryGetValue<bool>(out var shouldSave) && !shouldSave))
                {
                    var saved = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } };
                    if (save is JsonObject && save["includeText"]?.GetValue<bool>() == true) saved["text"] = text;
                    await _connection!.NotifyAsync("textDocument/didSave", saved, token);
                }
                _documents[uri] = (text, previous.Version + 1);
            }
        }

        private JsonArray WorkspaceFolders() => new(new JsonObject { ["uri"] = FileUri(Workspace), ["name"] = Path.GetFileName(Workspace) });

        private Task<JsonNode?> HandleRequestAsync(string method, JsonNode? parameters)
        {
            JsonNode? result = method switch
            {
                "workspace/configuration" => new JsonArray((parameters?["items"] as JsonArray ?? [])
                    .Select(item => ResolveSettings(item?["section"]?.GetValue<string>())?.DeepClone()).ToArray()),
                "workspace/workspaceFolders" => WorkspaceFolders(),
                "window/workDoneProgress/create" or "window/showMessageRequest" => null,
                "workspace/applyEdit" => new JsonObject { ["applied"] = false, ["failureReason"] = "The LSP tool supports read-only navigation." },
                _ => throw new NotSupportedException($"Unsupported server request: {method}"),
            };
            return Task.FromResult(result);
        }

        private JsonNode? ResolveSettings(string? section)
        {
            var settings = config.Settings;
            if (string.IsNullOrEmpty(section)) return settings;
            if (settings is JsonObject direct && direct.TryGetPropertyValue(section, out var exact)) return exact;
            foreach (var part in section.Split('.')) settings = settings is JsonObject obj ? obj[part] : null;
            return settings;
        }

        private async Task DrainStderrAsync(StreamReader reader)
        {
            try
            {
                var buffer = new char[1024];
                int count;
                while ((count = await reader.ReadAsync(buffer)) > 0)
                    lock (_stderr) { _stderr.Append(buffer, 0, count); if (_stderr.Length > 4096) _stderr.Remove(0, _stderr.Length - 4096); }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }

        private async Task StopAsync()
        {
            if (_connection is not null)
            {
                using var timeout = new CancellationTokenSource(config.ShutdownTimeout ?? Timeout.Infinite);
                try
                {
                    if (_initialized && !_connection.IsClosed)
                    {
                        foreach (var uri in _openDocuments)
                            await _connection.NotifyAsync("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, timeout.Token);
                        await _connection.RequestAsync("shutdown", null, timeout.Token);
                        await _connection.NotifyAsync("exit", null, timeout.Token);
                        if (_process is { HasExited: false }) await _process.WaitForExitAsync(timeout.Token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            if (_process is { HasExited: false })
                try { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
            if (_stderrReader is not null) await _stderrReader;
            _process?.Dispose(); _process = null;
            _documents.Clear(); _documentVersions.Clear(); _openDocuments.Clear(); _diagnostics.Clear(); _initialized = false;
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try { if (!_disposed) { _disposed = true; await StopAsync(); } }
            finally { _gate.Release(); }
        }
    }

    internal static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static void ConfigureCommand(ProcessStartInfo info, string command, IReadOnlyList<string> arguments)
    {
        var resolved = command;
        if (OperatingSystem.IsWindows())
        {
            var directories = Path.IsPathRooted(command) || command.IndexOfAny(['/', '\\']) >= 0
                ? new[] { info.WorkingDirectory }
                : new[] { info.WorkingDirectory }.Concat((info.Environment.TryGetValue("PATH", out var pathValue) ? pathValue ?? "" : "").Split(Path.PathSeparator));
            resolved = directories.SelectMany(directory => new[] { "", ".exe", ".com", ".cmd", ".bat" }
                .Select(extension => Path.Combine(directory.Trim('"'), command + extension)))
                .FirstOrDefault(File.Exists) ?? command;
        }
        if (OperatingSystem.IsWindows() && Path.GetExtension(resolved).ToLowerInvariant() is ".cmd" or ".bat")
        {
            // cmd shims require command parsing. Escape each argument through both cmd and native argv parsing.
            // Algorithm reference: moxystudio/node-cross-spawn lib/util/escape.js and lib/parse.js.
            const string meta = "()[]%!^\"`<>&|;, *?";
            static string EscapeMeta(string text) => string.Concat(text.Select(c => meta.Contains(c) ? "^" + c : c.ToString()));
            static string QuoteArgument(string argument)
            {
                var quoted = new StringBuilder("\"");
                var slashes = 0;
                foreach (var c in argument)
                {
                    if (c == '\\') { slashes++; continue; }
                    quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                    slashes = 0;
                }
                return quoted.Append('\\', slashes * 2).Append('"').ToString();
            }
            var doubleEscape = resolved.Replace('/', '\\').Contains("\\node_modules\\.bin\\", StringComparison.OrdinalIgnoreCase);
            var escaped = arguments.Select(argument => EscapeMeta(QuoteArgument(argument)))
                .Select(argument => doubleEscape ? EscapeMeta(argument) : argument);
            info.FileName = info.Environment.TryGetValue("ComSpec", out var commandProcessor) ? commandProcessor ?? "cmd.exe" : "cmd.exe";
            info.Arguments = "/d /s /c \"" + EscapeMeta(resolved) + " " + string.Join(" ", escaped) + "\"";
        }
        else
        {
            info.FileName = resolved;
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        }
    }
}
