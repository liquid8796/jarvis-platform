using JarvisCode.Core.Providers;
using JarvisCode.Providers.Anthropic;
using JarvisCode.Providers.OpenAi;

namespace JarvisCode.Providers.LlmApi;

/// <summary>
/// Which of llmapi.pro's protocols to speak. The service serves the same Claude-backed
/// models on all of them, so this only decides the wire format and the path.
/// </summary>
public enum LlmApiProtocol
{
    /// <summary>Messages API at /v1/messages — what Claude Code itself uses.</summary>
    Anthropic,

    /// <summary>Chat Completions at /v1/chat/completions, for the OpenAI-compatible route.</summary>
    OpenAi,
}

/// <summary>
/// The settings the llmapi relay reads. Supplied by the host so this project keeps knowing
/// nothing about where settings live.
/// </summary>
public interface ILlmApiOptions
{
    /// <summary>Protocol name as stored in settings; anything unknown means the default.</summary>
    string LlmApiProtocolName { get; }
}

/// <summary>
/// llmapi.pro — a relay that serves Claude models behind three protocols on one key
/// (documented at https://llmapi.pro/docs, read 2026-08-30):
/// <list type="bullet">
/// <item>Anthropic: base https://llmapi.pro, path /v1/messages, header x-api-key.</item>
/// <item>OpenAI: base https://llmapi.pro/v1, path /v1/chat/completions, Authorization: Bearer.</item>
/// <item>Gemini: base https://llmapi.pro, path /v1beta/models/{model}:generateContent — text
/// chat only ("images / tool-calls not yet mapped"), so this app does not speak it; the same
/// models are reachable, with tools, through the other two.</item>
/// </list>
/// Since the protocols differ in whether the base URL carries /v1, the vendor's own docs call a
/// missing or extra /v1 the most common cause of a 404. This adapter therefore takes whichever
/// of the three base URLs is configured and resolves the path itself, and reuses the existing
/// <see cref="AnthropicProvider"/> / <see cref="OpenAiProvider"/> wires rather than copying them.
/// </summary>
public sealed class LlmApiProvider : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "llmapi";

    /// <summary>
    /// The relay's root. Kept in sync with AppSettings.LlmApiBaseUrl (Core cannot reference
    /// this project); any of the three documented base URLs resolves to the same place.
    /// </summary>
    public const string DefaultBaseUrl = "https://llmapi.pro";

    /// <summary>
    /// The base URL the docs give for the OpenAI-compatible protocol — the same host
    /// with <c>/v1</c>, because that protocol's own path is <c>/v1/chat/completions</c>
    /// relative to it. Offered as a preset so the card can show the doc's two shapes;
    /// <see cref="ResolveEndpoint"/> accepts either against either protocol.
    /// </summary>
    public const string OpenAiBaseUrl = DefaultBaseUrl + "/v1";

    /// <summary>
    /// Catalog ids carry this prefix and the wire never sees it. The relay serves Anthropic's
    /// own model names, and a session stores only the model id: two catalog entries called
    /// "claude-opus-5" would be indistinguishable, and the one served by Anthropic directly
    /// would win, quietly sending llmapi turns to api.anthropic.com on the wrong key.
    /// </summary>
    public const string ModelIdPrefix = "llmapi/";

    private const string DisplayNameText = "LLM API (llmapi.pro)";
    private const string AnthropicPath = "/v1/messages";
    private const string OpenAiPath = "/v1/chat/completions";

    /// <summary>
    /// Path tails a configured base URL may already carry — the three documented bases plus a
    /// full endpoint pasted whole. Longest first: the loop strips the first one that matches.
    /// </summary>
    private static readonly string[] KnownTails =
    [
        "/v1/chat/completions",
        "/chat/completions",
        "/v1/messages",
        "/messages",
        "/v1beta",
        "/v1",
    ];

    private readonly AnthropicProvider _anthropicWire;
    private readonly OpenAiProvider _openAiWire;
    private readonly ILlmApiOptions _options;

    public LlmApiProvider(
        HttpClient http, IApiKeySource keys, IProviderEndpoints endpoints, ILlmApiOptions options)
    {
        _options = options;
        _anthropicWire = new AnthropicProvider(
            http, keys, ProviderId, DisplayNameText,
            () => ResolveEndpoint(endpoints.GetBaseUrl(ProviderId), LlmApiProtocol.Anthropic),
            // The relay documents exactly one Anthropic path, /v1/messages with no
            // query, so the ?beta=true namespace Anthropic's own API uses for
            // effort-era requests is not sent here.
            betaNamespace: false);
        _openAiWire = new OpenAiProvider(
            http, keys, ProviderId, DisplayNameText,
            () => ResolveEndpoint(endpoints.GetBaseUrl(ProviderId), LlmApiProtocol.OpenAi));
    }

    public string Id => ProviderId;

    public string DisplayName => DisplayNameText;

    /// <summary>The relay authenticates every protocol with the same sk-… key.</summary>
    public bool RequiresApiKey => true;

    /// <summary>The protocol in force right now; re-read per turn so a change applies at once.</summary>
    public LlmApiProtocol Protocol => ParseProtocol(_options.LlmApiProtocolName);

    public IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request, CancellationToken cancellationToken) =>
        ((ILlmProvider)WireFor(Protocol)).StreamChatAsync(Relay(request), cancellationToken);

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request) =>
        WireFor(Protocol).PreviewRequest(Relay(request));

    /// <summary>Both wires speak the inspector, so a preview costs no extra plumbing.</summary>
    private IRequestInspector WireFor(LlmApiProtocol protocol) =>
        protocol == LlmApiProtocol.OpenAi ? _openAiWire : _anthropicWire;

    /// <summary>
    /// The request as the underlying wire sees it. Sending and previewing share it so
    /// the inspector shows the relay's own rewrites — the stripped catalog prefix and
    /// the dropped web-search flag — rather than what this app asked for.
    /// </summary>
    private static LlmRequest Relay(LlmRequest request) => request with
    {
        ModelId = ToWireModelId(request.ModelId),
        // The relay documents chat and tool use, never Anthropic's server-side tools, and an
        // unknown tool type is a 400 for the whole turn. Chat turns ask for the vendor's web
        // search by default, so it is dropped here rather than failing every one of them;
        // Code turns run this app's own web_search tool and never set it.
        EnableWebSearch = false,
    };

    /// <summary>
    /// Turns whichever base URL the user configured — any of the three the docs give, with or
    /// without a trailing slash, or a full endpoint pasted whole — into the endpoint for
    /// <paramref name="protocol"/>. A root that is not a usable absolute URL falls back to the
    /// relay's own, because a half-typed host would otherwise fail as an unhandled URI error
    /// rather than as a provider message.
    /// </summary>
    public static string ResolveEndpoint(string? baseUrl, LlmApiProtocol protocol)
    {
        var root = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim()).TrimEnd('/');
        if (!root.Contains("://", StringComparison.Ordinal))
        {
            root = "https://" + root;
        }

        // Repeatedly, so a doubled "/v1/v1" left over from an earlier fix comes off too.
        bool stripped;
        do
        {
            stripped = false;
            foreach (var tail in KnownTails)
            {
                if (!root.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                root = root[..^tail.Length].TrimEnd('/');
                stripped = true;
                break;
            }
        }
        while (stripped);

        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            root = DefaultBaseUrl;
        }

        return root + (protocol == LlmApiProtocol.Anthropic ? AnthropicPath : OpenAiPath);
    }

    /// <summary>The stored protocol name; unknown or blank means the Anthropic protocol.</summary>
    public static LlmApiProtocol ParseProtocol(string? name) =>
        Enum.TryParse<LlmApiProtocol>(name?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : LlmApiProtocol.Anthropic;

    /// <summary>Drops the catalog prefix; a model id typed without one is sent as it is.</summary>
    public static string ToWireModelId(string modelId) =>
        modelId.StartsWith(ModelIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[ModelIdPrefix.Length..]
            : modelId;
}
