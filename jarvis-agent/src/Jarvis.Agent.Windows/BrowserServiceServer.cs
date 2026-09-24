using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;
using Microsoft.Win32;

namespace Jarvis.Agent.Windows;

public sealed partial class BrowserServiceServer : IDisposable
{
    private sealed record Suite(IReadOnlyDictionary<string, ITool> Tools)
    {
        public SemaphoreSlim Serial { get; } = new(1, 1);
    }

    private readonly string _servicePipeName;
    private readonly string _root;
    private readonly BrowserBridge _bridge;
    private readonly ConcurrentDictionary<string, Lazy<Suite>> _suites = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _eventWrite = new(1, 1);
    private StreamWriter? _connectedWriter;
    private Process? _devBrowser;
    private string? _devConnectionId;
    private int _disposed;

    public BrowserServiceServer(string servicePipeName, string extensionPipeName, string? root = null)
    {
        _servicePipeName = servicePipeName;
        _root = root ?? AgentProfile.Root;
        _bridge = new BrowserBridge(extensionPipeName);
        _bridge.ApplicationStopRequested += OnApplicationStopRequested;
        _bridge.ImageGenerationBindingRequested += OnImageGenerationBindingRequested;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(_servicePipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            _connectedWriter = writer;
            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    JsonObject? message;
                    try { message = JsonNode.Parse(line)?.AsObject(); }
                    catch (JsonException) { continue; }
                    if (message is null) continue;
                    var response = await HandleAsync(message, cancellationToken).ConfigureAwait(false);
                    await WriteAsync(writer, response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
            }
            finally { _connectedWriter = null; }
        }
    }

    private async Task<JsonObject> HandleAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var id = message["id"]?.GetValue<string>() ?? "";
        try
        {
            return message["kind"]?.GetValue<string>() switch
            {
                "hello" => Hello(id, message),
                "execute" => await ExecuteAsync(id, message, cancellationToken).ConfigureAwait(false),
                "endSession" => await EndSessionAsync(id, message, cancellationToken).ConfigureAwait(false),
                _ => Error(id, "Unknown browser service request.")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            return Error(id, ex.Message);
        }
    }

    private static JsonObject Hello(string id, JsonObject message)
    {
        var requested = message["protocolVersion"]?.GetValue<int>() ?? 0;
        if (requested != BrowserRuntimeProtocol.Version)
            return Error(id, $"Browser service protocol mismatch: client={requested}, server={BrowserRuntimeProtocol.Version}.");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var handshake = new BrowserRuntimeHandshake(
            BrowserRuntimeProtocol.Version,
            version,
            BrowserRuntimeProtocol.Capabilities,
            ["dev", "chrome", "edge", "extension"]);
        return Ok(id, new JsonObject
        {
            ["handshake"] = JsonNode.Parse(JsonSerializer.Serialize(handshake, BrowserRuntimeProtocol.Json))
        });
    }

