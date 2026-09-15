using JarvisCode.Core.Models;

namespace JarvisCode.Core.Providers;

/// <summary>
/// Streaming events emitted by a provider while producing one assistant message.
/// The orchestrator folds these into a complete <see cref="ChatMessage"/>.
/// </summary>
public abstract record ProviderEvent;

/// <summary>A fragment of assistant text.</summary>
public sealed record TextDeltaEvent(string Delta, int? Index = null) : ProviderEvent;

/// <summary>
/// A replaceable, display-only answer snapshot. Never persisted as conversation content or
/// interpreted as tool input. An empty snapshot clears it; authoritative text arrives separately.
/// </summary>
public sealed record TextPreviewEvent(string Text) : ProviderEvent;

/// <summary>A source annotation for an indexed text block, preserved verbatim for replay.</summary>
public sealed record CitationDeltaEvent(int Index, TextCitation Citation) : ProviderEvent;

/// <summary>The model started a tool call; arguments stream separately.</summary>
public sealed record ToolCallStartedEvent(int Index, string Id, string Name) : ProviderEvent;

/// <summary>A fragment of the JSON arguments for the tool call at <paramref name="Index"/>.</summary>
public sealed record ToolCallArgumentsDeltaEvent(int Index, string Delta) : ProviderEvent;

/// <summary>A fragment of model reasoning for live display.</summary>
public sealed record ThinkingDeltaEvent(int Index, string Delta) : ProviderEvent;

/// <summary>A reasoning block finished; <paramref name="Signature"/> enables replay.</summary>
public sealed record ThinkingCompletedEvent(int Index, string Thinking, string? Signature) : ProviderEvent;

/// <summary>An opaque vendor block to keep verbatim for replay (server tools, redacted thinking).</summary>
public sealed record RawBlockEvent(int Index, string RawJson) : ProviderEvent;

/// <summary>The vendor started executing a server-side tool (web search etc.) — display only.</summary>
public sealed record ServerToolNoticeEvent(string ToolName) : ProviderEvent;

/// <summary>The response finished. <paramref name="WantsToolUse"/> is true when the model stopped to call tools.</summary>
/// <param name="StopReason">
/// The vendor's own stop reason when it reports one ("end_turn", "tool_use",
/// "max_tokens", "stop_sequence"), normalized onto Anthropic's spelling.
/// Null when the provider does not say, which reads as "no opinion": the
/// orchestrator's recovery ladder only acts on a reason it was actually given.
/// </param>
public sealed record ResponseCompletedEvent(bool WantsToolUse, Usage Usage, string? StopReason = null) : ProviderEvent;

/// <summary>
/// The stop reasons the orchestrator's recovery ladder reads, spelled the way
/// Anthropic's Messages API spells them. Providers on the OpenAI wire report a
/// different vocabulary for the same states, so they map onto these.
/// </summary>
public static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string ToolUse = "tool_use";
    public const string MaxTokens = "max_tokens";
    public const string StopSequence = "stop_sequence";

    /// <summary>
    /// The model declined to answer. A deliberate stop, so the ladder never
    /// re-prompts it — the turn ends and the user is told.
    /// </summary>
    public const string Refusal = "refusal";

    /// <summary>The request outgrew the model's context window mid-call.</summary>
    public const string ModelContextWindowExceeded = "model_context_window_exceeded";

    /// <summary>
    /// OpenAI's finish_reason onto the names above. An unknown value answers
    /// null: the ladder must not act on a state it cannot name, and a wrong
    /// guess would re-prompt a model that had simply stopped. "content_filter"
    /// is the one it can name — it is that wire's word for a refusal.
    /// </summary>
    public static string? FromOpenAiFinishReason(string? finishReason) => finishReason switch
    {
        "stop" => EndTurn,
        "length" => MaxTokens,
        "tool_calls" => ToolUse,
        "function_call" => ToolUse,
        "content_filter" => Refusal,
        _ => null,
    };
}
