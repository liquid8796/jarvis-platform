namespace JarvisCode.Core.Agent;

/// <summary>
/// One piece of background work holding a goal's evaluation open. <paramref name="Kind"/>
/// is the reference's own label for it — "subagent", "shell", "monitor",
/// "workflow", "teammate" — and <paramref name="Detail"/> is a shell's command
/// or anything else's description.
/// </summary>
public sealed record DeferringTask(string Id, string Kind, string Detail, DateTimeOffset StartedAt);

/// <summary>
/// How long a goal's evaluation has been deferred and how often that has been
/// reported. Deferral starts when a turn ends with background work still
/// running: the goal cannot be judged from a transcript that is still being
/// written, so it waits, and says so at a widening interval.
/// </summary>
public sealed record GoalDeferral(
    DateTimeOffset? DeferredSince = null,
    int CheckinCount = 0,
    DateTimeOffset? LastDeferralPassAt = null,
    int IdleCheckinCount = 0)
{
    public static readonly GoalDeferral None = new();

    /// <summary>True once the goal is waiting on background work.</summary>
    public bool IsDeferred => DeferredSince is not null;
}

/// <summary>A check-in as it reaches the user and the model: a one-line notice and its body.</summary>
public sealed record CheckinMessage(string Summary, string Body);

/// <summary>
/// The goal check-in: while background work defers a goal's evaluation, the
/// session says where things stand instead of going quiet — at the end of a turn
/// that could not judge the goal, and on a timer while nothing is running at all.
/// The interval widens with each report and stops after three idle ones, so a
/// session left alone overnight does not talk to itself indefinitely.
/// </summary>
public static class GoalCheckins
{
    /// <summary>Minutes between check-ins unless the environment says otherwise.</summary>
    public const int DefaultIntervalMinutes = 30;

    public const string IntervalVariable = "CLAUDE_CODE_GOAL_CHECKIN_MINUTES";

    /// <summary>The interval doubles per check-in, up to four times the base.</summary>
    public const int MaxBackoffExponent = 2;

    /// <summary>Idle check-ins per deferral run before they pause until the user speaks.</summary>
    public const int MaxIdleCheckins = 3;

    /// <summary>How much of a task's command or description one line carries.</summary>
    public const int DetailMaxChars = 120;

    /// <summary>The timer never fires sooner than this, however short the backoff is.</summary>
    public static readonly TimeSpan MinimumIdleDelay = TimeSpan.FromMinutes(1);

    private const string SummaryPausedSuffix = " · idle check-ins paused until your next message";

    private const string BodyPausedSuffix =
        " Claude Code won't wake this session for another check-in until the user sends a message, " +
        "so say clearly where things stand.";

    /// <summary>
    /// The configured interval, or <see cref="TimeSpan.Zero"/> when check-ins are
    /// off — which is what a non-positive or unreadable value means.
    /// </summary>
    public static TimeSpan Interval(string? configuredMinutes = null)
    {
        var raw = configuredMinutes ?? Environment.GetEnvironmentVariable(IntervalVariable);
        if (raw is { Length: > 0 })
        {
            return double.TryParse(raw, out double minutes) && minutes > 0
                ? TimeSpan.FromMinutes(minutes)
                : TimeSpan.Zero;
        }

        return TimeSpan.FromMinutes(DefaultIntervalMinutes);
    }

    /// <summary>The wait before check-in number <paramref name="checkinCount"/> + 1.</summary>
    public static TimeSpan Backoff(TimeSpan interval, int checkinCount) =>
        interval * Math.Pow(2, Math.Min(Math.Max(checkinCount, 0), MaxBackoffExponent));

    /// <summary>True once this deferral run has spent its idle check-ins.</summary>
    public static bool IdleCapReached(GoalDeferral deferral) => deferral.IdleCheckinCount >= MaxIdleCheckins;

