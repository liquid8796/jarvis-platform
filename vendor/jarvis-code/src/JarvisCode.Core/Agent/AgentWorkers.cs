using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Agent;

public enum WorkerStatus
{
    Running,
    Completed,
    Failed,
    Killed,
}

/// <summary>
/// A worker as the previous process left it. In-process state does not survive
/// a restart — the reference says so too — so these are reported, never resumed.
/// </summary>
public sealed record PreviousWorker(string Id, string? Name, string AgentType, string PromptPreview, bool WasRunning);

public sealed record WorkerInfo(
    string Id,
    string AgentType,
    string PromptPreview,
    WorkerStatus Status,
    string? ResultText,
    string? ToolUseId = null,
    string? Model = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    long TotalTokens = 0,
    int ToolUses = 0,
    string? LastToolName = null,
    /// <summary>Teammate name when the agent was spawned with one; null otherwise.</summary>
    string? Name = null);

/// <summary>
/// Live counters for one background worker, fed from its own agent events so a
/// host can show what the run has spent. Written from the worker thread and read
/// from the UI thread, so every read and write is taken under the same lock.
/// </summary>
public sealed class WorkerProgress
{
    private readonly object _gate = new();
    private long _committedTokens;
    private long _currentRunTokens;
    private int _toolUses;
    private string? _lastToolName;

    /// <summary>Tokens across every run of this worker, follow-ups included.</summary>
    public long TotalTokens
    {
        get
        {
            lock (_gate)
            {
                return _committedTokens + _currentRunTokens;
            }
        }
    }

    public int ToolUses
    {
        get
        {
            lock (_gate)
            {
                return _toolUses;
            }
        }
    }

    public string? LastToolName
    {
        get
        {
            lock (_gate)
            {
                return _lastToolName;
            }
        }
    }

    /// <summary>
    /// A run of this worker is starting — the first one, or a SendMessage
    /// follow-up. <see cref="UsageReported"/> restarts its total each turn, so
    /// the finished run's tokens are banked before the next one overwrites them.
    /// </summary>
    public void BeginRun()
    {
        lock (_gate)
        {
            _committedTokens += _currentRunTokens;
            _currentRunTokens = 0;
        }
    }

    /// <summary>Folds one of the worker's agent events into the counters.</summary>
    public void Observe(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case UsageReported usage:
                lock (_gate)
                {
                    _currentRunTokens = usage.Total.InputTokens + usage.Total.OutputTokens;
                }

                break;
            case ToolExecutionStarted started:
                lock (_gate)
                {
                    _toolUses++;
                    _lastToolName = started.ToolName;
                }

                break;
        }
    }
}

/// <summary>
/// Background subagents ("workers", the reference app's Dispatch mechanics):
/// each runs its own agent loop off the turn, keeps its conversation for
/// follow-ups via SendMessage, and reports completion through
/// <see cref="WorkerFinished"/> so the host can deliver a task notification.
/// </summary>
public sealed class AgentWorkerManager
{
    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string AgentType { get; init; }
        public required string PromptPreview { get; init; }
        public required AgentTurnContext Context { get; set; }
        public required Func<AgentTurnContext, CancellationToken, Task<ToolResult>> Runner { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public WorkerStatus Status { get; set; } = WorkerStatus.Running;
        public ToolResult? Result { get; set; }
        public string? ToolUseId { get; init; }
        public string? Name { get; init; }
        public Queue<string> Inbox { get; } = new();
        public string? Model { get; init; }
        public WorkerProgress? Progress { get; init; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? CompletedAt { get; set; }
    }

    private readonly Dictionary<string, Entry> _workers = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly string? _statePath;
    private int _nextId;

    public AgentWorkerManager(string? statePath = null)
    {
        _statePath = statePath;
        PreviousRun = LoadPreviousRun();
    }

    /// <summary>What the previous process was running when it exited.</summary>
    public IReadOnlyList<PreviousWorker> PreviousRun { get; }

    /// <summary>
    /// The reference's report for agents with no completion record, or null when
    /// the previous process left none behind.
    /// </summary>
    public string? PreviousRunNotice()
    {
        var unfinished = PreviousRun.Where(w => w.WasRunning).ToList();
        if (unfinished.Count == 0)
            return null;
        var names = string.Join(", ", unfinished.Select(w => w.Name ?? w.Id));
        return $"No completion record was found for {unfinished.Count} background " +
            $"agent{(unfinished.Count == 1 ? "" : "s")} from the previous session: {names}. They may have been " +
            "stopped, or they may have been running when the previous process exited \u2014 either way their " +
            "reports are gone, so their in-process state was lost. Check each agent's output for partial work " +
            "before assuming the tasks landed.";
    }

    /// <summary>
    /// Stops every running worker, as an interrupt does, and returns the
    /// reference's banner text (null when nothing was running).
    /// </summary>
    public string? KillAll()
    {
        List<string> stopped;
        lock (_lock)
        {
            stopped = [];
            foreach (var entry in _workers.Values.Where(w => w.Status == WorkerStatus.Running))
            {
                entry.Cts.Cancel();
                stopped.Add(entry.Name ?? entry.Id);
            }
        }

        if (stopped.Count == 0)
            return null;
        return stopped.Count == 1
            ? $"Background agent \"{stopped[0]}\" was stopped by the user."
            : $"{stopped.Count} background agents were stopped by the user: {string.Join(", ", stopped)}";
    }

    private IReadOnlyList<PreviousWorker> LoadPreviousRun()
    {
        if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath))
            return [];
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<List<PreviousWorker>>(
                File.ReadAllText(_statePath));
            return stored ?? [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Writes this session's workers so the next process can report them.</summary>
    private void SaveLocked()
    {
        if (string.IsNullOrEmpty(_statePath))
            return;
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var records = _workers.Values
                .Select(w => new PreviousWorker(w.Id, w.Name, w.AgentType, w.PromptPreview,
                    w.Status == WorkerStatus.Running))
                .ToList();
            File.WriteAllText(_statePath, System.Text.Json.JsonSerializer.Serialize(records));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reporting is a courtesy; never fail a run over it.
        }
    }

