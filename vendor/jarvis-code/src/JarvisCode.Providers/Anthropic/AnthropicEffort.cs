using System.Text.RegularExpressions;
using JarvisCode.Core.Providers;

namespace JarvisCode.Providers.Anthropic;

/// <summary>How a model takes extended thinking on the Anthropic wire.</summary>
public enum ThinkingWire
{
    /// <summary>No <c>thinking</c> field at all (the Claude 3 generation).</summary>
    None,

    /// <summary><c>thinking: {"type":"enabled","budget_tokens":31999}</c>.</summary>
    Enabled,

    /// <summary><c>thinking: {"type":"adaptive"}</c>.</summary>
    Adaptive,
}

/// <summary>
/// One model's wire shape: the thinking field, whether the effort dial rides
/// <c>output_config</c>, whether the harness may send a mid-conversation
/// <c>system</c> turn, which trailing betas the class carries, and the output cap.
/// </summary>
/// <param name="ClaudeCodeBetaLast">
/// Haiku 4.5 alone puts <c>claude-code-20250219</c> at the end of its beta list
/// rather than the front; every other class leads with it.
/// </param>
public sealed record AnthropicModelWire(
    ThinkingWire Thinking,
    bool EffortDial,
    bool HarnessSystemTurn,
    bool FallbackCredit,
    bool AfkMode,
    int MaxOutputTokens,
    bool ClaudeCodeBetaLast = false);

/// <summary>
/// The reference CLI's per-model wire, measured by capturing its real requests
/// against a local listener — one run per model class on CLI 2.1.257, and the
/// same shapes on the desktop's bundled 2.1.255.
/// </summary>
/// <remarks>
/// The wire has four axes, not two, and they do not move together:
///
/// <list type="bullet">
/// <item><b>thinking</b>: adaptive for Opus/Sonnet 4.6+ and the whole Claude 5
/// family (fable, mythos, opus-5, sonnet-5), the fixed 31999 budget for the
/// rest of the 4 generation and haiku, and no field at all for Claude 3.x.</item>
/// <item><b>effort</b> (<c>output_config.effort</c> + the <c>effort-2025-11-24</c>
/// beta): every adaptive model, and opus-4-5 as well — an <i>enabled</i>-thinking
/// model that still takes the dial, which this port used to miss.</item>
/// <item><b>the harness system turn</b> (the <c>mid-conversation-system-2026-04-07</c>
/// beta, and with it a trailing <c>role: system</c> message): opus-5, opus-4-8,
/// sonnet-5 and the fable/mythos family. opus-4-6, opus-4-5, sonnet-4.x, haiku
/// and 3.x get neither — their harness text rides the user message as
/// <c>&lt;system-reminder&gt;</c> blocks instead, which the Anthropic adapter
/// reproduces by folding the turn back.</item>
/// <item><b>the trailing betas</b>: <c>afk-mode-2026-01-31</c> on every adaptive
/// model, and <c>fallback-credit-2026-06-01</c> only on opus-5 and the
/// fable/mythos family. Both were stable across two builds and every run, so
/// they are pinned; the earlier note that this slot was a rollout flag read a
/// single-build sample.</item>
/// </list>
///
/// <c>advisor-tool-2026-03-01</c> is sent by no model in 2.1.257 and is gone.
/// The output cap is the catalog's own <c>max_output_tokens.default</c>, and it
/// is a separate axis from the thinking class rather than a consequence of it:
/// 64000 for the adaptive models <i>except</i> sonnet-4-6, which is adaptive on
/// a 32000 cap, then 32000 for the enabled class and for 3-7-sonnet, and 8192
/// for 3-5-sonnet. Reading it off the thinking class instead cost sonnet-4-6 a
/// doubled cap until a capture caught it.
/// </remarks>
public static class AnthropicEffort
{
    private const string ClaudeCode = "claude-code-20250219";
    private const string Interleaved = "interleaved-thinking-2025-05-14";
    private const string ThinkingTokenCount = "thinking-token-count-2026-05-13";
    private const string ContextManagement = "context-management-2025-06-27";
    private const string PromptCachingScope = "prompt-caching-scope-2026-01-05";
    private const string MidConversationSystem = "mid-conversation-system-2026-04-07";
    private const string Effort = "effort-2025-11-24";
    private const string FallbackCreditBeta = "fallback-credit-2026-06-01";
    private const string AfkModeBeta = "afk-mode-2026-01-31";

