using System.Globalization;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The frequencies the Scheduled editor offers, in the reference's own order
/// (its <c>ra</c> in <c>c0243d234-BOJof1xz.js</c>, with <c>fireAt</c> prepended
/// only for a task that already carries a one-time moment).
/// </summary>
public enum ScheduleFrequency
{
    /// <summary>The reference's "once": no schedule at all, run by hand.</summary>
    Manual,

    /// <summary>Its "fireAt": one run at a chosen moment, then the routine ends.</summary>
    OneTime,

    Hourly,
    Daily,
    Weekdays,
    Weekly,

    /// <summary>A cron expression typed by hand.</summary>
    Custom,
}

/// <summary>What is wrong with a typed cron expression, in the reference's own four kinds.</summary>
public enum CronProblem
{
    None,
    Invalid,
    SubHourly,
    NumericOnly,
    SundayIsZero,
}

/// <summary>A cron expression the editor recognises as one of its presets.</summary>
public sealed record ParsedSchedule(ScheduleFrequency Frequency, int Hour, int Minute, int DayOfWeek);

/// <summary>
/// Frequency ⇄ cron, and the editor's cron validation — ported from the
/// reference's routine form (<c>c0243d234-BOJof1xz.js</c>: its <c>ve</c>
/// validator and <c>ua</c> field checker) and its schedule helpers in
/// <c>shared-7-pSrlloWM.js</c> (<c>wh</c> builds, <c>bh</c> parses).
///
/// One measured departure: the reference converts the chosen local time to UTC
/// when it builds a cron, and back when it parses one, because its routine
/// triggers are evaluated server-side in UTC. Every cron this app stores is
/// evaluated by <see cref="CronSchedule"/> in LOCAL time — which is also what
/// the reference's own <c>create_scheduled_task</c> doc promises of the
/// SKILL.md store ("Cron is evaluated in the user's LOCAL timezone, not UTC") —
/// so the shift is deliberately not carried. Applying it would make "0 9 * * *"
/// fire at 9am UTC on a page that says 9:00.
/// </summary>
public static class ScheduleFrequencies
{
    /// <summary>The reference's frequency row, in its order, without the one-time entry.</summary>
    public static readonly IReadOnlyList<ScheduleFrequency> Recurring =
    [
        ScheduleFrequency.Manual,
        ScheduleFrequency.Hourly,
        ScheduleFrequency.Daily,
        ScheduleFrequency.Weekdays,
        ScheduleFrequency.Weekly,
        ScheduleFrequency.Custom,
    ];

    /// <summary>
    /// The row as the editor draws it: the reference prepends "One-time" only
    /// when the task being edited already has a one-time moment, because that
    /// frequency cannot be chosen for a new one.
    /// </summary>
    public static IReadOnlyList<ScheduleFrequency> Offered(bool hasFireAt) =>
        hasFireAt ? [ScheduleFrequency.OneTime, .. Recurring] : Recurring;

    public static string Label(ScheduleFrequency frequency) => frequency switch
    {
        ScheduleFrequency.Manual => "Manual",
        ScheduleFrequency.OneTime => "One-time",
        ScheduleFrequency.Hourly => "Hourly",
        ScheduleFrequency.Daily => "Daily",
        ScheduleFrequency.Weekdays => "Weekdays",
        ScheduleFrequency.Weekly => "Weekly",
        ScheduleFrequency.Custom => "Custom",
        _ => frequency.ToString(),
    };

    /// <summary>Only these three frequencies show the time (and, for weekly, the day) controls.</summary>
    public static bool NeedsTimeOfDay(ScheduleFrequency frequency) =>
        frequency is ScheduleFrequency.Daily or ScheduleFrequency.Weekdays or ScheduleFrequency.Weekly;

    /// <summary>The jitter note rides every frequency that actually recurs.</summary>
    public static bool ShowsStaggerNote(ScheduleFrequency frequency) =>
        frequency is not (ScheduleFrequency.Manual or ScheduleFrequency.OneTime);

