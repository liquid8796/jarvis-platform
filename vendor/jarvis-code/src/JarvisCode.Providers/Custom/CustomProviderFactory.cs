using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Providers.Anthropic;
using JarvisCode.Providers.OpenAiCompatible;

namespace JarvisCode.Providers.Custom;

/// <summary>
/// Builds a provider out of a <see cref="CustomProviderSpec"/> — the endpoint a user
/// added by hand. Nothing new is implemented here: a custom provider is one of the two
/// wires this app already speaks, pointed somewhere else under an id of the user's own,
/// which is why it gets the same key rotation, request inspector and model list as a
/// built-in one.
/// </summary>
public static class CustomProviderFactory
{
    private const string MessagesPath = "/v1/messages";
    private const string VersionSegment = "/v1";

    /// <summary>
    /// The provider for one spec. The base URL is read through
    /// <paramref name="endpoints"/> rather than off the spec, so editing it applies to
    /// the next request instead of the next launch; the protocol is fixed at
    /// construction, because changing it changes which wire runs.
    /// </summary>
    public static ILlmProvider Create(
        HttpClient http, IApiKeySource keys, CustomProviderSpec spec, IProviderEndpoints endpoints)
    {
        var normalized = CustomProviders.Normalize(spec);
        var id = normalized.Id;
        var fallback = normalized.BaseUrl;
        bool requiresApiKey = normalized.RequiresApiKey;

        if (CustomProviderProtocols.IsAnthropic(normalized.ProtocolName))
        {
            return new AnthropicProvider(
                http, keys, id, normalized.DisplayName,
                () => ResolveMessagesEndpoint(endpoints.GetBaseUrl(id) ?? fallback),
                // The ?beta=true namespace is Anthropic's own; an endpoint reimplementing
                // this wire documents the bare path, exactly as the llmapi relay does.
                betaNamespace: false,
                requiresApiKey: () => requiresApiKey);
        }

        return new OpenAiCompatibleProvider(
            http, keys, id, normalized.DisplayName,
            () => endpoints.GetBaseUrl(id),
            new OpenAiCompatibleDialect
            {
                // The user's own URL is the default here: there is no vendor to fall back to.
                DefaultBaseUrl = fallback,
                // An unknown endpoint may be strict about fields it never published, so only
                // what every OpenAI-compatible server implements is sent. Reasoning it does
                // stream is still read.
                RequestsUsageInStream = false,
                StreamsReasoning = true,
            },
            () => requiresApiKey);
    }

    /// <summary>
    /// The Messages endpoint for a base URL. A full endpoint pasted whole is left alone,
    /// and a trailing version segment is dropped before the path is appended, so both
    /// <c>https://relay.example</c> and <c>https://relay.example/v1</c> land on
    /// <c>/v1/messages</c> rather than one of them on <c>/v1/v1/messages</c>.
    /// </summary>
    public static string ResolveMessagesEndpoint(string? baseUrl)
    {
        var root = (baseUrl ?? "").Trim().TrimEnd('/');
        if (root.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return root;
        if (root.EndsWith(VersionSegment, StringComparison.OrdinalIgnoreCase))
            root = root[..^VersionSegment.Length];
        return root + MessagesPath;
    }
}
