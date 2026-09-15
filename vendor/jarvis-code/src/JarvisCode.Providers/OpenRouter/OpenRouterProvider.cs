using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.OpenAiCompatible;

namespace JarvisCode.Providers.OpenRouter;

/// <summary>
/// OpenRouter (openrouter.ai) — one key and one endpoint in front of many vendors'
/// models, addressed as <c>vendor/model</c>. Its wire is OpenAI's chat completions with
/// two documented departures (docs read 2026-09-01): <c>stream_options</c> is deprecated
/// and has no effect because usage always rides the final chunk, and reasoning is
/// controlled through a <c>reasoning</c> object rather than <c>reasoning_effort</c>,
/// coming back as <c>reasoning</c> / <c>reasoning_details</c> deltas. Its keep-alive
/// comment lines are already dropped by the shared SSE reader.
/// </summary>
public static class OpenRouterProvider
{
    public const string ProviderId = "openrouter";

    public const string DisplayName = "OpenRouter";

    /// <summary>
    /// The documented API root. Kept in sync with AppSettings.OpenRouterBaseUrl
    /// (Core cannot reference this project).
    /// </summary>
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    public static readonly OpenAiCompatibleDialect Dialect = new()
    {
        DefaultBaseUrl = DefaultBaseUrl,
        // "The usage: { include: true } and stream_options: { include_usage: true }
        // parameters are deprecated and have no effect. Full usage details are now
        // always included automatically in every response."
        RequestsUsageInStream = false,
        StreamsReasoning = true,
        ApplyThinking = ApplyReasoning,
    };

    public static OpenAiCompatibleProvider Create(
        HttpClient http, IApiKeySource keys, IProviderEndpoints endpoints) =>
        new(http, keys, ProviderId, DisplayName, () => endpoints.GetBaseUrl(ProviderId), Dialect);

    /// <summary>
    /// OpenRouter normalizes the effort word for whichever model the request routes to,
    /// so the whole ladder can be passed straight through. Off sends nothing rather than
    /// <c>effort: "none"</c>, which the docs scope to OpenAI-style models only — the
    /// server default is the honest answer for a model that cannot stop reasoning.
    /// </summary>
    internal static void ApplyReasoning(JsonObject body, LlmRequest request)
    {
        if (request.ThinkingEffort == ThinkingEffort.Off)
            return;
        body["reasoning"] = new JsonObject { ["effort"] = Effort(request.ThinkingEffort) };
    }

    internal static string Effort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        ThinkingEffort.XHigh => "xhigh",
        _ => "max",
    };
}
