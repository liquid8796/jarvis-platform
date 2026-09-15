namespace JarvisCode.Core.Agent;

/// <summary>
/// The state a workflow agent reports for itself, as the reference's progress
/// events carry it: <c>start</c> when the call is made, <c>progress</c> once it
/// is really running, then <c>done</c> or <c>error</c>.
/// </summary>
public enum WorkflowAgentState
{
    Start,
    Progress,
    Done,
    Error,
}

/// <summary>
/// What an agent counts as in a phase's tally — the reference's classifier,
/// which reads a launched-but-not-started agent as pending and a running one
/// that has been quiet past the stall window as stalled.
/// </summary>
public enum WorkflowAgentPhaseState
{
    Pending,
    Running,
    Stalled,
    Done,
    Error,
}

/// <summary>Where a whole phase stands.</summary>
public enum WorkflowPhaseStatus
{
    Pending,
    Running,
    Done,
    Error,
}

/// <summary>
/// One entry of a run's live progress feed — the reference's
/// <c>workflowProgress</c> array, whose three shapes are <c>workflow_phase</c>,
/// <c>workflow_agent</c> and <c>workflow_log</c>.
/// </summary>
public abstract record WorkflowProgressEntry
{
    private WorkflowProgressEntry()
    {
    }

    /// <summary>A progress group. Declared phases are announced before the script runs.</summary>
    public sealed record Phase(int Index, string Title, string? Detail = null) : WorkflowProgressEntry;

    /// <summary>One <c>agent()</c> call, updated in place as it starts and settles.</summary>
    public sealed record Agent(
        int Index,
        string Label,
        WorkflowAgentState State,
        int? PhaseIndex = null,
        string? PhaseTitle = null,
        string PromptPreview = "",
        long Tokens = 0,
        string? Model = null,
        TimeSpan? Duration = null,
        DateTimeOffset? LastProgressAt = null) : WorkflowProgressEntry;

    /// <summary>A <c>log()</c> line, or the engine's own narration.</summary>
    public sealed record Log(string Message) : WorkflowProgressEntry;
}

/// <summary>The reference's per-phase tally.</summary>
public sealed record WorkflowPhaseCounts(
    int Done = 0,
    int Running = 0,
    int Stalled = 0,
    int Error = 0,
    int Pending = 0,
    int Total = 0);

/// <summary>One progress group: a phase, its agents and its tally.</summary>
public sealed record WorkflowPhaseGroup(
    int Index,
    string Title,
    string? Detail,
    IReadOnlyList<WorkflowProgressEntry.Agent> Agents,
    WorkflowPhaseCounts Counts,
    WorkflowPhaseStatus Status,
    long TotalTokens,
    TimeSpan? MaxDuration);

/// <summary>A run's phases with the tally across all of them.</summary>
public sealed record WorkflowProgressView(
    IReadOnlyList<WorkflowPhaseGroup> Phases,
    WorkflowPhaseCounts Counts,
    long TotalTokens);

/// <summary>
/// A workflow run's progress, merged the way the reference's task-registry
/// updater merges it: phase and agent entries are keyed by <c>type:index</c> and
/// replaced in place, log entries append, and once logs push the feed past twice
/// the cap the oldest logs are dropped. Ported from the CLI's local_workflow
/// task update (its <c>M = 500</c>).
/// </summary>
public sealed class WorkflowProgressFeed
{
    /// <summary>The reference's <c>M</c>: log entries trim once the feed passes twice this.</summary>
    public const int LogTrimThreshold = 500;

    /// <summary>The reference's default stall window: quiet longer than this reads as stalled.</summary>
    public static readonly TimeSpan StallWindow = TimeSpan.FromMilliseconds(90_000);

    private readonly object _gate = new();
    private readonly List<WorkflowProgressEntry> _entries = [];

    /// <summary>Highest agent index seen; the reference's agentCount.</summary>
    public int AgentCount { get; private set; }

    public long TotalTokens { get; private set; }

    /// <summary>Bumped by the number of entries applied, so a view can tell it changed.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<WorkflowProgressEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Apply(WorkflowProgressEntry entry) => Apply([entry]);

    public void Apply(IReadOnlyList<WorkflowProgressEntry> entries)
    {
        if (entries.Count == 0)
            return;

        lock (_gate)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _entries.Count; i++)
            {
                if (KeyOf(_entries[i]) is { } existing)
                    index[existing] = i;
            }

            var appendedLog = false;
            foreach (var entry in entries)
            {
                if (KeyOf(entry) is { } key)
                {
                    if (index.TryGetValue(key, out var at))
                    {
                        _entries[at] = entry;
                    }
                    else
                    {
                        index[key] = _entries.Count;
                        _entries.Add(entry);
                    }

                    if (entry is WorkflowProgressEntry.Agent agent)
                        AgentCount = Math.Max(AgentCount, agent.Index);
                }
                else
                {
                    _entries.Add(entry);
                    appendedLog = true;
                }
            }

