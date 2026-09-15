using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace JarvisCode.Core.Ide;

/// <summary>Public discovery metadata deliberately excludes the local authentication token.</summary>
public sealed record IdeEditor(string Id, string Name, int ProcessId, IReadOnlyList<string> WorkspaceFolders, string LockFile)
{
    public string Transport { get; init; } = "jarvis-pipe";
    public int Port { get; init; }
    public long ProcessStartTicks { get; init; }
}

/// <summary>Authenticated local editor RPC. Every call revalidates the live lock file and server process.</summary>
public sealed class IdeBridge(string? registryDirectory = null) : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (string Fingerprint, IdeMcpClient Client)> _clients = new();
    private readonly SemaphoreSlim _connections = new(1);
    public string RegistryDirectory { get; } = registryDirectory ?? Environment.GetEnvironmentVariable("JARVIS_IDE_REGISTRY") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jarvis", "ide");
    private const int MaximumPacketBytes = 8 * 1024 * 1024;

    private IEnumerable<string> RegistryDirectories()
    {
        yield return RegistryDirectory;
        if (registryDirectory is not null || Environment.GetEnvironmentVariable("JARVIS_IDE_REGISTRY") is not null) yield break;
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        yield return Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? defaultRoot, "ide");
        yield return Path.Combine(defaultRoot, "ide");
    }

    public IReadOnlyList<IdeEditor> Discover(string? workingDirectory = null)
    {
        List<IdeEditor> editors = [];
        foreach (var file in RegistryDirectories().Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(ReadLockFiles))
        {
            try
            {
                if (new FileInfo(file).Length > 1024 * 1024) continue;
                var value = ParseRecord(file, File.ReadAllText(file));
                var transport = value["transport"]?.ToString();
                if (transport != "jarvis-pipe")
                {
                    if (transport is not (null or "sse" or "ws") ||
                        !int.TryParse(Path.GetFileNameWithoutExtension(file), out var port) || port is < 1 or > 65535 ||
                        value["pid"]?.GetValue<int>() is not { } referencePid || referencePid <= 0) continue;
                    using var referenceProcess = Process.GetProcessById(referencePid);
                    if (referenceProcess.HasExited || !IdePortOwner.Matches(port, referencePid)) continue;
                    var startTicks = referenceProcess.StartTime.ToUniversalTime().Ticks;
                    if (File.GetLastWriteTimeUtc(file).AddSeconds(2).Ticks < startTicks) continue;
                    var referenceFolders = (value["workspaceFolders"] as JsonArray)?.Select(node => node?.GetValue<string>() ?? "")
                        .Where(Directory.Exists).Select(Path.GetFullPath).ToArray() ?? [];
                    if (workingDirectory is not null && !referenceFolders.Any(folder => Contains(folder, workingDirectory))) continue;
                    var referenceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file) + "|" + referencePid + "|" + startTicks)))[..32].ToLowerInvariant();
                    editors.Add(new(referenceId, value["ideName"]?.ToString() ?? "IDE", referencePid, referenceFolders, file)
                    { Transport = transport == "ws" ? "ws" : "sse", Port = port, ProcessStartTicks = startTicks });
                    continue;
                }
                var id = value["id"]?.ToString() ?? "";
                var pid = value["pid"]?.GetValue<int>() ?? 0;
                if (id.Length != 32 || !id.All(Uri.IsHexDigit) || pid <= 0) continue;
                using var process = Process.GetProcessById(pid);
                if (process.HasExited || value["startedAt"]?.GetValue<long>() is not { } started ||
                    Math.Abs((process.StartTime.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds - started) > 2000) continue;
                var folders = (value["workspaceFolders"] as JsonArray)?.Select(n => n?.ToString() ?? "")
                    .Where(Directory.Exists).Select(Path.GetFullPath).ToArray() ?? [];
                if (workingDirectory is not null && !folders.Any(folder => Contains(folder, workingDirectory))) continue;
                editors.Add(new IdeEditor(id, value["ideName"]?.ToString() ?? "Visual Studio Code", pid, folders, file));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or
                ArgumentException or FormatException or OverflowException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
        }
        return editors;
    }

    private static IEnumerable<string> ReadLockFiles(string directory)
    {
        try { return Directory.GetFiles(directory, "*.lock"); }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Utilities.DiagnosticLog.Write($"IDE discovery could not read registry {directory}: {error.GetType().Name}");
            return [];
        }
    }

    private static JsonObject ParseRecord(string file, string content)
    {
        try { return JsonNode.Parse(content) as JsonObject ?? throw new InvalidDataException("The editor record must be an object."); }
        catch (System.Text.Json.JsonException)
        {
            // Older reference extensions wrote one workspace folder per line. The listener supplies the missing PID.
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var port) || IdePortOwner.FindListenerProcess(port) is not { } pid)
                throw new InvalidDataException("The legacy editor record has no verified local listener.");
            return new JsonObject
            {
                ["pid"] = pid, ["transport"] = "sse", ["ideName"] = "IDE",
                ["workspaceFolders"] = new JsonArray(content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
            };
        }
    }

    public async Task ConnectAsync(string workingDirectory, CancellationToken token = default, string? editorId = null)
    {
        var editor = ChooseEditor(workingDirectory, editorId);
        if (editor.Transport == "jarvis-pipe") await CallAsync(workingDirectory, "getOpenEditors", new JsonObject(), token, editor.Id);
        else await NativeClientAsync(editor, token);
    }

    private IdeEditor ChooseEditor(string workingDirectory, string? editorId)
    {
        var editors = Discover(workingDirectory).Where(editor => editorId is null || editor.Id == editorId).ToArray();
        if (editors.Length == 0) throw new InvalidOperationException("No connected editor has this folder open. Open the folder in an editor with an active IDE extension.");
        if (editors.Length > 1) throw new InvalidOperationException("More than one editor has this folder open. Choose an editor with /ide before sending the request.");
        return editors[0];
    }

    private async Task<(IdeMcpClient Client, string? Authentication)> NativeClientAsync(IdeEditor editor, CancellationToken token)
    {
        await _connections.WaitAsync(token);
        try
        {
            foreach (var entry in _clients.ToArray())
                if (!entry.Value.Client.IsConnected && _clients.TryRemove(entry.Key, out var expired)) await expired.Client.DisposeAsync();
            var record = ParseRecord(editor.LockFile, await File.ReadAllTextAsync(editor.LockFile, token));
            using var process = Process.GetProcessById(editor.ProcessId);
            if (record["pid"]?.GetValue<int>() != editor.ProcessId || process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks != editor.ProcessStartTicks || !IdePortOwner.Matches(editor.Port, editor.ProcessId))
                throw new InvalidOperationException("The editor connection record changed or its local listener is no longer available.");
            if ((record["transport"]?.ToString() == "ws" ? "ws" : "sse") != editor.Transport ||
                !(record["workspaceFolders"] as JsonArray ?? []).Select(node => node?.ToString() ?? "").Where(Directory.Exists).Select(Path.GetFullPath)
                    .Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(editor.WorkspaceFolders.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The editor connection transport or workspace changed; discover it again.");
            var authentication = record["authToken"]?.GetValue<string>();
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(editor.Transport + "|" + editor.Port + "|" + authentication)));
            if (_clients.TryGetValue(editor.Id, out var existing))
            {
                if (existing.Fingerprint == fingerprint && existing.Client.IsConnected) return (existing.Client, authentication);
                _clients.TryRemove(editor.Id, out _);
                await existing.Client.DisposeAsync();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            IdeMcpClient client;
            try { client = await IdeMcpClient.ConnectAsync(editor, authentication, timeout.Token); }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new IOException("Could not connect to the local IDE: " + Redact(error.Message, authentication)); }
            _clients[editor.Id] = (fingerprint, client);
            return (client, authentication);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
        { throw new IOException("The editor connection record is no longer valid; discover the editor again."); }
        finally { _connections.Release(); }
    }

    public async Task<JsonNode> CallAsync(string workingDirectory, string method, JsonObject arguments,
        CancellationToken cancellationToken = default, string? editorId = null)
    {
        var editor = ChooseEditor(workingDirectory, editorId);
        if (editor.Transport != "jarvis-pipe")
        {
            var (client, authentication) = await NativeClientAsync(editor, cancellationToken);
            try { return await client.CallAsync(method, arguments, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception failure)
            {
                if (_clients.TryRemove(editor.Id, out var failed)) await failed.Client.DisposeAsync();
                throw new IOException("IDE operation failed: " + Redact(failure.Message, authentication));
            }
        }
        var record = JsonNode.Parse(await File.ReadAllTextAsync(editor.LockFile, cancellationToken))!.AsObject();
        var token = record["authToken"]?.ToString();
        var pipeName = record["pipeName"]?.ToString();
        if (string.IsNullOrEmpty(token) || token.Length != 64 || pipeName != $"jarvis-ide-{editor.ProcessId}-{editor.Id}")
            throw new InvalidOperationException("The editor's local connection record is invalid.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(method == "openDiff" && arguments["wait_for_decision"]?.GetValue<bool>() != false
            ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        if (OperatingSystem.IsWindows() && (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != editor.ProcessId))
            throw new InvalidOperationException("The editor pipe does not belong to its registered process.");
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = "tools/call",
            ["authToken"] = token,
            ["clientInfo"] = new JsonObject { ["name"] = "Jarvis Code", ["pid"] = Environment.ProcessId },
            ["params"] = new JsonObject { ["name"] = method, ["arguments"] = arguments.DeepClone() },
        };
        var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request);
        if (data.Length > MaximumPacketBytes) throw new InvalidOperationException("Editor request exceeds 8 MB.");
        await pipe.WriteAsync(BitConverter.GetBytes(data.Length), timeout.Token);
        await pipe.WriteAsync(data, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        var prefix = new byte[4];
        await pipe.ReadExactlyAsync(prefix, timeout.Token);
        var length = BitConverter.ToInt32(prefix);
        if (length is < 1 or > MaximumPacketBytes) throw new IOException("Invalid editor response length.");
        var response = new byte[length];
        await pipe.ReadExactlyAsync(response, timeout.Token);
        var parsed = JsonNode.Parse(response)!.AsObject();
        if (parsed["id"]?.ToString() != request["id"]!.ToString()) throw new IOException("Editor response id did not match the request.");
        if (parsed["error"] is { } error) throw new InvalidOperationException(error["message"]?.ToString() ?? "Editor request failed.");
        return parsed["result"]?.DeepClone() ?? throw new IOException("Editor returned no result.");
    }

    private static string Redact(string message, string? authentication) =>
        string.IsNullOrEmpty(authentication) ? message : message.Replace(authentication, "[redacted]", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        await _connections.WaitAsync();
        try
        {
            foreach (var entry in _clients.ToArray())
                if (_clients.TryRemove(entry.Key, out var client)) await client.Client.DisposeAsync();
        }
        finally { _connections.Release(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal static bool Contains(string root, string path)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Equals(directory, comparison) || full.StartsWith(directory + Path.DirectorySeparatorChar, comparison);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
