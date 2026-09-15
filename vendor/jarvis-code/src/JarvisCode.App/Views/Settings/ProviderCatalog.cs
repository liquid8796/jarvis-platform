using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Providers.ChatGptWeb;
using JarvisCode.Providers.Custom;
using JarvisCode.Providers.DeepSeek;
using JarvisCode.Providers.LlmApi;
using JarvisCode.Providers.MiniMax;
using JarvisCode.Providers.Nvidia;
using JarvisCode.Providers.Ollama;
using JarvisCode.Providers.OpenAiCompatible;
using JarvisCode.Providers.OpenRouter;
using JarvisCode.Providers.TokenRouter;
using JarvisCode.Providers.Zhipu;

namespace JarvisCode.App.Views.Settings;

/// <summary>How a provider proves who it is, which decides the editor its card shows.</summary>
public enum ProviderCredentialKind
{
    /// <summary>One or more API keys, rotated when one is rate limited.</summary>
    ApiKeys,

    /// <summary>A cookie export standing in for a signed-in browser session.</summary>
    CookieExport,
}

/// <summary>What the dot on a provider card says at a glance.</summary>
public enum ProviderReadiness
{
    /// <summary>Usable right now: credentials are saved, or the endpoint needs none.</summary>
    Ready,

    /// <summary>Registered, but the credential it needs is missing.</summary>
    NeedsCredential,

    /// <summary>Settings mention it, but no provider with that id is in this build.</summary>
    Unavailable,
}

/// <summary>A one-click endpoint (e.g. "Local Ollama") offered next to the base-URL box.</summary>
public sealed record ProviderEndpointPreset(string Label, string Url);

/// <summary>
/// A provider whose server address is configurable. The accessors read and write the
/// matching <see cref="AppSettings"/> field, so a card renders the box without knowing
/// which field backs it — the only per-provider knowledge lives in this record.
/// </summary>
public sealed record ProviderEndpointSpec(
    string Description,
    Func<AppSettings, string> Read,
    Action<AppSettings, string> Write,
    IReadOnlyList<ProviderEndpointPreset> Presets,
    Func<AppSettings, string>? Resolve = null);

/// <summary>
/// A free-text setting a provider needs beyond its credential (an AWS region, a GCP
/// project id). Rendered as a labelled box in the card, committed on blur, stored like
/// any other setting. <paramref name="Placeholder"/> shows what a value looks like.
/// </summary>
public sealed record ProviderFieldSpec(
    string Label,
    string Description,
    string Placeholder,
    Func<AppSettings, string> Read,
    Action<AppSettings, string> Write);

/// <summary>
/// A yes/no setting a provider needs beyond its credential — today whether a
/// hand-written endpoint authenticates at all. Rendered as a switch in the card.
/// </summary>
public sealed record ProviderToggleSpec(
    string Label,
    string Description,
    Func<AppSettings, bool> Read,
    Action<AppSettings, bool> Write);

/// <summary>
/// A named choice a provider needs beyond its credential — today the llmapi relay's
/// protocol. Rendered as a labelled combo in the card and stored like any other setting.
/// </summary>
public sealed record ProviderChoiceSpec(
    string Label,
    string Description,
    IReadOnlyList<string> Options,
    Func<AppSettings, string> Read,
    Action<AppSettings, string> Write);

/// <summary>
/// What a settings card knows about a provider that <see cref="ILlmProvider"/> does not:
/// the sentence under its name, where its credential comes from, and whether it has an
/// endpoint to edit. Adding a provider means registering it in AppServices and adding one
/// entry here — a provider with no entry still gets a working API-key card.
/// </summary>
public sealed record ProviderPresentation(
    string Id,
    string Tagline,
    ProviderCredentialKind Credential = ProviderCredentialKind.ApiKeys,
    string? CredentialUrl = null,
    ProviderEndpointSpec? Endpoint = null,
    ProviderChoiceSpec? Choice = null,
    string? ModelIdPrefix = null,
    IReadOnlyList<ProviderFieldSpec>? Fields = null,
    IReadOnlyList<ProviderToggleSpec>? Toggles = null);

