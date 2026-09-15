using JarvisCode.Core.Models;

namespace JarvisCode.Core.Settings;

/// <summary>User-editable application settings, persisted as JSON in app data.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Legacy single key per provider. Kept only so older settings files still
    /// deserialize; the store migrates entries into <see cref="ApiKeySets"/> on
    /// load and no longer writes this field.
    /// </summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ordered API keys per provider id, encrypted at rest. Multiple keys let the
    /// app rotate to the next one when a request is rate limited.
    /// </summary>
    public Dictionary<string, List<string>> ApiKeySets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Messages API root; the CLI may override it for an explicitly selected endpoint.</summary>
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>
    /// Base URL of the Ollama server. Defaults to the official hosted service,
    /// which needs an API key; set it to http://localhost:11434 (or another host)
    /// for a self-hosted server, where keys are not used.
    /// Kept in sync with OllamaProvider.DefaultBaseUrl (Core cannot reference it).
    /// </summary>
    public string OllamaBaseUrl { get; set; } = "https://ollama.com";

    /// <summary>
    /// Base URL of the NVIDIA chat-completions API. Defaults to the hosted
    /// build.nvidia.com catalog, which needs an API key; point it at a
    /// self-hosted NIM container (e.g. http://localhost:8000) to use that
    /// instead, where keys are not required.
    /// Kept in sync with NvidiaProvider.DefaultBaseUrl (Core cannot reference it).
    /// </summary>
    public string NvidiaBaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";

    /// <summary>
    /// Root of the OpenRouter API. Kept in sync with OpenRouterProvider.DefaultBaseUrl
    /// (Core cannot reference the providers project).
    /// </summary>
    public string OpenRouterBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Root of the TokenRouter gateway. Kept in sync with TokenRouterProvider.DefaultBaseUrl.
    /// </summary>
    public string TokenRouterBaseUrl { get; set; } = "https://api.tokenrouter.io/v1";

    /// <summary>
    /// Root of the DeepSeek API. The version segment is optional there — "/v1" is
    /// documented as an OpenAI-SDK alias, unrelated to the model version — so both
    /// shapes resolve. Kept in sync with DeepSeekProvider.DefaultBaseUrl.
    /// </summary>
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>
    /// Root of the Zhipu AI v4 API: api.z.ai internationally, open.bigmodel.cn in
    /// mainland China. The two issue their own keys, so this is a real choice rather
    /// than a mirror. Kept in sync with ZhipuProvider.DefaultBaseUrl.
    /// </summary>
    public string ZhipuBaseUrl { get; set; } = "https://api.z.ai/api/paas/v4";

    /// <summary>
    /// Root of the MiniMax API: api.minimax.io globally, api.minimaxi.com in mainland
    /// China, again with separately-issued keys. Kept in sync with
    /// MiniMaxProvider.DefaultBaseUrl.
    /// </summary>
    public string MiniMaxBaseUrl { get; set; } = "https://api.minimax.io/v1";

    /// <summary>
    /// Root of the llmapi.pro relay. Any of the three base URLs its docs give works — the
    /// provider resolves the path for the protocol below, so a missing or extra /v1 (the
    /// vendor's most common support case) cannot produce a 404 here.
    /// Kept in sync with LlmApiProvider.DefaultBaseUrl (Core cannot reference it).
    /// </summary>
    public string LlmApiBaseUrl { get; set; } = "https://llmapi.pro";

    /// <summary>
    /// Which llmapi protocol to speak: "Anthropic" (the one Claude Code uses, and the only
    /// one that carries images inside tool results) or "OpenAI". Unknown values fall back to
    /// Anthropic. The relay's Gemini protocol is deliberately absent: it is documented as text
    /// chat with no tool calls, which the Code surface cannot use.
    /// </summary>
    public string LlmApiProtocolName { get; set; } = "Anthropic";

    /// <summary>
    /// AWS region of the Bedrock runtime. The secret access key lives in the normal
    /// (protected) key slot for "bedrock"; only the region and key ID are plain config.
    /// </summary>
    public string BedrockRegion { get; set; } = "us-east-1";

    /// <summary>The AWS access key ID paired with the stored Bedrock secret access key.</summary>
    public string BedrockAccessKeyId { get; set; } = "";

    /// <summary>The Google Cloud project Vertex AI Claude models are enabled in.</summary>
    public string VertexProjectId { get; set; } = "";

    /// <summary>Vertex location (us-east5, europe-west1, …) or "global" for the global endpoint.</summary>
    public string VertexRegion { get; set; } = "us-east5";

    /// <summary>
    /// ChatGPT project every browser-session chat is created in, by name. Blank leaves chats
    /// outside any project. Not a secret, so unlike the cookies it is stored in the clear.
    /// </summary>
    public string ChatGptProjectName { get; set; } = "";

    /// <summary>
    /// The gizmo id ("g-p-…") the project name resolved to, remembered so a restart reuses the
    /// same project instead of trusting a name lookup again. Cleared whenever the name is edited.
    /// </summary>
    public string ChatGptProjectId { get; set; } = "";

    /// <summary>
    /// How many messages the app may send into one ChatGPT chat before it deletes that chat and
    /// starts a fresh one. Counts what the app sends, not the replies. Core cannot see the
    /// provider, so this default is repeated rather than shared; a test pins the two together.
    /// </summary>
    public int ChatGptRotateAfterMessages { get; set; } = 100;

    /// <summary>
    /// The ChatGPT chats the browser session is keeping the thread of, newest first. Held here
    /// rather than in memory alone so a restart carries on in the chat it was in instead of opening
    /// a second one and pasting the whole conversation into it.
    /// </summary>
    public List<ChatGptChatEntry> ChatGptChats { get; set; } = [];

    public string? DefaultModelId { get; set; }

    public string? LastWorkingDirectory { get; set; }

    public int ShellTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Which system prompt a model on another provider receives — one the
    /// reference's own tables cannot place at all, so neither its family rule
    /// nor its catalog has an answer for it.
    /// </summary>
    /// <remarks>
    /// <c>classic</c>, the default, is the reference's answer for a model it
    /// does not recognise, and the tier-equivalence answer besides: the lean
    /// document marks a model its vendor tuned for the shorter form rather than
    /// a capability tier, and claude-sonnet-5 is the control — a frontier Claude
    /// 5 model that still takes the classic form. <c>lean</c> sends the shorter
    /// <c># Harness</c> document instead. Claude models are never decided by
    /// this: they keep the form the reference gives them.
    /// </remarks>
    public string OtherProviderPromptForm { get; set; } = "classic";

    /// <summary>
    /// Whether this build resolves reminder-text overrides at all.
    /// </summary>
    /// <remarks>
    /// The reference resolves the text of its two post-tool-result reminders
    /// through a chain — an environment variable, then a <c>client_data</c> map
    /// its server delivers, then the model's own default. On by default, which
    /// is the reference's behaviour: it reads those variables. Off resolves
    /// neither override, leaving whatever text the model itself owns.
    /// </remarks>
    public bool ReminderOverridesEnabled { get; set; } = true;

    /// <summary>
    /// Where an enabled override may come from: <c>environment</c>, the
    /// reference's own first tier and nothing more, or <c>file</c>, which adds a
    /// local stand-in for the server tier this build has no counterpart for.
    /// </summary>
    /// <remarks>
    /// The reference's second tier is a <c>client_data</c> blob its config
    /// endpoint serves per account; there is no such endpoint here, so
    /// <c>file</c> reads the same model-pattern-to-text shape from a file in the
    /// profile instead. <c>environment</c> is the default: it is exactly what
    /// the reference does on an account the server sends no override to, which
    /// is every account measured.
    /// </remarks>
    public string ReminderOverrideSource { get; set; } = "environment";

    /// <summary>
    /// Summarize older history automatically when a Code turn approaches the
    /// model's context window (the reference app's auto-compact, on by default).
    /// </summary>
    public bool AutoCompactEnabled { get; set; } = true;

    /// <summary>
    /// The auto-compact window in tokens (/autocompact, the reference's
    /// <c>autoCompactWindow</c> user setting). Null is the reference's "auto":
    /// the model's own window decides. Valid values are 100k–1M, and the
    /// model's window still caps whatever is stored here.
    /// </summary>
    public int? AutoCompactWindow { get; set; }

    /// <summary>
    /// Write the next compaction's summary in the background once the context
    /// passes the arm point (the reference's <c>precomputeCompactionEnabled</c>).
    /// Off by default, because the flag the reference gates it behind ships
    /// false: turning it on spends a summarization the turn may never need.
    /// </summary>
    public bool PrecomputeCompactionEnabled { get; set; }

    /// <summary>
    /// Keep a precomputed summary on disk beside its session, so a later process
    /// can compact without paying for the summarization again (the reference's
    /// precompact sidecar). Off by default and independent of the background
    /// summary itself, exactly as the reference gates the two separately.
    /// </summary>
    public bool PrecomputeSidecarEnabled { get; set; }

    /// <summary>Web search: the vendor's server tool in chat, the WebSearch tool in Code sessions.</summary>
    public bool EnableWebSearch { get; set; } = true;

    /// <summary>
    /// SearXNG instance the web_search tool queries. Any instance works as long as its
    /// settings.yml enables the json format; the value may be the root or already point
    /// at /search.
    /// </summary>
    public string SearxngBaseUrl { get; set; } = "https://jarvis-searxng.vercel.app";

    /// <summary>
    /// URL patterns an HTTP hook may post to (the reference's allowedHttpHookUrls).
    /// Null means unconfigured, which allows any URL the SSRF guard permits; an
    /// empty list allows none.
    /// </summary>
    public List<string>? AllowedHttpHookUrls { get; set; }

    /// <summary>
    /// Environment variable names an HTTP hook's headers may interpolate, on top
    /// of the hook's own allowedEnvVars (the reference's httpHookAllowedEnvVars).
    /// Null leaves the hook's own list alone.
    /// </summary>
    public List<string>? HttpHookAllowedEnvVars { get; set; }

    /// <summary>
    /// Glob patterns, or absolute paths, of instruction files that must not be
    /// loaded (the reference's claudeMdExcludes). Matched against the absolute
    /// path with forward slashes; honoured for the user's, the project's and the
    /// private tiers, never for an organization policy file.
    /// </summary>
    public List<string> InstructionFileExcludes { get; set; } = [];

    /// <summary>Permission rules, one "allow|deny tool [pattern]" per line; invalid lines are ignored.</summary>
    public List<string> PermissionRuleLines { get; set; } = [];

    /// <summary>Last selected permission mode (Auto/Manual/AcceptEdits/Plan/Bypass); unknown values fall back to Auto.</summary>
    public string PermissionModeName { get; set; } = "Auto";

    /// <summary>Last selected reasoning effort (Off/Low/Medium/High); unknown values fall back to Off.</summary>
    public string ThinkingEffortName { get; set; } = "Off";

    /// <summary>
    /// The reference's <c>totalTokensReminder</c> setting: off | infinite | fixed |
    /// countdown | padded-countdown. Null — the default — resolves to
    /// padded-countdown, as the reference does; CLAUDE_CODE_TOTAL_TOKENS_REMINDER
    /// wins over it.
    /// </summary>
    public string? TotalTokensReminder { get; set; }

    /// <summary>The reference's <c>totalTokensReminderBudget</c>; null means 15000000.</summary>
    public long? TotalTokensReminderBudget { get; set; }

    /// <summary>
    /// The reference's <c>totalTokensReminderAfterUserTurn</c>: whether the block
    /// also follows each regular user prompt. Null means on.
    /// </summary>
    public bool? TotalTokensReminderAfterUserTurn { get; set; }

    /// <summary>
    /// The reference's <c>permissions.blockReadsOutsideWorkingDirectories</c>: the
    /// file tools refuse reads outside the working directories in every project.
    /// Set by the "block from now on" answer to the one-time auto-mode question.
    /// </summary>
    public bool BlockReadsOutsideWorkingDirectories { get; set; }

    /// <summary>
    /// The reference's <c>timeFormat</c>: "auto" (or null), "12-hour", "24-hour",
    /// "24-hour-utc", or a strftime pattern (containing %) for the turn-end clock
    /// and transcript timestamps.
    /// </summary>
    public string? TimeFormat { get; set; }

    /// <summary>The reference's <c>timeZone</c>: an IANA zone the clocks are shown in; null means local.</summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// The reference's <c>language</c> setting, which its own schema describes as
    /// "Preferred language for Claude responses and voice dictation (e.g.
    /// \"japanese\", \"spanish\")". A non-empty value puts the reference's
    /// <c># Language</c> block in the system prompt; null or empty sends none.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// The reference's per-model default effort (its <c>/effort</c> saves the level
    /// per model, so each model keeps its own when the user switches). Keyed by
    /// model id; the value is one of the effort names.
    /// </summary>
    public Dictionary<string, string> EffortByModel { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Opaque provider controls remembered per model. The host discovers and
    /// renders these options; Core persists their string keys and values without
    /// requiring a release whenever a provider adds another control.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ProviderOptionsByModel { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Providers the user defined by hand: an endpoint plus which of the two protocols
    /// this app already speaks it answers on. Entries whose id collides with a built-in
    /// provider, or whose URL is unusable, are skipped when the registry is assembled
    /// rather than registered and failing on every call.
    /// </summary>
    public List<CustomProviderSpec> CustomProviders { get; set; } = [];

    /// <summary>Extra models the user added on top of the built-in catalog.</summary>
    public List<ModelInfo> CustomModels { get; set; } = [];

    /// <summary>
    /// Context window per model id, overriding the catalog's figure. The built-in numbers are
    /// what each vendor publishes, but a plan or a relay can serve a different window and the
    /// entries themselves are not editable, so this is how one is corrected. Only the app's own
    /// arithmetic uses it — the context bar and the auto-compaction threshold — never the wire.
    /// </summary>
    public Dictionary<string, int> ModelContextOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetApiKeys(string providerId) =>
        ApiKeySets.TryGetValue(providerId, out var keys)
            ? [.. keys.Where(k => !string.IsNullOrWhiteSpace(k))]
            : [];

    public string? GetApiKey(string providerId)
    {
        var keys = GetApiKeys(providerId);
        return keys.Count > 0 ? keys[0] : null;
    }
}
