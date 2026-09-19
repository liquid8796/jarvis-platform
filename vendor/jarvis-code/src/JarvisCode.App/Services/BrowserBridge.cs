using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>
/// Chrome native-messaging frame codec: 4-byte little-endian length + UTF-8 JSON.
/// </summary>
public static class NativeMessageCodec
{
    public static byte[] Encode(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Reads one frame; null at end of stream.</summary>
    public static string? ReadFrame(Stream stream)
    {
        var header = new byte[4];
        if (!FillBuffer(stream, header))
        {
            return null;
        }

        var length = BitConverter.ToInt32(header, 0);
        if (length is < 0 or > 64_000_000)
        {
            return null;
        }

        var payload = new byte[length];
        return FillBuffer(stream, payload) ? Encoding.UTF8.GetString(payload) : null;
    }

    private static bool FillBuffer(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}

/// <summary>What the browsers panel and the list/select tools show per connection.</summary>
public sealed record BrowserConnectionInfo(string Id, string Name, bool Ready, bool Active,
    int BrowserProtocolVersion = 0, string BrowserFamily = "extension", IReadOnlyList<string>? Capabilities = null,
    string? ExtensionInstanceId = null);

/// <summary>
/// The app side of the Jarvis Browser connection: a named-pipe server the
/// relay processes (launched by their browsers) connect to. Several browsers
/// may be connected at once — each gets an id (b1, b2, …) and the name its
/// extension announces; commands go to the selected one (the first ready
/// connection until browser_select_browser changes it). Requests carry an id;
/// the extension's responses resolve the matching awaiter.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    public const string PipeName = "JarvisCode-browser";
    public const int RequiredBrowserProtocolVersion = 2;
    public const int RequiredNativeHostProtocolVersion = 2;

    private sealed class Connection
    {
        public required string Id { get; init; }
        public string Name { get; set; } = "";
        public required StreamWriter Writer { get; init; }
        public bool Ready { get; set; }
        public bool ApplicationSessions { get; set; }
        public int BrowserProtocolVersion { get; set; }
        public int NativeHostProtocolVersion { get; set; }
        public string BrowserFamily { get; set; } = "extension";
        public string[] Capabilities { get; set; } = [];
        public string? ExtensionInstanceId { get; set; }

        public string Describe() => Name.Length > 0 ? $"{Id} ({Name})" : Id;
    }

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (Connection Connection, TaskCompletionSource<JsonObject> Waiter)> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<Connection> _connections = [];
    private readonly Dictionary<string, string?> _requestOwners = new(StringComparer.Ordinal);
    private string? _activeId;
    private readonly AsyncLocal<string?> _applicationSession = new();
    private readonly Dictionary<string, string> _applicationSelections = new(StringComparer.Ordinal);
    public event Action<string>? ApplicationStopRequested;

    /// <summary>Scope selection and native envelopes without changing legacy desktop bridge clients.</summary>
    public IDisposable EnterApplicationSession(string sessionId)
    {
        if (sessionId.Length != 35 || !sessionId.StartsWith("js_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(sessionId[3..], "N", out _)) throw new ArgumentException("Invalid application session.");
        if (_remote is not null) throw new InvalidOperationException("Application sessions require the agent's own native browser bridge.");
        var previous = _applicationSession.Value;
        _applicationSession.Value = sessionId;
        return new ApplicationScope(_applicationSession, previous);
    }
    private sealed class ApplicationScope(AsyncLocal<string?> slot, string? previous) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) slot.Value = previous; }
    }

    public async Task EndApplicationSessionAsync(string sessionId, bool close, CancellationToken cancellationToken)
    {
        using var scope = EnterApplicationSession(sessionId);
        string[] browsers;
        lock (_connections) browsers = _connections.Where(c => c.Ready && c.ApplicationSessions).Select(c => c.Id).ToArray();
        try
        {
            await Task.WhenAll(browsers.Select(id => RequestForBrowserAsync(id, "session",
                new JsonObject { ["active"] = false, ["close"] = close }, cancellationToken)));
        }
        finally { if (close) lock (_connections) _applicationSelections.Remove(sessionId); }
    }

    private int _nextNumber;
    private readonly BrowserBridgeClient? _remote;

    public bool IsConnected
    {
        get
        {
            if (_remote is not null) return _remote.Connections.Count > 0;
            lock (_connections)
            {
                return _connections.Count > 0;
            }
        }
    }

    /// <summary>True once the selected browser's extension announced itself over its relay.</summary>
    public bool ExtensionReady => _remote is not null
        ? _remote.Connections.Any(connection => connection.Active && connection.Ready) : Active()?.Ready == true;

    public event Action? StateChanged;

    /// <summary>Raised when the user pressed Stop on a page the agent is driving.</summary>
    public event Action? StopRequested;
    internal event Action<string?>? ClientStopRequested;

    public BrowserBridge(string? pipeName = null)
    {
        _pipeName = pipeName ?? PipeName;
        _ = Task.Run(AcceptLoopAsync);
    }