    /// <summary>
    /// The betas of the fullest class — opus-5 and the fable/mythos family:
    /// adaptive thinking, the effort dial, the harness system turn and both
    /// trailing betas. Kept as a constant because tests compare against it.
    /// </summary>
    public const string AdaptiveBetas =
        ClaudeCode + "," + Interleaved + "," + ThinkingTokenCount + "," + ContextManagement + "," +
        PromptCachingScope + "," + MidConversationSystem + "," + Effort + "," + FallbackCreditBeta + "," +
        AfkModeBeta;

    /// <summary>
    /// The betas of the enabled-thinking, no-effort class (sonnet-4, sonnet-4-5):
    /// byte-identical to the recorded claude-sonnet-4 capture.
    /// </summary>
    public const string EnabledBetas =
        ClaudeCode + "," + Interleaved + "," + ThinkingTokenCount + "," + ContextManagement + "," +
        PromptCachingScope;

    /// <summary>The CLI's fixed thinking budget for non-adaptive models (cap − 1).</summary>
    public const int EnabledBudgetTokens = 31_999;

    public const int AdaptiveMaxOutputTokens = 64_000;
    public const int EnabledMaxOutputTokens = 32_000;
    private const int Claude35MaxOutputTokens = 8_192;

    // Family + version out of ids like "claude-opus-4-6", "claude-opus-5",
    // "claude-sonnet-4-20250514", "us.anthropic.claude-opus-5-v1:0" and
    // "claude-opus-5@20260115". A second number above 20 is a date stamp, not a
    // minor version ("sonnet-4-20250514" is 4.0, "haiku-4-5-20251001" is 4.5).
    private static readonly Regex Version = new(
        @"(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The Claude 3 generation puts its version before the family:
    // "claude-3-7-sonnet-20250219", "claude-3-5-sonnet-20241022", "claude-3-opus".
    private static readonly Regex LegacyVersion = new(
        @"claude-3(?:-(\d))?-(opus|sonnet|haiku)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly AnthropicModelWire Latest = new(
        ThinkingWire.Adaptive, EffortDial: true, HarnessSystemTurn: true, FallbackCredit: true,
        AfkMode: true, AdaptiveMaxOutputTokens);

    private static readonly AnthropicModelWire AdaptiveWithSystemTurn = Latest with { FallbackCredit = false };

    private static readonly AnthropicModelWire AdaptiveClassic = new(
        ThinkingWire.Adaptive, EffortDial: true, HarnessSystemTurn: false, FallbackCredit: false,
        AfkMode: true, AdaptiveMaxOutputTokens);

    /// <summary>
    /// sonnet-4-6: the adaptive class, on the 32000 output cap rather than the
    /// 64000 every other adaptive model takes.
    /// </summary>
    /// <remarks>
    /// The output cap is the catalog's <c>max_output_tokens.default</c> and is
    /// its own axis, not something adaptive thinking implies — the catalog gives
    /// opus-4-6 and opus-4-7 64000 and sonnet-4-6 32000 while all three are
    /// adaptive. Measured on CLI 2.1.257 at a local listener: a sonnet-4-6
    /// request carries <c>max_tokens: 32000</c>, where this class sent 64000.
    /// opus-4-6 was captured in the same run as a control and reproduced its own
    /// fixture's 64000, so the cap is a field the capture settles.
    /// </remarks>
    private static readonly AnthropicModelWire AdaptiveShortOutput =
        AdaptiveClassic with { MaxOutputTokens = EnabledMaxOutputTokens };

    private static readonly AnthropicModelWire EnabledWithEffort = new(
        ThinkingWire.Enabled, EffortDial: true, HarnessSystemTurn: false, FallbackCredit: false,
        AfkMode: false, EnabledMaxOutputTokens);

    private static readonly AnthropicModelWire Enabled = EnabledWithEffort with { EffortDial = false };

    private static readonly AnthropicModelWire EnabledHaiku = Enabled with { ClaudeCodeBetaLast = true };

    private static readonly AnthropicModelWire Claude37 = new(
        ThinkingWire.None, EffortDial: false, HarnessSystemTurn: false, FallbackCredit: false,
        AfkMode: false, EnabledMaxOutputTokens);

    private static readonly AnthropicModelWire Claude35 = Claude37 with { MaxOutputTokens = Claude35MaxOutputTokens };

    /// <summary>The wire shape the reference gives this model.</summary>
    public static AnthropicModelWire Classify(string modelId)
    {
        var legacy = LegacyVersion.Match(modelId);
        if (legacy.Success)
        {
            // 3-7-sonnet keeps the 32000 cap; every other 3.x model tops out at 8192.
            return legacy.Groups[1].Success && legacy.Groups[1].Value == "7" ? Claude37 : Claude35;
        }

        var match = Version.Match(modelId);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var major) || major > 20)
        {
            // An id this classifier cannot read — a hand-added model on a relay —
            // takes the plainest thinking class, which is what this port always
            // sent it.
            return Enabled;
        }

