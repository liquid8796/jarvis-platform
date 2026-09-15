using JarvisCode.Core.Agent;
using JarvisCode.Core.BackgroundTasks;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.Services;

/// <summary>What a Background tasks row is, the reference's task-kind map.</summary>
public enum BackgroundRowKind
{
    Agent,
    RemoteAgent,
    Bash,
    Workflow,
    Monitor,
    Dream,
    Task,
    Loop,
    Preview,
}

/// <summary>The reference's four task states (its loops and servers reuse them).</summary>
public enum BackgroundRowStatus
{
    Running,
    Completed,
    Stopped,
    Failed,
}

/// <summary>
/// One row of the Background tasks panel. Tasks, loops and preview servers all
/// land here: the reference renders them with two components off one card shape
/// (title + stop, a kind/status/elapsed line, then a detail line), so a single
/// row model covers both and <see cref="Expandable"/> separates them.
/// </summary>
public sealed record BackgroundRow
{
    public required string Id { get; init; }

    public required BackgroundRowKind Kind { get; init; }

    /// <summary>The row's heading — a task's description, a server or loop's name.</summary>
    public required string Title { get; init; }

    public required BackgroundRowStatus Status { get; init; }

    /// <summary>
    /// Replaces the status word for the states only servers and loops have:
    /// "Starting…", "Stopping…", "Stop requested…".
    /// </summary>
    public string? StatusOverride { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Chips on the second meta line: model, tokens, tool uses, port, countdown.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>Task rows open in place; loop and server rows do not.</summary>
    public bool Expandable { get; init; }

    public bool CanStop { get; init; }

    /// <summary>A shell task's command, rendered as a code block when expanded.</summary>
    public string? Command { get; init; }

    public string? Output { get; init; }

    /// <summary>The output already lost its head to the collector's own trimming.</summary>
    public bool OutputIsTail { get; init; }

    /// <summary>A finished agent's report, shown when it differs from the title.</summary>
    public string? Summary { get; init; }

    /// <summary>The Agent call this row belongs to — the "View transcript" target.</summary>
    public string? TranscriptCallId { get; init; }

    /// <summary>
    /// A workflow run's progress groups — its declared and announced phases with
    /// the agents that ran under each. Empty for every other kind of row.
    /// </summary>
    public JarvisCode.Core.Agent.WorkflowProgressView? Phases { get; init; }

    /// <summary>Which empty-phase notice this run's phases show.</summary>
    public JarvisCode.Core.Agent.WorkflowRunStatus RunStatus => Status switch
    {
        BackgroundRowStatus.Running => JarvisCode.Core.Agent.WorkflowRunStatus.Running,
        BackgroundRowStatus.Stopped => JarvisCode.Core.Agent.WorkflowRunStatus.Stopped,
        BackgroundRowStatus.Failed => JarvisCode.Core.Agent.WorkflowRunStatus.Failed,
        _ => JarvisCode.Core.Agent.WorkflowRunStatus.Completed,
    };

    /// <summary>Arrival order, the reference's tie-break within a section.</summary>
    public int Index { get; init; }

    public bool IsRunning => Status == BackgroundRowStatus.Running;
}

/// <summary>
/// The Background tasks panel's logic, ported from Claude Code Desktop
/// 1.40609.0.0's tasks pane (chunk c360a9e1c, ~619k-640k): the kind and status
/// label maps, the duration and countdown formatters, section sorting, the
/// output shaping with its three truncation notices, and the running count the
/// composer chip shows. Kept free of WPF so it can be tested directly.
/// </summary>
public static class BackgroundTaskPresentation
{
    /// <summary>Above this the reference shows the end of the output and says so.</summary>
    public const int OutputDisplayChars = 8000;

    /// <summary>Shown in a subagent view while the agent has produced nothing yet.</summary>
    public const string SubagentWorking = "This agent hasn\u2019t reported anything yet.";