    /// <summary>
    /// The cron a preset frequency produces, or null for the two that carry no
    /// cron at all. Custom returns null because the typed expression is the value.
    /// </summary>
    public static string? BuildCron(ScheduleFrequency frequency, int hour, int minute, int dayOfWeek) =>
        frequency switch
        {
            ScheduleFrequency.Hourly => $"{minute} * * * *",
            ScheduleFrequency.Daily => $"{minute} {hour} * * *",
            ScheduleFrequency.Weekdays => $"{minute} {hour} * * 1-5",
            ScheduleFrequency.Weekly => $"{minute} {hour} * * {dayOfWeek}",
            _ => null,
        };

    /// <summary>
    /// Reads a cron back into a preset, or null when it is not one of them —
    /// which is what puts the editor on "Custom" (the reference's own rule:
    /// <c>frequency ?? (cronExpression ? "custom" : …)</c>).
    /// </summary>
    public static ParsedSchedule? ParseCron(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return null;
        }

        var (minuteField, hourField, dayOfMonth, month, dayOfWeek) =
            (fields[0], fields[1], fields[2], fields[3], fields[4]);
        if (!IsWholeNumber(minuteField) || !int.TryParse(minuteField, out var minute) || minute is < 0 or > 59)
        {
            return null;
        }

        // Hourly is the one preset that leaves every other field open.
        if (hourField == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return new ParsedSchedule(ScheduleFrequency.Hourly, 9, minute, 1);
        }

        if (dayOfMonth != "*" || month != "*" || !IsWholeNumber(hourField) ||
            !int.TryParse(hourField, out var hour) || hour is < 0 or > 23)
        {
            return null;
        }

        if (dayOfWeek == "*")
        {
            return new ParsedSchedule(ScheduleFrequency.Daily, hour, minute, 1);
        }

        // The reference recognises a weekday range only as five consecutive days;
        // anything else stays custom.
        var weekdayRange = Regex.Match(dayOfWeek, "^([0-6])-([0-6])$");
        if (weekdayRange.Success)
        {
            var start = int.Parse(weekdayRange.Groups[1].Value, CultureInfo.InvariantCulture);
            var end = int.Parse(weekdayRange.Groups[2].Value, CultureInfo.InvariantCulture);
            return end - start == 4 && start == 1
                ? new ParsedSchedule(ScheduleFrequency.Weekdays, hour, minute, start)
                : null;
        }

