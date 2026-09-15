namespace JarvisCode.Core.Agent;

/// <summary>
/// What the reference does when a model call comes back unusable instead of
/// ending the turn on it: the answer was cut off, the tool call could not be
/// parsed, or nothing user-visible came back. Each of these re-enters the loop
/// with the turn count unchanged, so a recovery never spends the caller's
/// budget, and each is tried a bounded number of times.
/// </summary>
public static class TurnRecovery
{
    /// <summary>The reference's own attempt cap for the two resume paths (its <c>LAt</c>).</summary>
    public const int MaxResumeAttempts = 3;

    /// <summary>Sent after a response that stopped because it hit the output-token cap.</summary>
    public const string OutputLimitResume =
        "Output token limit hit. Resume directly — no apology, no recap of what you were doing. " +
        "Pick up mid-thought if that is where the cut happened. Break remaining work into smaller pieces.";

    /// <summary>
    /// Sent to the main thread after a response whose stream ended mid-message
    /// (print runs only — an interactive user can ask for the rest).
    /// </summary>
    public const string TruncatedResume =
        "Your response above was cut off mid-stream. Resume directly from where it stops — no apology, " +
        "no recap. If none of it survived, answer the request from the start.";

    /// <summary>
    /// Sent to a subagent in the same case, in every session kind: only its next
    /// message is delivered to the caller, so resuming mid-thought would hand
    /// back a fragment. The reference picks this wording when the query source
    /// is a subagent (CLI 2.1.257).
    /// </summary>
    public const string TruncatedRewriteForSubagent =
        "Your response above was cut off mid-stream and only your next message is delivered. " +
        "Write the complete response again from the start — no apology, no mention of the cut-off.";

    /// <summary>Sent after a response that claimed a tool call the provider never delivered.</summary>
    public const string MalformedToolUseRetry =
        "The previous response failed to produce a valid tool call. Please retry the tool call now.";

    /// <summary>Ends the turn when the retry above also came back malformed.</summary>
    public const string MalformedToolUseExhausted =
        "The model's tool call could not be parsed (retry also failed).";

    /// <summary>Sent after a response that produced only thinking, once per turn.</summary>
    public const string ThinkingOnlyNudge =
        "[Your previous response had no visible output. Please continue and produce a user-visible response.]";
}