    private BrowserBridge(BrowserBridgeClient remote)
    {
        _pipeName = "";
        _remote = remote;
        remote.StateChanged += () => StateChanged?.Invoke();
        remote.StopRequested += () => StopRequested?.Invoke();
    }

    /// <summary>Connects to the existing desktop bridge for this exact profile without claiming its native pipe.</summary>
    public static BrowserBridge ForDesktopProfile(string profileRoot) => new(new BrowserBridgeClient(profileRoot));

    public Task RefreshDesktopConnectionAsync(CancellationToken cancellationToken = default) =>
        _remote?.RefreshAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>The connected browsers, selection marked.</summary>
    public IReadOnlyList<BrowserConnectionInfo> Connections
    {
        get
        {
            if (_remote is not null) return _remote.Connections;
            lock (_connections)
            {
                return [.. _connections.Select(c =>
                    new BrowserConnectionInfo(c.Id, c.Name, c.Ready, c.Id == Active()?.Id,
                    c.BrowserProtocolVersion, c.BrowserFamily, c.Capabilities, c.ExtensionInstanceId))];
            }
        }
    }

    /// <summary>
    /// Points subsequent commands at another connected browser, by id or announced
    /// name. Returns null on success, otherwise the reason (unknown, ambiguous).
    /// </summary>
    public string? SelectBrowser(string idOrName)
    {
        if (_remote is not null) return _remote.SelectBrowser(idOrName);
        lock (_connections)
        {
            var matches = _connections
                .Where(c => c.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
                            c.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1)
            {
                if (_applicationSession.Value is { } sessionId) _applicationSelections[sessionId] = matches[0].Id;
                else _activeId = matches[0].Id;
            }
            else if (matches.Count == 0)
            {
                return _connections.Count == 0
                    ? "No browser is connected."
                    : $"No connected browser matches '{idOrName}'. Connected: " +
                      string.Join(", ", _connections.Select(static c => c.Describe())) + ".";
            }
            else
            {
                return $"'{idOrName}' matches {matches.Count} browsers — use the id: " +
                       string.Join(", ", matches.Select(static c => c.Describe())) + ".";
            }
        }

        StateChanged?.Invoke();
        return null;
    }

    private Connection? Active()
    {
        lock (_connections)
        {
            if (_applicationSession.Value is { } sessionId)
            {
                if (_applicationSelections.TryGetValue(sessionId, out var selected))
                    return _connections.FirstOrDefault(c => c.Id == selected); // Never silently switch a selected browser.
                var first = _connections.FirstOrDefault(c => c.Ready) ?? _connections.FirstOrDefault();
                if (first is not null) _applicationSelections[sessionId] = first.Id;
                return first;
            }
            return _connections.FirstOrDefault(c => c.Id == _activeId) ?? _connections.FirstOrDefault();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException)
            {
                return; // another process owns the pipe name — nothing to accept here
            }

            try
            {
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
            catch (IOException)
            {
                await server.DisposeAsync();
                continue;
            }

            // Serve this browser in the background; the loop goes straight back to
            // accepting so a second browser can connect alongside it.
            _ = Task.Run(() => ServeConnectionAsync(server));
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream server)
    {
        var connection = new Connection
        {
            Id = $"b{Interlocked.Increment(ref _nextNumber)}",
            Writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true },
        };
        lock (_connections)
        {
            _connections.Add(connection);
            _activeId ??= connection.Id;
        }

        StateChanged?.Invoke();
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                HandleIncoming(connection, line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Relay dropped; fall through to cleanup.
        }
        finally
        {
            lock (_connections)
            {
                _connections.Remove(connection);
                _requestOwners.Remove(connection.Id);
                if (_activeId == connection.Id)
                {
                    _activeId = _connections.FirstOrDefault()?.Id;
                }
            }

            StateChanged?.Invoke();
            FailPending(connection, "The browser connection dropped.");
            await server.DisposeAsync();
        }
    }

    private void HandleIncoming(Connection connection, string line)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(line) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (message is null)
        {
            return;
        }

        // The page's own "Stop Jarvis" button, relayed by the extension.
        if (message["event"]?.GetValue<string>() == "stop_requested")
        {
            if (message["sessionId"]?.GetValue<string>() is { Length: 35 } applicationSession && connection.ApplicationSessions)
            {
                ApplicationStopRequested?.Invoke(applicationSession);
                return;
            }
            string? owner;
            lock (_connections) _requestOwners.TryGetValue(connection.Id, out owner);
            if (owner is null) StopRequested?.Invoke();
            ClientStopRequested?.Invoke(owner);
            return;
        }

