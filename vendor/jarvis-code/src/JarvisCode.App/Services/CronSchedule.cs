using System.Globalization;

namespace JarvisCode.App.Services;

/// <summary>
/// A standard 5-field cron expression — <c>minute hour dayOfMonth month
/// dayOfWeek</c> — evaluated in the user's LOCAL time, which is what
/// mcp__scheduled-tasks__create_scheduled_task documents ("Cron is evaluated in
/// the user's LOCAL timezone, not UTC").
///
/// Supports <c>*</c>, a number, a list (<c>a,b</c>), a range (<c>a-b</c>) and a
/// step over either (<c>*<!---->/n</c>, <c>a-b/n</c>). Day-of-week accepts 0-7
/// with both 0 and 7 meaning Sunday. When day-of-month and day-of-week are both
/// restricted, a match on either fires — the Vixie-cron rule every crontab
/// implementation follows, and the one a user writing "0 9 1 * 1" expects.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronSchedule(string expression, string[] fields)
    {
        Expression = expression;
        Fill(_minutes, fields[0], 0, 59);
        Fill(_hours, fields[1], 0, 23);
        Fill(_daysOfMonth, fields[2], 1, 31);
        Fill(_months, fields[3], 1, 12);
        FillDaysOfWeek(fields[4]);
        _dayOfMonthRestricted = fields[2] != "*";
        _dayOfWeekRestricted = fields[4] != "*";
    }

    public string Expression { get; }

    /// <summary>
    /// How far ahead <see cref="NextAfter"/> looks before reporting "never".
    /// Five years, because the widest gap a legal expression can have is the four
    /// between leap days — two years would call "0 0 29 2 *" unschedulable from
    /// the wrong starting point.
    /// </summary>
    public static readonly TimeSpan SearchHorizon = TimeSpan.FromDays(366 * 5);

    /// <summary>Parses an expression, or returns null when it is not a valid 5-field cron.</summary>
    public static CronSchedule? TryParse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            return null;
        }

        try
        {
            return new CronSchedule(expression.Trim(), fields);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool Matches(DateTime local) =>
        _minutes[local.Minute] &&
        _hours[local.Hour] &&
        _months[local.Month] &&
        DayMatches(local);

    /// <summary>
    /// The first minute strictly after <paramref name="after"/> that matches, or
    /// null when nothing matches inside <see cref="SearchHorizon"/> (an
    /// expression like "0 0 30 2 *" never fires).
    /// </summary>
    public DateTime? NextAfter(DateTime after)
    {
        var candidate = new DateTime(
            after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var limit = after + SearchHorizon;
        while (candidate <= limit)
        {
            if (!_months[candidate.Month])
            {
                // Skip to the first minute of the next month rather than stepping
                // through every minute of one that can never match.
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, candidate.Kind).AddMonths(1);
                continue;
            }

            if (!DayMatches(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if (_minutes[candidate.Minute])
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);
        }

        return null;
    }

    private bool DayMatches(DateTime local)
    {
        var byMonth = _daysOfMonth[local.Day];
        var byWeek = _daysOfWeek[(int)local.DayOfWeek];

        // Both restricted: either one firing is enough. Otherwise the restricted
        // one decides and the unrestricted one is always true anyway.
        return _dayOfMonthRestricted && _dayOfWeekRestricted
            ? byMonth || byWeek
            : byMonth && byWeek;
    }

    /// <summary>
    /// Day-of-week is the one field with eight legal values for seven days: cron
    /// accepts 0-7 and both ends mean Sunday, so it is filled into an eight-slot
    /// array and folded down.
    /// </summary>
    private void FillDaysOfWeek(string field)
    {
        var slots = new bool[8];
        Fill(slots, field, 0, 7);
        for (var i = 0; i <= 7; i++)
        {
            if (slots[i])
            {
                _daysOfWeek[i % 7] = true;
            }
        }
    }

    private static void Fill(bool[] slots, string field, int min, int max)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var step = 1;
            var body = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                body = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) ||
                    step <= 0)
                {
                    throw new FormatException($"bad step in \"{part}\"");
                }
            }

            int from, to;
            if (body is "*")
            {
                (from, to) = (min, max);
            }
            else if (body.IndexOf('-') > 0)
            {
                var dash = body.IndexOf('-');
                from = Number(body[..dash], min, max);
                to = Number(body[(dash + 1)..], min, max);
                if (to < from)
                {
                    throw new FormatException($"reversed range \"{body}\"");
                }
            }
            else
            {
                from = to = Number(body, min, max);
                if (slash < 0)
                {
                    Mark(slots, from);
                    continue;
                }

                to = max;
            }

            for (var value = from; value <= to; value += step)
            {
                Mark(slots, value);
            }
        }
    }

    private static void Mark(bool[] slots, int value)
    {
        if (value >= 0 && value < slots.Length)
        {
            slots[value] = true;
        }
    }

    private static int Number(string text, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < min || value > max)
        {
            throw new FormatException($"\"{text}\" is outside {min}-{max}");
        }

        return value;
    }
}
