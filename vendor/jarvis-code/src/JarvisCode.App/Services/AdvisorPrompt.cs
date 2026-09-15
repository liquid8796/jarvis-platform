namespace JarvisCode.App.Services;

/// <summary>
/// The reference's <c># Advisor Tool</c> prompt block (CLI 2.1.257, its
/// <c>NQt</c> at byte 186887498), and the gate that decides whether it rides
/// the turn.
///
/// Measured on the wire rather than inferred: with
/// <c>CLAUDE_CODE_ENABLE_EXPERIMENTAL_ADVISOR_TOOL=1</c> and an
/// <c>advisorModel</c> in settings, a captured print run sends the block as the
/// <em>last</em> 2,017 characters of the system prompt — immediately after the
/// reduced-delegation line — and declares the tool as the server-side spec
/// <c>{"type":"advisor_20260301","name":"advisor","model":"…"}</c>, with no
/// description and no input schema of its own.
///
/// That server-side half is what this port re-derives rather than copies: the
/// API runs the reviewer model there, and nothing here can ask it to. What the
/// block actually promises the model — a tool taking no parameters, the whole
/// conversation forwarded, a stronger model answering — is reproduced locally
/// by <see cref="AdvisorTool"/>, so the text is true here for the same reason
/// it is true there.
/// </summary>
internal static class AdvisorPrompt
{
    /// <summary>The reference's off switch; it reads the name for truthiness.</summary>
    public const string DisableVariable = "CLAUDE_CODE_DISABLE_ADVISOR_TOOL";

    /// <summary>The reference's opt-in, beside the rollout this build cannot read.</summary>
    public const string EnableVariable = "CLAUDE_CODE_ENABLE_EXPERIMENTAL_ADVISOR_TOOL";

    /// <summary>The block, verbatim.</summary>
    public const string Block = """
# Advisor Tool

You have access to an `advisor` tool backed by a stronger reviewer model. It takes NO parameters -- when you call advisor(), your entire conversation history is automatically forwarded. They see the task, every tool call you've made, every result you've seen.

Call advisor BEFORE substantive work -- before writing, before committing to an interpretation, before building on an assumption. If the task requires orientation first (finding files, fetching a source, seeing what's there), do that, then call advisor. Orientation is not substantive work. Writing, editing, and declaring an answer are.

Also call advisor:
- When you believe the task is complete. BEFORE this call, make your deliverable durable: write the file, save the result, commit the change. The advisor call takes time; if the session ends during it, a durable result persists and an unwritten one doesn't.
- When stuck -- errors recurring, approach not converging, results that don't fit.
- When considering a change of approach.

On tasks longer than a few steps, call advisor at least once before committing to an approach and once before declaring done. On short reactive tasks where the next action is dictated by tool output you just read, you don't need to keep calling -- the advisor adds most of its value on the first call, before the approach crystallizes.

Give the advice serious weight. If you follow a step and it fails empirically, or you have primary-source evidence that contradicts a specific claim (the file says X, the paper states Y), adapt. A passing self-test is not evidence the advice is wrong -- it's evidence your test doesn't check what the advice is checking.

If you've already retrieved data pointing one way and the advisor points another: don't silently switch. Surface the conflict in one more advisor call -- "I found X, you suggest Y, which constraint breaks the tie?" The advisor saw your evidence but may have underweighted it; a reconcile call is cheaper than committing to the wrong branch.
""";

    /// <summary>
    /// The reference's <c>mx()</c> for the half a local build can answer: the
    /// disable variable wins, and otherwise an advisor is configured. Its
    /// remaining arms — a first-party endpoint and the
    /// <c>tengu_sage_compass2</c> rollout — decide whether the API would run
    /// the server-side tool at all, which this port does not depend on because
    /// it runs the advisor itself.
    /// </summary>
    public static bool IsEnabled(string? advisorModelId, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(environment(DisableVariable)))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(advisorModelId);
    }
}
