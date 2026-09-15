using JarvisCode.Core.Settings;

namespace JarvisCode.App.Services;

/// <summary>
/// The four prompt sections the reference's builder emits behind a gate this
/// port can reproduce: <c>language</c>, <c>bg-session</c>, <c>focus_mode</c> and
/// <c>subagent_steer_delegation</c>.
///
/// Measured on CLI 2.1.257 in the section builder itself (its <c>sS</c>, around
/// byte 189889500), which is one ordered list of named sections — so a block's
/// slot in the prompt is read off that list rather than guessed:
/// <c>env_info_simple</c>, <c>language</c>, <c>output_style</c>,
/// <c>bg-session</c>, <c>scratchpad</c>, <c>context_management</c>,
/// <c>brief</c>, <c>focus_mode</c>, <c>act_dont_rederive</c>,
/// <c>delivering_work_max</c>, <c>overcorrection</c>,
/// <c>subagent_steer_delegation</c>, <c>opus5_reduced_delegation</c>.
///
/// Each gate is the reference's own:
/// <list type="bullet">
/// <item><description><c>language</c>: the settings key <c>language</c>
///   ("Preferred language for Claude responses and voice dictation"), rendered
///   by its <c>l2e</c>.</description></item>
/// <item><description><c>bg-session</c>: <c>CLAUDE_CODE_SESSION_KIND=bg</c> plus
///   a <c>CLAUDE_JOB_DIR</c> (its <c>B3o</c>), with three isolation variants
///   read from <c>CLAUDE_BG_ISOLATION</c> (its <c>Y7e</c>).</description></item>
/// <item><description><c>focus_mode</c>: the settings key
///   <c>viewMode: "focus"</c> (its <c>wpe</c> over <c>G3o</c>), which this port
///   answers with the session's own Summary transcript view — the view that
///   shows prompts and responses and nothing between them.</description></item>
/// <item><description><c>subagent_steer_delegation</c>: the Agent tool is in the
///   turn's tool set and the latched steer is <c>counter_steer</c> (its
///   <c>hH</c>). Of that steer's four sources — <c>CLAUDE_CODE_THISTLE_GREBE</c>,
///   clientData, growthbook and a per-model floor that only ever answers
///   <c>no_nudges</c> — the environment variable is the one a local build can
///   reproduce, so it is the one this port reads.</description></item>
/// </list>
/// </summary>
public sealed record GatedPromptSections(
    string? Language = null,
    string? BackgroundSession = null,
    string? FocusMode = null,
    string? DelegationSteer = null)
{
    /// <summary>Nothing gated on: the shape every default capture has.</summary>
    public static readonly GatedPromptSections None = new();

    /// <summary>The reference's own environment variable for the delegation steer.</summary>
    public const string SteerVariable = "CLAUDE_CODE_THISTLE_GREBE";

    /// <summary>The reference's session-kind variable; "bg" marks a background job.</summary>
    public const string SessionKindVariable = "CLAUDE_CODE_SESSION_KIND";

    /// <summary>The reference's job directory, whose <c>tmp</c> the block names.</summary>
    public const string JobDirectoryVariable = "CLAUDE_JOB_DIR";

    /// <summary>The reference's isolation variable: "worktree" or "none".</summary>
    public const string IsolationVariable = "CLAUDE_BG_ISOLATION";

    /// <summary>
    /// Resolves the four blocks for a turn. <paramref name="environment"/> reads
    /// the process environment by default; the tests pass their own.
    /// </summary>
    /// <param name="steerLatch">
    /// The session's steer latch; null resolves the steer fresh, which is what
    /// a caller with no session of its own (and the tests) wants.
    /// </param>
    public static GatedPromptSections Resolve(
        AppSettings settings,
        bool leanPrompt,
        bool focusMode,
        bool hasAgentTool,
        Func<string, string?>? environment = null,
        SteerLatch? steerLatch = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var steer = steerLatch is null ? Steer(environment) : steerLatch.Resolve(environment);
        return new GatedPromptSections(
            Language: LanguageBlock(settings.Language),
            BackgroundSession: BackgroundSessionBlock(environment),
            FocusMode: focusMode ? FocusModeBlock(leanPrompt) : null,
            DelegationSteer: hasAgentTool && steer == "counter_steer" ? Delegating : null);
    }

    /// <summary>
    /// The steer, resolved. The reference reads the environment first, then
    /// clientData, then growthbook, then the model floor; only the first is
    /// readable here, and anything but its three spellings is ignored, as its
    /// <c>Ts</c> ignores it.
    /// </summary>
    internal static string Steer(Func<string, string?> environment)
    {
        var value = environment(SteerVariable);
        return value is "default" or "no_nudges" or "counter_steer" ? value : "default";
    }

    /// <summary>
    /// The session's latched steer — the reference's <c>hH</c> over its
    /// <c>um.latch</c>. It resolves once and answers with the same value
    /// thereafter, so the steer cannot change under a running conversation.
    ///
    /// The reference pairs that latch with a <c>resetLatch</c>; this port needs
    /// none, because the latch lives on the session's own
    /// <see cref="SkillSessionState"/> and a conversation that starts over gets
    /// a new one.
    /// </summary>
    public sealed class SteerLatch
    {
        private readonly Lock _lock = new();
        private string? _latched;

        /// <summary>The steer for this session, resolving it on the first call.</summary>
        public string Resolve(Func<string, string?> environment)
        {
            lock (_lock)
            {
                // The reference latches every outcome, "default" included, so a
                // variable set mid-session does not take effect either way.
                return _latched ??= Steer(environment);
            }
        }
    }

    /// <summary>The <c># Language</c> block for a configured language; null for none.</summary>
    public static string? LanguageBlock(string? language)
    {
        var name = language?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, LanguageTemplate, name);
    }

    /// <summary>The <c># Focus mode</c> block; the lean and classic prompts word it differently.</summary>
    public static string FocusModeBlock(bool leanPrompt) => leanPrompt ? FocusLean : FocusClassic;

    /// <summary>
    /// The <c># Background Session</c> block, or null when this is not a
    /// background job. The reference needs both its variables: the session kind
    /// says the job is one, and the job directory is the path the block names.
    /// </summary>
    public static string? BackgroundSessionBlock(Func<string, string?> environment)
    {
        if (!string.Equals(environment(SessionKindVariable), "bg", StringComparison.Ordinal))
        {
            return null;
        }

        var jobDirectory = environment(JobDirectoryVariable);
        if (string.IsNullOrEmpty(jobDirectory))
        {
            return null;
        }

        var isolation = environment(IsolationVariable);
        var inPlace = string.Equals(isolation, "none", StringComparison.Ordinal);
        var guidance = inPlace
            ? IsolationNone
            : string.Equals(isolation, "worktree", StringComparison.Ordinal)
                ? IsolationWorktree
                : IsolationDefault;
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            BackgroundSessionTemplate,
            System.IO.Path.Combine(jobDirectory, "tmp"),
            guidance + (inPlace ? "" : CommitTail));
    }

    /// <summary>The <c>## Delegating to subagents</c> block, verbatim.</summary>
    public const string Delegating = """
        ## Delegating to subagents

        Subagents multiply cost and time: each one re-establishes context, re-explores, and reports back, and you then re-read its report. Delegate only when the payoff clearly exceeds that overhead. Before spawning, apply these tests:

        - Do the work inline when it is a small, bounded sub-task — a few file reads, one search, a short edit, a single check. Do not spawn a subagent for work you could finish yourself in a handful of tool calls.
        - Do not fan out multiple subagents on a single small task. Parallel subagents are for genuinely independent, sizeable tracks (unrelated modules, a wide multi-file investigation), not for splitting one modest job into pieces.
        - Do not spawn a subagent to review, re-verify, or double-check work you can verify inline. Verification that fits in your own loop belongs in your own loop.
        - If you delegate, commit to the delegation: do not redo the subagent's work while waiting, and do not re-derive its findings once it reports. If you find yourself repeating what a subagent is doing, you should not have spawned it.
        - Keep spawn counts low. One well-briefed subagent for a large independent chunk is worth more than several loosely-briefed ones; brief it precisely the first time rather than launching, waiting, and re-briefing.

        Delegate for work that is genuinely independent, large enough to justify a fresh context, or naturally parallel. Otherwise, do it yourself.
        """;

    /// <summary>The <c># Language</c> block; {0} is the configured language, three times over.</summary>
    internal const string LanguageTemplate = """
        # Language
        Always respond in {0}. Use {0} for all explanations, comments, and communications with the user. Technical terms and code identifiers should remain in their original form.
        Maintain full orthographic correctness for {0}, including all required diacritical marks, accents, and special characters. Never substitute accented characters with their ASCII equivalents (e.g., never write "nao" for "não", "fur" for "für", or "loeschen" for "löschen").
        """;

    /// <summary>The lean prompt's <c># Focus mode</c> wording.</summary>
    internal const string FocusLean = """
        # Focus mode
        The user has focus mode enabled. They only see your final text message in each response — not tool calls, tool results, or any text you write between tool calls. Anything you say mid-turn is not seen, so don't narrate progress between tool calls. Put everything the user needs into your final message: what you investigated, what you found, what you changed, decisions you made, and what's next. Do not assume they saw earlier output.
        """;

    /// <summary>The classic prompt's <c># Focus mode</c> wording.</summary>
    internal const string FocusClassic = """
        # Focus mode
        The user has focus mode enabled. In focus mode, the user only sees your final text message in each response. They do not see tool calls, tool results, or any text you emit between tool calls. This overrides earlier guidance about giving short updates between tool calls — skip those updates and put everything the user needs to know in your final message. Do not assume they saw earlier progress updates.
        """;

    /// <summary>
    /// The <c># Background Session</c> block: {0} is the job's own tmp directory
    /// and {1} the isolation guidance plus, unless the job works in place, the
    /// commit-before-finishing tail.
    /// </summary>
    internal const string BackgroundSessionTemplate = """
        # Background Session

        This session runs as a background job. The user may be chatting with you live or may have stepped away to check results later — respond naturally either way, and don't refer to yourself as "a background agent."

        Use `$CLAUDE_JOB_DIR/tmp` (`{0}`) for any temporary files (scripts, query files, intermediate outputs) instead of `/tmp` — parallel bg jobs share `/tmp` and clobber each other's files. This directory already exists and is cleaned up when the job is deleted, so anything the user should keep belongs somewhere durable instead.

        {1}

        End the job with a report the user can act on: what you did, where it lives — path, branch, PR, or the answer itself — and the next command if one is needed. If you're running as a subagent, the git guidance above and this report don't apply: return your work to your caller.
        """;

    /// <summary>
    /// The isolation guidance for a job configured to work in place
    /// (<c>CLAUDE_BG_ISOLATION=none</c>).
    /// </summary>
    internal const string IsolationNone = """
        Edit files directly in your working directory — this session is configured to work in place rather than isolating into a worktree. Skip EnterWorktree unless the user explicitly asks to work in a worktree.
        """;

    /// <summary>
    /// The guidance for <c>CLAUDE_BG_ISOLATION=worktree</c>. The reference names
    /// <c>.claude/worktrees/</c>; this port's EnterWorktree makes
    /// <c>.jarvis-worktrees/</c>, so the path is the one adaptation.
    /// </summary>
    internal const string IsolationWorktree = """
        This agent is configured with `isolation: worktree`. Call the EnterWorktree tool as your first action — before reading files or running commands — unless your cwd is already under `.jarvis-worktrees/`. If EnterWorktree fails, continue in place.
        """;

    /// <summary>The guidance when no isolation was configured either way.</summary>
    internal const string IsolationDefault = """
        Before making any code changes, use the EnterWorktree tool to isolate your work from other parallel jobs and the user's working copy — unless your cwd is already under `.jarvis-worktrees/`, in which case you're already isolated. This is enforced: file edits in the shared checkout are rejected until you isolate, so call EnterWorktree before your first edit rather than after a rejected attempt. If you're only reading, searching, or answering questions, skip this and work in place. If EnterWorktree fails, continue in place.
        """;

    /// <summary>
    /// The tail every isolated background job carries, with the reference's own
    /// <c>VYt</c> ("Never push to main/master, force-push, or merge.")
    /// interpolated where it stands.
    /// </summary>
    internal const string CommitTail = """


        If you made code changes in a worktree you entered, commit before finishing — you don't need to ask — and push if the repository has a remote: the worktree can be deleted along with the session, and committed, pushed work survives. This holds unless the user's instructions, in the task, CLAUDE.md, or memory, reserve git for them. Never push to main/master, force-push, or merge. Open a draft PR when the task calls for one. If you didn't enter the worktree yourself this job, or you're in the user's own checkout, ask before committing or switching branches.
        """;
}
