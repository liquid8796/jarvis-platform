using System.Globalization;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Agent;

/// <summary>Where the auto-compact window came from (the reference's <c>GA</c> sources).</summary>
public enum AutoCompactWindowSource
{
    Env,
    Settings,
    ClientData,
    Experiment,
    ModelDefault,
    UnknownModel,
    Auto,
}

/// <summary>How full the context is, on the reference's four-rung ladder.</summary>
public enum ContextLevel
{
    Ok,
    Warn,
    Compact,
    Blocked,
}

/// <summary>The resolved auto-compact window: what is used, what was asked for, and why.</summary>
public readonly record struct AutoCompactWindow(int Window, int Configured, AutoCompactWindowSource Source)
{
    /// <summary>True once anything other than the model's own window decided it.</summary>
    public bool IsOverridden => Source != AutoCompactWindowSource.Auto;
}

/// <summary>
/// The reference CLI's context-window arithmetic, ported constant for constant
/// (2.1.251): the auto-compact window and where it came from, the effective
/// window once the model's output reserve is held back, the compaction
/// threshold, the precompute arm point, and the ok/warn/compact/blocked ladder.
/// The reference spells the same numbers as KYe/k4e/VYe/v9/ike/JNe/yU.
/// </summary>
public static class ContextWindows
{
    /// <summary>Tokens held back below the effective window before compaction fires (the reference's KYe).</summary>
    public const int CompactReserveTokens = 13_000;

    /// <summary>Ceiling on the output reserve subtracted from the window (k4e).</summary>
    public const int OutputReserveCap = 20_000;

    /// <summary>Tokens below the raw window at which a turn is refused outright (VYe).</summary>
    public const int BlockedReserveTokens = 3_000;

    /// <summary>Fraction of the window at which a background compact would be armed (v9).</summary>
    public const double PrecomputeBufferFraction = 0.2;

    /// <summary>Smallest window the setting accepts (ike).</summary>
    public const int MinConfigurableWindow = 100_000;

    /// <summary>Largest window the setting accepts (JNe).</summary>
    public const int MaxConfigurableWindow = 1_000_000;

    /// <summary>The window the reference pins recent models to when theirs is under 1M (yU).</summary>
    public const int ModelDefaultWindow = 200_000;

    /// <summary>The distance below the compaction threshold at which the reference warns.</summary>
    public const int WarnReserveTokens = 20_000;

