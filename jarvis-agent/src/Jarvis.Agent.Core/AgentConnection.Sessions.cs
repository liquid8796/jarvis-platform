using System.Collections.Concurrent;
using Jarvis.Agent.Core.Auditing;
using Jarvis.Agent.Core.Sessions;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core;

public sealed partial class AgentConnection
{
    private AgentSessionStore? _sessions;
    private bool _ownsSessions;
    private string _deviceId = string.Empty;
    private WorkspaceDirectories _defaultFolders = new("");
    private bool _sessionProtocol;
    private bool _sessionDeletionProtocol;
    private string? _authenticatedOwnerId;
    private int _sessionDeletionSyncRequested;
    private int _sessionDeletionSyncRunning;
    private readonly ConcurrentDictionary<string, AgentExecutionContext> _runningContexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AgentSessionIdentity, CancellationTokenSource> _sessionWorkStops = new();
    private readonly ConcurrentBag<CancellationTokenSource> _retiredSessionStops = [];

    public AgentSessionStore Sessions => _sessions ?? throw new InvalidOperationException("Session store is not initialized until the agent connects.");
    public event Action<AgentSessionIdentity>? SessionStopped;
    public event Action? SessionActivityChanged;

    private SessionToolServices SessionServices() => new(() => Sessions, () => _defaultFolders,
        context => CancelSessionWork(context.RequireSessionIdentity(), context.CallId), identity => GetSessionActivity(identity));

    public Func<AgentSessionIdentity, int>? SessionProcessCountProvider { get; set; }

    public AgentSessionActivity GetSessionActivity(AgentSessionIdentity identity)
    {
        var key = identity.OwnerId + "|" + identity.DeviceId + "|" + identity.SessionId;
        var calls = _execution.GetSessionCounts(key);
        var resources = _resources.GetSessionState(key);
        var tasks = _remoteTasks?.SessionCounts(identity);
        return new()
        {
            RunningCalls = calls.Running,
            QueuedCalls = calls.Queued + resources.WaitingCalls,
            RunningJobs = SessionProcessCountProvider?.Invoke(identity) ?? 0,
            RunningTasks = tasks?.Running ?? 0, QueuedTasks = tasks?.Queued ?? 0,
            HeldResources = resources.Held, WaitingResources = resources.WaitingFor
        };
    }

    public IReadOnlyList<LocalSessionOverview> GetLocalSessionOverview() => _sessions is null ? [] :
        _sessions.ListLocal(_deviceId).Select(entry => new LocalSessionOverview(entry.Identity, entry.Snapshot,
            GetSessionActivity(entry.Identity))).ToArray();

    private void InitializeSessions(AgentOptions options, string taskRoot)
    {
        _deviceId = options.DeviceId;
        _defaultFolders = new WorkspaceDirectories(options.Workspace, options.AdditionalDirectories);
        if (_sessions is null)
        {
            _sessions = new AgentSessionStore(Path.Combine(taskRoot, "session-runtime.db"));
            _ownsSessions = true;
        }
    }

