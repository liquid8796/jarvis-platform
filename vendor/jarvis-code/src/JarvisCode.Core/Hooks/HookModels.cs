using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Hooks;

public enum HookEvent
{
    PreToolUse,
    PostToolUse,
    TurnCompleted,
    UserPromptSubmit,
    SessionStart,
    SessionEnd,
    Stop,
    SubagentStop,
    SubagentStart,
    Notification,
    PreCompact,
    PermissionRequest,
    /// <summary>A tool finished with an error result (observational, tool-matched).</summary>
    PostToolUseFailure,
    /// <summary>All tool calls of one assistant message finished.</summary>
    PostToolBatch,
    /// <summary>A typed prompt was expanded (custom command, skill, MCP prompt).</summary>
    UserPromptExpansion,
    /// <summary>A turn ended with a provider error.</summary>
    StopFailure,
    /// <summary>Compaction finished (manual or auto).</summary>
    PostCompact,
    /// <summary>The user (or a deny rule) denied a permission prompt.</summary>
    PermissionDenied,
    /// <summary>A setup-flavored action ran (/init, /terminal-setup, /auto-mode-setup).</summary>
    Setup,
    /// <summary>An MCP server asked the user a question (elicitation).</summary>
    Elicitation,
    /// <summary>The user answered (or dismissed) an MCP elicitation.</summary>
    ElicitationResult,
    /// <summary>A setting changed through the config tool during the session.</summary>
    ConfigChange,
    /// <summary>EnterWorktree created (or reentered) a worktree.</summary>
    WorktreeCreate,
    /// <summary>ExitWorktree removed a worktree (discard).</summary>
    WorktreeRemove,
    /// <summary>Project instructions (CLAUDE.md files) loaded into the session.</summary>
    InstructionsLoaded,
    /// <summary>The session's working directory changed (/cd, worktrees).</summary>
    CwdChanged,
    /// <summary>A file this session touched changed on disk outside a tool call.</summary>
    FileChanged,
    /// <summary>An additional working directory joined the session.</summary>
    DirectoryAdded,
    /// <summary>An assistant message was displayed.</summary>
    MessageDisplay,
    /// <summary>A task was created on the session's task board.</summary>
    TaskCreated,
    /// <summary>A board task moved to completed (blocking: a non-zero exit keeps it open).</summary>
    TaskCompleted,
    /// <summary>A teammate finished its work and is waiting for the next assignment.</summary>
    TeammateIdle,
    /// <summary>A model switch is about to happen (blocking: a hook may refuse it).</summary>
    PreModelSwitch,
    /// <summary>A model switch happened.</summary>
    PostModelSwitch,
}

/// <summary>
/// Outcome of the user-prompt-submit hooks: a non-zero exit blocks the prompt,
/// and stdout from allowed hooks becomes extra context on the user message.
/// </summary>
public sealed record PromptSubmitResult(bool Allowed, string? BlockReason, string? AdditionalContext)
{
    public static readonly PromptSubmitResult Allow = new(true, null, null);
}

/// <summary>How a hook decides: by running something, or by asking a model.</summary>
public enum HookKind
{
    /// <summary>A shell command; its exit code is the verdict.</summary>
    Command,

    /// <summary>
    /// The reference LLM prompt hook: the condition is judged by a model, which
    /// answers {ok, reason} — and, for a stop condition, may answer that it can
    /// never be satisfied.
    /// </summary>
    Prompt,

    /// <summary>The hook payload is POSTed to a URL; a 2xx passes, anything else blocks.</summary>
    Http,

    /// <summary>A tool on a configured MCP server judges it; an error result blocks.</summary>
    McpTool,
    /// <summary>A callback registered by an SDK host over the control channel.</summary>
    Callback,
}

/// <summary>One configured hook: a shell command, or a condition a model judges.</summary>
public sealed record HookDefinition(
    HookEvent Event,
    /// <summary>Regex matched against the tool name; null matches every tool.</summary>
    string? ToolMatch,
    string Command,
    int TimeoutSeconds,
    HookKind Kind = HookKind.Command,
    /// <summary>The condition to evaluate, for <see cref="HookKind.Prompt"/>.</summary>
    string? Prompt = null,
    /// <summary>Model for a prompt hook; null uses the session's own.</summary>
    string? Model = null,
    /// <summary>
    /// The reference continueOnBlock: whether a failed condition still lets the
    /// turn proceed. Default false, which is what makes a Stop condition a loop.
    /// </summary>
    bool ContinueOnBlock = false,
    /// <summary>Spinner text while the hook runs (reference statusMessage).</summary>
    string? StatusMessage = null,
    /// <summary>Where to POST, for <see cref="HookKind.Http"/>.</summary>
    HttpHookSpec? Http = null,
    /// <summary>Which tool to call, for <see cref="HookKind.McpTool"/>.</summary>
    McpHookSpec? Mcp = null,
    string? CallbackId = null)
{
    public string? PluginRoot { get; init; }
    public string? PluginData { get; init; }
    public string? ProjectDirectory { get; init; }
}