    private async Task<JsonObject> ExecuteAsync(string id, JsonObject message, CancellationToken cancellationToken)
    {
        var request = message["request"]?.Deserialize<BrowserRuntimeRequest>(BrowserRuntimeProtocol.Json)
            ?? throw new ArgumentException("Browser runtime request is required.");
        var args = JsonNode.Parse(request.Arguments.GetRawText())?.AsObject()
            ?? throw new ArgumentException("Browser tool arguments must be an object.");
        if (request.ToolId.StartsWith("imagegen.", StringComparison.Ordinal))
            return await ExecuteImageGenerationAsync(id, request, args, cancellationToken).ConfigureAwait(false);
        var requestedFamily = BrowserFamilyRouting.Parse(request.Context.BrowserFamily);
        var family = BrowserFamilyRouting.Resolve(requestedFamily, args);
        var suite = GetSuite(request.Context.IsolationScopeId, family);

        await suite.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        IDisposable? applicationScope = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.Context.ApplicationSessionId))
                applicationScope = _bridge.EnterApplicationSession(request.Context.ApplicationSessionId!);

            var shortName = request.ToolId.StartsWith("browser.", StringComparison.Ordinal)
                ? request.ToolId["browser.".Length..]
                : request.ToolId;
            if (!suite.Tools.TryGetValue(shortName, out var tool))
                throw new ArgumentException($"Unknown browser tool '{request.ToolId}'.");

            // Discovery/selection tools define browser availability themselves and must remain callable
            // before any external browser is connected. Page-bound tools still route through a family.
            if (shortName is not "list_connected_browsers" and not "select_browser")
                await SelectFamilyAsync(family, cancellationToken).ConfigureAwait(false);

            using var consent = ToolConsentScope.Enter(request.Context.FullPermission, cancellationToken);
            var context = new ToolExecutionContext
            {
                WorkingDirectory = request.Context.WorkingDirectory,
                AdditionalDirectories = request.Context.AdditionalDirectories,
                EnforceWorkspaceFileScope = false,
                CallId = request.Context.CallId,
                SessionId = request.Context.ApplicationSessionId,
                ShellTimeout = TimeSpan.FromSeconds(110),
                MaxOutputChars = 60000,
                SessionLifetime = cancellationToken
            };
            var result = await tool.ExecuteAsync(args, context, cancellationToken).ConfigureAwait(false);
            var reply = new BrowserRuntimeReply(
                result.Content + (result.FollowUpText is null ? "" : "\n" + result.FollowUpText),
                result.IsError,
                result.Images?.Select(image => new WireImage(image.MediaType, image.Base64Data)).ToArray());
            return Ok(id, new JsonObject
            {
                ["reply"] = JsonNode.Parse(JsonSerializer.Serialize(reply, BrowserRuntimeProtocol.Json))
            });
        }
        finally
        {
            applicationScope?.Dispose();
            suite.Serial.Release();
        }
    }

    private async Task<JsonObject> EndSessionAsync(string id, JsonObject message, CancellationToken cancellationToken)
    {
        var sessionId = message["sessionId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId is required.");
        var close = message["close"]?.GetValue<bool>() == true;
        await _bridge.EndApplicationSessionAsync(sessionId, close, cancellationToken).ConfigureAwait(false);
        if (close)
        {
            foreach (var key in _suites.Keys.Where(key => key.StartsWith(sessionId + "|", StringComparison.Ordinal)))
                _suites.TryRemove(key, out _);
        }
        return Ok(id);
    }

    private Suite GetSuite(string isolationScopeId, BrowserFamily family)
    {
        var key = isolationScopeId + "|" + family.WireName();
        return _suites.GetOrAdd(key, _ => new Lazy<Suite>(() =>
        {
            var imageDirectory = Path.Combine(_root, "browser-images", SafeSegment(isolationScopeId), family.WireName());
            var tools = JarvisBrowserTools.Create(_bridge, imageDirectory)
                .ToDictionary(tool => tool.Name, tool => tool, StringComparer.Ordinal);
            return new Suite(tools);
        })).Value;
    }

    private async Task SelectFamilyAsync(BrowserFamily family, CancellationToken cancellationToken)
    {
        if (family == BrowserFamily.Dev)
        {
            var id = await EnsureDevBrowserAsync(cancellationToken).ConfigureAwait(false);
            var error = _bridge.SelectBrowser(id);
            if (error is not null) throw new InvalidOperationException(error);
            return;
        }

        if (family is BrowserFamily.Chrome or BrowserFamily.Edge)
        {
            var expected = family == BrowserFamily.Chrome ? "Chrome" : "Edge";
            var connection = _bridge.Connections.FirstOrDefault(item =>
                item.Ready && !StringComparer.Ordinal.Equals(item.Id, _devConnectionId) &&
                item.Name.Equals(expected, StringComparison.OrdinalIgnoreCase));
            if (connection is null)
                throw new InvalidOperationException($"{expected} browser extension is not connected.");
            var error = _bridge.SelectBrowser(connection.Id);
            if (error is not null) throw new InvalidOperationException(error);
            return;
        }

        if (family == BrowserFamily.Extension)
        {
            var external = _bridge.Connections.FirstOrDefault(item =>
                    item.Ready && item.Active && !StringComparer.Ordinal.Equals(item.Id, _devConnectionId))
                ?? _bridge.Connections.FirstOrDefault(item =>
                    item.Ready && !StringComparer.Ordinal.Equals(item.Id, _devConnectionId));
            if (external is null)
                throw new InvalidOperationException("External browser extension is not connected.");
            var error = _bridge.SelectBrowser(external.Id);
            if (error is not null) throw new InvalidOperationException(error);
        }
    }

    private async Task<string> EnsureDevBrowserAsync(CancellationToken cancellationToken)
    {
        if (_devConnectionId is { } existing &&
            _bridge.Connections.Any(item => item.Ready && StringComparer.Ordinal.Equals(item.Id, existing)))
            return existing;

        var extensionDirectory = Path.Combine(_root, "browser-extension");
        if (!Directory.Exists(extensionDirectory))
            throw new InvalidOperationException("Frontend browser unavailable: run 'jarvis-agent browser-install' first.");

        var executable = FindChromiumExecutable()
            ?? throw new InvalidOperationException("Frontend browser unavailable: Chrome or Edge was not found.");
        var before = _bridge.Connections.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var profile = Path.Combine(_root, "browser-dev-profile");
        Directory.CreateDirectory(profile);
        _devBrowser = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"--user-data-dir=\"{profile}\" --disable-extensions-except=\"{extensionDirectory}\" --load-extension=\"{extensionDirectory}\" --no-first-run --no-default-browser-check --new-window about:blank",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        if (_devBrowser is null) throw new InvalidOperationException("Frontend browser unavailable: failed to launch isolated dev browser.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            var connection = _bridge.Connections.FirstOrDefault(item => item.Ready && !before.Contains(item.Id));
            if (connection is not null)
            {
                _devConnectionId = connection.Id;
                return connection.Id;
            }
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Frontend browser unavailable: isolated dev browser did not connect to the browser service.");
    }

    private static string? FindChromiumExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async void OnApplicationStopRequested(string sessionId)
    {
        var writer = _connectedWriter;
        if (writer is null) return;
        try
        {
            await WriteAsync(writer, new JsonObject
            {
                ["kind"] = "event",
                ["event"] = "sessionStop",
                ["sessionId"] = sessionId
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task WriteAsync(StreamWriter writer, JsonObject message, CancellationToken cancellationToken)
    {
        await _eventWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _eventWrite.Release(); }
    }

    private static JsonObject Ok(string id, JsonObject? extra = null)
    {
        var result = extra ?? new JsonObject();
        result["kind"] = "response";
        result["id"] = id;
        result["ok"] = true;
        return result;
    }

    private static JsonObject Error(string id, string error) => new()
    {
        ["kind"] = "response",
        ["id"] = id,
        ["ok"] = false,
        ["error"] = error
    };

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _bridge.ApplicationStopRequested -= OnApplicationStopRequested;
        _bridge.ImageGenerationBindingRequested -= OnImageGenerationBindingRequested;
        _bridge.Dispose();
        try
        {
            if (_devBrowser is { HasExited: false }) _devBrowser.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        _devBrowser?.Dispose();
        foreach (var suite in _suites.Values.Where(value => value.IsValueCreated).Select(value => value.Value))
            suite.Serial.Dispose();
        _eventWrite.Dispose();
    }
}
