using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

/// <summary>Client-planned task runner. Only installed, locally authorized tools can perform actions.</summary>
internal sealed class RemoteTaskHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly RemoteTaskStore _store;
    private readonly WorkspaceDirectories _folders;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly Func<bool> _armed;
    private readonly Func<string, JsonElement, AgentExecutionContext, CancellationToken, Task<ToolReply>> _invoke;
    private readonly Func<string, AgentExecutionContext, Task> _cancelJob;
    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _storageFaults = new(StringComparer.Ordinal);
    private bool _disposed;

    public RemoteTaskHost(string root, WorkspaceDirectories folders, IReadOnlyDictionary<string, IAgentTool> tools,
        Func<bool> armed, Func<string, JsonElement, AgentExecutionContext, CancellationToken, Task<ToolReply>> invoke,
        Func<string, AgentExecutionContext, Task> cancelJob)
    { _store = new(root); _folders = folders; _tools = tools; _armed = armed; _invoke = invoke; _cancelJob = cancelJob; }

    public Task<RemoteTaskReply> HandleAsync(string operation, RemoteTaskRequest request, CancellationToken sessionToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerId) || request.OwnerId.Length > 256)
                throw new ArgumentException("Invalid owner identity.");
            var id = RemoteTaskRules.TaskId(request.TaskId);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_storageFaults.Contains(Key(request.OwnerId, id)))
                    return Task.FromResult(RemoteTaskReply.Failure("unavailable", "Task persistence failed. Inspect local storage before submitting new work."));
                var stored = _store.Load(request.OwnerId, id);
                if (operation == "create")
                {
                    if (request.Plan is null) throw new ArgumentException("Task plan metadata is required.");
                    RemoteTaskRules.Validate(request.Plan);
                    var digest = RemoteTaskStore.Digest(request.Plan);
                    if (stored is not null)
                        return Task.FromResult(stored.CreateDigest == digest ? new RemoteTaskReply(Task: stored.Snapshot)
                            : RemoteTaskReply.Failure("conflict", "taskId already belongs to a different request."));
                    RequireArmed();
                    var project = ResolveProject(request.Plan.Project);
                    ValidateTools(request.Plan);
                    if (_store.AtCapacity) return Task.FromResult(RemoteTaskReply.Failure("busy", "Local task history is full (128). Archive terminal task files locally before creating more."));
                    if (request.Plan.Steps.Count > 0 && _active.Count >= 2) return Busy();
                    var now = DateTimeOffset.UtcNow;
                    var snapshot = new RemoteTaskSnapshot(id, request.Plan.Goal, project,
                        request.Plan.Steps.Count == 0 ? "NEEDS_PLAN" : "QUEUED", null, 0, request.Plan.Steps.Count, now, now);
                    stored = new(1, request.OwnerId, digest, request.Plan.Steps.Count == 0 ? null : digest,
                        Clone(request.Plan), snapshot, []);
                    _store.Save(stored); // Acknowledge only after durable storage.
                    if (stored.Plan.Steps.Count > 0) Start(stored, sessionToken);
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
                        stored.Plan.TimeoutSeconds != request.Plan.TimeoutSeconds || stored.Snapshot.Project != ResolveProject(request.Plan.Project))
                        throw new ArgumentException("A submitted plan must retain the task's goal, project, mode and timeout.");
                    RequireArmed(); ValidateTools(request.Plan);
                    if (_active.Count >= 2) return Busy();
                    stored = stored with { Plan = Clone(request.Plan), PlanDigest = digest,
                        Snapshot = stored.Snapshot with { Status = "QUEUED", TotalSteps = request.Plan.Steps.Count, UpdatedAt = DateTimeOffset.UtcNow } };
                    _store.Save(stored); Start(stored, sessionToken);
                    return Task.FromResult(new RemoteTaskReply(Task: stored.Snapshot));
                }
                throw new ArgumentException("Unsupported task operation.");
            }
        }
        catch (UnauthorizedAccessException ex) { return Task.FromResult(RemoteTaskReply.Failure("forbidden", ex.Message)); }
        catch (ArgumentException ex) { return Task.FromResult(RemoteTaskReply.Failure("invalid", ex.Message)); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        { return Task.FromResult(RemoteTaskReply.Failure("unavailable", "Task storage/execution is unavailable: " + ex.GetType().Name)); }
    }

    private static RemoteTaskPlan Clone(RemoteTaskPlan plan) =>
        JsonSerializer.Deserialize<RemoteTaskPlan>(JsonSerializer.SerializeToUtf8Bytes(plan, WireJson.Options), WireJson.Options)!;
    private static string Key(string owner, string id) => owner + ":" + id;
    private static Task<RemoteTaskReply> Busy() => Task.FromResult(RemoteTaskReply.Failure("busy", "Two tasks are already running. No task was started."));
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
        foreach (var step in plan.Steps)
        {
            if (!_tools.TryGetValue(step.ToolId, out var tool)) throw new ArgumentException("Tool is not installed: " + step.ToolId);
            if (!SchemaGuard.Matches(SchemaGuard.Compile(tool.Descriptor.InputSchema), step.Arguments))
                throw new ArgumentException("Step arguments do not match installed schema: " + step.Id);
            if ((plan.ExecutionMode == "READ_ONLY" || step.MaxAttempts > 1) && (!tool.Descriptor.ReadOnly || tool.Descriptor.Sensitive))
                throw new ArgumentException("Read-only mode and retries cannot invoke mutating or sensitive tools: " + step.Id);
            if (step.ToolId == "process.start" && (!_tools.ContainsKey("process.read") || !_tools.ContainsKey("process.cancel")))
                throw new ArgumentException("Owned process read/cancel tools are required.");
        }
    }
    private void Start(StoredRemoteTask task, CancellationToken sessionToken)
    {
        var active = new Active(CancellationTokenSource.CreateLinkedTokenSource(sessionToken), sessionToken);
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
            foreach (var step in task.Plan.Steps)
            {
                active.Stop.Token.ThrowIfCancellationRequested();
                RequireArmed();
                task = Save(task with { Snapshot = task.Snapshot with { Status = "RUNNING", CurrentStep = step.Id, UpdatedAt = DateTimeOffset.UtcNow } });
                for (var attempt = 1; attempt <= step.MaxAttempts; attempt++)
                {
                    using var stepStop = CancellationTokenSource.CreateLinkedTokenSource(active.Stop.Token);
                    stepStop.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
                    var context = new AgentExecutionContext(task.Snapshot.Project,
                        task.Snapshot.TaskId + ":" + step.Id + ":" + attempt, "task:" + task.OwnerId + ":" + task.Snapshot.TaskId)
                    { AdditionalDirectories = _folders.Additional };
                    RemoteStepResult result;
                    try
                    {
                        if (step.ToolId == "process.start")
                            result = await RemoteProcessRunner.RunAsync(step, context, _invoke, _cancelJob, stepStop.Token);
                        else
                        {
                            var reply = await _invoke(step.ToolId, step.Arguments, context, stepStop.Token);
                            result = new(!reply.IsError, reply.Text, Error: reply.IsError ? reply.Text : null);
                        }
                        if (result.Output.Length > RemoteTaskRules.OutputLimit)
                            result = result with { Output = result.Output[^RemoteTaskRules.OutputLimit..], Truncated = true };
                        if (stepStop.IsCancellationRequested)
                            result = result with { Success = false, Error = result.Error ?? "Step cancelled or deadline exceeded; partial output retained." };
                        if (result.Success && !string.IsNullOrEmpty(step.ExpectedText) && !result.Output.Contains(step.ExpectedText, StringComparison.Ordinal))
                            result = result with { Success = false, Error = "Expected text was not found in the bounded step output." };
                    }
                    catch (Exception ex)
                    {
                        result = new(false, "", Error: ex is OperationCanceledException
                            ? "Step cancelled or deadline exceeded; mutating actions were not replayed."
                            : ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException ? ex.Message : "Step failed: " + ex.GetType().Name);
                    }
                    var output = result.Output ?? "";
                    var artifact = new RemoteTaskArtifact(task.Artifacts.Count, step.Id, step.Stage, step.ToolId, attempt,
                        result.Success, output.Length <= RemoteTaskRules.OutputLimit ? output : output[^RemoteTaskRules.OutputLimit..],
                        result.Truncated || output.Length > RemoteTaskRules.OutputLimit, result.ExitCode, DateTimeOffset.UtcNow,
                        result.Error is { Length: > 2000 } e ? e[..2000] : result.Error);
                    task = Save(task with { Artifacts = task.Artifacts.Append(artifact).ToArray() });
                    active.Stop.Token.ThrowIfCancellationRequested();
                    if (result.Success)
                    {
                        task = Save(task with { Snapshot = task.Snapshot with { CompletedSteps = task.Snapshot.CompletedSteps + 1, UpdatedAt = DateTimeOffset.UtcNow } });
                        break;
                    }
                    if (stepStop.IsCancellationRequested || attempt == step.MaxAttempts)
                    {
                        Save(task with { Snapshot = task.Snapshot with { Status = "FAILED", Error = artifact.Error, UpdatedAt = DateTimeOffset.UtcNow } });
                        return;
                    }
                    await Task.Delay(200 * attempt, active.Stop.Token);
                }
            }
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
                Error = "Task failed: " + ex.GetType().Name } }); } catch (IOException) { /* Corrupt/unwritable state fails closed on the next query. */ }
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
        finally { if (all.IsCompleted) _store.Dispose(); }
    }
    private sealed class Active(CancellationTokenSource stop, CancellationToken sessionToken)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public CancellationToken SessionToken { get; } = sessionToken;
        public string? Reason { get; set; }
        public Task Work { get; set; } = Task.CompletedTask;
    }
}
