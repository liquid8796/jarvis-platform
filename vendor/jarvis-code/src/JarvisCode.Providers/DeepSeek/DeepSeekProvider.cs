using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.OpenAiCompatible;

namespace JarvisCode.Providers.DeepSeek;

/// <summary>
/// DeepSeek's own API (api.deepseek.com), OpenAI-compatible with the classic
/// <c>max_tokens</c>. Reasoning is two sibling top-level fields rather than one
/// (docs read 2026-09-01): <c>thinking: {"type": "enabled"|"disabled"}</c> switches it
/// on — the default is enabled — and <c>reasoning_effort</c> ("low"|"high"|"max") sets
/// how deep it goes when it is. The chain of thought streams as <c>reasoning_content</c>
/// deltas alongside the answer.
/// <para>
/// The version segment is optional here: the docs give the base as
/// <c>https://api.deepseek.com</c> and describe <c>/v1</c> as an alias for OpenAI SDK
/// compatibility, unrelated to the model version. Both resolve.
/// </para>
/// </summary>
public static class DeepSeekProvider
{
    public const string ProviderId = "deepseek";

    public const string DisplayName = "DeepSeek";

    /// <summary>
    /// The documented API root. Kept in sync with AppSettings.DeepSeekBaseUrl
    /// (Core cannot reference this project).
    /// </summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    /// <summary>The same host under the OpenAI SDK's expected shape, offered as a preset.</summary>
    public const string VersionedBaseUrl = DefaultBaseUrl + "/v1";

    public static readonly OpenAiCompatibleDialect Dialect = new()
    {
        DefaultBaseUrl = DefaultBaseUrl,
        StreamsReasoning = true,
        ApplyThinking = ApplyReasoning,
    };

    public static OpenAiCompatibleProvider Create(
        HttpClient http, IApiKeySource keys, IProviderEndpoints endpoints) =>
        new(http, keys, ProviderId, DisplayName, () => endpoints.GetBaseUrl(ProviderId), Dialect);

    /// <summary>
    /// Unlike the vendors whose thinking cannot be turned off, DeepSeek documents
    /// "disabled", so Off says so explicitly instead of leaving the server on its
    /// enabled default and making Off the slowest setting of the six.
    /// </summary>
    internal static void ApplyReasoning(JsonObject body, LlmRequest request)
    {
        bool wantsThinking = request.ThinkingEffort != ThinkingEffort.Off;
        body["thinking"] = new JsonObject { ["type"] = wantsThinking ? "enabled" : "disabled" };
        if (wantsThinking)
            body["reasoning_effort"] = Effort(request.ThinkingEffort);
    }

    /// <summary>
    /// Three rungs are documented, so the app's six fold onto them: the middle of the
    /// ladder asks for the middle of what the vendor offers, which here is "high".
    /// </summary>
    internal static string Effort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Off or ThinkingEffort.Low => "low",
        ThinkingEffort.Medium or ThinkingEffort.High => "high",
        _ => "max",
    };
}
