namespace JarvisCode.Core.Hooks;

/// <summary>
/// The wording an LLM prompt hook is judged with. A hook of this kind hands a
/// condition to a model instead of to a shell; on Stop and SubagentStop the
/// condition is judged against the transcript, which is what turns a standing
/// goal into a loop the turn cannot leave until the goal is met.
/// </summary>
public static class PromptHooks
{
    /// <summary>The reference's default timeout for one evaluation.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times in a row a Stop hook may block before the turn ends
    /// anyway (the reference's CLAUDE_CODE_STOP_HOOK_BLOCK_CAP, default 8).
    /// </summary>
    public const int StopBlockCap = 8;

    public const string StopBlockCapEnvironmentVariable = "CLAUDE_CODE_STOP_HOOK_BLOCK_CAP";

    /// <summary>Shown when the cap above is reached and the turn ends regardless.</summary>
    public static string StopBlockCapReached(int count) =>
        $"A hook blocked the turn from ending {count} consecutive times — overriding and ending turn. " +
        "For Stop/SubagentStop hooks, check stop_hook_active in the input and return success while it's true. " +
        "Set CLAUDE_CODE_STOP_HOOK_BLOCK_CAP to raise this limit.";

    /// <summary>The user turn a stop-condition hook is asked about.</summary>
    public static string StopConditionQuestion(string condition) =>
        "Based on the conversation transcript above, has the following stopping condition been satisfied? " +
        "Answer based on transcript evidence only.\n\nCondition: " + condition;

    /// <summary>The system prompt for a Stop/SubagentStop condition.</summary>
    public const string StopConditionSystemPrompt = """
        You are evaluating a stop-condition hook in Claude Code. Read the conversation transcript carefully, then judge whether the user-provided condition is satisfied.

        Your response must be a JSON object with one of these shapes:
        - {"ok": true, "reason": "<quote evidence from the transcript that satisfies the condition>"}
        - {"ok": false, "reason": "<quote what is missing or what blocks the condition>"}
        - {"ok": false, "impossible": true, "reason": "<explain why the condition can never be satisfied>"}

        Always include a "reason" field, quoting specific text from the transcript whenever possible. If the transcript does not contain clear evidence that the condition is satisfied, return {"ok": false, "reason": "insufficient evidence in transcript"}.

        Only use {"ok": false, "impossible": true} when the condition is genuinely unachievable in this session — for example: the condition is self-contradictory, it depends on a resource or capability that is unavailable, or the assistant has explicitly tried, exhausted reasonable approaches, and stated it cannot be done. Apply your own judgment when deciding this — the assistant claiming the goal is impossible is evidence, not proof; independently confirm the condition is genuinely unachievable rather than deferring to the assistant's self-assessment. Do not use it just because the goal has not been reached yet or because progress is slow. When in doubt, return {"ok": false} without "impossible".
        """;

    /// <summary>The system prompt for a prompt hook on any other event.</summary>
    public const string GenericSystemPrompt = """
        You are evaluating a hook condition in Claude Code. Judge whether the user-provided condition is met.

        Your response must be a JSON object with one of these shapes:
        - {"ok": true, "reason": "<reason the condition is met>"}
        - {"ok": false, "reason": "<reason the condition is not met>"}

        Always include a "reason" field.
        """;

    /// <summary>The tool the evaluating model answers through.</summary>
    public const string VerdictToolName = "hook_verdict";

    public const string VerdictToolDescription =
        "Use this tool to return your verification result. You MUST call this tool exactly once at the end of your response.";

    /// <summary>The blocking error a failed condition returns to the model.</summary>
    public static string BlockingError(string condition, string? reason) =>
        $"[{condition}]: {reason ?? "condition not met"}";

    /// <summary>The reference's $ARGUMENTS placeholder for the hook payload.</summary>
    public const string ArgumentsPlaceholder = "$ARGUMENTS";

    /// <summary>Substitutes the hook payload into a prompt that asked for it.</summary>
    public static string Render(string prompt, string payloadJson) =>
        prompt.Contains(ArgumentsPlaceholder, StringComparison.Ordinal)
            ? prompt.Replace(ArgumentsPlaceholder, payloadJson, StringComparison.Ordinal)
            : prompt;
}