/// <summary>The engine-side facts a card needs, read off the registered provider.</summary>
public sealed record ProviderRuntime(string Id, string DisplayName, bool RequiresApiKey)
{
    public static ProviderRuntime From(ILlmProvider provider) =>
        new(provider.Id, provider.DisplayName, provider.RequiresApiKey);
}

/// <summary>
/// One model offered by a provider: custom ones are the user's own (and removable), and a
/// context window they corrected is flagged so the card can say the number is not the
/// catalog's.
/// </summary>
public sealed record ProviderModel(ModelInfo Info, bool IsCustom, bool IsContextOverridden = false);

/// <summary>Everything one provider card renders, assembled from the engine plus settings.</summary>
public sealed record ProviderEntry(
    string Id,
    string Name,
    string Tagline,
    ProviderCredentialKind Credential,
    bool IsRegistered,
    bool RequiresCredential,
    int CredentialCount,
    IReadOnlyList<ProviderModel> Models,
    ProviderEndpointSpec? Endpoint,
    string? CredentialUrl,
    ProviderChoiceSpec? Choice = null,
    string? ModelIdPrefix = null,
    IReadOnlyList<ProviderFieldSpec>? Fields = null,
    IReadOnlyList<ProviderToggleSpec>? Toggles = null,
    bool IsUserDefined = false)
{
    public ProviderReadiness Readiness =>
        !IsRegistered ? ProviderReadiness.Unavailable
        : CredentialCount > 0 || !RequiresCredential ? ProviderReadiness.Ready
        : ProviderReadiness.NeedsCredential;

    /// <summary>The short chip beside the name; empty when there is nothing worth flagging.</summary>
    public string Badge =>
        !IsRegistered ? "not in this build"
        : Credential == ProviderCredentialKind.CookieExport ? "browser session"
        : !RequiresCredential ? "no key needed"
        : IsUserDefined ? "yours"
        : "";

    /// <summary>The second line of the header: credentials first, then how many models it serves.</summary>
    public string StatusLine => string.Join(" · ", new[] { CredentialSummary, ModelSummary });

    private string CredentialSummary => (IsRegistered, Credential, CredentialCount) switch
    {
        (false, _, 0) => "No provider with this id",
        (false, _, var saved) => $"No provider with this id · {Keys(saved)} still stored",
        (_, ProviderCredentialKind.CookieExport, 0) => "No session saved",
        (_, ProviderCredentialKind.CookieExport, _) => "Session saved",
        (_, _, 0) => RequiresCredential ? "Needs an API key" : "No key saved",
        (_, _, var saved) => Keys(saved),
    };

    private string ModelSummary => Models.Count switch
    {
        0 => "no models yet",
        1 => "1 model",
        var count => $"{count} models",
    };

    private static string Keys(int count) => count == 1 ? "1 key" : $"{count} keys";
}

/// <summary>
/// Turns the registered providers plus the saved settings into the rows the Providers
/// panel draws. Pure on purpose: the panel owns pixels, this owns the reasoning.
/// </summary>
public static class ProviderCatalog
{
    private static readonly ProviderEndpointSpec OllamaEndpoint = new(
        "Point at a local server (no key) or the hosted cloud (needs a key).",
        static settings => settings.OllamaBaseUrl,
        static (settings, url) => settings.OllamaBaseUrl = url,
        [
            new("Local Ollama", OllamaProvider.LocalBaseUrl),
            new("Ollama cloud", OllamaProvider.CloudBaseUrl),
        ]);

    private static readonly ProviderEndpointSpec NvidiaEndpoint = new(
        "The hosted catalog (needs a key) or your own NIM container (no key).",
        static settings => settings.NvidiaBaseUrl,
        static (settings, url) => settings.NvidiaBaseUrl = url,
        [
            new("NVIDIA Build", NvidiaProvider.HostedBaseUrl),
            new("Local NIM", "http://localhost:8000/v1"),
        ]);

