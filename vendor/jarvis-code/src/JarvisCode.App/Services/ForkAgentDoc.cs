namespace JarvisCode.App.Services;

/// <summary>
/// The Agent tool doc as the reference writes it while its fork gate is on
/// (CLI 2.1.257's <c>zRn</c>, around byte 188417400, whose <c>y = J3() &amp;&amp; forkAvailable</c>
/// switches nine places at once).
/// </summary>
/// <remarks>
/// The doc is not the non-fork doc with a section added: the head sentence is
/// rewritten, "## When not to use" disappears from the classic form, the
/// background bullets are replaced by the fork note in the lean one, the
/// SendMessage bullet gains a clause, the "Don't race" bullet goes, "Writing the
/// prompt" changes two of its sentences, and the examples are a different set.
/// Both forms are therefore carried whole rather than patched.
///
/// The gate is <see cref="JarvisCode.Core.Agent.ForkAgent.IsEnabled"/>: an
/// interactive session and not coordinator mode, with
/// <c>CLAUDE_CODE_FORK_SUBAGENT</c> forcing it either way. That is why no
/// <c>-p</c> capture carries this text and why a live desktop session carries
/// none of it either — the desktop hosts the CLI over the SDK, which is not
/// interactive.
/// </remarks>
internal static class ForkAgentDoc
{
    /// <summary>The lean doc's head, whose third paragraph names the fork.</summary>
    internal const string LeanHead = """
        Launch a new agent to handle complex, multi-step tasks. Each agent type has specific capabilities and tools available to it.

        Available agent types are listed in <system-reminder> messages in the conversation.

        When using the Agent tool, specify a subagent_type to select an agent: `"fork"` forks yourself (the fork inherits your full conversation context and always runs on your model — a `model` override is ignored); any other type — or omitting it — starts a fresh agent (general-purpose by default).
        """;

    /// <summary>The paragraph that replaces the background bullet when a fork can be spawned.</summary>
    internal const string ForkNote = """
        A fork runs in the background and keeps its tool output out of your context. If you are the fork, execute directly — don't re-delegate. Subagents run in the background; you'll be notified when one completes. Never fabricate or predict a pending agent's results — the notification is never something you write yourself; if the user asks before it arrives, say it's still running.
        """;

    /// <summary>The lean doc's four bullets, with the fork clause on the SendMessage one.</summary>
    internal const string LeanTail = """
        - The agent's final report is not shown to the user — relay what matters.
        - Use SendMessage with the agent's ID or name to continue a previously spawned agent with its context intact; a new Agent call starts fresh (except subagent_type: "fork", which inherits your context).
        - Each agent type's model, reasoning effort, and tools come from its definition (`.claude/agents/*.md` frontmatter or SDK `agents`).
        - `isolation: "worktree"` gives the agent its own git worktree (auto-cleaned if unchanged).
        """;

    /// <summary>
    /// The lean doc. <paramref name="reachSentence"/> is the delegation-stance
    /// opener, which the reference includes exactly when the model is not under
    /// the reduced-delegation stance.
    /// </summary>
    internal static string Lean(bool reachSentence) =>
        LeanHead + "\n\n## When to use\n\n" +
        (reachSentence ? ReferenceToolDocs.AgentDocReachSentence : "") +
        SingleFactSentence + "\n\n" + ForkNote + "\n\n" + LeanTail;

    /// <summary>The sentence both lean forms share, ahead of the bullets.</summary>
    private const string SingleFactSentence =
        "For a single-fact lookup where you already know the file, symbol, or value, search directly. Once " +
        "you've delegated a search, don't also run it yourself \u2014 wait for the result.";

