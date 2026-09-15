using JarvisCode.Core.Models;

namespace JarvisCode.Core.Agent;

/// <summary>
/// Events the orchestrator emits while running one user turn. Consumers fold
/// these into the transcript as they arrive.
/// </summary>
public abstract record AgentEvent;

/// <summary>A fragment of assistant text for the block currently streaming.</summary>
public sealed record AssistantTextDelta(string Delta) : AgentEvent;

/// <summary>A replaceable answer preview, excluded from authoritative conversation history.</summary>
public sealed record AssistantTextPreviewed(string Text) : AgentEvent;

/// <summary>A citation at the current claim; complete block positions arrive with the message.</summary>
public sealed record AssistantCitationDelta(TextCitation Citation) : AgentEvent;

/// <summary>The resolved child model, reported before its first streamed message.</summary>
public sealed record SubagentModelSelected(string ModelId) : AgentEvent;

/// <summary>A fragment of model reasoning, streamed as it arrives.</summary>
public sealed record AssistantThinkingDelta(string Delta) : AgentEvent;

/// <summary>The vendor is running a server-side tool (web search etc.).</summary>
public sealed record ServerToolActivity(string ToolName) : AgentEvent;

/// <summary>Display-only input streaming; the tool has not been approved or executed.</summary>
public sealed record ToolInputStarted(string CallId, string ToolName) : AgentEvent;

/// <summary>A raw JSON input fragment for a tool still being generated.</summary>
public sealed record ToolInputDelta(string CallId, string ToolName, string Delta) : AgentEvent;

/// <summary>A complete assistant message was produced (text and/or tool calls).</summary>
public sealed record AssistantMessageCompleted(ChatMessage Message) : AgentEvent;

/// <summary>
/// The assistant message just reported was taken back out of the conversation:
/// it claimed a tool call the provider never delivered, so the reference
/// tombstones it and retries rather than leaving a turn the model cannot read.
/// A host that renders messages live should drop what it rendered for it.
/// </summary>
public sealed record AssistantMessageRetracted(ChatMessage Message) : AgentEvent;

/// <summary>
/// A tool call is about to execute (already approved). <paramref name="ArgumentsJson"/>
/// is the call's raw input, for hosts that show it; it is optional so older listeners
/// and hand-built events are unaffected.
/// </summary>
public sealed record ToolExecutionStarted(
    string CallId, string ToolName, string CallDescription, string? ArgumentsJson = null) : AgentEvent;

/// <summary>
/// A tool call finished; <see cref="Result"/> is what the model will see.
/// <paramref name="Images"/> carries any screenshots the tool returned, for hosts
/// that render them inline; it is optional so older listeners are unaffected.
/// </summary>
public sealed record ToolExecutionCompleted(
    string CallId, string ToolName, string Result, bool IsError,
    IReadOnlyList<ImageBlock>? Images = null) : AgentEvent;

/// <summary>The user denied a tool call; the model is told and continues.</summary>
public sealed record ToolExecutionDenied(string CallId, string ToolName) : AgentEvent;

/// <summary>
/// Token usage after each model response. <paramref name="Total"/> accumulates
/// across the turn's iterations; <paramref name="LastCall"/> is the final call
/// alone — its input+output measures the real context size.
/// </summary>
public sealed record UsageReported(Usage Total, Usage LastCall) : AgentEvent;

/// <summary>Auto-compaction is about to summarize older history mid-turn.</summary>
public sealed record ConversationCompacting : AgentEvent;

/// <summary>
/// Microcompact ran instead of a full summarize: the contents of old tool
/// results were cleared in place (the messages list already reflects it).
/// </summary>
public sealed record ConversationMicrocompacted(int ClearedResults, long EstimatedSavedTokens) : AgentEvent;

/// <summary>
/// Auto-compaction finished: <see cref="AgentTurnContext.Messages"/> now holds
/// the continuation summary plus the kept tail. The caller owns archiving
/// <paramref name="Result"/>.Archived and persisting the session.
/// </summary>
public sealed record ConversationCompacted(CompactionResult Result) : AgentEvent;

/// <summary>The turn ended (the model produced a final answer or the loop was stopped).</summary>
public sealed record TurnCompleted(TurnEndReason Reason, string? Detail = null) : AgentEvent
{
    /// <summary>Whether an error permits automatic replay or fallback to another model.</summary>
    public bool CanRetry { get; init; } = true;
}