    private static readonly ProviderEndpointSpec LlmApiEndpoint = new(
        "llmapi.pro gives a different base per protocol — Anthropic without /v1, OpenAI with it — "
        + "and calls a missing or extra /v1 its most common cause of a 404. Either preset works "
        + "for either protocol here: the path below is resolved from the protocol, not from what "
        + "the box ends in.",
        static settings => settings.LlmApiBaseUrl,
        static (settings, url) => settings.LlmApiBaseUrl = url,
        [
            new("Anthropic (no /v1)", LlmApiProvider.DefaultBaseUrl),
            new("OpenAI (with /v1)", LlmApiProvider.OpenAiBaseUrl),
        ],
        static settings => LlmApiProvider.ResolveEndpoint(
            settings.LlmApiBaseUrl, LlmApiProvider.ParseProtocol(settings.LlmApiProtocolName)));

    /// <summary>The two llmapi protocols this app speaks, in the wording its docs use.</summary>
    public const string AnthropicProtocolOption = "Anthropic";

    public const string OpenAiProtocolOption = "OpenAI";

    private static readonly ProviderChoiceSpec LlmApiProtocolChoice = new(
        "Protocol",
        "Anthropic is what Claude Code itself uses and the only one that carries images inside tool "
        + "results; OpenAI is the compatible route. The relay's third protocol, Gemini, is text-only "
        + "— its docs say tool calls are not mapped — so it is not offered here.",
        [AnthropicProtocolOption, OpenAiProtocolOption],
        static settings => LlmApiProvider.ParseProtocol(settings.LlmApiProtocolName) == LlmApiProtocol.OpenAi
            ? OpenAiProtocolOption
            : AnthropicProtocolOption,
        static (settings, value) => settings.LlmApiProtocolName = value);

    private static readonly ProviderEndpointSpec OpenRouterEndpoint = new(
        "The documented API root. A bare host works too — the path is resolved from it.",
        static settings => settings.OpenRouterBaseUrl,
        static (settings, url) => settings.OpenRouterBaseUrl = url,
        [new("OpenRouter API", OpenRouterProvider.DefaultBaseUrl)],
        static settings => OpenAiCompatibleEndpoint.Resolve(
            settings.OpenRouterBaseUrl, OpenRouterProvider.DefaultBaseUrl));

    private static readonly ProviderEndpointSpec TokenRouterEndpoint = new(
        "The documented API root. A bare host works too — the path is resolved from it.",
        static settings => settings.TokenRouterBaseUrl,
        static (settings, url) => settings.TokenRouterBaseUrl = url,
        [new("TokenRouter API", TokenRouterProvider.DefaultBaseUrl)],
        static settings => OpenAiCompatibleEndpoint.Resolve(
            settings.TokenRouterBaseUrl, TokenRouterProvider.DefaultBaseUrl));

    private static readonly ProviderEndpointSpec DeepSeekEndpoint = new(
        "Either shape reaches the same API: the vendor documents \"/v1\" as an alias for the "
        + "OpenAI SDK, unrelated to the model version.",
        static settings => settings.DeepSeekBaseUrl,
        static (settings, url) => settings.DeepSeekBaseUrl = url,
        [
            new("DeepSeek API", DeepSeekProvider.DefaultBaseUrl),
            new("With /v1", DeepSeekProvider.VersionedBaseUrl),
        ],
        static settings => OpenAiCompatibleEndpoint.Resolve(
            settings.DeepSeekBaseUrl, DeepSeekProvider.DefaultBaseUrl));

    private static readonly ProviderEndpointSpec ZhipuEndpoint = new(
        "Two roots serve the same models to two separately-issued keys, so pick the one your "
        + "key was issued on: a mismatch reads as an authentication failure.",
        static settings => settings.ZhipuBaseUrl,
        static (settings, url) => settings.ZhipuBaseUrl = url,
        [
            new("Z.ai (international)", ZhipuProvider.DefaultBaseUrl),
            new("BigModel (China)", ZhipuProvider.ChinaBaseUrl),
        ],
        static settings => OpenAiCompatibleEndpoint.Resolve(
            settings.ZhipuBaseUrl, ZhipuProvider.DefaultBaseUrl));