    private AgentExecutionContext PrepareContext(WireMessage call, WorkspaceDirectories legacyWorkspace)
    {
        var id = call.SessionId ?? "remote";
        if (!AgentSessionRules.IsSessionId(id))
        {
            if (AgentSessionRules.IsTool(call.ToolId ?? ""))
                throw new AgentRequestException("SESSION_REQUIRED", "Open a new session with session__open before using session or workspace management tools.");
            if (_sessionProtocol)
            {
                if (!AgentSessionRules.IsEphemeralExecutionId(id))
                    throw new AgentRequestException("SESSION_INVALID", "Sessionless calls require a gateway-issued ephemeral execution ID.");
                if (string.IsNullOrWhiteSpace(call.OwnerId) || string.IsNullOrWhiteSpace(_deviceId))
                    throw new AgentRequestException("SESSION_INVALID", "Authenticated owner and enrolled agent identity are required.");
                return new("", call.Id!, id)
                {
                    AdditionalDirectories = [], OwnerId = call.OwnerId, AgentDeviceId = _deviceId,
                    ThreadId = call.ThreadId, TurnId = call.TurnId, SessionCancellation = CancellationToken.None
                };
            }
            // Only a legacy peer which did not negotiate application sessions can use the old context.
            return new(legacyWorkspace.Primary, call.Id!, id)
            { AdditionalDirectories = legacyWorkspace.Additional, ThreadId = call.ThreadId, TurnId = call.TurnId };
        }
        var identity = new AgentSessionIdentity(call.OwnerId ?? "", _deviceId, id);
        if (string.IsNullOrWhiteSpace(identity.OwnerId) || string.IsNullOrWhiteSpace(identity.DeviceId))
            throw new AgentRequestException("SESSION_REQUIRED", "Authenticated owner and enrolled agent identity are required.");
        AgentSessionSnapshot? session = null;
        if (call.ToolId != "session.open")
            session = Sessions.Get(identity, allowClosed: call.ToolId == "session.close");
        var context = new AgentExecutionContext(session?.Workspace ?? _defaultFolders.Primary, call.Id!, id)
        {
            AdditionalDirectories = session?.AdditionalDirectories ?? _defaultFolders.Additional,
            OwnerId = identity.OwnerId, AgentDeviceId = identity.DeviceId,
            WorkspaceRevision = session?.WorkspaceRevision ?? 0,
            ThreadId = call.ThreadId, TurnId = call.TurnId,
            SessionCancellation = call.ToolId == "session.close" ? CancellationToken.None : _sessionWorkStops.GetOrAdd(identity, _ => new CancellationTokenSource()).Token
        };
        return context;
    }

    private void ValidateSessionBeforeExecution(string toolId, AgentExecutionContext context)
    {
        if (!AgentSessionRules.IsSessionId(context.SessionId)) return;
        var identity = context.RequireSessionIdentity();
        if (identity.DeviceId != _deviceId) throw new AgentRequestException("SESSION_NOT_FOUND", "Session is not bound to this enrolled agent.");
        if (toolId is not ("session.open" or "session.close"))
        {
            context.SessionCancellation.ThrowIfCancellationRequested();
            Sessions.Get(identity);
            Sessions.Touch(identity);
        }
    }

    /// <summary>Local operator action; never grants authority and never pauses unrelated sessions.</summary>
    public void StopSession(AgentSessionIdentity identity, bool close = false)
    {
        if (identity.DeviceId != _deviceId) throw new AgentRequestException("SESSION_NOT_FOUND", "Select a session on this agent.");
        if (close) Sessions.Close(identity); else Sessions.Get(identity);
        CancelSessionWork(identity, null);
    }

    /// <summary>
    /// Local operator deletion. Metadata/mailbox rows are removed immediately, a permanent local
    /// tombstone prevents handle resurrection, and negotiated servers receive the monotonic revocation.
    /// A failed transport send is safe: every reconnect replays all tombstones for the authenticated owner.
    /// </summary>
    public void DeleteSession(AgentSessionIdentity identity)
    {
        if (identity.DeviceId != _deviceId) throw new AgentRequestException("SESSION_NOT_FOUND", "Select a session on this agent.");
        AgentSessionSnapshot session;
        try { session = Sessions.Get(identity, allowClosed: true); }
        catch (AgentRequestException ex) when (ex.Code == "SESSION_DELETED")
        {
            QueueSessionDeletionSync();
            return;
        }
        if (session.ClosedAt is null) Sessions.Close(identity);
        CancelSessionWork(identity, null);
        Sessions.Delete(identity);
        _audit.Write("SESSION_DELETED", "session",
            "Session metadata was deleted and its server revocation was queued.", sessionId: identity.SessionId);
        QueueSessionDeletionSync();
    }

