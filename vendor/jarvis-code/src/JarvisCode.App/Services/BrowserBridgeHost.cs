using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace JarvisCode.App.Services;

internal sealed record BrowserBridgeRegistration(string Profile, string Pipe, int ProcessId, long StartedUtcTicks, string Token);

/// <summary>Same-user access to one desktop profile's existing extension connection.</summary>
public sealed class BrowserBridgeHost : IDisposable
{
    private readonly BrowserBridge _bridge;
    private readonly BrowserBridgeRegistration _registration;
    private readonly string _file;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _server;
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private int _nextRequest;
    private long _stopVersion;
    private string? _stopClient;
    private readonly object _stopLock = new();
    private int _disposed;

    public BrowserBridgeHost(string profileRoot, BrowserBridge bridge)
    {
        _bridge = bridge;
        var profile = ProfileKey(profileRoot);
        _file = RegistrationPath(profileRoot);
        _registration = new(profile, "JarvisCode-browser-client-" + profile + "-" + Guid.NewGuid().ToString("N"),
            Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(_registration));
        File.Move(temporary, _file, true);
        bridge.ClientStopRequested += OnStop;
        _server = Task.Run(AcceptAsync);
    }

    private void OnStop(string? client) { lock (_stopLock) { _stopClient = client; _stopVersion++; } }
    private JsonObject Status()
    {
        lock (_stopLock) return new JsonObject
        {
            ["connections"] = JsonSerializer.SerializeToNode(_bridge.Connections),
            ["stopVersion"] = _stopVersion, ["stopClient"] = _stopClient,
        };
    }
    internal static string RegistrationPath(string root) => Path.Combine(Path.GetFullPath(root), "browser-bridge-host.json");
    internal static string ProfileKey(string root) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant()
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))))[..24];

    private async Task AcceptAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_registration.Pipe, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (IOException) { pipe.Dispose(); continue; }
            var id = Interlocked.Increment(ref _nextRequest);
            var serving = ServeAsync(pipe);
            _requests[id] = serving;
            _ = serving.ContinueWith(_ => _requests.TryRemove(id, out var removed), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var request = await BrowserBridgeProtocol.ReadAsync(pipe, timeout.Token);
                var supplied = request["token"]?.GetValue<string>() ?? "";
                if (request["profile"]?.GetValue<string>() != _registration.Profile ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_registration.Token)))
                    throw new InvalidOperationException("The browser bridge profile identity did not match.");
                JsonNode data = request["operation"]?.GetValue<string>() switch
                {
                    "status" => Status(),
                    "request" => await _bridge.RequestForBrowserAsync(request["browser"]?.GetValue<string>(),
                        request["command"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing browser command."),
                        request["arguments"] as JsonObject, timeout.Token, request["client"]?.GetValue<string>()),
                    _ => throw new InvalidOperationException("Unknown browser bridge operation."),
                };
                await BrowserBridgeProtocol.WriteAsync(pipe, new JsonObject { ["ok"] = true, ["data"] = data.DeepClone() }, timeout.Token);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or OperationCanceledException or TimeoutException)
            {
                try { await BrowserBridgeProtocol.WriteAsync(pipe, new JsonObject { ["ok"] = false, ["error"] = error.Message }, timeout.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _bridge.ClientStopRequested -= OnStop;
        _lifetime.Cancel();
        try { _server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Task.WhenAll(_requests.Values).GetAwaiter().GetResult();
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<BrowserBridgeRegistration>(File.ReadAllText(_file))?.Token == _registration.Token)
                File.Delete(_file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        _lifetime.Dispose();
    }
}

internal sealed class BrowserBridgeClient : IDisposable
{
    private readonly string _root;
    private readonly string _profile;
    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _state = new();
    private BrowserConnectionInfo[] _connections = [];
    private string? _selected;
    private string? _host;
    private long _stopVersion;
    private readonly Task _poll;

    public BrowserBridgeClient(string root)
    {
        _root = Path.GetFullPath(root); _profile = BrowserBridgeHost.ProfileKey(root);
        _poll = Task.Run(PollAsync);
    }

    public event Action? StateChanged;
    public event Action? StopRequested;
    public IReadOnlyList<BrowserConnectionInfo> Connections
    { get { lock (_state) return [.. _connections.Select(item => item with { Active = item.Id == _selected })]; } }

    public string? SelectBrowser(string selection)
    {
        lock (_state)
        {
            var found = _connections.Where(item => item.Id.Equals(selection, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Equals(selection, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length != 1) return found.Length == 0 ? "No connected browser matches '" + selection + "'." : "Use an unambiguous browser id.";
            _selected = found[0].Id;
        }
        StateChanged?.Invoke();
        return null;
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        await _refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (data, host) = await SendAsync(new JsonObject { ["operation"] = "status" }, token);
            var connections = data["connections"]?.Deserialize<BrowserConnectionInfo[]>() ?? [];
            var stop = data["stopVersion"]?.GetValue<long>() ?? 0;
            bool changed, stopped;
            lock (_state)
            {
                changed = !connections.SequenceEqual(_connections) || _host != host;
                stopped = _host == host && stop > _stopVersion && data["stopClient"]?.GetValue<string>() == _clientId;
                if (_host != host || !connections.Any(item => item.Id == _selected))
                    _selected = connections.FirstOrDefault(item => item.Active && item.Ready)?.Id ?? connections.FirstOrDefault(item => item.Ready)?.Id;
                _host = host; _stopVersion = stop; _connections = connections;
            }
            if (changed) StateChanged?.Invoke();
            if (stopped) StopRequested?.Invoke();
        }
        finally { _refresh.Release(); }
    }

    public async Task<JsonObject> RequestAsync(string command, JsonObject? arguments, CancellationToken token)
    {
        string? selected;
        lock (_state) selected = _selected;
        if (selected is null) throw new InvalidOperationException("No browser is connected to this desktop profile.");
        return (await SendAsync(new JsonObject { ["operation"] = "request", ["browser"] = selected,
            ["command"] = command, ["arguments"] = arguments?.DeepClone() }, token)).Data;
    }

    private async Task<(JsonObject Data, string Host)> SendAsync(JsonObject request, CancellationToken token)
    {
        BrowserBridgeRegistration? registration;
        try
        {
            await using var file = new FileStream(BrowserBridgeHost.RegistrationPath(_root), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            if (file.Length > 4096) throw new IOException("The browser bridge registration is too large.");
            registration = await JsonSerializer.DeserializeAsync<BrowserBridgeRegistration>(file, cancellationToken: token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("Open Jarvis Code Desktop with the same profile and connect the Jarvis Browser extension.", error); }
        if (registration is not { Token.Length: 64, Pipe.Length: > 0, ProcessId: > 0, StartedUtcTicks: > 0 } ||
            registration.Token.Any(character => !Uri.IsHexDigit(character)) || registration.Profile != _profile ||
            !registration.Pipe.StartsWith("JarvisCode-browser-client-" + _profile + "-", StringComparison.Ordinal))
            throw new InvalidOperationException("The saved browser bridge identity is invalid.");
        try
        {
            using var owner = Process.GetProcessById(registration.ProcessId);
            if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != registration.StartedUtcTicks)
                throw new InvalidOperationException("The desktop browser host has exited. Reopen that profile.");
        }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException("The desktop browser host is unavailable. Reopen that profile.", error); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(28));
        await using var pipe = new NamedPipeClientStream(".", registration.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, timeout.Token).ConfigureAwait(false);
        if (OperatingSystem.IsWindows() && (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != registration.ProcessId))
            throw new InvalidOperationException("The browser bridge process identity did not match.");
        request["profile"] = _profile; request["token"] = registration.Token; request["client"] = _clientId;
        await BrowserBridgeProtocol.WriteAsync(pipe, request, timeout.Token);
        var result = await BrowserBridgeProtocol.ReadAsync(pipe, timeout.Token);
        if (result["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(result["error"]?.GetValue<string>() ?? "The desktop browser bridge rejected the request.");
        return (result["data"] as JsonObject ?? new JsonObject(), registration.Token);
    }

    private async Task PollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try { await RefreshAsync(_lifetime.Token); }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or JsonException)
            {
                bool changed;
                lock (_state) { changed = _connections.Length > 0; _connections = []; _selected = null; }
                if (changed) StateChanged?.Invoke();
            }
            try { await Task.Delay(500, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try { _poll.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
        _refresh.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}

internal static class BrowserBridgeProtocol
{
    private const int MaximumPacket = 64_000_000;
    public static async Task WriteAsync(Stream stream, JsonObject value, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaximumPacket) throw new IOException("Browser bridge packet is too large.");
        await stream.WriteAsync(BitConverter.GetBytes(data.Length), token);
        await stream.WriteAsync(data, token);
        await stream.FlushAsync(token);
    }
    public static async Task<JsonObject> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BitConverter.ToInt32(header);
        if (length is < 1 or > MaximumPacket) throw new IOException("Invalid browser bridge packet size.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonNode.Parse(data) as JsonObject ?? throw new IOException("Invalid browser bridge packet.");
    }
}