    /// <summary>
    /// Shown when an agent left no steps behind. A reopened session rebuilds its
    /// transcript from the stored messages, which never carried the inner
    /// events, so this is what an agent from an earlier run reads as.
    /// </summary>
    /// <summary>Shown when the agent was stopped or denied before doing anything.</summary>
    public const string SubagentDidNotRun = "This agent didn\u2019t run.";

    public const string SubagentNoTranscript =
        "This agent\u2019s transcript wasn\u2019t kept \u2014 inner steps are only held for the session that ran them.";

    /// <summary>
    /// The tail of a subagent view: the agent's report once it has settled with
    /// one, nothing at all when its steps speak for themselves, and otherwise
    /// the notice for where it stands.
    /// </summary>
    public static string? SubagentTail(bool isRunning, string report, int stepCount, bool didNotRun = false)
    {
        if (!isRunning && report.Length > 0)
        {
            return report;
        }

        if (stepCount > 0)
        {
            return null;
        }

        if (isRunning)
        {
            return SubagentWorking;
        }

        return didNotRun ? SubagentDidNotRun : SubagentNoTranscript;
    }

    public const string EmptyStateText = "Background work appears here";

    public const string OutputTruncatedHeadNotice = "… (output too large — showing the beginning only)\n";

    public const string OutputEarlierTruncatedNotice = "… (earlier output truncated)";

    public const string NoOutputYet = "No output yet";

    public const string NoOutputCaptured = "No output captured.";

    public static string KindLabel(BackgroundRowKind kind) => kind switch
    {
        BackgroundRowKind.Agent => "Agent",
        BackgroundRowKind.RemoteAgent => "Remote agent",
        BackgroundRowKind.Bash => "Bash",
        BackgroundRowKind.Workflow => "Workflow",
        BackgroundRowKind.Monitor => "Monitor",
        BackgroundRowKind.Dream => "Dream",
        BackgroundRowKind.Loop => "Loop",
        BackgroundRowKind.Preview => "Preview",
        _ => "Task",
    };

    public static string StatusLabel(BackgroundRowStatus status) => status switch
    {
        BackgroundRowStatus.Running => "Running",
        BackgroundRowStatus.Completed => "Completed",
        BackgroundRowStatus.Stopped => "Stopped",
        _ => "Failed",
    };

    /// <summary>
    /// The reference's elapsed/duration formatter: seconds under a minute are
    /// zero-padded ("05s"), every unit after the first is padded to two digits
    /// ("2m 07s", "1h 02m 07s"), and zero units inside the span are kept.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var total = (long)Math.Max(0, Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero));
        var seconds = total % 60;
        var minutes = total / 60 % 60;
        var hours = total / 3600;

        if (hours > 0)
        {
            return $"{hours}h {minutes:00}m {seconds:00}s";
        }

        return minutes > 0 ? $"{minutes}m {seconds:00}s" : $"{seconds:00}s";
    }

    /// <summary>
    /// The reference's loop countdown: "now" at or past the due moment, then
    /// seconds, minutes (with seconds only under five minutes), hours, days.
    /// </summary>
    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        var seconds = (long)Math.Round(remaining.TotalSeconds, MidpointRounding.AwayFromZero);
        if (seconds < 60)
        {
            return $"{seconds:00}s";
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            var rest = seconds % 60;
            return rest > 0 && minutes < 5 ? $"{minutes}m {rest:00}s" : $"{minutes}m";
        }

        var hours = minutes / 60;
        if (hours < 24)
        {
            var rest = minutes % 60;
            return rest > 0 ? $"{hours}h {rest:00}m" : $"{hours}h";
        }

