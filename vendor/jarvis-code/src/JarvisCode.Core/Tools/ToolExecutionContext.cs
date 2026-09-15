using JarvisCode.Core.Agent;
using JarvisCode.Core.Checkpoints;

namespace JarvisCode.Core.Tools;

/// <summary>Per-invocation environment for tools.</summary>
public sealed record ToolExecutionContext
{
    public required string WorkingDirectory { get; init; }

    /// <summary>Extra source directories attached to the session besides the main one.</summary>
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

    /// <summary>Host-controlled file-upload scope. Jarvis Agent uses folders as context, not a boundary.</summary>
    public bool EnforceWorkspaceFileScope { get; init; } = true;

    /// <summary>Hard cap applied to tool output before it is sent to the model.</summary>
    public int MaxOutputChars { get; init; } = 60_000;

    public TimeSpan ShellTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Id of the tool call being executed; set by the orchestrator per call.</summary>
    public string? CallId { get; init; }

    /// <summary>Id of the session this turn belongs to; tags session-scoped state (background tasks).</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Facilities for spawning subagents. Null when subagents are unavailable
    /// (inside a subagent, or in hosts that don't provide them).
    /// </summary>
    public SubagentServices? Subagents { get; init; }

    /// <summary>
    /// The turn's conversation as it stands at this call, whose last entry is the
    /// assistant message carrying the call. Set by the orchestrator; null for a
    /// host that runs a tool outside a turn.
    /// </summary>
    /// <remarks>
    /// It exists for the reference's <c>fork</c> agent, which inherits the
    /// parent's whole history rather than starting cold, and is a snapshot
    /// because the turn's own list keeps growing under it.
    /// </remarks>
    public Func<IReadOnlyList<Models.ChatMessage>>? ConversationSnapshot { get; init; }

    /// <summary>File-mutating tools report targets here before changing them (/rewind).</summary>
    public ICheckpointRecorder? Checkpoints { get; init; }

    /// <summary>Receives the model's task list from the todo tool (null hides it).</summary>
    public Action<IReadOnlyList<BuiltIn.TodoItem>>? TodoSink { get; init; }

    /// <summary>Session-wide background shell tasks (null disables run_in_background).</summary>
    public BackgroundTasks.BackgroundTaskManager? BackgroundTasks { get; init; }

    /// <summary>Armed monitors deliver lines here after the tool call/turn has returned.</summary>
    public Action<string, string, string>? MonitorEvent { get; init; }

    /// <summary>Cancelled when the owning host session ends, rather than after each model turn.</summary>
    public CancellationToken SessionLifetime { get; init; }

    /// <summary>
    /// The reference's "Run in background": tools that can hand a running call
    /// off to <see cref="BackgroundTasks"/> register their call id here and race
    /// the token they get back.
    /// </summary>
    public BackgroundTasks.BackgroundMoveRequests? BackgroundMoves { get; init; }

    /// <summary>Skills the skill tool can load (null/empty disables it).</summary>
    public IReadOnlyList<Customization.SkillDefinition>? Skills { get; init; }

    /// <summary>Session-scoped invoked-skill state (elision + the post-compaction reminder); null disables both.</summary>
    public Customization.SkillInvocationTracker? SkillTracker { get; init; }

    /// <summary>Coordinator mode: the skill tool loads instructions read-only and executes nothing.</summary>
    public bool CoordinatorMode { get; init; }

    /// <summary>True when the user themselves typed /name in the current turn's prompt (unlocks disable-model-invocation).</summary>
    public Func<string, bool>? UserTypedSkillThisTurn { get; init; }

    /// <summary>
    /// Fired when a skill's instructions load (not in coordinator mode): the
    /// host records usage, applies allowed/disallowed-tools grants, registers
    /// the skill's hooks, and applies model/effort overrides.
    /// </summary>
    public Action<Customization.SkillDefinition>? SkillInvoked { get; init; }

    /// <summary>Inside a forked skill's subagent: that skill's name, guarding recursive re-invocation.</summary>
    public string? ForkedSkillName { get; init; }

    /// <summary>
    /// Asks the user to approve running a skill that declares allowed-tools,
    /// disallowed-tools, hooks, or a shell (reference "Execute skill: {name}"
    /// prompt). Null hosts approve silently.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? ConfirmSkillExecutionAsync { get; init; }

    /// <summary>Runs one skill !`cmd` preprocessing command (command, shell) through the host's permission path.</summary>
    public Func<string, string, Customization.SkillShellOutcome>? RunSkillShell { get; init; }

    /// <summary>The session's effort level name, substituted as ${CLAUDE_EFFORT} in skill bodies.</summary>
    public string? EffortName { get; init; }

    /// <summary>Per-project persistent memory directory (null disables the memory tool).</summary>
    public string? MemoryDirectory { get; init; }

    /// <summary>
    /// The team's shared memory directory, written with the file tools rather
    /// than the memory tool. Null outside a team.
    /// </summary>
    public string? TeamMemoryDirectory { get; init; }

