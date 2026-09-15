using System.Globalization;
using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.OpenAiCompatible;

namespace JarvisCode.Providers.Zhipu;

/// <summary>
/// Zhipu AI's GLM models over the v4 chat-completions API. The same platform is served
/// from two roots — <c>api.z.ai</c> internationally and <c>open.bigmodel.cn</c> in
/// mainland China — and a key issued on one is not accepted by the other, so the base
/// URL is a real choice rather than a mirror.
/// <para>
/// Thinking is <c>thinking: {"type": "enabled"|"disabled"}</c>, and the depth dial
/// <c>reasoning_effort</c> is a sibling of it that the docs scope to GLM-5.2 and above
/// (read 2026-09-01) — so it rides only the ids that advertise it, an older model being
/// sent a field it never published risking a 400 for the whole turn. Reasoning streams
/// as <c>reasoning_content</c> deltas.
/// </para>
/// </summary>
public static class ZhipuProvider
{
    public const string ProviderId = "zhipu";

    public const string DisplayName = "Zhipu AI";

    /// <summary>
    /// The international root. Kept in sync with AppSettings.ZhipuBaseUrl (Core cannot
    /// reference this project).
    /// </summary>
    public const string DefaultBaseUrl = "https://api.z.ai/api/paas/v4";

    /// <summary>The mainland China root, whose keys are issued separately.</summary>
    public const string ChinaBaseUrl = "https://open.bigmodel.cn/api/paas/v4";

    /// <summary>The first GLM generation whose models document the reasoning_effort dial.</summary>
    private static readonly (int Major, int Minor) FirstEffortVersion = (5, 2);

    public static readonly OpenAiCompatibleDialect Dialect = new()
    {
        DefaultBaseUrl = DefaultBaseUrl,
        // Not in the endpoint's documented parameter list; usage rides the final chunk.
        RequestsUsageInStream = false,
        StreamsReasoning = true,
        ApplyThinking = ApplyReasoning,
    };

    public static OpenAiCompatibleProvider Create(
        HttpClient http, IApiKeySource keys, IProviderEndpoints endpoints) =>
        new(http, keys, ProviderId, DisplayName, () => endpoints.GetBaseUrl(ProviderId), Dialect);

    internal static void ApplyReasoning(JsonObject body, LlmRequest request)
    {
        bool wantsThinking = request.ThinkingEffort != ThinkingEffort.Off;
        body["thinking"] = new JsonObject { ["type"] = wantsThinking ? "enabled" : "disabled" };
        if (wantsThinking && SupportsReasoningEffort(request.ModelId))
            body["reasoning_effort"] = Effort(request.ThinkingEffort);
    }

    /// <summary>Three rungs are documented (low/high/max), so the app's six fold onto them.</summary>
    internal static string Effort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Off or ThinkingEffort.Low => "low",
        ThinkingEffort.Medium or ThinkingEffort.High => "high",
        _ => "max",
    };

    /// <summary>
    /// True for "glm-5.2" and every later GLM. The version is whatever follows "glm-" up
    /// to the next hyphen, so a suffixed id ("glm-5.3-flash") reads as its own family and
    /// an id from another vendor entirely — this endpoint will serve whatever the account
    /// has — reads as unsupported rather than being guessed at.
    /// </summary>
    internal static bool SupportsReasoningEffort(string modelId)
    {
        const string prefix = "glm-";
        if (!modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var version = modelId[prefix.Length..];
        int end = version.IndexOf('-');
        if (end >= 0)
            version = version[..end];

        var parts = version.Split('.');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            return false;
        int minor = parts.Length > 1
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        return major > FirstEffortVersion.Major
               || (major == FirstEffortVersion.Major && minor >= FirstEffortVersion.Minor);
    }
}
