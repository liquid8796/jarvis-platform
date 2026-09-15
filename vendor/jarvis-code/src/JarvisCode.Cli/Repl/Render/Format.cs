using System.Globalization;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The reference's small formatters (CLI 2.1.257 chunk <c>@182394124</c>):
/// <c>Ft</c> for a duration, <c>lse</c> for a one-decimal seconds figure,
/// <c>Jo</c> for a compact token count, <c>cse</c> for a fold line, <c>Li</c>
/// for a folded multi-line result, and <c>k_</c> for a relative timestamp.
/// </summary>
internal static class Format
{
    /// <summary>The reference's <c>Ft</c>.</summary>
    public static string Duration(TimeSpan span, bool mostSignificantOnly = false, bool hideTrailingZeros = false)
    {
        double ms = span.TotalMilliseconds;
        if (ms < 60000)
        {
            if (ms == 0)
            {
                return "0s";
            }

            if (ms < 1)
            {
                return (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s";
            }

            return Math.Floor(ms / 1000).ToString(CultureInfo.InvariantCulture) + "s";
        }

        long days = (long)Math.Floor(ms / 86400000);
        long hours = (long)Math.Floor(ms % 86400000 / 3600000);
        long minutes = (long)Math.Floor(ms % 3600000 / 60000);
        long seconds = (long)Math.Round(ms % 60000 / 1000, MidpointRounding.AwayFromZero);
        if (seconds == 60)
        {
            seconds = 0;
            minutes++;
        }

        if (minutes == 60)
        {
            minutes = 0;
            hours++;
        }

        if (hours == 24)
        {
            hours = 0;
            days++;
        }

        if (mostSignificantOnly)
        {
            if (days > 0) return $"{days}d";
            if (hours > 0) return $"{hours}h";
            if (minutes > 0) return $"{minutes}m";
            return $"{seconds}s";
        }

        if (days > 0)
        {
            if (hideTrailingZeros && hours == 0 && minutes == 0) return $"{days}d";
            if (hideTrailingZeros && minutes == 0) return $"{days}d {hours}h";
            return $"{days}d {hours}h {minutes}m";
        }

        if (hours > 0)
        {
            if (hideTrailingZeros && minutes == 0 && seconds == 0) return $"{hours}h";
            if (hideTrailingZeros && seconds == 0) return $"{hours}h {minutes}m";
            return $"{hours}h {minutes}m {seconds}s";
        }

        if (minutes > 0)
        {
            if (hideTrailingZeros && seconds == 0) return $"{minutes}m";
            return $"{minutes}m {seconds}s";
        }

        return $"{seconds}s";
    }

    /// <summary>The reference's <c>lse</c>: hook durations.</summary>
    public static string Seconds(double ms) => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// <summary>
    /// The reference's <c>Jo</c>: <c>Intl.NumberFormat</c> compact notation with
    /// one fraction digit, lowercased — 950 → "950", 9500 → "9.5k", 10000 → "10.0k",
    /// 1200000 → "1.2m".
    /// </summary>
    public static string Tokens(long count)
    {
        if (count < 1000)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        if (count < 1_000_000)
        {
            return (count / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }

        if (count < 1_000_000_000)
        {
            return (count / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }

        return (count / 1_000_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "b";
    }

    /// <summary>The reference's <c>Fn</c>: a plain grouped count ("12,345").</summary>
    public static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The reference's <c>cse</c>: "… +N lines".</summary>
    public static string Fold(int hidden, string unit = "line") =>
        hidden <= 0 ? "" : $"… +{hidden} {Plural(hidden, unit)}";

    public static string Plural(int count, string singular, string? plural = null) =>
        count == 1 ? singular : plural ?? singular + "s";

    public const int ResultMaxChars = 200;
    public const int ResultMaxLines = 5;

    /// <summary>
    /// The reference's <c>Li</c>: the non-empty trimmed lines of a result, up to
    /// <paramref name="maxLines"/> (its <c>jf + 1</c>), each cut to
    /// <paramref name="maxChars"/> with an ellipsis, then "… +N more lines".
    /// </summary>
    public static IReadOnlyList<string> FoldLines(string text, int maxChars = ResultMaxChars, int maxLines = ResultMaxLines + 1, bool preserveLayout = false)
    {
        List<string> lines;
        if (preserveLayout)
        {
            var all = text.Split('\n');
            int first = Array.FindIndex(all, l => l.Trim().Length > 0);
            int last = Array.FindLastIndex(all, l => l.Trim().Length > 0);
            lines = first < 0 ? [] : all[first..(last + 1)].Select(l => l.TrimEnd() is { Length: > 0 } t ? t : " ").ToList();
        }
        else
        {
            lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        var shown = lines.Take(maxLines).Select(line =>
        {
            var cut = TextWidth.Truncate(line, maxChars);
            return cut.Length < line.Length ? cut + "…" : line;
        }).ToList();
        int hidden = lines.Count - shown.Count;
        if (hidden > 0)
        {
            shown.Add($"… +{hidden} more {Plural(hidden, "line")}");
        }

        return shown;
    }

    /// <summary>The reference's <c>k_</c> relative time in its short style ("3m ago", "2d ago", "in 5m").</summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset now)
    {
        var delta = now - when;
        bool past = delta >= TimeSpan.Zero;
        var abs = past ? delta : -delta;
        string unit;
        long value;
        if (abs.TotalSeconds < 60)
        {
            value = (long)abs.TotalSeconds;
            unit = "s";
        }
        else if (abs.TotalMinutes < 60)
        {
            value = (long)abs.TotalMinutes;
            unit = "m";
        }
        else if (abs.TotalHours < 24)
        {
            value = (long)abs.TotalHours;
            unit = "h";
        }
        else if (abs.TotalDays < 30)
        {
            value = (long)abs.TotalDays;
            unit = "d";
        }
        else if (abs.TotalDays < 365)
        {
            value = (long)(abs.TotalDays / 30);
            unit = "mo";
        }
        else
        {
            value = (long)(abs.TotalDays / 365);
            unit = "y";
        }

        return past ? $"{value}{unit} ago" : $"in {value}{unit}";
    }

    /// <summary>The reference's <c>Cd</c>: a wall-clock time like "3:05pm" or "Sep 2, 3:05pm" past a day.</summary>
    public static string ClockTime(DateTimeOffset when, DateTimeOffset now)
    {
        bool sameDay = (when - now).TotalHours <= 24;
        var minute = when.Minute == 0 ? "" : ":" + when.Minute.ToString("00", CultureInfo.InvariantCulture);
        int hour12 = when.Hour % 12 == 0 ? 12 : when.Hour % 12;
        var ampm = when.Hour < 12 ? "am" : "pm";
        var time = $"{hour12}{minute}{ampm}";
        if (sameDay)
        {
            return time;
        }

        var date = when.ToString("MMM d", CultureInfo.InvariantCulture);
        if (when.Year != now.Year)
        {
            date += ", " + when.Year;
        }

        return $"{date}, {time}";
    }

    /// <summary>The reference's cost figure: two decimals past fifty cents, four below.</summary>
    public static string Cost(double usd) =>
        "$" + (usd > 0.5 ? Math.Round(usd, 2).ToString("0.00", CultureInfo.InvariantCulture) : usd.ToString("0.0000", CultureInfo.InvariantCulture));
}
