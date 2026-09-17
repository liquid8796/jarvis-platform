using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

/// <summary>Client-planned task runner. Only installed, locally authorized tools can perform actions.</summary>
internal sealed partial class RemoteTaskHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly RemoteTaskStore _store;
    private readonly WorkspaceDirectories _folders;
    private readonly DynamicToolRegistry _registry;
    private readonly Func<bool> _armed;
    private readonly Func<string, JsonElement, AgentExecutionContext, CancellationToken, Task<ToolReply>> _invoke;
    private readonly Func<string, AgentExecutionContext, Task> _cancelJob;
    private readonly IRemoteTaskAdaptiveCoordinator? _adaptive;
    private readonly IRemoteTaskAgenticCoordinator? _agentic;
    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _storageFaults = new(StringComparer.Ordinal);
    private bool _disposed;

    public RemoteTaskHost(string root, WorkspaceDirectories folders, DynamicToolRegistry registry,
        Func<bool> armed, Func<string, JsonElement, AgentExecutionContext, CancellationToken, Task<ToolReply>> invoke,
        Func<string, AgentExecutionContext, Task> cancelJob, IRemoteTaskAdaptiveCoordinator? adaptive = null, Func<RemoteTaskRequest, AgentExecutionContext?>? resolveSession = null, AgentExecutionSettings? settings = null)
    { _store = new(root); _folders = folders; _registry = registry; _armed = armed; _invoke = invoke; _cancelJob = cancelJob; _adaptive = adaptive; _agentic = adaptive as IRemoteTaskAgenticCoordinator; _resolveSession = resolveSession; if (settings is not null) ConfigureExecutionSettings(settings); }

    public Task<RemoteTaskReply> HandleAsync(string operation, RemoteTaskRequest request, CancellationToken sessionToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerId) || request.OwnerId.Length > 256)
                throw new ArgumentException("Invalid owner identity.");
            var id = RemoteTaskRules.TaskId(request.TaskId);
            var caller = ResolveCaller(request);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_storageFaults.Contains(Key(request.OwnerId, id)))
                    return Task.FromResult(RemoteTaskReply.Failure("unavailable", "Task persistence failed. Inspect local storage before submitting new work."));
                var stored = _store.Load(request.OwnerId, id);
                if (stored is not null && !CanAccess(stored, request))
                    return Task.FromResult(RemoteTaskReply.Failure("not_found", "No task owned by this session on this agent."));
                if (operation == "create")
                {
                    if (request.Plan is null) throw new ArgumentException("Task plan metadata is required.");
                    RemoteTaskRules.Validate(request.Plan);
                    var planDigest = RemoteTaskStore.Digest(request.Plan);
                    var createDigest = RemoteTaskStore.CreateDigest(request.Plan, request.ParentTaskId);
                    if (stored is not null)
                        return Task.FromResult(stored.CreateDigest == createDigest ? new RemoteTaskReply(Task: stored.Snapshot)
                            : RemoteTaskReply.Failure("conflict", "taskId already belongs to a different request."));
                    RequireArmed();
                    var project = ResolveProject(request.Plan.Project, caller);
                    RemoteTaskLineage lineage;
                    if (string.IsNullOrWhiteSpace(request.ParentTaskId))
                    {
                        lineage = RemoteTaskDelegation.Root(id);
                    }
                    else
                    {
                        var parentId = RemoteTaskRules.TaskId(request.ParentTaskId);
                        if (StringComparer.Ordinal.Equals(parentId, id)) throw new ArgumentException("A task cannot be its own parent.");
                        var parent = _store.Load(request.OwnerId, parentId) ?? throw new ArgumentException("Parent task was not found for this owner on this device.");
                        if (!CanAccess(parent, request)) throw new UnauthorizedAccessException("Parent task is not owned by this session.");
                        if (_store.CountChildren(request.OwnerId, parentId) >= RemoteTaskDelegation.MaxChildren)
                            return Task.FromResult(RemoteTaskReply.Failure("busy", $"Parent task already has the maximum {RemoteTaskDelegation.MaxChildren} children."));
                        lineage = RemoteTaskDelegation.Child(parent, request.Plan, project);
                    }
                    ValidateTools(request.Plan);
                    var agentPlansGoal = request.Plan.Steps.Count == 0 && request.Plan.ExecutionMode == "AUTONOMOUS" && _agentic is not null;
                    if (_store.AtCapacity) return Task.FromResult(RemoteTaskReply.Failure("busy", "Local task history is full (128). Archive terminal task files locally before creating more."));
                    if ((request.Plan.Steps.Count > 0 || agentPlansGoal) && _active.Count >= (long)_settings.MaxDurableTasks + _settings.MaxQueuedCalls) return Busy();
                    var now = DateTimeOffset.UtcNow;
                    var snapshot = new RemoteTaskSnapshot(id, request.Plan.Goal, project,
                        request.Plan.Steps.Count == 0 && !agentPlansGoal ? "NEEDS_PLAN" : "QUEUED", null, 0, request.Plan.Steps.Count, now, now,
                        ParentTaskId: lineage.ParentTaskId, RootTaskId: lineage.RootTaskId, Depth: lineage.Depth);
                    stored = new(1, request.OwnerId, createDigest, request.Plan.Steps.Count == 0 ? null : planDigest,
                        Clone(request.Plan), snapshot, [])
                    {
                        OwnerSessionId = request.SessionId, AgentDeviceId = caller?.AgentDeviceId,
                        WorkspaceRevision = caller?.WorkspaceRevision,
                        WorkspaceDirectories = (caller?.AdditionalDirectories ?? _folders.Additional).ToArray()
                    };
                    _store.Save(stored); // Acknowledge only after durable storage.
                    if (stored.Plan.Steps.Count > 0 || agentPlansGoal) Start(stored, sessionToken, caller?.SessionCancellation ?? default);
                    return Task.FromResult(new RemoteTaskReply(Task: snapshot));
                }
                if (stored is null) return Task.FromResult(RemoteTaskReply.Failure("not_found", "Task not found on this device for this owner."));
                if (operation == "get") return Task.FromResult(new RemoteTaskReply(Task: stored.Snapshot));
                if (operation == "artifacts")
                {
                    if (request.Offset < 0 || request.Limit is < 1 or > 20) throw new ArgumentException("offset must be nonnegative; limit must be 1..20.");
                    var page = stored.Artifacts.Skip(request.Offset).Take(request.Limit).ToArray();
                    int? next = request.Offset < stored.Artifacts.Count - page.Length ? request.Offset + page.Length : null;
                    return Task.FromResult(new RemoteTaskReply(Task: stored.Snapshot, Artifacts: page, NextOffset: next));
                }
                if (operation == "cancel")
                {
                    if (!RemoteTaskRules.IsTerminal(stored.Snapshot.Status))
                    {
                        var key = Key(stored.OwnerId, id);
                        if (_active.TryGetValue(key, out var active))
                        {
                            active.Reason = "CANCELLED";
                            stored = stored with { Snapshot = stored.Snapshot with { Status = "CANCELLING", UpdatedAt = DateTimeOffset.UtcNow } };
                            _store.Save(stored); active.Stop.Cancel();
                        }
                        else
                        {
                            stored = stored with { Snapshot = stored.Snapshot with { Status = "CANCELLED", UpdatedAt = DateTimeOffset.UtcNow } };
                            _store.Save(stored);
                        }
                    }
                    return Task.FromResult(new RemoteTaskReply(Task: stored.Snapshot));
                }
                if (operation == "plan")
                {
                    if (request.Plan is null) throw new ArgumentException("An explicit non-empty tool plan is required.");
                    RemoteTaskRules.Validate(request.Plan);
                    if (request.Plan.Steps.Count == 0) throw new ArgumentException("An explicit non-empty tool plan is required.");
                    var digest = RemoteTaskStore.Digest(request.Plan);
                    if (stored.PlanDigest == digest) return Task.FromResult(new RemoteTaskReply(Task: stored.Snapshot));
                    if (stored.Snapshot.Status != "NEEDS_PLAN")
                        return Task.FromResult(RemoteTaskReply.Failure("conflict", "A running or terminal task cannot be replanned or replayed. Use a new taskId."));
                    if (stored.Plan.Goal != request.Plan.Goal || stored.Plan.ExecutionMode != request.Plan.ExecutionMode ||
                        stored.Plan.TimeoutSeconds != request.Plan.TimeoutSeconds || stored.Snapshot.Project != ResolveProject(request.Plan.Project, stored.OwnerSessionId is null ? null : StoredContext(stored)))
                        throw new ArgumentException("A submitted plan must retain the task's goal, project, mode and timeout.");
                    RequireArmed(); ValidateTools(request.Plan);
                    if (_active.Count >= (long)_settings.MaxDurableTasks + _settings.MaxQueuedCalls) return Busy();
                    stored = stored with { Plan = Clone(request.Plan), PlanDigest = digest,
                        Snapshot = stored.Snapshot with { Status = "QUEUED", TotalSteps = request.Plan.Steps.Count, UpdatedAt = DateTimeOffset.UtcNow } };
                    _store.Save(stored); Start(stored, sessionToken, caller?.SessionCancellation ?? default);
                    return Task.FromResult(new RemoteTaskReply(Task: stored.Snapshot));
                }
                throw new ArgumentException("Unsupported task operation.");
            }
        }
        catch (AgentRequestException ex) { return Task.FromResult(RemoteTaskReply.Failure(ex.Code, ex.Message)); }
        catch (UnauthorizedAccessException ex) { return Task.FromResult(RemoteTaskReply.Failure("forbidden", ex.Message)); }
        catch (ArgumentException ex) { return Task.FromResult(RemoteTaskReply.Failure("invalid", ex.Message)); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        { return Task.FromResult(RemoteTaskReply.Failure("unavailable", "Task storage/execution is unavailable: " + ex.GetType().Name)); }
    }

    private static RemoteTaskPlan Clone(RemoteTaskPlan plan) =>
        JsonSerializer.Deserialize<RemoteTaskPlan>(JsonSerializer.SerializeToUtf8Bytes(plan, WireJson.Options), WireJson.Options)!;
    private static string Key(string owner, string id) => owner + ":" + id;
    private static Task<RemoteTaskReply> Busy() => Task.FromResult(RemoteTaskReply.Failure("busy", "TASK_QUEUE_FULL: The bounded durable task queue is full. No task was started."));
    private void RequireArmed()
    { if (!_armed()) throw new UnauthorizedAccessException("Local control is paused. Arm it locally before submitting work."); }
    private string ResolveProject(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return _folders.Primary;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var found = _folders.Directories.Where(d => comparer.Equals(d, project) || comparer.Equals(Path.GetFileName(d), project)).ToArray();
        return found.Length == 1 ? found[0] : throw new ArgumentException("project must identify exactly one selected project folder by full path or directory name.");
    }
    private void ValidateTools(RemoteTaskPlan plan)
    {
        var snapshot = _registry.Snapshot;
        foreach (var step in plan.Steps)
        {
            if (!snapshot.Tools.TryGetValue(step.ToolId, out var tool)) throw new ArgumentException("Tool is not installed: " + step.ToolId);
            if (!SchemaGuard.Matches(snapshot.Schemas[step.ToolId], step.Arguments))
                throw new ArgumentException("Step arguments do not match installed schema: " + step.Id);
            if ((plan.ExecutionMode == "READ_ONLY" || step.MaxAttempts > 1) && (!tool.Descriptor.ReadOnly || tool.Descriptor.Sensitive))
                throw new ArgumentException("Read-only mode and retries cannot invoke mutating or sensitive tools: " + step.Id);
            if ((step.ToolId is "process.start" or "process.spawn") && (!snapshot.Tools.ContainsKey("process.read") || !snapshot.Tools.ContainsKey("process.cancel")))
                throw new ArgumentException("Owned process read/cancel tools are required.");
        }
    }
    private void Start(StoredRemoteTask task, CancellationToken sessionToken, CancellationToken ownerSessionToken)
    {
        var active = new Active(CancellationTokenSource.CreateLinkedTokenSource(sessionToken, ownerSessionToken), sessionToken, StoredContext(task, ownerSessionToken));
        active.Stop.CancelAfter(TimeSpan.FromSeconds(task.Plan.TimeoutSeconds));
        var key = Key(task.OwnerId, task.Snapshot.TaskId);
        _active.Add(key, active);
        active.Work = Task.Run(() => RunAsync(task, active));
        _ = active.Work.ContinueWith(work => { _ = work.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private async Task RunAsync(StoredRemoteTask initial, Active active)
    {
        var task = initial;
        try
        {
            using var slot = await _taskSlots.AcquireAsync(task.OwnerId + "|" + (task.OwnerSessionId ?? task.Snapshot.TaskId), active.Stop.Token);
            task = await EnsureAgenticPlanAsync(task, active).ConfigureAwait(false);
            if (task.Plan.Steps.Count == 0)
            {
                Save(task with { Snapshot = task.Snapshot with { Status = "FAILED", Error = "Task has no executable plan.", UpdatedAt = DateTimeOffset.UtcNow } });
                return;
            }
            var execution = await ExecuteStepsAsync(task, active, task.Plan.Steps).ConfigureAwait(false);
            task = execution.Task;
            if (!execution.Success) return;
            var goal = await VerifyAgenticGoalAsync(task, active).ConfigureAwait(false);
            task = goal.Task;
            if (!goal.Success) return;
            lock (_sync)
            {
                active.Stop.Token.ThrowIfCancellationRequested();
                Save(task with { Snapshot = task.Snapshot with { Status = "COMPLETED", CurrentStep = null, UpdatedAt = DateTimeOffset.UtcNow } });
            }
        }
        catch (OperationCanceledException)
        {
            var status = active.Reason ?? (active.SessionToken.IsCancellationRequested ? "INTERRUPTED" : "FAILED");
            Save(task with { Snapshot = task.Snapshot with { Status = status, UpdatedAt = DateTimeOffset.UtcNow,
                Error = status switch
                {
                    "CANCELLED" => "Task cancelled by user or local control policy.",
                    "FAILED" => "Task deadline exceeded. Inspect partial output before submitting new work.",
                    _ => "Task interrupted. Inspect outputs before submitting new work; no actions were replayed."
                } } });
        }
        catch (Exception ex)
        {
            try { Save(task with { Snapshot = task.Snapshot with { Status = "FAILED", UpdatedAt = DateTimeOffset.UtcNow,
                Error = ex is AgentRequestException ? ex.Message : "Task failed: " + ex.GetType().Name } }); } catch (IOException) { /* Corrupt/unwritable state fails closed on the next query. */ }
        }
        finally
        {
            lock (_sync) { _active.Remove(Key(initial.OwnerId, initial.Snapshot.TaskId)); active.Stop.Dispose(); }
        }
    }
    private StoredRemoteTask Save(StoredRemoteTask task)
    {
        lock (_sync)
        {
            var key = Key(task.OwnerId, task.Snapshot.TaskId);
            try
            {
                var current = _store.Load(task.OwnerId, task.Snapshot.TaskId);
                if (current?.Snapshot.Status == "CANCELLING" && !RemoteTaskRules.IsTerminal(task.Snapshot.Status))
                    task = task with { Snapshot = task.Snapshot with { Status = "CANCELLING" } };
                _store.Save(task); _storageFaults.Remove(key); return task;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { _storageFaults.Add(key); throw; }
        }
    }
    public void CancelAll(string reason = "INTERRUPTED")
    {
        lock (_sync) foreach (var active in _active.Values) { active.Reason ??= reason; active.Stop.Cancel(); }
    }
    public async ValueTask DisposeAsync()
    {
        Task all;
        lock (_sync) { _disposed = true; CancelAll(); all = Task.WhenAll(_active.Values.Select(a => a.Work)); }
        try { await all.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { _ = all.ContinueWith(_ => _store.Dispose(), TaskScheduler.Default); return; }
        finally { _taskSlots.Dispose(); if (all.IsCompleted) _store.Dispose(); }
    }
    private sealed class Active(CancellationTokenSource stop, CancellationToken sessionToken, AgentExecutionContext context)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public CancellationToken SessionToken { get; } = sessionToken;
        public AgentExecutionContext Context { get; } = context;
        public string? Reason { get; set; }
        public Task Work { get; set; } = Task.CompletedTask;
    }
}