    /// <summary>
    /// The reference's <c>fqe</c>: "auto", a token count, or a k/m suffix.
    /// A bare 100-1000 is shorthand for thousands. Out-of-range values return null.
    /// </summary>
    public static int? ParseWindow(string? text)
    {
        var trimmed = (text ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
            return null;

        double value;
        if (trimmed.EndsWith('m'))
        {
            if (!TryParseLeadingNumber(trimmed, out value))
                return null;
            value *= 1_000_000;
        }
        else if (trimmed.EndsWith('k'))
        {
            if (!TryParseLeadingNumber(trimmed, out value))
                return null;
            value *= 1_000;
        }
        else
        {
            if (!TryParseLeadingNumber(trimmed, out value))
                return null;
            if (value is >= 100 and <= 1000)
                value *= 1_000;
        }

        if (double.IsNaN(value) || double.IsInfinity(value) ||
            value < MinConfigurableWindow || value > MaxConfigurableWindow)
        {
            return null;
        }

        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The reference's <c>fqe</c> in full: "auto" answers true with no window,
    /// a token count answers true with one, and anything it cannot read answers
    /// false. <see cref="ParseWindow"/> alone cannot tell the first from the
    /// last, and the two lead to opposite messages.
    /// </summary>
    public static bool TryParseWindow(string? text, out int? window)
    {
        window = null;
        var trimmed = (text ?? "").Trim().ToLowerInvariant();
        if (trimmed == "auto")
            return true;

        window = ParseWindow(trimmed);
        return window is not null;
    }

    /// <summary>JavaScript's parseFloat: a leading number, trailing junk ignored.</summary>
    private static bool TryParseLeadingNumber(string text, out double value)
    {
        int end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '.' or '-' or '+'))
            end++;
        return double.TryParse(
            text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The reference's <c>Gn</c>: en-US compact notation at one fraction digit,
    /// lowercased, with the first ".0" removed - 200000 becomes "200k",
    /// 1000000 becomes "1m", 187500 becomes "187.5k", 500 stays "500".
    /// </summary>
    public static string FormatTokens(long tokens)
    {
        bool keepFraction = Math.Abs(tokens) >= 1000;
        string text = CompactNotation(tokens, keepFraction).ToLowerInvariant();
        int dotZero = text.IndexOf(".0", StringComparison.Ordinal);
        return dotZero < 0 ? text : text.Remove(dotZero, 2);
    }

    /// <summary>The reference's <c>uR</c>: "&lt; 20" under twenty, else "~" and the nearest ten.</summary>
    public static string FormatApproxTokens(long tokens) =>
        tokens < 20
            ? "< 20"
            : "~" + FormatTokens((long)Math.Round(tokens / 10.0, MidpointRounding.AwayFromZero) * 10);

    private static string CompactNotation(long value, bool minimumOneFractionDigit)
    {
        (double Scale, string Suffix)[] units =
        [
            (1e3, "K"),
            (1e6, "M"),
            (1e9, "B"),
            (1e12, "T"),
        ];

        double magnitude = Math.Abs(value);
        int unit = -1;
        for (int i = units.Length - 1; i >= 0; i--)
        {
            if (magnitude >= units[i].Scale)
            {
                unit = i;
                break;
            }
        }

        if (unit < 0)
            return Number(value, minimumOneFractionDigit);

        double scaled = Math.Round(value / units[unit].Scale, 1, MidpointRounding.AwayFromZero);
        // Rounding can carry a value up into the next unit (999,999 -> 1000.0K -> 1M).
        if (Math.Abs(scaled) >= 1000 && unit + 1 < units.Length)
        {
            unit++;
            scaled = Math.Round(value / units[unit].Scale, 1, MidpointRounding.AwayFromZero);
        }

        return Number(scaled, minimumOneFractionDigit) + units[unit].Suffix;
    }

    private static string Number(double value, bool minimumOneFractionDigit) =>
        value.ToString(minimumOneFractionDigit ? "0.0" : "0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// The models the reference pins to <see cref="ModelDefaultWindow"/> when
    /// their own window is under 1M (its <c>PZt</c> set).
    /// </summary>
    private static readonly IReadOnlySet<string> PinnedToModelDefault =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "claude-sonnet-4-6", "claude-opus-4-6", "claude-opus-4-8", "claude-opus-5",
        };

    /// <summary>
    /// The reference's per-model default table (its <c>QYe</c>). Only the
    /// surface-independent default is carried: the remote_cowork/local-agent
    /// overrides beside it name surfaces this app does not have.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> ModelDefaultWindows =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["claude-sonnet-5"] = MaxConfigurableWindow,
            // The fable/mythos entries of the 2.1.257 catalog carry
            // context:{window:1e6, native_1m:true}.
            ["claude-fable-5"] = MaxConfigurableWindow,
            ["claude-fable-5-1"] = MaxConfigurableWindow,
            ["claude-mythos-5"] = MaxConfigurableWindow,
            ["claude-mythos-5-1"] = MaxConfigurableWindow,
        };

    /// <summary>The reference's switch off unknown-model enforcement.</summary>
    public const string DisableUnknownModelEnforcementVariable = "CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT";

    /// <summary>
    /// The reference's <c>Xe</c>, reduced to what the tables above need: the
    /// bare "claude-{family}-{major}[-{minor}]" an id carries, with a provider
    /// prefix, a date stamp and an "@" or ":" suffix dropped. A second number
    /// above 20 is a date stamp rather than a minor version, which is the same
    /// rule the effort classifier applies.
    /// </summary>
    public static string CanonicalModelName(string? modelId)
    {
        var match = CanonicalName.Match(modelId ?? "");
        if (!match.Success)
            return "";

        string name = $"claude-{match.Groups[1].Value}-{match.Groups[2].Value}";
        return match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out int minor) && minor <= 20
            ? $"{name}-{minor}"
            : name;
    }