        var days = hours / 24;
        var restHours = hours % 24;
        return restHours > 0 ? $"{days}d {restHours:00}h" : $"{days}d";
    }

    /// <summary>
    /// The reference's output shaping. Short output is shown whole; anything
    /// longer keeps its last <see cref="OutputDisplayChars"/> characters from the
    /// first line break onwards, under the notice that says what was dropped.
    /// <paramref name="truncated"/> is the reader's own "file too big" flag,
    /// which flips the notice to the head-only one.
    /// </summary>
    public static string ShapeOutput(string text, bool isTail = false, bool truncated = false)
    {
        var head = truncated ? OutputTruncatedHeadNotice : "";
        if (text.Length <= OutputDisplayChars && !isTail)
        {
            return head + text;
        }

        if (head.Length > 0)
        {
            return head + text[..Math.Min(text.Length, OutputDisplayChars)];
        }

        var tail = text.Length <= OutputDisplayChars ? text : text[^OutputDisplayChars..];
        var breakIndex = tail.IndexOf('\n');
        var body = breakIndex >= 0 && breakIndex < tail.Length - 1 ? tail[(breakIndex + 1)..] : tail;
        var notice = isTail
            ? OutputEarlierTruncatedNotice
            : $"… ({text.Length - body.Length} chars truncated)";
        return notice + "\n" + body;
    }

    /// <summary>Running rows, oldest start first — the reference's Running section order.</summary>
    public static IReadOnlyList<BackgroundRow> SortRunning(IEnumerable<BackgroundRow> rows) =>
        [.. rows.OrderBy(r => r.StartedAt ?? DateTimeOffset.MaxValue).ThenBy(r => r.Index)];

    /// <summary>Finished rows, most recently finished first.</summary>
    public static IReadOnlyList<BackgroundRow> SortFinished(IEnumerable<BackgroundRow> rows) =>
        [.. rows.OrderByDescending(r => r.CompletedAt ?? DateTimeOffset.MinValue).ThenByDescending(r => r.Index)];

    /// <summary>
    /// The count the composer's tasks chip shows: the reference adds the
    /// session's loops to its running background tasks.
    /// </summary>
    public static int RunningCount(IEnumerable<BackgroundRow> rows) =>
        rows.Count(r => r.IsRunning || r.Kind == BackgroundRowKind.Loop);

    /// <summary>
    /// Builds every row for one session. <paramref name="serverTaskIds"/> are the
    /// background tasks a preview server owns — they ride the server's own row,
    /// so the reference never lists them twice.
    /// </summary>
    public static IReadOnlyList<BackgroundRow> Build(
        IReadOnlyList<PreviewServerInfo> servers,
        IReadOnlyList<LoopRowInfo> loops,
        IReadOnlyList<BackgroundTaskInfo> tasks,
        IReadOnlyList<WorkerInfo> workers,
        IReadOnlySet<string> serverTaskIds,
        IReadOnlySet<string> stoppingIds,
        DateTimeOffset now,
        IReadOnlyDictionary<string, JarvisCode.Core.Agent.WorkflowProgressFeed>? workflowRuns = null)
    {
        var rows = new List<BackgroundRow>();
        var index = 0;

        foreach (var server in servers)
        {
            var stopping = stoppingIds.Contains(server.ServerId);
            rows.Add(new BackgroundRow
            {
                Id = server.ServerId,
                Kind = BackgroundRowKind.Preview,
                Title = server.Name,
                Status = server.Status == PreviewServerStatus.Error
                    ? BackgroundRowStatus.Failed
                    : BackgroundRowStatus.Running,
                StatusOverride = stopping
                    ? "Stopping…"
                    : server.Status == PreviewServerStatus.Starting ? "Starting…" : null,
                StartedAt = server.StartedAt,
                Details = server.Port is { } port ? [$"localhost:{port}"] : [],
                CanStop = !stopping,
                Index = index++,
            });
        }

        foreach (var loop in loops)
        {
            var stopping = stoppingIds.Contains(loop.Id);
            rows.Add(new BackgroundRow
            {
                Id = loop.Id,
                Kind = BackgroundRowKind.Loop,
                Title = loop.Title,
                Status = BackgroundRowStatus.Running,
                StatusOverride = stopping ? "Stop requested…" : loop.Schedule,
                Details = loop.NextRunAt is { } next ? [$"Next {FormatCountdown(next - now)}"] : [],
                CanStop = !stopping,
                Index = index++,
            });
        }

        foreach (var task in tasks)
        {
            if (serverTaskIds.Contains(task.Id))
            {
                continue;
            }

            var status = task.Status switch
            {
                BackgroundTaskStatus.Running => BackgroundRowStatus.Running,
                BackgroundTaskStatus.Killed => BackgroundRowStatus.Stopped,
                _ => task.ExitCode is 0 or null ? BackgroundRowStatus.Completed : BackgroundRowStatus.Failed,
            };
            var output = AnsiText.Strip(task.Output);
            var isWorkflow = task.Kind == "workflow";
            rows.Add(new BackgroundRow
            {
                Id = task.Id,
                Kind = isWorkflow ? BackgroundRowKind.Workflow : BackgroundRowKind.Bash,
                // The reference titles a shell task with the call's description
                // and keeps the command itself for the expanded code block.
                Title = string.IsNullOrWhiteSpace(task.Description) ? task.Command : task.Description,
                Status = status,
                StartedAt = task.StartedAt,
                CompletedAt = task.CompletedAt,
                Expandable = true,
                CanStop = status == BackgroundRowStatus.Running,
                Command = task.Command,
                Output = output,
                OutputIsTail = output.StartsWith(TrimmedOutputMarker, StringComparison.Ordinal),
                Phases = isWorkflow && workflowRuns?.GetValueOrDefault(task.Id) is { } feed
                    ? feed.Group(now, settled: status != BackgroundRowStatus.Running)
                    : null,
                Index = index++,
            });
        }

        foreach (var worker in workers)
        {
            var status = worker.Status switch
            {
                WorkerStatus.Running => BackgroundRowStatus.Running,
                WorkerStatus.Completed => BackgroundRowStatus.Completed,
                WorkerStatus.Killed => BackgroundRowStatus.Stopped,
                _ => BackgroundRowStatus.Failed,
            };
            var details = new List<string>();
            if (worker.Name is { Length: > 0 })
            {
                details.Add(worker.PromptPreview);
            }

            if (!string.IsNullOrWhiteSpace(worker.Model))
            {
                details.Add(worker.Model);
            }

            if (worker.TotalTokens > 0)
            {
                details.Add($"{Controls.TurnStatusLine.FormatTokenCount(worker.TotalTokens)} tokens");
            }

            if (worker.ToolUses > 0)
            {
                details.Add(worker.ToolUses == 1 ? "1 tool use" : $"{worker.ToolUses} tool uses");
            }

            if (status == BackgroundRowStatus.Running && !string.IsNullOrWhiteSpace(worker.LastToolName))
            {
                details.Add(worker.LastToolName);
            }

            var summary = worker.ResultText;
            rows.Add(new BackgroundRow
            {
                Id = worker.Id,
                Kind = BackgroundRowKind.Agent,
                Title = worker.Name is { Length: > 0 } teammate ? teammate : worker.PromptPreview,
                Status = status,
                StartedAt = worker.StartedAt,
                CompletedAt = worker.CompletedAt,
                Details = details,
                // The reference makes a row expandable only when nothing better
                // exists to open: an agent whose transcript can be shown gets the
                // View transcript button instead of a caret.
                Expandable = worker.ToolUseId is null &&
                             !string.IsNullOrWhiteSpace(summary) && summary != worker.PromptPreview,
                CanStop = status == BackgroundRowStatus.Running,
                Summary = summary,
                TranscriptCallId = worker.ToolUseId,
                Index = index++,
            });
        }

        return rows;
    }

    /// <summary>The head marker <see cref="BackgroundTaskManager"/> writes when it drops old output.</summary>
    private const string TrimmedOutputMarker = "[… earlier output trimmed]";
}

/// <summary>A running loop, as the panel needs it (id, name, schedule, next fire).</summary>
public sealed record LoopRowInfo(string Id, string Title, string Schedule, DateTimeOffset? NextRunAt);
