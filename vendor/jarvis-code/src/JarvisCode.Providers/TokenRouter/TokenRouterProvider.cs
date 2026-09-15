using JarvisCode.Core.Providers;
using JarvisCode.Providers.OpenAiCompatible;

namespace JarvisCode.Providers.TokenRouter;

/// <summary>
/// TokenRouter (tokenrouter.io) — an OpenAI-compatible gateway across several vendors,
/// addressed either as <c>vendor:model</c> or through its routing aliases
/// (<c>auto:quality</c>, <c>auto:cost</c>, …). Its documented parameter table
/// (read 2026-09-01) lists messages, tools, stream and the sampling dials, and names
/// <c>n</c> and <c>logprobs</c> as unsupported; it documents neither
/// <c>stream_options</c> nor any reasoning switch, so neither is sent — a missing token
/// count costs a number in the status line, while an unknown field can cost the turn.
/// Reasoning that a routed model streams anyway is still read.
/// </summary>
public static class TokenRouterProvider
{
    public const string ProviderId = "tokenrouter";

    public const string DisplayName = "TokenRouter";

    /// <summary>
    /// The documented API root. Kept in sync with AppSettings.TokenRouterBaseUrl
    /// (Core cannot reference this project).
    /// </summary>
    public const string DefaultBaseUrl = "https://api.tokenrouter.io/v1";

    public static readonly OpenAiCompatibleDialect Dialect = new()
    {
        DefaultBaseUrl = DefaultBaseUrl,
        RequestsUsageInStream = false,
        StreamsReasoning = true,
    };

    public static OpenAiCompatibleProvider Create(
        HttpClient http, IApiKeySource keys, IProviderEndpoints endpoints) =>
        new(http, keys, ProviderId, DisplayName, () => endpoints.GetBaseUrl(ProviderId), Dialect);
}