    private void QueueSessionDeletionSync()
    {
        var wire = _current;
        var ownerId = _authenticatedOwnerId;
        if (!_sessionDeletionProtocol || wire is null || wire.State != System.Net.WebSockets.WebSocketState.Open ||
            string.IsNullOrWhiteSpace(ownerId) || _sessions is null) return;
        Interlocked.Exchange(ref _sessionDeletionSyncRequested, 1);
        if (Interlocked.CompareExchange(ref _sessionDeletionSyncRunning, 1, 0) != 0) return;
        Track("session-deletions-" + Guid.NewGuid().ToString("N"), RunSessionDeletionSyncLoopAsync());
    }

    private async Task RunSessionDeletionSyncLoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _sessionDeletionSyncRequested, 0) != 0)
            {
                var wire = _current;
                var ownerId = _authenticatedOwnerId;
                if (!_sessionDeletionProtocol || wire is null || wire.State != System.Net.WebSockets.WebSocketState.Open ||
                    string.IsNullOrWhiteSpace(ownerId) || _sessions is null) return;
                try { await PushSessionDeletionsAsync(wire, ownerId, _lifetime.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or
                    OperationCanceledException or ObjectDisposedException)
                {
                    Emit("session", "Session deletion will synchronize after reconnect: " + ex.GetType().Name);
                    return;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _sessionDeletionSyncRunning, 0);
            if (Volatile.Read(ref _sessionDeletionSyncRequested) != 0) QueueSessionDeletionSync();
        }
    }

    private async Task PushSessionDeletionsAsync(WireSocket wire, string ownerId, CancellationToken ct)
    {
        var offset = 0;
        while (ReferenceEquals(wire, _current) && _sessionDeletionProtocol &&
            StringComparer.Ordinal.Equals(ownerId, _authenticatedOwnerId))
        {
            var page = Sessions.ListDeletions(ownerId, _deviceId, offset, 200);
            if (page.Count == 0) return;
            await wire.SendAsync(new WireMessage("session.deletions.changed")
            {
                SessionDeletions = page
            }, ct).ConfigureAwait(false);
            offset += page.Count;
            if (page.Count < 200) return;
        }
    }

    private AgentExecutionContext? ResolveTaskSession(RemoteTaskRequest request) => request.SessionId is null ? null :
        PrepareContext(new WireMessage("call")
        {
            Id = "task-request:" + request.TaskId, ToolId = "task.request", OwnerId = request.OwnerId, SessionId = request.SessionId
        }, _defaultFolders);

    public void UpdateDefaultWorkspace(string? workspace, IReadOnlyList<string>? additionalDirectories = null) =>
        Volatile.Write(ref _defaultFolders, new WorkspaceDirectories(workspace, additionalDirectories));

    private void CancelSessionWork(AgentSessionIdentity identity, string? exceptCallId)
    {
        if (_sessionWorkStops.TryRemove(identity, out var stop))
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            _retiredSessionStops.Add(stop);
        }
        foreach (var pair in _runningContexts)
            if (pair.Key != exceptCallId && pair.Value.SessionId == identity.SessionId && pair.Value.OwnerId == identity.OwnerId &&
                pair.Value.AgentDeviceId == identity.DeviceId && _running.TryGetValue(pair.Key, out var call))
                try { call.Cancel(); } catch (ObjectDisposedException) { }
        _remoteTasks?.CancelSession(identity);
        _toolRepl.Reset(identity);
        _collaborationWorkers.Reset(identity);
        foreach (var handler in SessionStopped?.GetInvocationList() ?? [])
            try { ((Action<AgentSessionIdentity>)handler)(identity); } catch (Exception ex) { Emit("session", "Owned session cleanup: " + ex.GetType().Name); }
        NotifySessionActivity();
    }

    private void NotifySessionActivity()
    {
        foreach (var handler in SessionActivityChanged?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception) { }
    }

    private void DisposeSessions()
    {
        foreach (var stop in _sessionWorkStops.Values) { stop.Cancel(); stop.Dispose(); }
        foreach (var stop in _retiredSessionStops) stop.Dispose();
        _sessionWorkStops.Clear();
        if (_ownsSessions) _sessions?.Dispose();
    }
}
