using JarvisCode.Core.Agent;
using JarvisCode.Host;

namespace JarvisCode.App.Services;

/// <summary>
/// The one prompt-form decision the reference cannot make for us: which system
/// prompt a model on another provider receives.
/// </summary>
/// <remarks>
/// <para>
/// The reference picks between its lean <c># Harness</c> document and its
/// classic <c># System</c> one by family and version, and every id it routes is
/// a Claude one — so for a model it has never seen it has no answer, only a
/// default. That default is the classic form, and this setting is the seam that
/// lets the user say otherwise per installation.
/// </para>
/// <para>
/// It applies to a model the reference's own tables cannot place <i>at all</i>:
/// no catalog row, and nothing a <c>claude-{family}-{version}</c> canonical name
/// can be read out of. A Claude model always keeps the form the reference gives
/// it, and so does a Claude id the catalog does not yet name — a hypothetical
/// <c>claude-sonnet-9</c> still reads as a sonnet, and the reference's family
/// rule answers for it.
/// </para>
/// <para>
/// The default is <see cref="Classic"/> on tier grounds rather than caution: the
/// lean document marks a model its vendor tuned for the shorter form rather than
/// a capability tier, and <c>claude-sonnet-5</c> is the control — same vendor,
/// same generation, frontier tier, 1M context, adaptive thinking — which the
/// reference still sends the classic form. A third-party flagship is at best
/// that model's peer.
/// </para>
/// </remarks>
public static class PromptFormPreference
{
    /// <summary>The reference's answer for a model it does not recognise.</summary>
    public const string Classic = "classic";

    /// <summary>The shorter <c># Harness</c> document.</summary>
    public const string Lean = "lean";

    private static Func<string?>? configured;

    /// <summary>
    /// Installs the setting reader. Read through a delegate rather than
    /// snapshotted, so saving the Settings page is felt on the next turn without
    /// a restart.
    /// </summary>
    public static void Install(SettingsService settings) =>
        configured = () => settings.Current.OtherProviderPromptForm;

    /// <summary>Whether a stored value names the lean document.</summary>
    public static bool IsLean(string? form) =>
        string.Equals(form?.Trim(), Lean, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this model takes the lean document because the user configured
    /// it for other providers. False for every model the reference can place,
    /// and false until <see cref="Install"/> has run — which is what keeps a
    /// headless run and the tests on the reference's own default.
    /// </summary>
    internal static bool LeanForOtherProvider(string modelId) =>
        LeanForOtherProvider(modelId, configured?.Invoke());

    /// <param name="form">The configured value; the process setting in every caller but the tests.</param>
    /// <inheritdoc cref="LeanForOtherProvider(string)"/>
    internal static bool LeanForOtherProvider(string modelId, string? form) =>
        IsLean(form) && IsOtherProviderModel(modelId);

    /// <summary>
    /// Whether the reference's tables can place this id at all — its catalog
    /// names it, or a canonical <c>claude-{family}-{version}</c> reads out of
    /// it. Anything else came from another provider.
    /// </summary>
    internal static bool IsOtherProviderModel(string modelId) =>
        !string.IsNullOrWhiteSpace(modelId) &&
        ContextWindows.CanonicalModelName(modelId).Length == 0 &&
        PromptModelProfile.Lookup(modelId).Capabilities is null;
}