        if (message["event"]?.GetValue<string>() == "ready")
        {
            connection.BrowserProtocolVersion = message["browserProtocolVersion"]?.GetValue<int>() ?? 0;
            connection.NativeHostProtocolVersion = message["nativeHostProtocolVersion"]?.GetValue<int>() ?? 0;
            connection.ApplicationSessions = message["applicationSessions"]?.GetValue<bool>() == true;
            connection.BrowserFamily = message["browserFamily"]?.GetValue<string>() ?? "extension";
            connection.ExtensionInstanceId = message["extensionInstanceId"]?.GetValue<string>();
            connection.Capabilities = message["capabilities"] is JsonArray capabilities
                ? capabilities.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray()
                : [];
            connection.Ready = connection.BrowserProtocolVersion >= RequiredBrowserProtocolVersion &&
                connection.NativeHostProtocolVersion >= RequiredNativeHostProtocolVersion;
            if (message["browser"]?.GetValue<string>() is { Length: > 0 } name)
            {
                connection.Name = name;
            }

            StateChanged?.Invoke();
            return;
        }

        if (message["id"]?.GetValue<string>() is { } id && _pending.TryGetValue(id, out var candidate) &&
            ReferenceEquals(candidate.Connection, connection) && _pending.TryRemove(id, out var entry))
        {
            entry.Waiter.TrySetResult(message);
        }
    }

    private void FailPending(Connection connection, string reason)
    {
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryGetValue(key, out var entry) && ReferenceEquals(entry.Connection, connection) &&
                _pending.TryRemove(key, out entry))
            {
                entry.Waiter.TrySetException(new InvalidOperationException(reason));
            }
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var entry))
            {
                entry.Waiter.TrySetException(new InvalidOperationException(reason));
            }
        }
    }

    /// <summary>
    /// Tells the extension a turn is over, so the glow and the Stop button come
    /// off the pages it was driving. Best-effort: a browser that is not connected
    /// has nothing showing anyway.
    /// </summary>
    public void EndSession()
    {
        if (!ExtensionReady)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await RequestAsync("session", new JsonObject { ["active"] = false }, timeout.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                // The browser went away mid-turn; the extension clears on disconnect.
            }
        });
    }

    /// <summary>Sends a command to the selected browser's extension and awaits its response.</summary>
    public async Task<JsonObject> RequestAsync(string cmd, JsonObject? args, CancellationToken cancellationToken)
    {
        if (_remote is not null) return await _remote.RequestAsync(cmd, args, cancellationToken);
        return await RequestForBrowserAsync(null, cmd, args, cancellationToken);
    }

    internal async Task<JsonObject> RequestForBrowserAsync(string? browserId, string cmd, JsonObject? args, CancellationToken cancellationToken,
        string? requestingClient = null)
    {
        Connection? connection;
        lock (_connections) connection = browserId is null ? Active() : _connections.FirstOrDefault(item => item.Id == browserId);
        if (connection is null)
        {
            throw new InvalidOperationException(
                "Jarvis Browser is not connected. Install the extension (Customize → Connectors) and make sure the browser is running.");
        }
        if (!connection.Ready)
            throw new InvalidOperationException(
                $"Update and reload the Jarvis browser extension. Browser protocol {RequiredBrowserProtocolVersion}+ and native-host protocol {RequiredNativeHostProtocolVersion}+ are required.");
        if (cmd == "qa" && !connection.Capabilities.Contains("structured-qa-v1", StringComparer.Ordinal))
            throw new InvalidOperationException("Browser QA requires Jarvis Agent Browser 1.4.0+ (structured-qa-v1). Run browser-install, reload the extension, and retry.");
        var applicationSession = _applicationSession.Value;
        if (applicationSession is not null && !connection.ApplicationSessions)
            throw new InvalidOperationException("Update and reload the Jarvis browser extension before using isolated application sessions.");
        // Status reads do not steal ownership of a page's Stop button from
        // the session currently driving it. Direct desktop actions use null.
        if (applicationSession is null && cmd == "session" && args?["active"]?.GetValue<bool>() == false)
        {
            lock (_connections)
                if (_requestOwners.GetValueOrDefault(connection.Id) != requestingClient) return new JsonObject();
        }
        else if (applicationSession is null && cmd is not ("tabs" or "tab_origin"))
            lock (_connections) _requestOwners[connection.Id] = requestingClient;

        var id = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (connection, waiter);

        var request = new JsonObject { ["id"] = id, ["cmd"] = cmd };
        if (applicationSession is not null) request["sessionId"] = applicationSession;
        if (args is not null)
        {
            request["args"] = args.DeepClone();
        }

        try { await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { _pending.TryRemove(id, out _); throw; }
        try
        {
            await connection.Writer.WriteLineAsync(request.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { _pending.TryRemove(id, out _); throw; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException($"The {connection.Describe()} connection dropped mid-request.", ex);
        }
        finally
        {
            _writeLock.Release();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(cmd == "qa" ? 110 : 25));
        await using var registration = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Waiter.TrySetException(new TimeoutException("The browser did not answer in time."));
            }
        });

        var response = await waiter.Task.ConfigureAwait(false);
        if (response["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException(response["error"]?.GetValue<string>() ?? "The browser reported an error.");
        }

        return response["data"] as JsonObject ?? new JsonObject { ["result"] = response["data"]?.DeepClone() };
    }

    public void Dispose()
    {
        _remote?.Dispose();
        _cts.Cancel();
        FailAllPending("The app is shutting down.");
        _cts.Dispose();
    }
}