    private static readonly ProviderEndpointSpec MiniMaxEndpoint = new(
        "Two roots serve the same models to two separately-issued keys, so pick the one your "
        + "key was issued on: a mismatch reads as an authentication failure.",
        static settings => settings.MiniMaxBaseUrl,
        static (settings, url) => settings.MiniMaxBaseUrl = url,
        [
            new("MiniMax (global)", MiniMaxProvider.DefaultBaseUrl),
            new("MiniMax (China)", MiniMaxProvider.ChinaBaseUrl),
        ],
        static settings => OpenAiCompatibleEndpoint.Resolve(
            settings.MiniMaxBaseUrl, MiniMaxProvider.DefaultBaseUrl));

    /// <summary>
    /// Presentation for the providers this build ships, in the order the panel lists them.
    /// A provider missing from this table still appears — it falls back to its engine name
    /// and a plain API-key card.
    /// </summary>
    public static readonly IReadOnlyList<ProviderPresentation> Presentations =
    [
        new("anthropic",
            "Claude models over the Anthropic API.",
            CredentialUrl: "https://console.anthropic.com/settings/keys"),
        new("openai",
            "GPT models over the OpenAI API.",
            CredentialUrl: "https://platform.openai.com/api-keys"),
        new("gemini",
            "Gemini models over Google AI Studio.",
            CredentialUrl: "https://aistudio.google.com/apikey"),
        new("ollama",
            "Your own machine or the Ollama cloud — the only provider that runs models locally.",
            CredentialUrl: "https://ollama.com/settings/keys",
            Endpoint: OllamaEndpoint),
        new("nvidia",
            "The build.nvidia.com catalog, or a NIM container you host yourself.",
            CredentialUrl: "https://build.nvidia.com",
            Endpoint: NvidiaEndpoint),
        new("kimi",
            "Moonshot AI's Kimi models.",
            CredentialUrl: "https://platform.moonshot.ai/console/api-keys"),
        new(OpenRouterProvider.ProviderId,
            "One key in front of many vendors’ models, addressed as vendor/model.",
            CredentialUrl: "https://openrouter.ai/keys",
            Endpoint: OpenRouterEndpoint),
        new(TokenRouterProvider.ProviderId,
            "One key across several vendors, addressed as vendor:model or through its routing "
            + "aliases (auto:quality, auto:cost).",
            CredentialUrl: "https://tokenrouter.io",
            Endpoint: TokenRouterEndpoint),
        new(DeepSeekProvider.ProviderId,
            "DeepSeek’s own API. Its thinking is a switch plus a depth dial, both sent for you.",
            CredentialUrl: "https://platform.deepseek.com/api_keys",
            Endpoint: DeepSeekEndpoint),
        new(ZhipuProvider.ProviderId,
            "The GLM models, from Z.ai internationally or BigModel in mainland China.",
            CredentialUrl: "https://z.ai/manage-apikey/apikey-list",
            Endpoint: ZhipuEndpoint),
        new(MiniMaxProvider.ProviderId,
            "MiniMax’s own models over its OpenAI-compatible API.",
            CredentialUrl: "https://platform.minimax.io/user-center/basic-information/interface-key",
            Endpoint: MiniMaxEndpoint),
        new("bedrock",
            "Claude through your AWS account. The key slot takes the secret access key; model ids "
            + "are the account's inference profiles (us.anthropic.…-v1:0), added below per region.",
            CredentialUrl: "https://console.aws.amazon.com/iam/home#/security_credentials",
            Fields:
            [
                new ProviderFieldSpec(
                    "Access key ID",
                    "The AKIA…/ASIA… id paired with the stored secret access key.",
                    "AKIA…",
                    static settings => settings.BedrockAccessKeyId,
                    static (settings, value) => settings.BedrockAccessKeyId = value),
                new ProviderFieldSpec(
                    "Region",
                    "Where your Bedrock model access lives.",
                    "us-east-1",
                    static settings => settings.BedrockRegion,
                    static (settings, value) => settings.BedrockRegion = value),
            ]),
        new("vertex",
            "Claude through Google Vertex AI. The key slot takes a service-account JSON key file's "
            + "contents (or a ready access token); model ids are the Vertex form (claude-…@YYYYMMDD), "
            + "added below per project.",
            CredentialUrl: "https://console.cloud.google.com/iam-admin/serviceaccounts",
            Fields:
            [
                new ProviderFieldSpec(
                    "Project ID",
                    "The Google Cloud project with Vertex AI Claude enabled.",
                    "my-project-id",
                    static settings => settings.VertexProjectId,
                    static (settings, value) => settings.VertexProjectId = value),
                new ProviderFieldSpec(
                    "Region",
                    "A Vertex location with Claude capacity, or \"global\".",
                    "us-east5",
                    static settings => settings.VertexRegion,
                    static (settings, value) => settings.VertexRegion = value),
            ]),
        new(LlmApiProvider.ProviderId,
            "One sk-… key for the full Claude lineup through the llmapi.pro relay, metered by "
            + "subscription rather than per token.",
            CredentialUrl: "https://llmapi.pro/dashboard",
            Endpoint: LlmApiEndpoint,
            Choice: LlmApiProtocolChoice,
            ModelIdPrefix: LlmApiProvider.ModelIdPrefix),
        new(ChatGptWebProvider.ProviderId,
            "Signs in with your own chatgpt.com session instead of a key. Messages are typed into the "
            + "real page, so tools and pictures ride the message itself and token counts are estimates.",
            Credential: ProviderCredentialKind.CookieExport),
    ];

