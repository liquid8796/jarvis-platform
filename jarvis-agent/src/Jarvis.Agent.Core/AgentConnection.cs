using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using Json.Schema;
using Jarvis.Protocol;
namespace Jarvis.Agent.Core;

/// <summary>Outbound-only WebSocket, bounded concurrency, heartbeats, cancellation, no replay on reconnect.</summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly IApprovalService _approval;
    private readonly ToolPermissionPolicy _permissions;
    private readonly IReadOnlyDictionary<string, JsonSchema> _schemas;
    private readonly LocalControlGate _gate;
    private readonly SemaphoreSlim _parallel = new(4, 4);
    private readonly SemaphoreSlim _interactive = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, Task> _tasks = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ToolReply Reply)> _completed = new();
    private readonly CancellationTokenSource _lifetime = new();
    private WireSocket? _current;
    private int _started;
    private long _lastPong;
    public event Action<AgentEvent>? Activity;
    public event Action<bool>? ConnectionChanged;
    public bool IsConnected { get; private set; }
    public IReadOnlyList<ToolDescriptor> Descriptors => _tools.Values.Select(t => t.Descriptor).OrderBy(t => t.Id).ToArray();

    public AgentConnection(IEnumerable<IAgentTool> tools, IApprovalService approval, LocalControlGate gate, ToolPermissionPolicy? permissions = null)
    {
        _tools = tools.ToDictionary(t => t.Descriptor.Id, StringComparer.Ordinal);
        _approval = approval; _gate = gate;
        _permissions = permissions ?? new ToolPermissionPolicy();
        _permissions.PermissionsRevoked += CancelInFlight;
        _schemas = _tools.ToDictionary(t => t.Key, t => SchemaGuard.Compile(t.Value.Descriptor.InputSchema));
    }
    private void CancelInFlight()
    {
        foreach (var cancellation in _running.Values)
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        Emit("security", "Standing tool permission revoked; in-flight calls cancelled.");
    }
    public void Pause()
    {
        _gate.Disarm();
        foreach (var cancellation in _running.Values) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        Emit("security", "Remote control paused; in-flight calls cancelled.");
    }
    public void Disconnect() { Pause(); _lifetime.Cancel(); _current?.Abort(); }

    public async Task RunAsync(AgentOptions options, string token, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Connection already started.");
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Missing enrollment token.");
        var endpoint = options.ValidateAndGetWebSocketUri();
        var folders = new WorkspaceDirectories(options.Workspace, options.AdditionalDirectories);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            try
            {
                Emit("connection", attempt == 0 ? "Connecting securely…" : "Reconnecting; interrupted calls will not be replayed.");
                await socket.ConnectAsync(endpoint, stop.Token).ConfigureAwait(false);
                var wire = _current = new WireSocket(socket);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                await wire.SendAsync(new WireMessage("hello")
                {
                    Hello = new AgentHello(options.DeviceId,
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.24.0",
                        Environment.OSVersion.ToString(), Environment.MachineName, Descriptors)
                }, session.Token).ConfigureAwait(false);
                using (var welcomeTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    welcomeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    if ((await wire.ReceiveAsync(welcomeTimeout.Token).ConfigureAwait(false))?.Type != "welcome")
                        throw new InvalidDataException("Server did not accept the agent handshake.");
                }
                Interlocked.Exchange(ref _lastPong, Environment.TickCount64);
                IsConnected = true; ConnectionChanged?.Invoke(true); attempt = 0;
                Emit("connection", "Connected. Local tool-permission settings apply; control must be armed locally.");
                var heartbeat = HeartbeatAsync(wire, session.Token);
                try
                {
                    while (!session.IsCancellationRequested)
                    {
                        var message = await wire.ReceiveAsync(session.Token).ConfigureAwait(false);
                        if (message is null) break;
                        switch (message.Type)
                        {
                            case "pong": Interlocked.Exchange(ref _lastPong, Environment.TickCount64); break;
                            case "ping": await wire.SendAsync(new WireMessage("pong") { Timestamp = message.Timestamp }, session.Token); break;
                            case "cancel":
                                if (message.Id is not null && _running.TryGetValue(message.Id, out var pending))
                                    try { pending.Cancel(); } catch (ObjectDisposedException) { }
                                break;
                            case "call": Dispatch(wire, message, folders, session.Token); break;
                            default: throw new InvalidDataException("Unexpected server message.");
                        }
                    }
                }
                finally
                {
                    session.Cancel();
                    try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    try { await Task.WhenAll(_tasks.Values).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is WebSocketException or IOException or System.Text.Json.JsonException or OperationCanceledException)
            { Emit("connection", "Connection interrupted: " + ex.GetType().Name); }
            finally { IsConnected = false; ConnectionChanged?.Invoke(false); _current = null; }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5))) + Random.Shared.NextDouble()), stop.Token); }
            catch (OperationCanceledException) { break; }
        }
        Emit("connection", "Disconnected.");
    }
    private async Task HeartbeatAsync(WireSocket wire, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastPong) > 50_000)
            { wire.Abort(); return; }
            try { await wire.SendAsync(new WireMessage("ping") { Timestamp = Environment.TickCount64 }, cancellationToken); }
            catch (Exception ex) when (ex is WebSocketException or IOException) { wire.Abort(); return; }
            if (!_gate.IsArmed) foreach (var active in _running.Values)
                try { active.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
    private void Dispatch(WireSocket wire, WireMessage message, WorkspaceDirectories workspace, CancellationToken sessionToken)
    {
        if (string.IsNullOrEmpty(message.Id) || message.Id.Length > 100 || message.Arguments is null || message.ToolId is null)
            throw new InvalidDataException("Invalid tool invocation envelope.");
        var id = message.Id;
        if (_running.ContainsKey(id)) return;
        // The receive loop never waits for approval or a long-running command.
        if (_running.Count >= 16) { Track(id + "-busy", ReplyAsync(wire, id, ToolReply.Error("Agent busy; call not started."), sessionToken)); return; }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        if (!_running.TryAdd(id, cts)) { cts.Dispose(); return; }
        Track(id, ExecuteAsync(wire, message, workspace, cts));
    }
    private void Track(string id, Task task)
    {
        _tasks[id] = task;
        _ = task.ContinueWith(completed => { _tasks.TryRemove(id, out _); _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private async Task ExecuteAsync(WireSocket wire, WireMessage call, WorkspaceDirectories workspace, CancellationTokenSource cts)
    {
        var id = call.Id!; var acquired = false; var interactiveAcquired = false;
        try
        {
            var remaining = (call.DeadlineUtc ?? DateTimeOffset.UtcNow).Subtract(DateTimeOffset.UtcNow);
            if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(5))
                throw new InvalidOperationException("Missing, expired or excessive deadline.");
            cts.CancelAfter(remaining);
            if (!_gate.IsArmed) throw new UnauthorizedAccessException("Remote control is paused. Arm it locally in Jarvis Agent.");
            if (_completed.TryGetValue(id, out var cached))
            { await ReplyAsync(wire, id, cached.Reply, cts.Token); return; }
            if (!_tools.TryGetValue(call.ToolId!, out var tool)) throw new InvalidOperationException("Tool is not installed on this agent.");
            if (!SchemaGuard.Matches(_schemas[call.ToolId!], call.Arguments!.Value)) throw new ArgumentException("Arguments do not match the local tool schema.");
            await _parallel.WaitAsync(cts.Token); acquired = true;
            if (!tool.Descriptor.ReadOnly || tool.Descriptor.Sensitive)
            { await _interactive.WaitAsync(cts.Token); interactiveAcquired = true; }
            if (!_gate.IsArmed) throw new UnauthorizedAccessException("Local control was paused.");
            if (_permissions.RequiresApproval(tool.Descriptor) &&
                !await _approval.ApproveAsync(tool.Descriptor, call.Arguments!.Value, cts.Token))
                throw new UnauthorizedAccessException("The local user denied this action.");
            cts.Token.ThrowIfCancellationRequested();
            if (!_gate.IsArmed) throw new UnauthorizedAccessException("Local control was paused.");
            Emit("tool", "Started " + tool.Descriptor.Name);
            var result = await tool.ExecuteAsync(call.Arguments!.Value,
                new AgentExecutionContext(workspace.Primary, id, call.SessionId ?? "remote")
                { AdditionalDirectories = workspace.Additional, FullPermission = _permissions.HasFullPermission(tool.Descriptor.Id) }, cts.Token);
            _completed[id] = (DateTimeOffset.UtcNow, result);
            foreach (var old in _completed.OrderBy(x => x.Value.At).Take(Math.Max(0, _completed.Count - 128)))
                _completed.TryRemove(old.Key, out _);
            Emit("tool", (result.IsError ? "Failed " : "Completed ") + tool.Descriptor.Name);
            await ReplyAsync(wire, id, result, cts.Token);
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "Call cancelled or deadline exceeded; do not replay mutating actions blindly."
                : ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException ? ex.Message
                : "Tool failed: " + ex.GetType().Name;
            _completed[id] = (DateTimeOffset.UtcNow, ToolReply.Error(message));
            using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await ReplyAsync(wire, id, ToolReply.Error(message), responseTimeout.Token); }
            catch (Exception sendError) when (sendError is WebSocketException or IOException or OperationCanceledException) { }
        }
        finally
        {
            if (interactiveAcquired) _interactive.Release();
            if (acquired) _parallel.Release();
            _running.TryRemove(id, out _); cts.Dispose();
            foreach (var old in _completed.OrderBy(x => x.Value.At).Take(Math.Max(0, _completed.Count - 128))) _completed.TryRemove(old.Key, out _);
        }
    }
    private static Task ReplyAsync(WireSocket wire, string id, ToolReply reply, CancellationToken token) =>
        wire.SendAsync(new WireMessage("result") { Id = id, Result = reply }, token);
    private void Emit(string kind, string message) => Activity?.Invoke(new(DateTimeOffset.Now, kind, message));
    public async ValueTask DisposeAsync()
    {
        _permissions.PermissionsRevoked -= CancelInFlight;
        Disconnect();
        try { await Task.WhenAll(_tasks.Values).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _lifetime.Dispose();
    }
}
