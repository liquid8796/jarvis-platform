using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

internal sealed partial class RemoteTaskHost
{
    private Func<RemoteTaskRequest, AgentExecutionContext?>? _resolveSession;
    private readonly FairExecutionScheduler _taskSlots = new(5, 100, TimeSpan.FromSeconds(60));
    private AgentExecutionSettings _settings = new();

    public void ConfigureExecutionSettings(AgentExecutionSettings settings)
    {
        settings.Validate();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _taskSlots.Configure(settings.MaxDurableTasks, settings.MaxQueuedCalls, TimeSpan.FromSeconds(settings.QueueTimeoutSeconds));
            _settings = settings;
        }
    }

    private AgentExecutionContext? ResolveCaller(RemoteTaskRequest request)
    {
        if (request.SessionId is null) return null; // Authenticated owner dashboard / negotiated legacy API, not scoped MCP.
        if (!AgentSessionRules.IsSessionId(request.SessionId)) throw new AgentRequestException("SESSION_REQUIRED", "A valid application session is required.");
        var context = _resolveSession?.Invoke(request);
        if (context is not null && (context.OwnerId != request.OwnerId || context.SessionId != request.SessionId))
            throw new UnauthorizedAccessException("Task session identity does not match the authenticated request.");
        return context;
    }

    private static bool CanAccess(StoredRemoteTask task, RemoteTaskRequest request) =>
        request.SessionId is null || StringComparer.Ordinal.Equals(task.OwnerSessionId, request.SessionId);

    private AgentExecutionContext StoredContext(StoredRemoteTask task, CancellationToken sessionCancellation = default) =>
        new(task.Snapshot.Project, "task:" + task.Snapshot.TaskId,
            task.OwnerSessionId ?? "task:" + task.OwnerId + ":" + task.Snapshot.TaskId)
        {
            OwnerId = task.OwnerSessionId is null ? null : task.OwnerId,
            AgentDeviceId = task.AgentDeviceId,
            WorkspaceRevision = task.WorkspaceRevision,
            AdditionalDirectories = task.WorkspaceDirectories ?? _folders.Additional,
            TaskAllowedTools = task.AllowedToolIds,
            SessionCancellation = sessionCancellation
        };

    private string ResolveProject(string? project, AgentExecutionContext? session)
    {
        if (session is null) return ResolveProject(project);
        if (string.IsNullOrWhiteSpace(project)) return session.Workspace;
        if (Path.IsPathFullyQualified(project)) return WorkspaceDirectories.Normalize(project);
        var folders = new WorkspaceDirectories(session.Workspace, session.AdditionalDirectories);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = folders.Directories.Where(path => Path.GetFileName(path).Equals(project, comparison)).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new ArgumentException("project must be an absolute directory on this agent or an unambiguous session workspace folder name.");
    }

    public SessionScheduleCounts SessionCounts(AgentSessionIdentity identity)
    {
        lock (_sync) return _taskSlots.GetSessionCounts(identity.OwnerId + "|" + identity.SessionId);
    }

    public void CancelSession(AgentSessionIdentity identity)
    {
        lock (_sync)
        {
            if (_disposed) return;
            foreach (var stored in _store.ForSession(identity.OwnerId, identity.SessionId))
            {
                if (stored.AgentDeviceId is not null && stored.AgentDeviceId != identity.DeviceId) continue;
                if (RemoteTaskRules.IsTerminal(stored.Snapshot.Status)) continue;
                if (_active.TryGetValue(Key(stored.OwnerId, stored.Snapshot.TaskId), out var active))
                {
                    active.Reason = "CANCELLED";
                    _store.Save(stored with { Snapshot = stored.Snapshot with { Status = "CANCELLING", UpdatedAt = DateTimeOffset.UtcNow } });
                    active.Stop.Cancel();
                }
                else _store.Save(stored with { Snapshot = stored.Snapshot with
                {
                    Status = "CANCELLED", UpdatedAt = DateTimeOffset.UtcNow,
                    Error = "Owning session stopped or closed. Other sessions were not affected."
                } });
            }
        }
    }
}