    /// <summary>
    /// One entry per provider the user can act on: everything registered in this build,
    /// then anything settings still mention (a key or a custom model left behind by a
    /// provider that is gone), so nothing becomes invisible and unremovable.
    /// </summary>
    public static IReadOnlyList<ProviderEntry> Build(
        IEnumerable<ProviderRuntime> registered,
        AppSettings settings,
        IEnumerable<ModelInfo> models)
    {
        var runtimes = new Dictionary<string, ProviderRuntime>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtime in registered)
        {
            runtimes[runtime.Id] = runtime;
        }

        var allModels = models.ToList();
        var customIds = new HashSet<string>(
            settings.CustomModels.Select(static m => m.ProviderId), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        void Add(string id)
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        foreach (var presentation in Presentations.Where(p => runtimes.ContainsKey(p.Id)))
        {
            Add(presentation.Id);
        }

        // The user's own follow this build's own, in the order they were added rather
        // than alphabetically: that order is the only one they chose.
        foreach (var spec in settings.CustomProviders)
        {
            var customId = CustomProviders.NormalizeId(spec.Id);
            if (runtimes.ContainsKey(customId))
            {
                Add(customId);
            }
        }

        foreach (var runtime in runtimes.Values.OrderBy(static r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            Add(runtime.Id);
        }

        foreach (var leftover in settings.ApiKeySets.Keys
                     .Concat(customIds)
                     .Where(id => !runtimes.ContainsKey(id))
                     .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase))
        {
            Add(leftover);
        }

        return [.. ordered.Select(id => Entry(id, runtimes, settings, allModels))];
    }