        return IsWholeNumber(dayOfWeek) && int.TryParse(dayOfWeek, out var day) && day is >= 0 and <= 6
            ? new ParsedSchedule(ScheduleFrequency.Weekly, hour, minute, day)
            : null;
    }

    /// <summary>
    /// The editor's validation, in the reference's order — which is what decides
    /// which of the four sentences the user reads. Its first pass rejects the
    /// shape and a sub-hourly minute; only then does it complain about named
    /// fields, then about a 7 for Sunday, then about ranges.
    /// </summary>
    public static CronProblem Validate(string? expression)
    {
        var text = (expression ?? "").Trim();
        if (text.Length == 0)
        {
            return CronProblem.None;
        }

        var shape = ValidateShape(text);
        if (shape != CronProblem.None)
        {
            return shape;
        }

        // Everything past here is the reference narrowing a valid-looking cron
        // down to the subset its own evaluator accepts.
        if (!Regex.IsMatch(text, @"^[\d\s*,/-]+$"))
        {
            return CronProblem.NumericOnly;
        }

        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var dayOfWeekParts = fields[4]
            .Split(',')
            .SelectMany(part => part.Split('/')[0].Split('-'));
        if (dayOfWeekParts.Contains("7", StringComparer.Ordinal))
        {
            return CronProblem.SundayIsZero;
        }

        return FieldInRange(fields[0], 0, 59) && FieldInRange(fields[1], 0, 23) &&
               FieldInRange(fields[2], 1, 31) && FieldInRange(fields[3], 1, 12) &&
               FieldInRange(fields[4], 0, 6)
            ? CronProblem.None
            : CronProblem.Invalid;
    }

    /// <summary>
    /// The reference's first pass (its <c>l</c> in <c>c0958e5bf-DD4zNxrC.js</c>):
    /// five fields, no field opening with a step, and a minute that names one
    /// minute of the hour — anything else runs more than hourly.
    /// </summary>
    private static CronProblem ValidateShape(string text)
    {
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 || fields.Any(static f => f.StartsWith('/')))
        {
            return CronProblem.Invalid;
        }

        if (!IsWholeNumber(fields[0]))
        {
            return CronProblem.SubHourly;
        }

        if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minute) ||
            minute is < 0 or > 59)
        {
            return CronProblem.Invalid;
        }

        // The reference hands the expression to its cron describer here and calls
        // a throw "invalid". The describer accepts named days and months, which
        // is exactly why the NumericOnly check downstream can still fire — so the
        // check here has to accept them too, or it would answer "invalid" for a
        // cron the reference names more precisely.
        return Describable(fields) ? CronProblem.None : CronProblem.Invalid;
    }

    private static readonly string[] DayNames =
        ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>
    /// Whether a cron describer could read this at all: every token is a number,
    /// a wildcard, one of the crontab symbols, or a three-letter day or month.
    /// </summary>
    private static bool Describable(string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            foreach (var item in fields[i].Split(','))
            {
                var value = item.Split('/')[0];
                foreach (var bound in value.Split('-'))
                {
                    if (!IsDescribableToken(bound, i))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool IsDescribableToken(string token, int fieldIndex)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (token is "*" or "?" or "L" or "LW" or "W")
        {
            return true;
        }

        if (token.All(char.IsAsciiDigit))
        {
            return true;
        }

        // "5#2" (the second Friday) and "15W" are crontab extensions the describer
        // reads and the reference then rejects with its numeric-fields sentence.
        var head = token.TrimEnd('W');
        if (head.Length > 0 && head.All(char.IsAsciiDigit))
        {
            return true;
        }

        var hash = token.Split('#');
        if (hash.Length == 2 && hash.All(static p => p.Length > 0 && p.All(char.IsAsciiDigit)))
        {
            return true;
        }

        var names = fieldIndex switch
        {
            3 => MonthNames,
            4 => DayNames,
            _ => [],
        };
        return names.Contains(token.ToLowerInvariant(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The reference's <c>ua</c>: a field is in range when it is a wildcard, or
    /// every item of a list is, or a step whose base is, or a range inside the
    /// bounds, or a number inside them.
    /// </summary>
    internal static bool FieldInRange(string field, int min, int max)
    {
        if (field == "*")
        {
            return true;
        }

        if (field.Contains(','))
        {
            return field.Split(',').All(part => FieldInRange(part.Trim(), min, max));
        }

        if (field.Contains('/'))
        {
            var parts = field.Split('/');
            return parts.Length == 2 &&
                   int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var step) &&
                   step >= 1 &&
                   (parts[0] == "*" || FieldInRange(parts[0], min, max));
        }

        if (field.Contains('-'))
        {
            var bounds = field.Split('-');
            return bounds.Length == 2 &&
                   int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from) &&
                   int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var to) &&
                   from >= min && to <= max && from <= to;
        }

        return int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
               value >= min && value <= max;
    }

    private static bool IsWholeNumber(string field) =>
        field.Length > 0 && field.All(char.IsAsciiDigit);

    /// <summary>The sentence the editor prints under the cron box.</summary>
    public static string? ProblemMessage(CronProblem problem) => problem switch
    {
        CronProblem.Invalid => "Invalid cron expression. Check the format (for example: 0 9 * * *).",
        CronProblem.SubHourly => "Schedules must run at most once per hour.",
        CronProblem.NumericOnly =>
            "Use numeric fields only (for example 1 for Monday). Named days/months and the ? L W # symbols aren’t supported here.",
        CronProblem.SundayIsZero => "Use 0 for Sunday (7 isn’t supported here).",
        _ => null,
    };
}