public delegate Task<JsonObject?> SdkHookCaller(HookDefinition hook, JsonObject payload,
    string? toolUseId, CancellationToken cancellationToken);

/// <summary>An observational lifecycle record; a host must never use it to settle a hook.</summary>
public sealed record HookLifecycleEvent(string HookId, HookEvent Event, string Name,
    string Phase, int? ExitCode = null, string Stdout = "", string Stderr = "");

/// <summary>What a prompt hook is asked to judge.</summary>
public sealed record PromptHookRequest(
    HookEvent Event,
    /// <summary>The user's condition, verbatim.</summary>
    string Condition,
    /// <summary>The hook payload as JSON, substituted for $ARGUMENTS in the prompt.</summary>
    string PayloadJson,
    /// <summary>The model the hook asked for, or null for the session's own.</summary>
    string? Model,
    TimeSpan Timeout,
    /// <summary>True for Stop/SubagentStop, where the condition is judged against the transcript.</summary>
    bool IsStopCondition);

/// <summary>
/// A prompt hook's verdict. <paramref name="Impossible"/> is the reference's
/// third shape: the condition can never be satisfied in this session, so the
/// loop it drives is abandoned rather than retried.
/// </summary>
public sealed record PromptHookVerdict(bool Ok, string? Reason, bool Impossible = false);

/// <summary>
/// Judges a prompt hook's condition. The host owns this because it needs a
/// model and the conversation; a null evaluator leaves prompt hooks with no
/// opinion rather than blocking on something nothing can answer.
/// </summary>
public delegate Task<PromptHookVerdict?> PromptHookEvaluator(
    PromptHookRequest request, CancellationToken cancellationToken);

/// <summary>
/// What an in-process hook answers. The reference's own built-in hooks are
/// functions rather than commands, and this is the shape they return:
/// <paramref name="AdditionalContext"/> rides the tool result back to the model.
/// </summary>
public sealed record FunctionHookResult(
    string? AdditionalContext = null, bool Blocked = false, string? Reason = null)
{
    public static readonly FunctionHookResult None = new();
}

/// <summary>
/// A hook the host registers in process rather than in hooks.json — the shape
/// the reference's own preview-verification hooks use.
/// </summary>
public sealed record FunctionHook(
    HookEvent Event,
    Func<JsonObject, CancellationToken, Task<FunctionHookResult>> Run,
    /// <summary>Regex matched against the tool name; null matches every tool.</summary>
    string? ToolMatch = null);

/// <summary>Outcome of the pre-tool-use hooks: a non-zero exit blocks the call.</summary>
public sealed record HookDecision(bool Allowed, string? BlockReason)
{
    public static readonly HookDecision Allow = new(true, null);
    /// <summary>An explicit per-call permission verdict from a trusted SDK hook.</summary>
    public string? PermissionBehavior { get; init; }
    public bool StopImmediately { get; init; }
}

public sealed class HookStopRequestedException(string reason) : Exception(reason);

/// <summary>
/// A permission_request hook's verdict, parsed from its stdout JSON
/// ({"decision":"allow"|"deny","reason":...}). Null anywhere a hook has no
/// opinion — the request then reaches the user prompt as usual.
/// </summary>
public sealed record PermissionRequestHookResult(bool Allow, string? Reason)
{
    public JsonObject? UpdatedInput { get; init; }
}

/// <summary>Gate the orchestrator consults around every tool execution.</summary>
public interface IToolHooks
{
    /// <summary>Runs before permission assessment; may block or rewrite the proposed call.</summary>
    Task<HookDecision> BeforeToolAsync(ITool tool, JsonObject arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Runs after the tool finished; failures are advisory and never fail the
    /// tool. A non-null answer is additional context the model should see with
    /// the result (the reference's PostToolUse additionalContext).
    /// </summary>
    Task<string?> AfterToolAsync(ITool tool, JsonObject arguments, ToolResult result, CancellationToken cancellationToken);
}

/// <summary>
/// What the stop hooks decided: whether the turn may end, and — when a prompt
/// hook judged the condition — whether it can never be satisfied at all.
/// </summary>
public sealed record StopHookOutcome(HookDecision Decision, bool Impossible, string? Condition);