    /// <summary>
    /// A worker finished (completed, failed or killed). Raised on a worker
    /// thread — hosts must marshal before touching UI or session state.
    /// </summary>
    public event Action<WorkerInfo, ToolResult>? WorkerFinished;
    public event Action<WorkerInfo>? WorkerStarted;

    /// <summary>
    /// Starts a worker. <paramref name="progress"/> and <paramref name="toolUseId"/>
    /// are optional: hosts that render a task list pass a collector the runner
    /// feeds, plus the Agent call the worker belongs to.
    /// </summary>
    public string Start(
        string agentType,
        string promptPreview,
        AgentTurnContext turnContext,
        Func<AgentTurnContext, CancellationToken, Task<ToolResult>> runner,
        WorkerProgress? progress = null,
        string? toolUseId = null,
        string? name = null)
    {
        Entry entry;
        lock (_lock)
        {
            entry = new Entry
            {
                Id = $"agent-{++_nextId}",
                AgentType = agentType,
                PromptPreview = promptPreview,
                Context = turnContext,
                Runner = runner,
                Progress = progress,
                ToolUseId = toolUseId,
                Name = name,
                Model = turnContext.ModelId,
            };
            _workers[entry.Id] = entry;
            SaveLocked();
        }

        // The worker drains its own mailbox between model calls.
        entry.Context = entry.Context with { DrainInbox = () => DrainInbox(entry.Id) };
        try { WorkerStarted?.Invoke(Snapshot(entry)); }
        catch (Exception ex) { Utilities.DiagnosticLog.Write("Worker-start observer failed: " + ex.Message); }
        _ = Task.Run(() => RunAsync(entry));
        return entry.Id;
    }

    /// <summary>
    /// Continues a finished worker with a follow-up message on its intact
    /// conversation; the reply arrives as another task notification.
    /// </summary>
    public string? Continue(string idOrName, string message)
    {
        Entry? entry;
        string id;
        lock (_lock)
        {
            id = ResolveLocked(idOrName) ?? idOrName;
            if (!_workers.TryGetValue(id, out entry))
                return $"Unknown agent id '{idOrName}'. Known agents: {KnownIdsLocked()}.";
            if (entry.Status == WorkerStatus.Running)
                return $"Agent {id} is still running — its result will arrive as a task notification; wait for it before following up.";
            entry.Status = WorkerStatus.Running;
            entry.Result = null;
            entry.StartedAt = DateTimeOffset.Now;
            entry.CompletedAt = null;
        }

        entry.Context.Messages.Add(Models.ChatMessage.FromUserText(message));
        try { WorkerStarted?.Invoke(Snapshot(entry)); }
        catch (Exception ex) { Utilities.DiagnosticLog.Write("Worker-start observer failed: " + ex.Message); }
        _ = Task.Run(() => RunAsync(entry));
        return null;
    }