    private static readonly Regex CanonicalName = new(
        @"(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The reference's <c>GA</c>: the window in force, capped by the model's own
    /// window, and the source that decided it. Priority is env, then the
    /// setting, then the per-model default, then auto.
    /// </summary>
    public static AutoCompactWindow ResolveWindow(
        int modelWindow,
        int? configuredWindow,
        string? environmentWindow = null,
        string? modelId = null,
        bool modelIsRecognized = true)
    {
        if (!string.IsNullOrWhiteSpace(environmentWindow) && ParseWindow(environmentWindow) is { } fromEnv)
        {
            int clamped = Math.Max(MinConfigurableWindow, fromEnv);
            return new AutoCompactWindow(Math.Min(modelWindow, clamped), clamped, AutoCompactWindowSource.Env);
        }

        if (configuredWindow is { } fromSettings)
        {
            return new AutoCompactWindow(
                Math.Min(modelWindow, fromSettings), fromSettings, AutoCompactWindowSource.Settings);
        }

        string canonical = CanonicalModelName(modelId);
        if (modelWindow < MaxConfigurableWindow && PinnedToModelDefault.Contains(canonical))
        {
            return new AutoCompactWindow(
                Math.Min(modelWindow, ModelDefaultWindow), ModelDefaultWindow, AutoCompactWindowSource.ModelDefault);
        }

        if (ModelDefaultWindows.TryGetValue(canonical, out int tabled))
        {
            return new AutoCompactWindow(
                Math.Min(modelWindow, tabled), tabled, AutoCompactWindowSource.ModelDefault);
        }

        // An unrecognized model is enforced at its declared window (the reference's
        // "unknown-model" source; its CLAUDE_CODE_MAX_CONTEXT_TOKENS is this app's
        // catalog entry), unless the reference's own switch turns that off.
        if (!modelIsRecognized &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DisableUnknownModelEnforcementVariable)))
            return new AutoCompactWindow(modelWindow, modelWindow, AutoCompactWindowSource.UnknownModel);

        return new AutoCompactWindow(modelWindow, modelWindow, AutoCompactWindowSource.Auto);
    }

    /// <summary>
    /// The reference's <c>nF</c>: the window minus the model's output reserve,
    /// itself capped at <see cref="OutputReserveCap"/>.
    /// </summary>
    public static long EffectiveWindow(int window, int maxOutputTokens) =>
        window - Math.Min(Math.Max(0, maxOutputTokens), OutputReserveCap);

    /// <summary>
    /// The reference's <c>E9</c>: the effective window less the 13k reserve, or
    /// the smaller of that and a percentage override when one is set.
    /// </summary>
    public static long CompactThreshold(long effectiveWindow, double? percentOverride = null)
    {
        long reserved = effectiveWindow - CompactReserveTokens;
        if (percentOverride is > 0 and <= 100 && !double.IsNaN(percentOverride.Value))
            return Math.Min((long)Math.Floor(effectiveWindow * (percentOverride.Value / 100.0)), reserved);
        return reserved;
    }

    /// <summary>The reference's <c>nhe</c>: where a background compact would be armed.</summary>
    public static long PrecomputeArmThreshold(
        long effectiveWindow,
        double fraction = PrecomputeBufferFraction,
        double? percentOverride = null) =>
        Math.Min(
            effectiveWindow - (long)Math.Round(effectiveWindow * fraction, MidpointRounding.AwayFromZero),
            CompactThreshold(effectiveWindow, percentOverride));

    /// <summary>
    /// The reference's <c>rhe</c>: the ok/warn/compact/blocked ladder plus the
    /// percentage of the working window still free.
    /// </summary>
    public static (ContextLevel Level, int PercentLeft) Classify(
        long usedTokens,
        long effectiveWindow,
        long rawEffectiveWindow,
        bool autoCompactEnabled,
        double? percentOverride = null)
    {
        long compactAt = CompactThreshold(effectiveWindow, percentOverride);
        long ceiling = autoCompactEnabled ? compactAt : effectiveWindow;
        long warnAt = ceiling - WarnReserveTokens;
        long blockedAt = rawEffectiveWindow - BlockedReserveTokens;
        int percentLeft = (int)Math.Max(0, Math.Round(
            (ceiling - usedTokens) / (double)Math.Max(1, ceiling) * 100, MidpointRounding.AwayFromZero));

        if (usedTokens >= blockedAt)
            return (ContextLevel.Blocked, percentLeft);
        if (autoCompactEnabled && usedTokens >= compactAt)
            return (ContextLevel.Compact, percentLeft);
        if (usedTokens >= warnAt)
            return (ContextLevel.Warn, percentLeft);
        return (ContextLevel.Ok, 0);
    }
}
