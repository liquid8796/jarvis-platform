using System.Globalization;
using System.Text.RegularExpressions;
using JarvisCode.Core.Routines;

namespace JarvisCode.App.Services;

/// <summary>The five states the reference's Scheduled list distinguishes.</summary>
public enum ScheduledStatus
{
    Active,
    Paused,
    Completed,
    OnHold,
    AutoDisabled,
}

/// <summary>The two sorts the reference offers, "Next run" first.</summary>
public enum ScheduledSort
{
    NextRun,
    Name,
}

/// <summary>
/// One row of the Scheduled page. Both stores project into this: the routine
/// store the page edits, and the SKILL.md scheduled-task store the
/// <c>scheduled-tasks</c> MCP server writes — which is how the reference shows
/// them, one list over <c>[...scheduledTasks, ...routines]</c>.
/// </summary>
public sealed record ScheduledItem
{
    public required string Id { get; init; }

    /// <summary>The stored display name; empty falls back to the id (<see cref="DisplayName"/>).</summary>
    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public string Prompt { get; init; } = "";

    public bool Enabled { get; init; }

    public string? CronExpression { get; init; }

    public DateTimeOffset? FireAt { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>One of <see cref="RoutineEndedReasons"/>, or null while nothing ended it.</summary>
    public string? EndedReason { get; init; }

    public string? SuspensionReason { get; init; }

    /// <summary>The dispatch delay <see cref="ScheduledTaskJitter"/> gives this item.</summary>
    public int JitterSeconds { get; init; }

    /// <summary>True for a row backed by the SKILL.md store rather than by a routine.</summary>
    public bool IsScheduledTask { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? ModelId { get; init; }

    public string? PermissionModeName { get; init; }

    public bool UseWorktree { get; init; }

    public string? SourceBranch { get; init; }

    public bool NotifyOnCompletion { get; init; } = true;

    /// <summary>The browser grant this task's runs were given, the reference's chromePermissionMode.</summary>
    public string? ChromePermissionMode { get; init; }

    /// <summary>The sites its runs were allowed.</summary>
    public IReadOnlyList<string> ChromeAllowedDomains { get; init; } = [];

    /// <summary>The tool rules its runs were allowed, as the store's own identity keys.</summary>
    public IReadOnlyList<string> ApprovedPermissions { get; init; } = [];

    /// <summary>How many runs the history holds, shown at the end of a list row.</summary>
    public int RunCount { get; init; }

    /// <summary>The next dispatch as the real evaluator computes it, jitter included.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    public int JitterMinutes => (int)Math.Round(JitterSeconds / 60.0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// What the Scheduled page shows for an item: its status, its schedule in
/// words, when it next runs, and how the list is searched, filtered and sorted.
///
/// Ported from the reference desktop 1.40609.1.0 — the list route in
/// <c>ccd3f68fe-BCipNHOa.js</c> (its <c>$s</c> status, <c>mr</c> filter,
/// <c>ns</c> sort, <c>xn</c> description preview), the status folding in
/// <c>cc630ea76-DIPg7mm0.js</c>, the schedule summary in
/// <c>c511d59b8-ClqwCj6I.js</c>, and the schedule maths in
/// <c>shared-7-pSrlloWM.js</c> (<c>Zh</c> status, <c>gh</c> next run,
/// <c>fh</c> relative date, <c>kh</c>/<c>Ah</c> time and day names,
/// <c>wv</c>/<c>hv</c> display name, <c>Sv</c> slug).
/// </summary>
public static class ScheduledTaskPresentation
{
    /// <summary>
    /// Ended reasons the user cannot simply re-enable past (the reference's
    /// <c>H</c> set). Only the first arises locally; the rest name repositories,
    /// organizations, agents and devices, and are listed so the predicate is the
    /// reference's rather than a subset that happens to agree today.
    /// </summary>
    private static readonly HashSet<string> TerminalReasons = new(StringComparer.Ordinal)
    {
        RoutineEndedReasons.RunOnceFired,
        "auto_disabled_session_gone",
        "auto_disabled_session_unsafe",
        "auto_disabled_agent_deleted",
        "auto_disabled_member_removed",
        "auto_disabled_owner_inactive",
        "auto_disabled_org_deleted",
        "auto_disabled_act_as_bot_denied",
        "auto_disabled_not_silo_member",
        "auto_disabled_silo_revoked",
        "mainwatcher_disabled",
        "auto_disabled_hold_expired",
        "auto_disabled_device_removed",
        "auto_disabled_wake_superseded",
    };

    /// <summary>
    /// The two the user fixes by editing the schedule rather than by switching
    /// the routine back on (the reference's <c>q</c> set).
    /// </summary>
    private static readonly HashSet<string> ScheduleFixableReasons = new(StringComparer.Ordinal)
    {
        RoutineEndedReasons.InvalidCron,
        RoutineEndedReasons.SubHourly,
    };

    /// <summary>
    /// The reference's <c>Zh</c>: enabled is Active; otherwise an ended reason
    /// decides, with the one-time reason reading as Completed rather than as a
    /// failure; nothing ended it means the user paused it.
    /// </summary>
    public static ScheduledStatus BaseStatus(bool enabled, string? endedReason) =>
        enabled ? ScheduledStatus.Active
        : endedReason is { Length: > 0 } reason
            ? reason == RoutineEndedReasons.RunOnceFired ? ScheduledStatus.Completed : ScheduledStatus.AutoDisabled
        : ScheduledStatus.Paused;

    /// <summary>
    /// The reference's fold in <c>cc630ea76</c>: a suspension turns Active or
    /// Paused into On hold, and leaves Completed and Auto-disabled alone —
    /// a hold cannot revive something that already ended.
    /// </summary>
    public static ScheduledStatus WithSuspension(ScheduledStatus status, string? suspensionReason) =>
        suspensionReason is { Length: > 0 } &&
        status is ScheduledStatus.Paused or ScheduledStatus.Active
            ? ScheduledStatus.OnHold
            : status;

    /// <summary>
    /// The status the list shows (its <c>$s</c>): a device-absent hold is
    /// dropped first, then a one-time item that has run reads as Completed even
    /// with no ended reason stored.
    /// </summary>
    public static ScheduledStatus Status(ScheduledItem item)
    {
        var suspension = item.SuspensionReason == "device_absent" ? null : item.SuspensionReason;
        var status = WithSuspension(BaseStatus(item.Enabled, item.EndedReason), suspension);
        if (status is ScheduledStatus.AutoDisabled or ScheduledStatus.OnHold)
        {
            return status;
        }

        return status == ScheduledStatus.Completed || OneTimeCompleted(item)
            ? ScheduledStatus.Completed
            : item.Enabled ? ScheduledStatus.Active : ScheduledStatus.Paused;
    }

    /// <summary>The reference's <c>Ws</c>: a one-time item that has already run.</summary>
    public static bool OneTimeCompleted(ScheduledItem item) =>
        item.FireAt is not null && item.LastRunAt is not null;

    public static string StatusLabel(ScheduledStatus status) => status switch
    {
        ScheduledStatus.Active => "Active",
        ScheduledStatus.Paused => "Paused",
        ScheduledStatus.Completed => "Ran",
        ScheduledStatus.OnHold => "On hold",
        ScheduledStatus.AutoDisabled => "Auto-disabled",
        _ => status.ToString(),
    };

    /// <summary>
    /// Whether the enable/pause switch is offered (the reference's <c>ae</c>):
    /// never for something completed, always for something running, and for a
    /// stopped one only when the reason is neither terminal nor one the user
    /// must fix in the schedule, and its one-time moment has not passed.
    /// </summary>
    public static bool CanToggleEnabled(ScheduledItem item, DateTimeOffset now)
    {
        if (BaseStatus(item.Enabled, item.EndedReason) == ScheduledStatus.Completed)
        {
            return false;
        }

        if (item.Enabled)
        {
            return true;
        }

        var reason = item.EndedReason;
        var reEnableable = reason is not { Length: > 0 } || !TerminalReasons.Contains(reason);
        var scheduleFixable = reason is { Length: > 0 } && ScheduleFixableReasons.Contains(reason);
        var firedMomentPassed = item.FireAt is { } fireAt && fireAt < now;
        return reEnableable && !scheduleFixable && !firedMomentPassed;
    }

    /// <summary>The sentence the detail page prints for a routine that stopped by itself.</summary>
    public static string? EndedReasonMessage(string? reason) => reason switch
    {
        RoutineEndedReasons.RunOnceFired => "This one-time routine has run. Set a new schedule to run it again.",
        RoutineEndedReasons.InvalidCron =>
            "This routine was disabled because its cron expression is invalid. Edit the schedule and re-enable it.",
        RoutineEndedReasons.SubHourly =>
            "This routine was disabled because it was scheduled more than once per hour. Choose a less frequent schedule and re-enable it.",
        RoutineEndedReasons.ConfigRejected =>
            "This routine was disabled because its configuration is no longer valid. Review your settings and re-enable it.",
        { Length: > 0 } => "This routine was automatically disabled. Review your settings and re-enable it.",
        _ => null,
    };

    /// <summary>The short form of the same reason, for a list row.</summary>
    public static string? EndedReasonShortLabel(string? reason) => reason switch
    {
        RoutineEndedReasons.RunOnceFired => "already ran",
        RoutineEndedReasons.InvalidCron => "invalid schedule",
        RoutineEndedReasons.SubHourly => "schedule too frequent",
        RoutineEndedReasons.ConfigRejected => "configuration rejected",
        { Length: > 0 } => "unrecognized reason",
        _ => null,
    };

    // ---- names -------------------------------------------------------------

    /// <summary>
    /// The reference's <c>wv</c>/<c>hv</c>: the stored name, or the id read back
    /// as words — "daily-code-review" becomes "Daily code review".
    /// </summary>
    public static string DisplayName(ScheduledItem item) =>
        item.Name is { Length: > 0 } name ? name : NameFromId(item.Id);

    /// <summary>The reference's <c>hv</c>.</summary>
    public static string NameFromId(string id) =>
        id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id.Replace('-', ' ')[1..];

    /// <summary>
    /// The reference's <c>Sv</c>: the identifier a typed name becomes. Spaces
    /// join with hyphens, anything else outside <c>[a-z0-9_-]</c> is dropped,
    /// and the result is trimmed of separators and capped at 120 characters.
    /// </summary>
    public static string Slug(string name)
    {
        var lowered = Regex.Replace(name.Trim().ToLowerInvariant(), @"\s+", "-");
        var kept = Regex.Replace(lowered, "[^a-z0-9_-]", "");
        var trimmed = kept.Trim('-', '_');
        return trimmed.Length > 120 ? trimmed[..120].TrimEnd('-', '_') : trimmed;
    }

    /// <summary>Names the reference refuses because they address its own routes.</summary>
    private static readonly string[] ReservedNames = ["new", "new-local"];

    /// <summary>
    /// The error under the Name box, or null. The reference validates a name
    /// only while creating: an existing routine keeps whatever name it has.
    /// </summary>
    public static string? NameError(string name, IReadOnlySet<string> existingIds, bool isEditing)
    {
        if (isEditing || name.Trim().Length == 0)
        {
            return null;
        }

        var slug = Slug(name);
        if (slug.Length == 0)
        {
            return "Name must contain at least one letter or number.";
        }

        if (ReservedNames.Contains(slug, StringComparer.Ordinal))
        {
            return "This name is reserved. Choose a different name.";
        }

        return existingIds.Contains(slug)
            ? $"A routine named “{name.Trim()}” already exists."
            : null;
    }

    // ---- schedule in words -------------------------------------------------

    /// <summary>
    /// The time of day as the reference prints it — its <c>kh</c>, which formats
    /// a fixed date so only the clock shows.
    /// </summary>
    public static string TimeOfDay(int hour, int minute, IFormatProvider? culture = null) =>
        new DateTime(2024, 1, 1, hour, minute, 0, DateTimeKind.Unspecified)
            .ToString("h:mm tt", culture ?? CultureInfo.CurrentCulture);

    /// <summary>
    /// The weekday name, the reference's <c>Ah</c>: 7 January 2024 was a Sunday,
    /// so index 0 is Sunday.
    /// </summary>
    public static string DayName(int dayOfWeek, IFormatProvider? culture = null) =>
        new DateTime(2024, 1, 7 + dayOfWeek, 0, 0, 0, DateTimeKind.Unspecified)
            .ToString("dddd", culture ?? CultureInfo.CurrentCulture);

    /// <summary>Which way <see cref="Relative"/> reads a neighbouring day.</summary>
    public enum RelativeDirection
    {
        Future,
        Past,
    }

    /// <summary>
    /// The reference's <c>fh</c>: today, tomorrow or yesterday by name, anything
    /// else as a short date, with a <c>~</c> before the time when the dispatch
    /// carries jitter.
    /// </summary>
    public static string Relative(
        DateTimeOffset when,
        DateTimeOffset now,
        bool approximate,
        RelativeDirection direction,
        IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var time = when.LocalDateTime.ToString("h:mm tt", culture);
        var tilde = approximate ? "~" : "";
        var day = when.LocalDateTime.Date;
        var today = now.LocalDateTime.Date;
        if (day == today)
        {
            return $"today at {tilde}{time}";
        }

        if (direction == RelativeDirection.Future && day == today.AddDays(1))
        {
            return $"tomorrow at {tilde}{time}";
        }

        if (direction == RelativeDirection.Past && day == today.AddDays(-1))
        {
            return $"yesterday at {tilde}{time}";
        }

        var date = day.Year == today.Year
            ? day.ToString("MMM d", culture)
            : day.ToString("MMM d, yyyy", culture);
        return $"{date} at {tilde}{time}";
    }

    /// <summary>
    /// The schedule as a sentence — the reference's <c>c511d59b8</c> describer.
    /// Returns null for an item with no schedule at all, which the page renders
    /// as "Manual only".
    ///
    /// One measured delta: for a valid cron that is none of the four presets the
    /// reference prints a cronstrue description ("At 09:00 AM, on day 1 of the
    /// month"). Bundling a cron describer would be a new dependency and writing
    /// one would be a partial imitation, so this port shows the expression
    /// itself; the "Next run" line beside it still comes from the real evaluator.
    /// </summary>
    public static string? Describe(
        ScheduledItem item,
        DateTimeOffset now,
        IFormatProvider? culture = null)
    {
        if (item.FireAt is { } fireAt)
        {
            return $"Once — {Relative(fireAt, now, approximate: false, RelativeDirection.Future, culture)}";
        }

        if (item.CronExpression is not { Length: > 0 } cron)
        {
            return null;
        }

        var approximate = item.JitterMinutes > 0;
        var tilde = approximate ? "~" : "";
        if (ScheduleFrequencies.ParseCron(cron) is { } parsed)
        {
            var time = TimeOfDay(parsed.Hour, parsed.Minute, culture);
            return parsed.Frequency switch
            {
                ScheduleFrequency.Hourly => "Hourly",
                ScheduleFrequency.Daily => $"Every day at {tilde}{time}",
                ScheduleFrequency.Weekdays => $"Weekdays at {tilde}{time}",
                ScheduleFrequency.Weekly => $"Every {DayName(parsed.DayOfWeek, culture)} at {tilde}{time}",
                _ => cron,
            };
        }

        return CronSchedule.TryParse(cron) is null ? "Invalid schedule" : cron;
    }

    /// <summary>
    /// The "Next run: {date}" value, or null when nothing is scheduled. The
    /// reference computes the preset case itself and otherwise formats the next
    /// run the server reported; this port fills that second slot from
    /// <see cref="CronSchedule"/>, which is the same fact from a local evaluator.
    /// </summary>
    public static string? NextRunText(
        ScheduledItem item,
        DateTimeOffset now,
        IFormatProvider? culture = null)
    {
        if (!item.Enabled)
        {
            return null;
        }

        if (item.FireAt is { } fireAt)
        {
            return OneTimeCompleted(item)
                ? null
                : Relative(fireAt, now, approximate: false, RelativeDirection.Future, culture);
        }

        var approximate = item.JitterMinutes > 0;
        if (ScheduleFrequencies.ParseCron(item.CronExpression) is { } parsed)
        {
            var next = NextOccurrence(parsed, now);
            return Relative(next, now, approximate, RelativeDirection.Future, culture);
        }

        return item.NextRunAt is { } reported
            ? Relative(reported, now, approximate, RelativeDirection.Future, culture)
            : null;
    }

    /// <summary>
    /// When a preset next fires, ported from the inner function of the
    /// reference's <c>gh</c>. Hourly rolls to the next hour at the minute it
    /// already reached; weekdays scans forward for the first Monday-to-Friday
    /// slot still ahead.
    /// </summary>
    public static DateTimeOffset NextOccurrence(ParsedSchedule schedule, DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        var offset = now.Offset;
        DateTimeOffset At(DateTime moment) => new(DateTime.SpecifyKind(moment, DateTimeKind.Unspecified), offset);

        switch (schedule.Frequency)
        {
            case ScheduleFrequency.Hourly:
            {
                var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
                if (local.Minute >= schedule.Minute)
                {
                    start = start.AddHours(1);
                }

                return At(start.AddMinutes(schedule.Minute));
            }

            case ScheduleFrequency.Daily:
            {
                var slot = new DateTime(local.Year, local.Month, local.Day, schedule.Hour, schedule.Minute, 0);
                return At(slot <= local ? slot.AddDays(1) : slot);
            }

            case ScheduleFrequency.Weekdays:
            {
                for (var day = 0; day <= 7; day++)
                {
                    var slot = new DateTime(local.Year, local.Month, local.Day, schedule.Hour, schedule.Minute, 0)
                        .AddDays(day);
                    if (slot.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday && slot > local)
                    {
                        return At(slot);
                    }
                }

                return At(new DateTime(local.Year, local.Month, local.Day, schedule.Hour, schedule.Minute, 0)
                    .AddDays(1));
            }

            case ScheduleFrequency.Weekly:
            {
                var ahead = (schedule.DayOfWeek - (int)local.DayOfWeek + 7) % 7;
                var slot = new DateTime(local.Year, local.Month, local.Day, schedule.Hour, schedule.Minute, 0)
                    .AddDays(ahead);
                return At(slot <= local ? slot.AddDays(7) : slot);
            }

            default:
                return now;
        }
    }

    // ---- list --------------------------------------------------------------

    /// <summary>
    /// The line under a row's name, the reference's <c>xn</c>: the description,
    /// or the prompt when there is none, whitespace-collapsed and cut at 280.
    /// </summary>
    public static string? DescriptionPreview(ScheduledItem item)
    {
        var source = item.Description is { Length: > 0 } description ? description : item.Prompt;
        if (source.Length == 0)
        {
            return null;
        }

        var collapsed = Regex.Replace(source, @"\s+", " ").Trim();
        if (collapsed.Length == 0)
        {
            return null;
        }

        return collapsed.Length > 280 ? collapsed[..279] + "…" : collapsed;
    }

    /// <summary>
    /// The run count at the end of a row, and in the sidebar. The reference
    /// ships this as an ICU plural, and carrying the literal is what lets the
    /// parity suite pin it by message id — a resolved "3 runs" matches no
    /// catalogue entry.
    /// </summary>
    public const string RunCountMessage = "{count, plural, one {# run} other {# runs}}";

    /// <summary>The routine count beside the list's controls.</summary>
    public const string RoutineCountMessage = "{count, plural, one {# routine} other {# routines}}";

    public static string RunCountLabel(int count) => IcuMessages.Format(RunCountMessage, "count", count);

    public static string RoutineCountLabel(int count) =>
        IcuMessages.Format(RoutineCountMessage, "count", count);

    /// <summary>
    /// The reference's <c>mr</c>: keep the rows whose status the filter admits
    /// and whose name or description contains the query, case-insensitively.
    /// </summary>
    public static IReadOnlyList<ScheduledItem> Filter(
        IReadOnlyList<ScheduledItem> items,
        string query,
        ScheduledStatus? status)
    {
        var needle = query.Trim().ToLowerInvariant();
        return
        [
            .. items.Where(item =>
                (status is null || Status(item) == status) &&
                (needle.Length == 0 ||
                 DisplayName(item).ToLowerInvariant().Contains(needle, StringComparison.Ordinal) ||
                 item.Description.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))),
        ];
    }

    /// <summary>
    /// The reference's <c>ns</c>: by name, or by when each next runs with
    /// everything not running sorted to the end.
    /// </summary>
    public static IReadOnlyList<ScheduledItem> Sort(
        IReadOnlyList<ScheduledItem> items,
        ScheduledSort sort,
        DateTimeOffset now)
    {
        if (sort == ScheduledSort.Name)
        {
            return [.. items.OrderBy(DisplayName, StringComparer.CurrentCulture)];
        }

        return [.. items.OrderBy(item => SortKey(item, now))];
    }

    private static DateTimeOffset SortKey(ScheduledItem item, DateTimeOffset now)
    {
        if (!item.Enabled)
        {
            return DateTimeOffset.MaxValue;
        }

        if (item.FireAt is { } fireAt)
        {
            return fireAt;
        }

        if (ScheduleFrequencies.ParseCron(item.CronExpression) is { } parsed)
        {
            return NextOccurrence(parsed, now);
        }

        return item.NextRunAt ?? DateTimeOffset.MaxValue;
    }

    /// <summary>
    /// The line an empty result prints, which depends on what emptied it —
    /// three different sentences in the reference, and the caller has to have
    /// asked the right one.
    /// </summary>
    public static string EmptyResultMessage(bool searching, bool filtering) =>
        searching && filtering ? "No routines match your search and filters."
        : searching ? "No routines match your search."
        : "No routines match these filters.";
}
