namespace JarvisCode.Core.Routines;

/// <summary>Schedule presets for a routine.</summary>
public enum RoutineSchedule
{
    /// <summary>Runs only via "Run now".</summary>
    Manual,

    /// <summary>Runs at the top of every hour.</summary>
    Hourly,

    /// <summary>Runs every day at <see cref="Routine.TimeOfDayMinutes"/>.</summary>
    Daily,

    /// <summary>Runs Monday through Friday at <see cref="Routine.TimeOfDayMinutes"/>.</summary>
    Weekdays,

    /// <summary>Runs once a week on <see cref="Routine.Day"/> at <see cref="Routine.TimeOfDayMinutes"/>.</summary>
    Weekly,
}

/// <summary>
/// A recurring task: an instruction that runs in a fresh session on a schedule
/// while the app is open. Times are local; slots are minute-precision.
/// </summary>
public sealed class Routine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>The prompt sent as the first user message of the routine's session.</summary>
    public string Instruction { get; set; } = "";

    public RoutineSchedule Schedule { get; set; } = RoutineSchedule.Manual;

    /// <summary>Minutes past local midnight for Daily/Weekdays/Weekly slots.</summary>
    public int TimeOfDayMinutes { get; set; } = 9 * 60;

    /// <summary>Day of week for Weekly slots.</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;

    /// <summary>Working directory the routine's session opens in; null keeps the current one.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Model the routine runs with; null keeps the currently selected model.</summary>
    public string? ModelId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>When the routine was created; slots before this never fire.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>The schedule slot of the last run that started; slots at or before it never re-fire.</summary>
    public DateTime? LastRunSlot { get; set; }

    /// <summary>When the last run actually started.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>
    /// A standard 5-field cron expression in local time ("M H DoM Mon DoW"). When
    /// set it decides the schedule and <see cref="Schedule"/> is not consulted —
    /// this is what the reference's CronCreate tool writes.
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>Fire once at the next match, then delete (CronCreate's recurring: false).</summary>
    public bool OneShot { get; set; }

    /// <summary>
    /// Set for a routine that is not durable: it belongs to the session that
    /// created it. RoutineStore keeps these jobs only in memory and the host
    /// removes them when the owning session ends.
    /// </summary>
    public string? OwnerSessionId { get; set; }

    /// <summary>The conversation that created a CronCreate job, including a durable job.</summary>
    public string? CronSessionId { get; set; }

    /// <summary>Deleted once this moment passes (a recurring CronCreate job auto-expires after 7 days).</summary>
    public DateTime? ExpiresAt { get; set; }

    // ---- fields the Scheduled page edits; every one is optional, so a routine
    // ---- written by CronCreate before they existed still loads unchanged.

    /// <summary>The one-line summary the list and the detail page show under the name.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// A one-time run: fires once at this moment and then ends as
    /// <see cref="RoutineEndedReasons.RunOnceFired"/>. Mutually exclusive with
    /// <see cref="CronExpression"/>, which is how the reference's editor models
    /// its "One-time" frequency.
    /// </summary>
    public DateTimeOffset? FireAt { get; set; }

    /// <summary>
    /// Why the routine stopped running by itself, or null while nothing ended it.
    /// One of <see cref="RoutineEndedReasons"/>; it is what separates the
    /// reference's Completed status from its Auto-disabled one.
    /// </summary>
    public string? EndedReason { get; set; }

    /// <summary>
    /// Why the routine is held rather than paused, or null. The reference's own
    /// hold reasons are account- and fleet-level, so nothing local sets this yet;
    /// the field exists because the status ladder is ported whole.
    /// </summary>
    public string? SuspensionReason { get; set; }

    /// <summary>The permission mode a run opens in; null takes the Settings default.</summary>
    public string? PermissionModeName { get; set; }

    /// <summary>Run in a git worktree of the working folder rather than in it.</summary>
    public bool UseWorktree { get; set; }

    /// <summary>The branch a worktree run starts from; null keeps the current branch.</summary>
    public string? SourceBranch { get; set; }

    /// <summary>Notify when a run finishes ("Notify me when this routine finishes").</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    // ---- what a run was allowed, kept on the routine and re-applied to later runs.
    // ---- These are the reference's own per-task fields; both are optional, so a
    // ---- routine written before they existed still loads unchanged.

    /// <summary>
    /// The browser grant a run was given, kept for later runs — the reference's
    /// <c>chromePermissionMode</c>, whose two values are
    /// <c>skip_all_permission_checks</c> (every site) and <c>follow_a_plan</c>
    /// (the sites in <see cref="ChromeAllowedDomains"/>). Null while the routine
    /// has never been granted one.
    /// </summary>
    public string? ChromePermissionMode { get; set; }

    /// <summary>The sites a run was allowed, the reference's <c>chromeAllowedDomains</c>.</summary>
    public List<string> ChromeAllowedDomains { get; set; } = [];

    /// <summary>
    /// The tool permissions a run was allowed, the reference's
    /// <c>approvedPermissions</c>. Each entry is its own identity key —
    /// <c>{toolName}\0{ruleContent}</c> — so a rule and a bare tool approval are
    /// two different rows.
    /// </summary>
    public List<string> ApprovedPermissions { get; set; } = [];
}

/// <summary>
/// The reference's <c>ended_reason</c> strings, in its own spelling, for the
/// three a local scheduler can determine. Its other twenty name repositories,
/// organizations, cloud environments and devices, none of which exist here.
/// </summary>
public static class RoutineEndedReasons
{
    /// <summary>A one-time run has happened; the reference reads this as Completed, not disabled.</summary>
    public const string RunOnceFired = "run_once_fired";

    /// <summary>The cron expression stopped parsing.</summary>
    public const string InvalidCron = "auto_disabled_invalid_cron";

    /// <summary>The schedule asks for more than one run an hour.</summary>
    public const string SubHourly = "auto_disabled_subhourly";

    /// <summary>The stored configuration is no longer usable.</summary>
    public const string ConfigRejected = "auto_disabled_config_rejected";
}