    public IReadOnlyList<WorkerInfo> List()
    {
        lock (_lock)
        {
            return [.. _workers.Values.Select(Snapshot)];
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_lock)
            {
                return _workers.Values.Count(w => w.Status == WorkerStatus.Running);
            }
        }
    }

    public bool Kill(string idOrName)
    {
        lock (_lock)
        {
            var id = ResolveLocked(idOrName) ?? idOrName;
            if (!_workers.TryGetValue(id, out var entry) || entry.Status != WorkerStatus.Running)
                return false;
            entry.Cts.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Maps a teammate name or an agent id onto an agent id. Names are matched
    /// canonically (the reference's NFKC/lowercase key), and a live worker wins
    /// over a finished one when a name was reused.
    /// </summary>
    public string? Resolve(string idOrName)
    {
        lock (_lock)
            return ResolveLocked(idOrName);
    }

    private string? ResolveLocked(string idOrName)
    {
        if (_workers.ContainsKey(idOrName))
            return idOrName;
        var key = TeamNames.Canonical(idOrName);
        var named = _workers.Values
            .Where(w => w.Name is not null &&
                        string.Equals(TeamNames.Canonical(w.Name), key, StringComparison.Ordinal))
            .OrderByDescending(w => w.Status == WorkerStatus.Running)
            .ThenByDescending(w => w.StartedAt)
            .FirstOrDefault();
        return named?.Id;
    }

    /// <summary>
    /// Delivers a message to a RUNNING agent's inbox; it is picked up before
    /// the agent's next model call. Returns an error when no running agent
    /// answers to <paramref name="idOrName"/>.
    /// </summary>
    public string? Deliver(string idOrName, string message)
    {
        lock (_lock)
        {
            var id = ResolveLocked(idOrName);
            if (id is null || !_workers.TryGetValue(id, out var entry))
                return $"Unknown agent '{idOrName}'. Known agents: {KnownIdsLocked()}.";
            if (entry.Status != WorkerStatus.Running)
                return $"Agent {idOrName} is not running.";
            entry.Inbox.Enqueue(message);
            return null;
        }
    }

    /// <summary>Takes everything queued for one worker, oldest first.</summary>
    private IReadOnlyList<string> DrainInbox(string id)
    {
        lock (_lock)
        {
            if (!_workers.TryGetValue(id, out var entry) || entry.Inbox.Count == 0)
                return [];
            var messages = new List<string>(entry.Inbox.Count);
            while (entry.Inbox.Count > 0)
                messages.Add(entry.Inbox.Dequeue());
            return messages;
        }
    }

    /// <summary>True when a running agent answers to this id or name.</summary>
    public bool IsRunning(string idOrName)
    {
        lock (_lock)
        {
            var id = ResolveLocked(idOrName);
            return id is not null && _workers.TryGetValue(id, out var entry) &&
                entry.Status == WorkerStatus.Running;
        }
    }

    /// <summary>Names of workers that are still running, in start order.</summary>
    public IReadOnlyList<string> RunningNames()
    {
        lock (_lock)
        {
            return [.. _workers.Values
                .Where(w => w.Status == WorkerStatus.Running && w.Name is not null)
                .OrderBy(w => w.StartedAt)
                .Select(w => w.Name!)];
        }
    }

    private async Task RunAsync(Entry entry)
    {
        ToolResult result;
        var status = WorkerStatus.Completed;
        entry.Progress?.BeginRun();
        try
        {
            result = await entry.Runner(entry.Context, entry.Cts.Token);
            if (result.IsError)
                status = WorkerStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            status = WorkerStatus.Killed;
            result = ToolResult.Error("The agent was stopped before finishing.");
        }
        catch (Exception ex)
        {
            status = WorkerStatus.Failed;
            result = ToolResult.Error($"The agent crashed: {ex.Message}");
        }

        WorkerInfo info;
        lock (_lock)
        {
            entry.Status = status;
            entry.Result = result;
            entry.CompletedAt = DateTimeOffset.Now;
            info = Snapshot(entry);
            SaveLocked();
        }

        WorkerFinished?.Invoke(info, result);
    }

    private string KnownIdsLocked() =>
        _workers.Count == 0
            ? "(none)"
            : string.Join(", ", _workers.Values.Select(w => w.Name is null ? w.Id : $"{w.Name} ({w.Id})"));

    private static WorkerInfo Snapshot(Entry entry) =>
        new(entry.Id, entry.AgentType, entry.PromptPreview, entry.Status, entry.Result?.Content,
            entry.ToolUseId, entry.Model, entry.StartedAt, entry.CompletedAt,
            entry.Progress?.TotalTokens ?? 0, entry.Progress?.ToolUses ?? 0, entry.Progress?.LastToolName,
            entry.Name);
}