            if (appendedLog && _entries.Count > LogTrimThreshold * 2)
            {
                var toDrop = _entries.Count - LogTrimThreshold;
                var kept = new List<WorkflowProgressEntry>(_entries.Count);
                foreach (var entry in _entries)
                {
                    if (toDrop > 0 && entry is WorkflowProgressEntry.Log)
                    {
                        toDrop--;
                        continue;
                    }

                    kept.Add(entry);
                }

                _entries.Clear();
                _entries.AddRange(kept);
            }

            long tokens = 0;
            foreach (var entry in _entries)
            {
                if (entry is WorkflowProgressEntry.Agent agent)
                    tokens += agent.Tokens;
            }

            TotalTokens = tokens;
            Version += entries.Count;
        }
    }

    /// <summary>
    /// The reference's dedup key: phase and agent entries replace the entry with
    /// the same type and index; a log entry has none and always appends.
    /// </summary>
    private static string? KeyOf(WorkflowProgressEntry entry) => entry switch
    {
        WorkflowProgressEntry.Phase phase => $"workflow_phase:{phase.Index}",
        WorkflowProgressEntry.Agent agent => $"workflow_agent:{agent.Index}",
        _ => null,
    };

    /// <summary>The feed grouped into phases; see <see cref="WorkflowProgressGrouping.Group"/>.</summary>
    public WorkflowProgressView? Group(DateTimeOffset? now = null, bool settled = false) =>
        WorkflowProgressGrouping.Group(Entries, now, settled);
}

/// <summary>
/// Turns a progress feed into progress groups, ported from the reference
/// renderer's own grouping (its <c>hD</c>, with the <c>pD</c> classifier, the
/// <c>fD</c> tally and the <c>gD</c> phase status).
/// </summary>
public static class WorkflowProgressGrouping
{
    /// <summary>An agent's contribution to the tally, as the reference classifies it.</summary>
    public static WorkflowAgentPhaseState StateOf(
        WorkflowProgressEntry.Agent agent, DateTimeOffset? now, TimeSpan? stallWindow = null)
    {
        if (agent.State == WorkflowAgentState.Done)
            return WorkflowAgentPhaseState.Done;
        if (agent.State == WorkflowAgentState.Error)
            return WorkflowAgentPhaseState.Error;
        if (agent.State == WorkflowAgentState.Progress)
        {
            var window = stallWindow ?? WorkflowProgressFeed.StallWindow;
            return now is { } clock && agent.LastProgressAt is { } last && clock - last > window
                ? WorkflowAgentPhaseState.Stalled
                : WorkflowAgentPhaseState.Running;
        }

        return WorkflowAgentPhaseState.Pending;
    }

    /// <summary>The phase's status from its tally; a settled run has nothing still running.</summary>
    public static WorkflowPhaseStatus StatusOf(WorkflowPhaseCounts counts, bool settled)
    {
        if (counts.Error > 0)
            return WorkflowPhaseStatus.Error;
        if (counts.Running > 0)
            return settled ? WorkflowPhaseStatus.Done : WorkflowPhaseStatus.Running;
        if (counts.Total == 0 || counts.Pending == counts.Total)
            return WorkflowPhaseStatus.Pending;
        return counts.Done == counts.Total || settled ? WorkflowPhaseStatus.Done : WorkflowPhaseStatus.Running;
    }