    /// <summary>
    /// The models one provider serves, in catalog order, flagged with whether the user
    /// added them (only those can be removed again).
    /// </summary>
    public static IReadOnlyList<ProviderModel> ModelsFor(
        string providerId, AppSettings settings, IEnumerable<ModelInfo> models) =>
        [.. models
            .Where(m => m.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            .Select(m => new ProviderModel(
                m,
                IsCustom(settings, m),
                settings.ModelContextOverrides.ContainsKey(m.ModelId)))];

    /// <summary>
    /// Context windows as a card shows them: "200k", and "1M" rather than "1000k" once a
    /// model reaches a million tokens.
    /// </summary>
    public static string FormatContext(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000}k",
        _ => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Smallest context window worth setting; below it the app's own reserve dwarfs it.</summary>
    public const int MinContextWindow = 1_000;

    /// <summary>
    /// Upper bound for a typed context window. Nothing published comes close, so anything larger
    /// is a slipped digit — and an absurd number would silently switch auto-compaction off.
    /// </summary>
    public const int MaxContextWindow = 20_000_000;

    /// <summary>
    /// The context window typed text commits to, or null while it is not a usable number. Unlike
    /// a blank field elsewhere, an unusable value is never clamped: the number decides when a
    /// conversation gets compacted, so a typo must leave the old one standing.
    /// </summary>
    public static int? ParseContextWindow(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var typed)
        && typed is >= MinContextWindow and <= MaxContextWindow
            ? typed
            : null;

    /// <summary>
    /// Qualifies a model id the way its provider's catalog entries are qualified. A relay that
    /// serves another vendor's model names needs the prefix or the id collides with the vendor's
    /// own entry, and a session — which stores nothing but the id — would resolve to the wrong
    /// provider. An id that already carries it is left alone.
    /// </summary>
    public static string ApplyModelIdPrefix(string? prefix, string modelId) =>
        string.IsNullOrEmpty(prefix) || modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId
            : prefix + modelId;

    /// <summary>Filter for the panel's search box: matches the name, the id or the tagline.</summary>
    public static bool Matches(ProviderEntry entry, string query)
    {
        var trimmed = query.Trim();
        return trimmed.Length == 0
               || entry.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
               || entry.Id.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
               || entry.Tagline.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rotate-after value typed text commits to: null while the text is still being typed
    /// and not yet a usable number, so a half-typed "1" on the way to "150" is neither saved
    /// nor snapped to the floor. A final commit clamps instead, because anything below the
    /// floor would rotate faster than a conversation can be useful.
    /// </summary>
    public static int? ParseRotateAfter(string text, bool final)
    {
        const int minimum = IChatGptWebOptions.MinimumRotateAfterMessages;
        return int.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var typed)
               && typed >= minimum
            ? typed
            : final ? minimum : null;
    }

    private static ProviderEntry Entry(
        string id,
        IReadOnlyDictionary<string, ProviderRuntime> runtimes,
        AppSettings settings,
        IReadOnlyList<ModelInfo> models)
    {
        var runtime = runtimes.GetValueOrDefault(id);
        var presentation =
            Presentations.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? CustomPresentation(id, settings);

        return new ProviderEntry(
            Id: id,
            Name: runtime?.DisplayName ?? id,
            Tagline: presentation?.Tagline ?? (runtime is null
                ? "Left behind in settings.json by a provider this build does not have."
                : ""),
            Credential: presentation?.Credential ?? ProviderCredentialKind.ApiKeys,
            IsRegistered: runtime is not null,
            RequiresCredential: runtime?.RequiresApiKey ?? true,
            CredentialCount: settings.GetApiKeys(id).Count,
            Models: ModelsFor(id, settings, models),
            Endpoint: presentation?.Endpoint,
            CredentialUrl: presentation?.CredentialUrl,
            Choice: presentation?.Choice,
            ModelIdPrefix: presentation?.ModelIdPrefix,
            Fields: presentation?.Fields,
            Toggles: presentation?.Toggles,
            IsUserDefined: FindCustom(settings, id) is not null);
    }

    /// <summary>
    /// The stored definition for a hand-written provider, or null when the id belongs to
    /// one this build ships (or to nothing at all).
    /// </summary>
    public static CustomProviderSpec? FindCustom(AppSettings settings, string providerId)
    {
        var normalized = CustomProviders.NormalizeId(providerId);
        return settings.CustomProviders.FirstOrDefault(
            spec => CustomProviders.NormalizeId(spec.Id) == normalized);
    }

    /// <summary>
    /// The card for a provider the user wrote. Unlike the built-in presentations this one
    /// is per-instance: every accessor reaches back into that entry’s own stored
    /// definition, so several custom providers each edit their own row rather than a
    /// shared setting.
    /// </summary>
    public static ProviderPresentation? CustomPresentation(string providerId, AppSettings settings)
    {
        if (FindCustom(settings, providerId) is null)
        {
            return null;
        }

        var id = CustomProviders.NormalizeId(providerId);
        return new ProviderPresentation(
            id,
            "Added by you. Keys, models and sessions are stored against the id, so the id and "
            + "the name stay as they were entered — remove and re-add to change them.",
            Endpoint: new ProviderEndpointSpec(
                "The API root. The protocol above decides what is appended to it, so either the "
                + "root or a full endpoint pasted whole will do.",
                s => FindCustom(s, id)?.BaseUrl ?? "",
                (s, url) => Update(s, id, spec => spec.BaseUrl = url),
                [],
                s => ResolveCustomEndpoint(FindCustom(s, id))),
            Choice: new ProviderChoiceSpec(
                "Protocol",
                "Which wire the endpoint answers on. OpenAI is chat completions and is what most "
                + "compatible servers speak; Anthropic is the Messages API, and the only one that "
                + "carries images inside tool results.",
                CustomProviderProtocols.All,
                s => CustomProviderProtocols.Normalize(FindCustom(s, id)?.ProtocolName),
                (s, value) => Update(s, id, spec => spec.ProtocolName = value)),
            Toggles:
            [
                new ProviderToggleSpec(
                    "Needs an API key",
                    "Off for a server that answers unauthenticated requests, so a call is not "
                    + "refused for a key it never wanted. A saved key is still sent either way.",
                    s => FindCustom(s, id)?.RequiresApiKey ?? true,
                    (s, value) => Update(s, id, spec => spec.RequiresApiKey = value)),
            ]);
    }

    /// <summary>Where a custom provider’s requests land, for the card’s resolved-endpoint line.</summary>
    public static string ResolveCustomEndpoint(CustomProviderSpec? spec)
    {
        if (spec is null)
        {
            return "";
        }

        return CustomProviderProtocols.IsAnthropic(spec.ProtocolName)
            ? CustomProviderFactory.ResolveMessagesEndpoint(spec.BaseUrl)
            : OpenAiCompatibleEndpoint.Resolve(spec.BaseUrl, spec.BaseUrl);
    }

    /// <summary>
    /// Removes a hand-written provider along with everything filed under its id. Leaving
    /// the keys and models behind would show a card claiming the build has no such
    /// provider, which is a confusing way to describe something the user just deleted.
    /// </summary>
    public static void RemoveCustom(AppSettings settings, string providerId)
    {
        var id = CustomProviders.NormalizeId(providerId);
        settings.CustomProviders.RemoveAll(spec => CustomProviders.NormalizeId(spec.Id) == id);
        settings.ApiKeySets.Remove(id);
        settings.ApiKeys.Remove(id);
        foreach (var model in settings.CustomModels
                     .Where(m => m.ProviderId.Equals(id, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            settings.ModelContextOverrides.Remove(model.ModelId);
            settings.CustomModels.Remove(model);
        }
    }

    private static void Update(AppSettings settings, string providerId, Action<CustomProviderSpec> edit)
    {
        if (FindCustom(settings, providerId) is { } spec)
        {
            edit(spec);
        }
    }

    private static bool IsCustom(AppSettings settings, ModelInfo model) =>
        settings.CustomModels.Any(custom =>
            custom.ModelId.Equals(model.ModelId, StringComparison.OrdinalIgnoreCase)
            && custom.ProviderId.Equals(model.ProviderId, StringComparison.OrdinalIgnoreCase));
}
