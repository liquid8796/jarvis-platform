using JarvisCode.Core.Providers;
using JarvisCode.Providers.OpenAiCompatible;

namespace JarvisCode.Providers.MiniMax;

/// <summary>
/// MiniMax's OpenAI-compatible chat completions (the MiniMax-M series). Like Zhipu it
/// publishes two roots for two separately-issued credentials — <c>api.minimax.io</c>
/// globally, <c>api.minimaxi.com</c> in mainland China — and a key from one is rejected
/// by the other, so pointing the base URL at the wrong region reads as an auth failure
/// rather than a routing one.
/// <para>
/// Its documented body is OpenAI's, taking <c>max_tokens</c> as well as
/// <c>max_completion_tokens</c>; the docs (read 2026-09-01) describe no reasoning switch
/// on this surface, so none is sent and the model's own default stands. Thinking that a
/// model streams anyway is read from the deltas.
/// </para>
/// </summary>
public static class MiniMaxProvider
{
    public const string ProviderId = "minimax";

    public const string DisplayName = "MiniMax";

    /// <summary>
    /// The global root. Kept in sync with AppSettings.MiniMaxBaseUrl (Core cannot
    /// reference this project).
    /// </summary>
    public const string DefaultBaseUrl = "https://api.minimax.io/v1";

    /// <summary>The mainland China root, whose keys are issued separately.</summary>
    public const string ChinaBaseUrl = "https://api.minimaxi.com/v1";

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