    /// <summary>
    /// Opens a deferral pass. A run that started before the last pass is the same
    /// wait continuing; work that started after it, following a gap longer than
    /// the interval, is a new one and starts the count over — otherwise a session
    /// that keeps launching agents would never report at all.
    /// </summary>
    public static (GoalDeferral Deferral, bool IsNewRun) BeginPass(
        GoalDeferral deferral, IReadOnlyList<DeferringTask> tasks, DateTimeOffset now, TimeSpan interval)
    {
        if (deferral.DeferredSince is null)
        {
            return (deferral with { DeferredSince = now, CheckinCount = 0 }, true);
        }

        if (deferral.LastDeferralPassAt is { } lastPass && tasks.Count > 0)
        {
            var earliest = tasks.Min(t => t.StartedAt);
            if (earliest > lastPass && now - lastPass > interval)
            {
                return (deferral with { DeferredSince = now, CheckinCount = 0 }, true);
            }
        }

        return (deferral, false);
    }

    /// <summary>
    /// What a turn ending on deferred work should do: keep waiting, or report.
    /// The returned deferral is always the one to keep.
    /// </summary>
    public static (GoalDeferral Deferral, string? CheckinText) AtTurnEnd(
        string condition,
        GoalDeferral deferral,
        IReadOnlyList<DeferringTask> tasks,
        DateTimeOffset now,
        TimeSpan? intervalOverride = null)
    {
        var interval = intervalOverride ?? Interval();
        if (interval <= TimeSpan.Zero)
        {
            return (deferral, null);
        }

        var (opened, _) = BeginPass(deferral, tasks, now, interval);
        var deferred = now - (opened.DeferredSince ?? now);
        if (deferred < Backoff(interval, opened.CheckinCount))
        {
            return (opened with { LastDeferralPassAt = now }, null);
        }

        var next = opened with
        {
            DeferredSince = now,
            CheckinCount = opened.CheckinCount + 1,
            LastDeferralPassAt = now,
        };
        return (next, Message(condition, deferred, tasks).Body);
    }

    /// <summary>
    /// How long the idle timer should wait before looking again: what is left of
    /// the current backoff, never under a minute. A run that has spent its idle
    /// check-ins keeps only the tick, so a resumed session still notices.
    /// </summary>
    public static TimeSpan IdleDelay(GoalDeferral deferral, DateTimeOffset now, TimeSpan interval)
    {
        var backoff = Backoff(interval, deferral.CheckinCount);
        var elapsed = IdleCapReached(deferral) || deferral.DeferredSince is null
            ? TimeSpan.Zero
            : now - deferral.DeferredSince.Value;
        var remaining = backoff - elapsed;
        return remaining < MinimumIdleDelay ? MinimumIdleDelay : remaining;
    }

    /// <summary>The check-in itself, in the two shapes the reference has.</summary>
    public static CheckinMessage Message(
        string condition, TimeSpan deferred, IReadOnlyList<DeferringTask> tasks)
    {
        int minutes = Math.Max(1, (int)Math.Round(deferred.TotalMinutes, MidpointRounding.AwayFromZero));
        var quoted = "Goal check-in: «" + Clean(condition) + "»";
        if (tasks.Count == 0)
        {
            return new CheckinMessage(
                "Goal check-in: background work no longer running",
                $"{quoted} is still active. Its evaluation was deferred for {minutes} min while background " +
                "work ran, and that work is no longer running (it finished or was stopped without reporting " +
                "back). Continue toward the goal.");
        }

        var lines = string.Join(
            "\n", tasks.Select(t => Truncate($"- {t.Id} · {t.Kind} · {Clean(t.Detail)}", DetailMaxChars)));
        return new CheckinMessage(
            "Goal check-in: background work still running",
            $"{quoted} is still active, and evaluation has been deferred for {minutes} min because " +
            "background work is still running:\n" + lines + "\n" +
            "Check on their progress (e.g. read their output). If they are progressing, say so briefly and " +
            "keep waiting; if they are stuck or no longer needed, fix or stop them and continue toward the goal.");
    }

    /// <summary>The last idle check-in of a run says that it is the last one.</summary>
    public static CheckinMessage Paused(CheckinMessage message) =>
        new(message.Summary + SummaryPausedSuffix, message.Body + BodyPausedSuffix);

    /// <summary>One task is one line, so its newlines and control characters go.</summary>
    private static string Clean(string value)
    {
        var cleaned = new string([.. value.Select(c => char.IsControl(c) ? ' ' : c)]);
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned.Trim();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
