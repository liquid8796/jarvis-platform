using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Jarvis.Agent.Core.DeveloperTools;
using Jarvis.Agent.Core.Plugins;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Agent.Core.ToolPrograms;
using Jarvis.Protocol;
namespace Jarvis.Agent.Core;

/// <summary>Outbound-only WebSocket, bounded concurrency, heartbeats, cancellation, no replay on reconnect.</summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly DynamicToolRegistry _registry;
    private readonly string? _taskStorageRoot;
    private readonly Func<Uri, string, CancellationToken, Task<WebSocket>> _socketConnector;
    private RemoteTaskHost? _remoteTasks;
    private readonly IApprovalService _approval;
    private readonly ToolPermissionPolicy _permissions;
    private readonly AgentLifecycleHub _lifecycle;
    private readonly IRemoteTaskAdaptiveCoordinator? _adaptiveCoordinator;
    private readonly LocalControlGate _gate;
    private readonly SemaphoreSlim _parallel = new(4, 4);
    private readonly SemaphoreSlim _interactive = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, Task> _tasks = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ToolReply Reply)> _completed = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _pluginSync = new();
    private HashSet<string> _pluginToolIds = new(StringComparer.Ordinal);
    private WireSocket? _current;
    private int _started;
    private long _lastPong;
    public event Action<AgentEvent>? Activity;
    public event Action<bool>? ConnectionChanged;
    public bool IsConnected { get; private set; }
    public DynamicToolRegistry ToolRegistry => _registry;
    public IReadOnlyList<ToolDescriptor> Descriptors => _registry.Snapshot.Descriptors;
    public bool AdaptiveCoordinatorAvailable => _adaptiveCoordinator is not null;

    public AgentConnection(IEnumerable<IAgentTool> tools, IApprovalService approval, LocalControlGate gate, ToolPermissionPolicy? permissions = null,
        string? taskStorageRoot = null, Func<Uri, string, CancellationToken, Task<WebSocket>>? socketConnector = null)
        : this(new DynamicToolRegistry(tools), approval, gate, permissions, taskStorageRoot, socketConnector) { }

    public AgentConnection(DynamicToolRegistry registry, IApprovalService approval, LocalControlGate gate, ToolPermissionPolicy? permissions = null,
        string? taskStorageRoot = null, Func<Uri, string, CancellationToken, Task<WebSocket>>? socketConnector = null, AgentLifecycleHub? lifecycle = null,
        IRemoteTaskAdaptiveCoordinator? adaptiveCoordinator = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _approval = approval; _gate = gate;
        _taskStorageRoot = taskStorageRoot;
        _socketConnector = socketConnector ?? ConnectSocketAsync;
        _lifecycle = lifecycle ?? new AgentLifecycleHub();
        _adaptiveCoordinator = adaptiveCoordinator;
        _gate.Changed += OnGateChanged;
        _permissions = permissions ?? new ToolPermissionPolicy();
        _permissions.PermissionsRevoked += CancelInFlight;
        _registry.Changed += OnRegistryChanged;
        EnsureCompositeTools();
    }

    private void EnsureCompositeTools()
    {
        var snapshot = _registry.Snapshot;
        var additions = new List<IAgentTool>();
        if (!snapshot.Tools.ContainsKey("tool_program.run"))
            additions.Add(new ToolProgramTool(new ToolProgramEngine(InvokeInstalledToolAsync)));
        if (!snapshot.Tools.ContainsKey("developer.symbol_search"))
            additions.Add(new DeveloperSymbolSearchTool());
        if (!snapshot.Tools.ContainsKey("developer.test"))
            additions.Add(new DeveloperTestTool());
        if (additions.Count > 0) _registry.Replace(snapshot.Tools.Values.Concat(additions));
    }
    public void ApplyPluginCatalog(PluginCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (_pluginSync)
        {
            EnsureCompositeTools();
            var current = _registry.Snapshot.Tools.Values
                .Where(tool => !_pluginToolIds.Contains(tool.Descriptor.Id))
                .ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);
            foreach (var pair in catalog.Tools)
            {
                if (current.ContainsKey(pair.Key))
                    throw new ArgumentException("Plugin tool ID conflicts with an installed non-plugin tool: " + pair.Key);
                current.Add(pair.Key, pair.Value);
            }
            _registry.Replace(current.Values);
            _pluginToolIds = catalog.Tools.Keys.ToHashSet(StringComparer.Ordinal);
        }
    }

    private void OnRegistryChanged(DynamicToolSnapshot snapshot)
    {
        var wire = _current;
        if (wire is null || wire.State != WebSocketState.Open) return;
        Track("catalog-" + snapshot.Generation, PushCatalogChangedAsync(wire, snapshot));
    }

    private async Task PushCatalogChangedAsync(WireSocket wire, DynamicToolSnapshot snapshot)
    {
        try
        {
            await wire.SendAsync(new WireMessage("catalog.changed")
            {
                CatalogGeneration = snapshot.Generation,
                CatalogDigest = snapshot.Digest,
                CatalogTools = snapshot.Descriptors
            }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException) { }
    }

    private void OnGateChanged(bool armed)
    { if (!armed) _remoteTasks?.CancelAll("CANCELLED"); }
    private void CancelInFlight()
    {
        _remoteTasks?.CancelAll("CANCELLED");
        foreach (var cancellation in _running.Values)
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        Emit("security", "Standing tool permission revoked; in-flight calls cancelled.");
    }
    public void Pause()
    {
        _gate.Disarm();
        _ = _lifecycle.NotifyAsync(new AgentLifecycleEvent(AgentLifecycleKind.Stop, Reason: "Local control paused."), CancellationToken.None);
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
        var taskRoot = Path.Combine(_taskStorageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAgent", "TaskRuns"), RemoteTaskStore.Hash(endpoint.AbsoluteUri + "|" + options.DeviceId));
        _remoteTasks = new RemoteTaskHost(taskRoot, folders, _registry, () => _gate.IsArmed,
            InvokeInstalledToolAsync, CancelOwnedJobAsync, _adaptiveCoordinator);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Emit("connection", attempt == 0 ? "Connecting securely…" : "Reconnecting; interrupted calls will not be replayed.");
                using var socket = await _socketConnector(endpoint, token, stop.Token).ConfigureAwait(false);
                var wire = _current = new WireSocket(socket);
                var catalog = _registry.Snapshot;
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                await wire.SendAsync(new WireMessage("hello")
                {
                    Hello = new AgentHello(options.DeviceId,
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                        Environment.OSVersion.ToString(), Environment.MachineName, catalog.Descriptors)
                    { TaskProtocolVersion = RemoteTaskRules.ProtocolVersion, ProtocolVersion = AgentProtocolVersion.Current,
                        Capabilities = AgentProtocolCapabilities.Agent, CatalogGeneration = catalog.Generation, CatalogDigest = catalog.Digest }
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
                            case "catalog.ack": break;
                            case "ping": await wire.SendAsync(new WireMessage("pong") { Timestamp = message.Timestamp }, session.Token); break;
                            case "cancel":
                                if (message.Id is not null && _running.TryGetValue(message.Id, out var pending))
                                    try { pending.Cancel(); } catch (ObjectDisposedException) { }
                                _ = _lifecycle.NotifyAsync(new AgentLifecycleEvent(AgentLifecycleKind.Interrupt, message.ThreadId, message.TurnId, message.Id), CancellationToken.None);
                                break;
                            case "call": Dispatch(wire, message, folders, session.Token); break;
                            case "task.request": DispatchTask(wire, message, session.Token); break;
                            default: throw new InvalidDataException("Unexpected server message.");
                        }
                    }
                }
                finally
                {
                    _remoteTasks.CancelAll();
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
        var id = call.Id!;
        try
        {
            var remaining = (call.DeadlineUtc ?? DateTimeOffset.UtcNow).Subtract(DateTimeOffset.UtcNow);
            if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(5))
                throw new InvalidOperationException("Missing, expired or excessive deadline.");
            cts.CancelAfter(remaining);
            if (!_gate.IsArmed) throw new UnauthorizedAccessException("Remote control is paused. Arm it locally in Jarvis Agent.");
            if (_completed.TryGetValue(id, out var cached))
            { await ReplyAsync(wire, id, cached.Reply, cts.Token); return; }
            var result = await InvokeInstalledToolAsync(call.ToolId!, call.Arguments!.Value,
                new AgentExecutionContext(workspace.Primary, id, call.SessionId ?? "remote")
                { AdditionalDirectories = workspace.Additional, ThreadId = call.ThreadId, TurnId = call.TurnId }, cts.Token,
                call.ExpectedCatalogGeneration, call.ExpectedCatalogDigest);
            _completed[id] = (DateTimeOffset.UtcNow, result);
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
            _running.TryRemove(id, out _); cts.Dispose();
            foreach (var old in _completed.OrderBy(x => x.Value.At).Take(Math.Max(0, _completed.Count - 128))) _completed.TryRemove(old.Key, out _);
        }
    }

    // One policy path for ordinary MCP calls and every task step. Remote mode never grants FullPermission.
    private Task<ToolReply> InvokeInstalledToolAsync(string toolId, JsonElement arguments,
        AgentExecutionContext context, CancellationToken ct) =>
        InvokeInstalledToolAsync(toolId, arguments, context, ct, null, null);

    private async Task<ToolReply> InvokeInstalledToolAsync(string toolId, JsonElement arguments,
        AgentExecutionContext context, CancellationToken ct, long? expectedCatalogGeneration, string? expectedCatalogDigest)
    {
        var acquired = false; var interactiveAcquired = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!_gate.IsArmed) throw new UnauthorizedAccessException("Local control is paused.");
            var snapshot = _registry.Snapshot;
            if (expectedCatalogGeneration is { } generation && generation != snapshot.Generation)
                throw new InvalidOperationException("Tool catalog changed; refresh descriptors before invoking this call.");
            if (!string.IsNullOrWhiteSpace(expectedCatalogDigest) && !StringComparer.Ordinal.Equals(expectedCatalogDigest, snapshot.Digest))
                throw new InvalidOperationException("Tool catalog changed; refresh descriptors before invoking this call.");
            if (!snapshot.Tools.TryGetValue(toolId, out var tool)) throw new InvalidOperationException("Tool is not installed on this agent.");
            if (!SchemaGuard.Matches(snapshot.Schemas[toolId], arguments)) throw new ArgumentException("Arguments do not match the local tool schema.");
            var composite = tool is ICompositeAgentTool;
            await _parallel.WaitAsync(ct); acquired = true;
            if (!tool.Descriptor.ReadOnly || tool.Descriptor.Sensitive)
            { await _interactive.WaitAsync(ct); interactiveAcquired = true; }
            if (!_gate.IsArmed) throw new UnauthorizedAccessException("Local control was paused.");
            if (_permissions.RequiresApproval(tool.Descriptor, arguments, context) && !await _approval.ApproveAsync(tool.Descriptor, arguments, ct))
                throw new UnauthorizedAccessException("The local user denied this action.");
            ct.ThrowIfCancellationRequested();
            if (!_gate.IsArmed) throw new UnauthorizedAccessException("Local control was paused.");
            if (composite)
            {
                if (interactiveAcquired) { _interactive.Release(); interactiveAcquired = false; }
                if (acquired) { _parallel.Release(); acquired = false; }
            }
            Emit("tool", "Started " + tool.Descriptor.Name);
            var result = await tool.ExecuteAsync(arguments,
                context with { FullPermission = _permissions.HasFullPermission(toolId, arguments, context) }, ct);
            Emit("tool", (result.IsError ? "Failed " : "Completed ") + tool.Descriptor.Name);
            return result;
        }
        finally
        {
            if (interactiveAcquired) _interactive.Release();
            if (acquired) _parallel.Release();
        }
    }
    private async Task CancelOwnedJobAsync(string jobId, AgentExecutionContext context)
    {
        if (!_registry.Snapshot.Tools.TryGetValue("process.cancel", out var cancel)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await cancel.ExecuteAsync(WireJson.Element(new { jobId }), context with { FullPermission = false }, timeout.Token); }
        catch (Exception ex) { Emit("task", "Owned-job cleanup: " + ex.GetType().Name); }
    }
    private void DispatchTask(WireSocket wire, WireMessage message, CancellationToken sessionToken)
    {
        if (string.IsNullOrEmpty(message.Id) || message.Id.Length > 100 || message.TaskRequest is null ||
            message.TaskOperation is null || !RemoteTaskRules.Operations.Contains(message.TaskOperation))
            throw new InvalidDataException("Invalid task envelope.");
        if (_running.ContainsKey(message.Id)) return;
        if (_running.Count >= 16)
        {
            Track(message.Id + "-busy", wire.SendAsync(new("task.result") { Id = message.Id,
                TaskReply = RemoteTaskReply.Failure("busy", "Agent control queue is full.") }, sessionToken));
            return;
        }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        if (!_running.TryAdd(message.Id, stop)) { stop.Dispose(); return; }
        Track(message.Id, HandleTaskAsync(wire, message, stop, sessionToken));
    }
    private async Task HandleTaskAsync(WireSocket wire, WireMessage message, CancellationTokenSource stop, CancellationToken sessionToken)
    {
        try
        {
            var remaining = (message.DeadlineUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(30)) throw new ArgumentException("Invalid task RPC deadline.");
            stop.CancelAfter(remaining);
            stop.Token.ThrowIfCancellationRequested();
            var reply = await _remoteTasks!.HandleAsync(message.TaskOperation!, message.TaskRequest!, sessionToken);
            await wire.SendAsync(new("task.result") { Id = message.Id, TaskReply = reply }, stop.Token);
        }
        catch (Exception ex)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await wire.SendAsync(new("task.result") { Id = message.Id,
                TaskReply = RemoteTaskReply.Failure("unavailable", "Task request failed: " + ex.GetType().Name) }, timeout.Token); }
            catch (Exception sendError) when (sendError is WebSocketException or IOException or OperationCanceledException) { }
        }
        finally { _running.TryRemove(message.Id!, out _); stop.Dispose(); }
    }
    private static async Task<WebSocket> ConnectSocketAsync(Uri endpoint, string token, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        try { await socket.ConnectAsync(endpoint, ct); return socket; }
        catch { socket.Dispose(); throw; }
    }
    private static Task ReplyAsync(WireSocket wire, string id, ToolReply reply, CancellationToken token) =>
        wire.SendAsync(new WireMessage("result") { Id = id, Result = reply }, token);
    private void Emit(string kind, string message) => Activity?.Invoke(new(DateTimeOffset.Now, kind, message));
    public async ValueTask DisposeAsync()
    {
        _permissions.PermissionsRevoked -= CancelInFlight;
        _registry.Changed -= OnRegistryChanged;
        _gate.Changed -= OnGateChanged;
        Disconnect();
        try { await Task.WhenAll(_tasks.Values).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        if (_remoteTasks is not null) await _remoteTasks.DisposeAsync();
        _lifetime.Dispose();
    }
}
