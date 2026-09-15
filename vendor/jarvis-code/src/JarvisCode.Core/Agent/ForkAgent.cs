using JarvisCode.Core.Models;

namespace JarvisCode.Core.Agent;

/// <summary>
/// The reference's <c>fork</c> agent: a child that inherits the parent's whole
/// conversation instead of starting cold, runs on the parent's model whatever
/// the call asks for, and executes one directive before stopping.
/// </summary>
/// <remarks>
/// Measured in CLI 2.1.257 (its <c>uW</c>/<c>wP</c>/<c>J3</c>/<c>XRn</c>/<c>JRn</c>,
/// around byte 188415400):
/// <list type="bullet">
/// <item><description>The gate is <c>VAo</c>/<c>e_r</c>: <c>CLAUDE_CODE_FORK_SUBAGENT</c>
///   forces it either way; coordinator mode disables it; a <b>non-interactive</b>
///   session (<c>De() === !isInteractive()</c>) disables it; otherwise it is
///   on. That is why the fork branch appears in no <c>-p</c> capture — and why a
///   live desktop session carries none of it either: the desktop hosts the CLI
///   over the SDK, which is not interactive. This port therefore offers it on
///   an interactive terminal front-end and nowhere else, which is the same
///   answer on both surfaces the reference gives.</description></item>
/// <item><description>Availability is <c>XRn</c>: the gate, plus no active agent
///   definition already named <c>fork</c>, plus <c>fork</c> being in the allowed
///   agent types when the session narrows them.</description></item>
/// <item><description>The definition <c>wP</c> declares <c>maxTurns: 200</c>,
///   <c>model: "inherit"</c> and <c>tools: ["*"]</c>. It is deliberately
///   <b>not</b> an entry in the roster: the roster is built from
///   <c>M8e(activeAgents)</c> and <c>XRn</c> refuses when <c>fork</c> is among
///   those, so the reference never lists it. Its <c>whenToUse</c> sentence is
///   carried here for the one place it does surface — the "Available agents"
///   line of a missing-subagent_type error.</description></item>
/// </list>
/// </remarks>
public static class ForkAgent
{
    /// <summary>The reference's agent type name (its <c>uW</c>).</summary>
    public const string AgentType = "fork";

    /// <summary>The reference's <c>querySource</c> for a fork's own turn (its <c>Jtr</c>).</summary>
    public const string QuerySource = "agent:builtin:fork";

    /// <summary>The reference's environment override; "1"/"true" forces it on, "0"/"false" off.</summary>
    public const string EnabledVariable = "CLAUDE_CODE_FORK_SUBAGENT";

    /// <summary>The tag the fork's inherited-context preamble is wrapped in (its <c>XQ</c>).</summary>
    public const string BoilerplateTag = "fork-boilerplate";

    /// <summary>The reference's <c>FRe</c>, which introduces the directive.</summary>
    public const string DirectivePrefix = "Your directive: ";

    /// <summary>
    /// The reference's <c>wP.maxTurns</c>: the one built-in definition that
    /// declares a cap. Explore, Plan, general-purpose, teammate and the workflow
    /// subagent declare none.
    /// </summary>
    public const int MaxTurns = 200;

    /// <summary>The tool result each of the parent's in-flight calls is given (its <c>KAo</c>).</summary>
    public const string LaunchedResult = "Fork started — processing in background";

    /// <summary>
    /// The definition's <c>whenToUse</c>, which the reference shows in the
    /// "Available agents" list of a missing-subagent_type error rather than in
    /// the roster.
    /// </summary>
    public const string WhenToUse =
        "Fork — inherits full conversation context. Selected explicitly via subagent_type: \"fork\" when the " +
        "fork gate is on; never the default.";

    /// <summary>
    /// Whether the fork gate is on. <paramref name="interactive"/> is the
    /// reference's own <c>isInteractive()</c> launch option.
    /// </summary>
    public static bool IsEnabled(
        bool interactive, bool coordinatorMode, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var forced = environment(EnabledVariable)?.Trim().ToLowerInvariant();
        if (forced is "0" or "false" or "no" or "off")
        {
            return false;
        }

        if (coordinatorMode)
        {
            return false;
        }

        if (forced is { Length: > 0 })
        {
            return true;
        }

        return interactive;
    }