/// <summary>
/// Why a turn stopped. The set is the reference's own terminal-reason vocabulary
/// (its <c>q3</c> plus <c>X3</c>, nineteen in all); <see cref="TurnEndReasons.WireName"/>
/// spells each one the way the reference reports it. Members this build cannot
/// reach yet say so on themselves rather than being left to look reachable.
/// </summary>
public enum TurnEndReason
{
    /// <summary>The model produced a final answer (reference <c>completed</c>).</summary>
    Completed,

    /// <summary>The user interrupted while the model was streaming (<c>aborted_streaming</c>).</summary>
    Cancelled,

    /// <summary>The user interrupted while tool calls were running (<c>aborted_tools</c>).</summary>
    AbortedTools,

    /// <summary>The provider failed the call (<c>api_error</c>).</summary>
    Error,

    /// <summary>The turn hit an explicit turn cap (<c>max_turns</c>); unset means no cap.</summary>
    MaxIterationsReached,

    /// <summary>Two consecutive responses claimed a tool call that could not be parsed.</summary>
    MalformedToolUseExhausted,

    /// <summary>The request no longer fits the model's context window.</summary>
    PromptTooLong,

    /// <summary>A Stop hook refused to let the turn end and continuation was prevented.</summary>
    StopHookPrevented,

    /// <summary>A hook ended the turn (a PostToolBatch hook's stop, or the Stop block cap).</summary>
    HookStopped,

    /// <summary>The context ceiling was reached with no room left to compact.</summary>
    BlockingLimit,

    /// <summary>Compaction refilled the window three times in three turns.</summary>
    RapidRefillBreaker,

    /// <summary>The provider rejected an image in the request.</summary>
    ImageError,

    /// <summary>The model itself failed (refusal, overloaded, invalid response).</summary>
    ModelError,

    /// <summary>A tool asked to be deferred; not produced here — no tool defers itself.</summary>
    ToolDeferred,

    /// <summary>The turn was moved to a background agent; not produced here.</summary>
    BackgroundRequested,

    /// <summary>A spend budget ran out; not produced here — this engine does not price turns.</summary>
    BudgetExhausted,

    /// <summary>Structured-output retries ran out; not produced here — no structured-output mode.</summary>
    StructuredOutputRetryExhausted,

    /// <summary>A deferred tool could not be resolved; not produced here.</summary>
    ToolDeferredUnavailable,

    /// <summary>The turn could not be set up (context assembly failed).</summary>
    TurnSetupFailed,
}

/// <summary>The reference's snake_case spelling for each terminal reason.</summary>
public static class TurnEndReasons
{
    public static string WireName(TurnEndReason reason) => reason switch
    {
        TurnEndReason.Completed => "completed",
        TurnEndReason.Cancelled => "aborted_streaming",
        TurnEndReason.AbortedTools => "aborted_tools",
        TurnEndReason.Error => "api_error",
        TurnEndReason.MaxIterationsReached => "max_turns",
        TurnEndReason.MalformedToolUseExhausted => "malformed_tool_use_exhausted",
        TurnEndReason.PromptTooLong => "prompt_too_long",
        TurnEndReason.StopHookPrevented => "stop_hook_prevented",
        TurnEndReason.HookStopped => "hook_stopped",
        TurnEndReason.BlockingLimit => "blocking_limit",
        TurnEndReason.RapidRefillBreaker => "rapid_refill_breaker",
        TurnEndReason.ImageError => "image_error",
        TurnEndReason.ModelError => "model_error",
        TurnEndReason.ToolDeferred => "tool_deferred",
        TurnEndReason.BackgroundRequested => "background_requested",
        TurnEndReason.BudgetExhausted => "budget_exhausted",
        TurnEndReason.StructuredOutputRetryExhausted => "structured_output_retry_exhausted",
        TurnEndReason.ToolDeferredUnavailable => "tool_deferred_unavailable",
        TurnEndReason.TurnSetupFailed => "turn_setup_failed",
        _ => "completed",
    };

    /// <summary>
    /// The reference's <c>iSt</c>: which terminal reasons count as an error in
    /// the result object. Interruptions and a plain completion do not.
    /// </summary>
    public static bool IsError(TurnEndReason reason) => reason switch
    {
        TurnEndReason.Completed => false,
        TurnEndReason.Cancelled => false,
        TurnEndReason.AbortedTools => false,
        TurnEndReason.MaxIterationsReached => false,
        TurnEndReason.BackgroundRequested => false,
        TurnEndReason.ToolDeferred => false,
        _ => true,
    };
}