    /// <summary>
    /// Groups the feed. An agent with no phase of its own joins the phase most
    /// recently announced before it, or a synthetic first phase when none was;
    /// a phase that only ever appeared as an agent's phase index keeps the
    /// placeholder title "Phase {n}" until a real title arrives for it.
    /// </summary>
    public static WorkflowProgressView? Group(
        IReadOnlyList<WorkflowProgressEntry> entries,
        DateTimeOffset? now = null,
        bool settled = false,
        TimeSpan? stallWindow = null)
    {
        if (entries.Count == 0)
            return null;

        var ordered = new List<Builder>();
        var byIndex = new Dictionary<int, Builder>();
        Builder? current = null;
        var total = new Counter();
        long totalTokens = 0;

        Builder Phase(int index, string? title, string? detail)
        {
            if (byIndex.TryGetValue(index, out var found))
            {
                if (title is { Length: > 0 } named &&
                    found.Title.StartsWith("Phase ", StringComparison.Ordinal))
                {
                    found.Title = named;
                }

                found.Detail ??= detail;
                return found;
            }

            found = new Builder
            {
                Index = index,
                Title = title is { Length: > 0 } declared ? declared : $"Phase {index + 1}",
                Detail = detail,
            };
            byIndex[index] = found;
            ordered.Add(found);
            return found;
        }

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case WorkflowProgressEntry.Phase phase:
                    current = Phase(phase.Index, phase.Title, phase.Detail);
                    break;

                case WorkflowProgressEntry.Agent agent:
                {
                    var group = agent.PhaseIndex is { } phaseIndex
                        ? Phase(phaseIndex, agent.PhaseTitle, null)
                        : current ?? Phase(0, null, null);
                    group.Agents.Add(agent);
                    var state = StateOf(agent, now, stallWindow);
                    group.Counts.Add(state);
                    total.Add(state);
                    if (agent.Tokens != 0)
                    {
                        group.TotalTokens += agent.Tokens;
                        totalTokens += agent.Tokens;
                    }

                    if (agent.Duration is { } duration &&
                        (group.MaxDuration is null || duration > group.MaxDuration))
                    {
                        group.MaxDuration = duration;
                    }

                    break;
                }
            }
        }

        var phases = ordered
            .OrderBy(phase => phase.Index)
            .Select(phase =>
            {
                var counts = phase.Counts.Snapshot();
                return new WorkflowPhaseGroup(
                    phase.Index,
                    phase.Title,
                    phase.Detail,
                    phase.Agents,
                    counts,
                    StatusOf(counts, settled),
                    phase.TotalTokens,
                    phase.MaxDuration);
            })
            .ToList();

        return new WorkflowProgressView(phases, total.Snapshot(), totalTokens);
    }

    private sealed class Builder
    {
        public required int Index { get; init; }

        public required string Title { get; set; }

        public string? Detail { get; set; }

        public List<WorkflowProgressEntry.Agent> Agents { get; } = [];

        public Counter Counts { get; } = new();

        public long TotalTokens { get; set; }

        public TimeSpan? MaxDuration { get; set; }
    }

    /// <summary>A stalled agent counts as running as well, exactly as the reference tallies it.</summary>
    private sealed class Counter
    {
        private int _done, _running, _stalled, _error, _pending, _total;

        public void Add(WorkflowAgentPhaseState state)
        {
            _total++;
            switch (state)
            {
                case WorkflowAgentPhaseState.Stalled:
                    _running++;
                    _stalled++;
                    break;
                case WorkflowAgentPhaseState.Running:
                    _running++;
                    break;
                case WorkflowAgentPhaseState.Done:
                    _done++;
                    break;
                case WorkflowAgentPhaseState.Error:
                    _error++;
                    break;
                default:
                    _pending++;
                    break;
            }
        }

        public WorkflowPhaseCounts Snapshot() => new(_done, _running, _stalled, _error, _pending, _total);
    }
}

/// <summary>
/// One entry of a script's <c>meta.phases</c>: the progress groups a workflow
/// declares up front. The reference announces every one of them, in order,
/// before the script body runs.
/// </summary>
public sealed record WorkflowPhaseDeclaration(string Title, string? Detail = null);

/// <summary>
/// The reference's own labels for the phase list, taken from the desktop app's
/// message catalogue by id (P7djTuHDN0 "Phases", fDzdmwIyjw the phase button's
/// accessible name, ihIcdpwqY4 the "{done}/{total}" tally, and the four
/// empty-phase notices).
/// </summary>
public static class WorkflowPhaseLabels
{
    /// <summary>The section heading above the phase list.</summary>
    public const string Phases = "Phases";

    public const string AgentColumn = "Agent";

    public const string ModelColumn = "Model";

    public const string TokensColumn = "Tokens";

    public const string TimeColumn = "Time";

    public static string Status(WorkflowPhaseStatus status) => status switch
    {
        WorkflowPhaseStatus.Pending => "Pending",
        WorkflowPhaseStatus.Running => "Running",
        WorkflowPhaseStatus.Done => "Done",
        _ => "Error",
    };

    /// <summary>What an empty phase says, chosen by the run's own status.</summary>
    public static string NoAgents(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Running => "No agents have started yet",
        WorkflowRunStatus.Stopped => "Stopped before any agents started",
        WorkflowRunStatus.Failed => "Failed before any agents started",
        _ => "No agents ran in this phase",
    };

    /// <summary>The header's "{done}/{total}" tally.</summary>
    public static string Tally(WorkflowPhaseCounts counts) => $"{counts.Done}/{counts.Total}";

    /// <summary>The phase button's accessible name, with the reference's plural rule.</summary>
    public static string AccessibleName(WorkflowPhaseGroup phase)
    {
        var name = $"Phase: {phase.Title}, {Status(phase.Status)}";
        return phase.Counts.Total switch
        {
            0 => name,
            1 => $"{name}, {phase.Counts.Done} of {phase.Counts.Total} agent done",
            _ => $"{name}, {phase.Counts.Done} of {phase.Counts.Total} agents done",
        };
    }
}

/// <summary>How a workflow run ended, for the empty-phase notice.</summary>
public enum WorkflowRunStatus
{
    Running,
    Completed,
    Stopped,
    Failed,
}
