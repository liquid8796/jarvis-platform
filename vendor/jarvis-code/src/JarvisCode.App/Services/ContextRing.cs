using System.Globalization;

namespace JarvisCode.App.Services;

/// <summary>How full the ring is, in the reference's three colour tiers.</summary>
public enum ContextRingTier
{
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// The composer chin's context ring (desktop 1.40609.1.0: `Yg` in the ccd chunk, the
/// `eI` ring and `tI` summary in shared-10-3). An icon-only button whose icon is a
/// 12px ring — radius (12−2)/2, stroke 2, drawn from twelve o'clock — coloured by
/// how full the context is: accent under 75%, warning from 75%, danger from 90%.
/// Its tooltip is "Context {summary}", where the summary reads
/// "{used} / {max} ({pct}%)" with the reference's k/M/B token formatting. Pure so
/// the numbers and the geometry are unit-testable.
/// </summary>
public static class ContextRing
{
    public const double Size = 12;
    public const double StrokeWidth = 2;
    public static double Radius => (Size - StrokeWidth) / 2;
    public static double Circumference => 2 * Math.PI * Radius;

    /// <summary>The reference's `Wx`: ≥90 critical, ≥75 warning, else normal.</summary>
    public static ContextRingTier Tier(double pct) =>
        pct >= 90 ? ContextRingTier.Critical : pct >= 75 ? ContextRingTier.Warning : ContextRingTier.Normal;

    /// <summary>The theme token the arc paints with.</summary>
    public static string StrokeBrushKey(ContextRingTier tier) => tier switch
    {
        ContextRingTier.Critical => "Danger100Brush",
        ContextRingTier.Warning => "Warning100Brush",
        _ => "AccentBrandBrush",
    };

    /// <summary>The dash offset that leaves `pct` of the circumference visible.</summary>
    public static double DashOffset(double pct) =>
        Circumference * (1 - Math.Max(0, Math.Min(100, pct)) / 100);

    /// <summary>
    /// The reference's `tI`: "{used} / {max} ({pct}%)", or just the used count when
    /// the window is unknown or exceeded. The percentage is rounded half-up and
    /// clamped to 0..100.
    /// </summary>
    public static (string Summary, int? Pct) Summarize(long used, long? max, bool allowOver = false)
    {
        if (max is null || (used > max && !allowOver))
        {
            return (Format(used), null);
        }

        var pct = (int)Math.Round(100 * Math.Max(0, Math.Min(1, (double)used / max.Value)), MidpointRounding.AwayFromZero);
        return ($"{Format(used)} / {Format(max.Value)} ({pct}%)", pct);
    }

    /// <summary>The tooltip on the ring: "Context {summary}".</summary>
    public static string Tooltip(string summary) => $"Context {summary}";

    /// <summary>The ring's accessible name: "Usage · Context {summary}".</summary>
    public static string AccessibleName(string summary) => $"Usage · Context {summary}";

    /// <summary>
    /// The reference's `Vx`: one decimal at k/M/B with a trailing ".0" dropped,
    /// carrying over to the next unit at 1000 ("999.9k", then "1M").
    /// </summary>
    public static string Format(long count)
    {
        if (count >= 1_000_000)
        {
            var millions = Math.Round(count / 1_000_000.0, 1, MidpointRounding.AwayFromZero);
            return millions >= 1000
                ? Trim(Math.Round(count / 1_000_000_000.0, 1, MidpointRounding.AwayFromZero)) + "B"
                : Trim(millions) + "M";
        }

        if (count >= 1000)
        {
            var thousands = Math.Round(count / 1000.0, 1, MidpointRounding.AwayFromZero);
            return thousands >= 1000
                ? Trim(Math.Round(count / 1_000_000.0, 1, MidpointRounding.AwayFromZero)) + "M"
                : Trim(thousands) + "k";
        }

        return count.ToString(CultureInfo.InvariantCulture);
    }

    private static string Trim(double value)
    {
        var text = value.ToString("0.0", CultureInfo.InvariantCulture);
        return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