    /// <summary>The classic doc, whole.</summary>
    internal const string Classic = """
        Launch a new agent to handle complex, multi-step tasks. Each agent type has specific capabilities and tools available to it.

        Available agent types are listed in <system-reminder> messages in the conversation.

        When using the Agent tool, specify a subagent_type to select an agent: `"fork"` forks yourself (the fork inherits your full conversation context and always runs on your model — a `model` override is ignored); any other type — or omitting it — starts a fresh agent (general-purpose by default).

        ## Usage notes

        - Always include a short description summarizing what the agent will do
        - When the agent is done, its final report is not visible to the user. To show the user the result, you should send a text message back to the user with a concise summary of the result.
        - Trust but verify: an agent's summary describes what it intended to do, not necessarily what it did. When an agent writes or edits code, check the actual changes before reporting the work as done.
        - Agents run in the background by default. When an agent runs in the background, you will be automatically notified when it completes — do NOT sleep, poll, or proactively check on its progress. Continue with other work or respond to the user instead.
        - **Foreground vs background**: Pass `run_in_background: false` only when your very next action depends on the agent's result and nothing else could usefully happen while it runs — e.g., a research agent whose finding gates the edit you're about to make. Otherwise let it run in the background (the default) — this includes fire-and-forget work, independent investigations, and anything where the user might hand you something else in the meantime. Wanting the result "next" is not enough on its own.
        - To continue a previously spawned agent, use SendMessage with the agent's ID or name as the `to` field — that resumes it with full context. A new Agent call starts a fresh agent with no memory of prior runs (except subagent_type: "fork"), so the prompt must be self-contained.
        - Each agent type's model, reasoning effort, and tool access are set in its definition (`.claude/agents/*.md` frontmatter, or the SDK `agents` option); the `model` parameter here overrides the definition for this one call.
        - Clearly tell the agent whether you expect it to write code or just to do research (search, file reads, web fetches, etc.), since a fresh agent is not aware of the user's intent
        - If the agent description mentions that it should be used proactively, then you should try your best to use it without the user having to ask for it first.
        - If the user specifies that they want you to run agents "in parallel", you MUST send a single message with multiple Agent tool use content blocks. For example, if you need to launch both a build-validator agent and a test-runner agent in parallel, send a single message with both tool calls.
        - With `isolation: "worktree"`, the worktree is automatically cleaned up if the agent makes no changes; otherwise the path and branch are returned in the result.

        ## When to fork

        Fork yourself (pass `subagent_type: "fork"`) when the intermediate tool output isn't worth keeping in your context. The criterion is qualitative — "will I need this output again" — not task size. Fork open-ended questions. If research can be broken into independent questions, launch parallel forks in one message. A fork beats a fresh subagent for this — it inherits context and shares your cache.

        Forks are cheap because they share your prompt cache.

        **Don't peek.** The tool result includes an `output_file` path — do not Read or tail it. You get a completion notification; trust it. Reading the transcript mid-flight pulls the fork's tool noise into your context, which defeats the point of forking.

        **Don't race.** After launching, you know nothing about what the fork found. Never fabricate or predict fork results in any format — not as prose, summary, or structured output. The notification arrives as a user-role message in a later turn; it is never something you write yourself. If the user asks a follow-up before the notification lands, tell them the fork is still running — give status, not a guess.

        **Writing a fork prompt.** Since the fork inherits your context, the prompt is a *directive* — what to do, not what the situation is. Be specific about scope: what's in, what's out, what another agent is handling. Don't re-explain background.

        ## Writing the prompt

        Any agent other than a fork starts with zero context. Brief the agent like a smart colleague who just walked into the room — it hasn't seen this conversation, doesn't know what you've tried, doesn't understand why this task matters.
        - Explain what you're trying to accomplish and why.
        - Describe what you've already learned or ruled out.
        - Give enough context about the surrounding problem that the agent can make judgment calls rather than just following a narrow instruction.
        - If you need a short response, say so ("report in under 200 words").
        - Lookups: hand over the exact command. Investigations: hand over the question — prescribed steps become dead weight when the premise is wrong.

        For fresh agents, terse command-style prompts produce shallow, generic work.

        **Never delegate understanding.** Don't write "based on your findings, fix the bug" or "based on the research, implement it." Those phrases push synthesis onto the agent instead of doing it yourself. Write prompts that prove you understood: include file paths, line numbers, what specifically to change.

        Example usage:

        <example>
        user: "What's left on this branch before we can ship?"
        assistant: <thinking>Forking this — it's a survey question. I want the punch list, not the git output in my context.</thinking>
        Agent({
          subagent_type: "fork",
          name: "ship-audit",
          description: "Branch ship-readiness audit",
          prompt: "Audit what's left before this branch can ship. Check: uncommitted changes, commits ahead of main, whether tests exist, whether the GrowthBook gate is wired up, whether CI-relevant files changed. Report a punch list — done vs. missing. Under 200 words."
        })
        assistant: Ship-readiness audit running.
        <commentary>
        Turn ends here. The coordinator knows nothing about the findings yet. What follows is a SEPARATE turn — the notification arrives from outside, as a user-role message. It is not something the coordinator writes.
        </commentary>
        [later turn — notification arrives as user message]
        assistant: Audit's back. Three blockers: no tests for the new prompt path, GrowthBook gate wired but not in build_flags.yaml, and one uncommitted file.
        </example>

        <example>
        user: "so is the gate wired up or not"
        <commentary>
        User asks mid-wait. The audit fork was launched to answer exactly this, and it hasn't returned. The coordinator does not have this answer. Give status, not a fabricated result.
        </commentary>
        assistant: Still waiting on the audit — that's one of the things it's checking. Should land shortly.
        </example>

        <example>
        user: "Can you get a second opinion on whether this migration is safe?"
        assistant: <thinking>I'll ask the code-reviewer agent — it won't see my analysis, so it can give an independent read.</thinking>
        <commentary>
        A non-fork subagent_type is specified, so the agent starts fresh. It needs full context in the prompt. The briefing explains what to assess and why.
        </commentary>
        Agent({
          name: "migration-review",
          description: "Independent migration review",
          subagent_type: "code-reviewer",
          prompt: "Review migration 0042_user_schema.sql for safety. Context: we're adding a NOT NULL column to a 50M-row table. Existing rows get a backfill default. I want a second opinion on whether the backfill approach is safe under concurrent writes — I've checked locking behavior but want independent verification. Report: is this safe, and if not, what specifically breaks?"
        })
        </example>
        """;
}