    /// <summary>
    /// Plan mode: the host offers only read-only tools and subagents are forced
    /// to the explore type so nothing can be modified while planning.
    /// </summary>
    public bool PlanMode { get; init; }

    /// <summary>
    /// Live plan-mode check, when the host can leave plan mode mid-turn (an
    /// approved ExitPlanMode). Falls back to the turn-start snapshot.
    /// </summary>
    public Func<bool>? PlanModeProvider { get; init; }

    public bool IsPlanModeActive => PlanModeProvider?.Invoke() ?? PlanMode;

    /// <summary>The session's plan file; set only while plan mode is on.</summary>
    public string? PlanFilePath { get; init; }

    /// <summary>
    /// True inside a subagent. The reference answers an approved plan
    /// differently there: the agent is told the plan is approved and that
    /// nothing further is needed from it, because the implementation belongs
    /// to the session that spawned it.
    /// </summary>
    public bool IsSubagent { get; init; }

    /// <summary>
    /// No way to reach the user this turn — a print run. The reference gates its
    /// automatic move of a slow MCP call to the background on this, because a
    /// notification nobody will read is worse than the wait.
    /// </summary>
    public bool IsNonInteractiveSession { get; init; }

    /// <summary>Background subagents ("workers"); null disables run_in_background and SendMessage.</summary>
    /// <summary>
    /// Where a background agent's JSONL transcript is written — the reference's
    /// <c>output_file</c>, which its launch result names and tells the model not
    /// to Read. Null leaves the result on its no-output-file wording, which the
    /// reference also has.
    /// </summary>
    public string? AgentTranscriptDirectory { get; init; }

    public Agent.AgentWorkerManager? Workers { get; init; }

    /// <summary>The session's structured task board (TaskCreate/get/list/update).</summary>
    public Agent.TaskBoard? Tasks { get; init; }

    /// <summary>Dynamic-workflow services; null when the session has none.</summary>
    public BuiltIn.WorkflowServices? Workflows { get; init; }

    /// <summary>Set while this agent runs as a member of a team; null in a plain session.</summary>
    public Agent.TeamContext? Team { get; init; }

    /// <summary>The team file store, so a member's state can be recorded.</summary>
    public Agent.TeamStore? TeamStore { get; init; }

    /// <summary>The session's implicit team name; the lead has no TeamContext of its own.</summary>
    public string? TeamName { get; init; }

    /// <summary>
    /// Delivers a teammate's message to the team lead (the session that
    /// spawned it) while the teammate is still running: (from, message,
    /// summary). Null outside a teammate.
    /// </summary>
    public Func<string, string, string?, Task>? ReportToLeadAsync { get; init; }

    /// <summary>
    /// Plan-approval requests teammates raised with the lead. The lead answers
    /// them with SendMessage; a teammate sees its own request through it too.
    /// </summary>
    public Agent.PlanApprovalRegistry? PlanApprovals { get; init; }

    /// <summary>
    /// Ends this agent after it approved its own shutdown. Null outside an agent
    /// that can be shut down.
    /// </summary>
    public Action? RequestOwnShutdown { get; init; }


    /// <summary>Renders the AskUserQuestion card; null when the host has no UI for it.</summary>
    public Func<IReadOnlyList<BuiltIn.UserQuestion>, CancellationToken, Task<BuiltIn.UserQuestionAnswers?>>? AskUserAsync { get; init; }

    /// <summary>Renders the exit-plan-mode approval; null outside plan mode.</summary>
    public Func<string, CancellationToken, Task<BuiltIn.PlanApprovalDecision>>? PlanApprovalAsync { get; init; }

    /// <summary>Lets tools fire observational hook events (elicitation etc.); null when the host has no hooks.</summary>
    public Action<Hooks.HookEvent, System.Text.Json.Nodes.JsonObject>? FireHook { get; init; }

    /// <summary>
    /// Delivers a message to another local session (by title or id prefix),
    /// returning an error line or null on success. Null when the host has no
    /// cross-session messaging; SendMessage then only reaches workers.
    /// </summary>
    public Func<string, string, Task<string?>>? SendToSessionAsync { get; init; }

    /// <summary>
    /// Subscribes this session to ONE notice when another local session next goes
    /// idle (finishes a turn with nothing queued) or exits — the reference
    /// SendMessage's <c>notify_when_idle</c>. Takes the same address
    /// <see cref="SendToSessionAsync"/> takes and answers an error, or null when
    /// the subscription was made. Null when the host has no other sessions.
    /// </summary>
    public Func<string, Task<string?>>? SubscribeToSessionIdleAsync { get; init; }

    /// <summary>
    /// Resolves a possibly relative path against the working directory and returns
    /// the absolute form. Throws when the input is empty.
    /// </summary>
    public string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path));
    }

    public string Truncate(string content, string what = "output")
    {
        if (content.Length <= MaxOutputChars)
            return content;
        return content[..MaxOutputChars] +
               $"\n... [{what} truncated: {content.Length - MaxOutputChars:N0} of {content.Length:N0} characters omitted]";
    }
}