        var minor = 0;
        if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var parsed) && parsed <= 20)
        {
            minor = parsed;
        }

        var family = match.Groups[1].Value;
        return family switch
        {
            "fable" or "mythos" => Latest,
            "opus" when major >= 5 => Latest,
            "opus" when major == 4 && minor >= 8 => AdaptiveWithSystemTurn,
            "opus" when major == 4 && minor >= 6 => AdaptiveClassic,
            "opus" when major == 4 && minor == 5 => EnabledWithEffort,
            "opus" => Enabled,
            "sonnet" when major >= 5 => AdaptiveWithSystemTurn,
            "sonnet" when major == 4 && minor >= 6 => AdaptiveShortOutput,
            "sonnet" => Enabled,
            "haiku" when major >= 5 => AdaptiveWithSystemTurn,
            "haiku" => EnabledHaiku,
            _ => Enabled,
        };
    }

    /// <summary>
    /// True for models the CLI runs on adaptive thinking: Opus/Sonnet 4.6+ and
    /// every Claude 5-family model. Old-style ids with the version first
    /// ("claude-3-7-sonnet-…") never match and stay on the fixed budget, like
    /// the CLI.
    /// </summary>
    public static bool SupportsAdaptive(string modelId) =>
        Classify(modelId).Thinking == ThinkingWire.Adaptive;

    /// <summary>
    /// True when the harness may send this model a mid-conversation
    /// <c>role: system</c> message — the <c>mid-conversation-system</c> beta. The
    /// same set of models takes no task-board tools in the reference, which is
    /// why the host reads this to decide both.
    /// </summary>
    public static bool SupportsHarnessSystemTurn(string modelId) =>
        Classify(modelId).HarnessSystemTurn;

    /// <summary>True when <c>output_config.effort</c> rides the request.</summary>
    public static bool HasEffortDial(string modelId) => Classify(modelId).EffortDial;

    public static int MaxOutputTokens(string modelId) => Classify(modelId).MaxOutputTokens;

    /// <summary>The <c>anthropic-beta</c> header for this model, in the reference's order.</summary>
    public static string Betas(string modelId)
    {
        var wire = Classify(modelId);
        var betas = new List<string>(9);
        if (wire.Thinking == ThinkingWire.None)
        {
            // Claude 3.x: no thinking, so none of the thinking-adjacent betas.
            return ClaudeCode + "," + PromptCachingScope;
        }

        if (!wire.ClaudeCodeBetaLast)
        {
            betas.Add(ClaudeCode);
        }

        betas.Add(Interleaved);
        betas.Add(ThinkingTokenCount);
        betas.Add(ContextManagement);
        betas.Add(PromptCachingScope);
        if (wire.HarnessSystemTurn)
        {
            betas.Add(MidConversationSystem);
        }

        if (wire.EffortDial)
        {
            betas.Add(Effort);
        }

        if (wire.FallbackCredit)
        {
            betas.Add(FallbackCreditBeta);
        }

        if (wire.AfkMode)
        {
            betas.Add(AfkModeBeta);
        }

        if (wire.ClaudeCodeBetaLast)
        {
            betas.Add(ClaudeCode);
        }

        return string.Join(",", betas);
    }

    /// <summary>The CLI's on-wire effort names (Ultracode travels as its xhigh value).</summary>
    public static string WireName(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.XHigh => "xhigh",
        ThinkingEffort.Max => "max",
        _ => "high",
    };
}
