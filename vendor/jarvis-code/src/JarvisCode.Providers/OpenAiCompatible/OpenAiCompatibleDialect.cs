using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;

namespace JarvisCode.Providers.OpenAiCompatible;

/// <summary>
/// The handful of things OpenAI-compatible endpoints disagree about. Everything else —
/// the message shapes, the tool block, the SSE grammar — is identical across them, so a
/// vendor is a dialect rather than an adapter of its own.
/// </summary>
public sealed record OpenAiCompatibleDialect
{
    /// <summary>
    /// Where requests go when nothing is configured. Its path is also what a bare host
    /// gets appended to it, so <c>https://openrouter.ai</c> still reaches
    /// <c>/api/v1/chat/completions</c> — see <see cref="OpenAiCompatibleEndpoint.Resolve"/>.
    /// </summary>
    public required string DefaultBaseUrl { get; init; }

    /// <summary>
    /// Output cap field. The classic <c>max_tokens</c> is what every vendor here
    /// documents; OpenAI's own <c>max_completion_tokens</c> is the exception and is
    /// rejected outright by some of these endpoints.
    /// </summary>
    public string MaxTokensField { get; init; } = "max_tokens";

    /// <summary>
    /// Whether to ask for usage in the final chunk. OpenRouter documents
    /// <c>stream_options</c> as deprecated and no-op — it always reports usage — and an
    /// unknown field is a 400 on a strict endpoint, so it is not sent there.
    /// </summary>
    public bool RequestsUsageInStream { get; init; } = true;

    /// <summary>
    /// Whether this endpoint streams a chain of thought beside the answer. Reading a
    /// field the vendor never sends costs nothing, but announcing a thinking block that
    /// never fills would leave an empty cell in the transcript, so it is opt-in.
    /// </summary>
    public bool StreamsReasoning { get; init; }

    /// <summary>
    /// Writes the vendor's own reasoning switch into the body, or null where the vendor
    /// documents none — which is not the same as "off": the server's default stands, and
    /// inventing a field name would 400 the whole turn.
    /// </summary>
    public Action<JsonObject, LlmRequest>? ApplyThinking { get; init; }
}

/// <summary>
/// Turns whatever base URL is configured into the chat-completions endpoint. Vendors put
/// their API root at different depths (<c>/api/v1</c>, <c>/v1</c>, <c>/api/paas/v4</c>,
/// nothing at all), so the rule cannot be "append /v1": it is the dialect's own default
/// that says what a bare host is missing.
/// </summary>
public static class OpenAiCompatibleEndpoint
{
    private const string ChatPath = "/chat/completions";

    /// <summary>
    /// Accepts the API root with or without its version segment, a full chat-completions
    /// URL pasted whole, a trailing slash, stray whitespace and a host typed without a
    /// scheme. An unparseable value falls back to <paramref name="defaultBaseUrl"/>
    /// rather than producing a URL no request can be built from.
    /// </summary>
    public static string Resolve(string? baseUrl, string defaultBaseUrl)
    {
        var root = Normalize(baseUrl) ?? Normalize(defaultBaseUrl) ?? defaultBaseUrl.Trim();
        if (root.EndsWith(ChatPath, StringComparison.OrdinalIgnoreCase))
            return root;

        // A bare host is missing whatever the vendor's own default carries as a path.
        if (IsBareHost(root))
            root += PathOf(defaultBaseUrl);
        return root + ChatPath;
    }

    /// <summary>Trimmed, scheme-completed and without its trailing slash; null when unusable.</summary>
    private static string? Normalize(string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out _) ? trimmed.TrimEnd('/') : null;
    }

    private static bool IsBareHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.Trim('/').Length == 0;

    /// <summary>The default's path, without its trailing slash ("" when it is a bare host too).</summary>
    private static string PathOf(string defaultBaseUrl) =>
        Uri.TryCreate(Normalize(defaultBaseUrl) ?? defaultBaseUrl, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.TrimEnd('/')
            : "";
}
