namespace JarvisCode.Core.Tools;

/// <summary>
/// Whether tool search engages at all, ported from CLI 2.1.257's own module
/// (its <c>eJe</c>/<c>sW</c>/<c>y_</c>, exported together around byte
/// 183761252). The reference has <em>no</em> size threshold: the mode defaults
/// to tool-search and every deferrable tool defers from the first turn, which
/// is what a live desktop session shows when it lists its deferred tools before
/// the agent roster.
/// </summary>
public static class ToolSearchAvailability
{
    /// <summary>The reference's own environment override.</summary>
    public const string EnableVariable = "ENABLE_TOOL_SEARCH";

    /// <summary>
    /// The models the reference refuses tool search for (its <c>E</c>). Matched
    /// as a case-insensitive substring of the model id, as its <c>sW</c> does,
    /// so a dated or vendor-prefixed spelling still resolves.
    /// </summary>
    public static readonly string[] UnsupportedModels = ["claude-3-5-haiku", "claude-3-haiku"];

    /// <summary>The reference's three modes (its <c>eJe</c>).</summary>
    public enum Mode
    {
        /// <summary>Tool search off; every tool rides the tools block.</summary>
        Standard,

        /// <summary>Tool search on — the reference's default.</summary>
        ToolSearch,

        /// <summary>Its <c>auto</c> rollout arm, which engages tool search here.</summary>
        ToolSearchAuto,
    }

    /// <summary>
    /// Its <c>AEn</c>: <c>auto:N</c> alone carries a number, clamped to [0, 100].
    /// Anything else — including a bare integer — is not a percentage at all.
    /// </summary>
    public static int? ParseAutoPercent(string? value)
    {
        if (value is null || !value.StartsWith("auto:", StringComparison.Ordinal))
            return null;

        return int.TryParse(value.AsSpan(5), out var parsed)
            ? Math.Max(0, Math.Min(100, parsed))
            : null;
    }

    /// <summary>Its <c>m</c>: the auto arm.</summary>
    private static bool IsAuto(string? value) =>
        !string.IsNullOrEmpty(value) &&
        (value == "auto" || value.StartsWith("auto:", StringComparison.Ordinal));

    private static bool IsTruthy(string? value) =>
        value is "1" or "true" or "yes" or "on" or "True" or "TRUE";

    private static bool IsFalsy(string? value) =>
        value is "0" or "false" or "no" or "off" or "False" or "FALSE";

    /// <summary>
    /// Its <c>eJe</c>, in its own order. The <c>ZXe()</c> arm ahead of it is not
    /// modelled: it reads a host capability this app has no counterpart for, and
    /// its absence only ever leaves the mode at the default the reference ships.
    /// </summary>
    public static Mode ResolveMode(string? enableToolSearch)
    {
        var percent = ParseAutoPercent(enableToolSearch);
        if (percent == 0)
            return Mode.ToolSearch;
        if (percent == 100)
            return Mode.Standard;
        if (IsAuto(enableToolSearch))
            return Mode.ToolSearchAuto;
        if (IsTruthy(enableToolSearch))
            return Mode.ToolSearch;
        if (IsFalsy(enableToolSearch))
            return Mode.Standard;

        return Mode.ToolSearch;
    }

    /// <summary>Its <c>sW</c>: the model carries tool search unless it is one of the two.</summary>
    public static bool ModelSupports(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return true;

        foreach (var unsupported in UnsupportedModels)
        {
            if (modelId.Contains(unsupported, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Its <c>y_()</c> and <c>sW</c> together: tool search engages unless the
    /// mode says standard or the model is one of the two it is refused for.
    ///
    /// Its remaining clause — off for a base URL that is not a first-party
    /// Anthropic host unless <c>ENABLE_TOOL_SEARCH</c> is set — is deliberately
    /// not carried. It exists there because its ToolSearch answers with
    /// <c>tool_reference</c> blocks the API expands server-side, so a proxy that
    /// does not forward them breaks the mechanism; this port writes the fetched
    /// schemas into the tool result itself and needs nothing forwarded, so the
    /// restriction would refuse deferral for a reason that cannot arise here.
    /// </summary>
    public static bool IsEnabled(string? modelId, string? enableToolSearch) =>
        ResolveMode(enableToolSearch) != Mode.Standard && ModelSupports(modelId);

    /// <summary>Reads the override from the environment.</summary>
    public static bool IsEnabled(string? modelId) =>
        IsEnabled(modelId, Environment.GetEnvironmentVariable(EnableVariable));
}
