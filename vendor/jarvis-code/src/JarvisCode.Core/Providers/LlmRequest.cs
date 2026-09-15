using System.Text.Json.Nodes;
using JarvisCode.Core.Models;

namespace JarvisCode.Core.Providers;

/// <summary>
/// How much extended reasoning ("thinking") to request from the model.
/// Off sends nothing, keeping requests identical to pre-effort behavior.
/// </summary>
public enum ThinkingEffort
{
    Off,
    Low,
    Medium,
    High,

    /// <summary>The reference CLI's "xhigh" ("Extra high") rung.</summary>
    XHigh,

    /// <summary>The reference CLI's top "max" rung.</summary>
    Max,
}

/// <summary>
/// A system block that rides ahead of the main prompt, as its own entry in the
/// wire's system array — the reference sends its <c># Reporting outcomes</c>
/// block this way, uncached, between its identity line and the prompt proper.
/// </summary>
/// <param name="Text">The block's text.</param>
/// <param name="Cached">Whether the block carries a prompt-cache breakpoint.</param>
public sealed record SystemPromptBlock(string Text, bool Cached = false);

/// <summary>Everything a provider needs to produce one assistant response.</summary>
public sealed record LlmRequest
{
    /// <summary>
    /// Stable local conversation identity for stateful transports. Forks and auxiliary model
    /// calls use distinct identities; null asks for an independent, disposable conversation.
    /// This value is routing metadata and must not be sent as prompt text.
    /// </summary>
    public string? ConversationScopeId { get; init; }

    public required string ModelId { get; init; }
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// System blocks sent ahead of <see cref="SystemPrompt"/>, in order. Empty —
    /// the default — sends the prompt alone, exactly as before. An adapter whose
    /// wire has a single system string folds them in front of it.
    /// </summary>
    public IReadOnlyList<SystemPromptBlock> LeadingSystemBlocks { get; init; } = [];

    /// <summary>
    /// The prompt-cache TTL to ask for ("5m" or "1h"); null — the default — leaves
    /// the cache breakpoints at the vendor's own default lifetime. Adapters
    /// without a cache dial ignore it.
    /// </summary>
    public string? CacheTtl { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public int MaxOutputTokens { get; init; } = 8192;

    /// <summary>Ask the vendor to run its server-side web search (Anthropic only today).</summary>
    public bool EnableWebSearch { get; init; }

    /// <summary>
    /// Reasoning budget. Providers map it to their own dial (Anthropic
    /// thinking.budget_tokens, OpenAI reasoning_effort, Gemini thinkingConfig,
    /// Ollama think) and omit it for models that cannot reason.
    /// </summary>
    public ThinkingEffort ThinkingEffort { get; init; } = ThinkingEffort.Off;

    /// <summary>
    /// Provider-defined request options discovered by the host for this model.
    /// Keys and values stay opaque to Core so a provider can evolve its controls
    /// without adding a new request property for every option.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderOptions { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Hand edits from the request inspector, as an RFC 7386 merge patch applied to
    /// the vendor body after the adapter has built it (see
    /// <see cref="RequestBodyOverride"/>). Null — the default — sends exactly what
    /// the adapter produced.
    /// </summary>
    public JsonObject? BodyOverride { get; init; }

    /// <summary>
    /// Extra request headers the client asked to send. Empty — the default — leaves
    /// the adapter's own header set untouched; an adapter that has no use for them
    /// ignores the list.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ExtraHeaders { get; init; } = [];
}