    /// <summary>
    /// The reference's <c>XRn</c>: the gate, and no custom agent has taken the
    /// name, and the session's allowed types still admit it.
    /// </summary>
    public static bool IsAvailable(
        bool enabled,
        IEnumerable<string>? activeAgentTypes,
        IReadOnlyCollection<string>? allowedAgentTypes)
    {
        if (!enabled)
        {
            return false;
        }

        if (activeAgentTypes is not null &&
            activeAgentTypes.Any(type => string.Equals(type, AgentType, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return allowedAgentTypes is null ||
            allowedAgentTypes.Contains(AgentType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The reference's refusal for <c>isolation: "remote"</c> on a fork.</summary>
    public const string RemoteIsolationRefusal =
        "Fork cannot use isolation: \"remote\" — a remote session cannot inherit the conversation context. " +
        "Omit isolation (or use \"worktree\"), or spawn a named agent type for remote work.";

    /// <summary>The reference's refusal for a fork spawned from inside a fork.</summary>
    public const string RecursiveForkRefusal =
        "Fork is not available inside a forked worker. Complete your task directly using your tools.";

    /// <summary>
    /// The preamble a fork's own turn opens with, wrapped in the reference's
    /// <c>&lt;fork-boilerplate&gt;</c> tag, followed by the directive.
    /// </summary>
    public static string Directive(string directive) =>
        "<" + BoilerplateTag + ">\n" + Boilerplate + "\n</" + BoilerplateTag + ">\n\n" +
        DirectivePrefix + directive;

    /// <summary>The body of that preamble, verbatim (the reference's <c>kpt</c>).</summary>
    internal const string Boilerplate = """
        You are a worker fork. The transcript above is the parent's history — inherited reference, not your situation. You are NOT a continuation of that agent. Execute ONE directive, then stop.

        Hard rules:
        - Do NOT spawn subagents with the Agent tool. The "default to forking" guidance is for the parent; you ARE the fork, execute directly.
        - One shot: report once and stop. No follow-up questions, no proposed next steps, no waiting for the user.

        Guidelines (your directive may override any of these):
        - Stay in scope. Other forks may be handling adjacent work; if you spot something outside your directive, note it in a sentence and move on.
        - Open with one line restating your task, so the parent can spot scope drift at a glance.
        - Be concise — as short as the answer allows, no shorter. Plain text, no preamble, no meta-commentary.
        - If you committed changes, list the paths and commit hashes in your report.
        """;

    /// <summary>
    /// The sentence a fork running in its own worktree is given on top of the
    /// inherited context (the reference's <c>hKn</c>).
    /// </summary>
    public static string WorktreeNotice(string parentDirectory, string worktreePath) =>
        $"You've inherited the conversation context above from a parent agent working in {parentDirectory}. " +
        $"You are operating in an isolated git worktree at {worktreePath} — same repository, same relative " +
        "file structure, separate working copy. Paths in the inherited context refer to the parent's working " +
        "directory; translate them to your worktree root. Re-read files before editing if the parent may have " +
        "modified them since they appear in the context. Your changes stay in this worktree and will not affect " +
        "the parent's files.";

    /// <summary>
    /// Whether a message list already carries a fork preamble — the reference's
    /// <c>mKn</c>/<c>Rtt</c>, which is how it refuses a fork inside a fork.
    /// </summary>
    public static bool IsForkedConversation(IEnumerable<ChatMessage>? messages) =>
        messages is not null && messages.Any(message =>
            message.Role == Role.User &&
            message.Content.OfType<TextBlock>().Any(block =>
                block.Text.StartsWith("<" + BoilerplateTag + ">", StringComparison.Ordinal)));

    /// <summary>
    /// The conversation a fork starts from: the parent's history — whose last
    /// entry is the assistant message carrying this call — plus one user message
    /// answering every tool_use in it and carrying the fork's directive. That is
    /// the reference's <c>gKn</c>, including its fallback for an assistant
    /// message with no tool_use blocks in it.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildConversation(
        IReadOnlyList<ChatMessage> parentMessages, string directive)
    {
        var preamble = Directive(directive);
        var last = parentMessages.Count > 0 ? parentMessages[^1] : null;
        var toolCalls = last is { Role: Role.Assistant }
            ? last.Content.OfType<ToolCallBlock>().ToList()
            : [];
        if (toolCalls.Count == 0)
        {
            return [.. parentMessages, ChatMessage.FromUserText(preamble)];
        }

        var blocks = new List<ContentBlock>();
        foreach (var call in toolCalls)
        {
            blocks.Add(new ToolResultBlock(call.Id, call.Name, LaunchedResult, IsError: false));
        }

        blocks.Add(new TextBlock(preamble));
        return [.. parentMessages, new ChatMessage(Role.User, blocks)];
    }
}
