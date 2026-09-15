using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// Swaps the built-in tools' one-line descriptions for ports of the reference
/// app's tool docs (Claude Code Desktop, CLI 2.1.247) — the workflow rules the
/// harness teaches through its tool prompts: dedicated tools over shell, exact
/// unique old_string edits, git etiquette, todo discipline. Texts are adapted
/// where our tools differ (PowerShell, fresh shell per call, .NET regex, no
/// read-before-edit enforcement). Wrapping happens in the App so Core's tools
/// keep their own descriptions for other consumers.
/// </summary>
public static class ReferenceToolDocs
{
    /// <summary>
    /// The reference app's TodoWrite tool-result line, returned by todo_write in
    /// place of the engine's own summary.
    /// </summary>
    public const string TodoWriteResult =
        "Todos have been modified successfully. Ensure that you continue to use the todo list to track your " +
        "progress. Please proceed with the current tasks if applicable";

    /// <summary>
    /// Gives every tool the description and schema the reference sends for it.
    /// </summary>
    /// <remarks>
    /// Measured on CLI 2.1.257 (one -p run per model class; the tools array read
    /// off the request): every reference tool's doc and schema are carried
    /// verbatim from <see cref="CapturedToolDocs"/>, with the doc in its lean or
    /// classic form after the model's prompt form, and two docs built per model —
    /// Agent (<see cref="RunAgentDoc"/>) and Bash, whose commit trailer names the
    /// model and whose dedicated-tools bullet the fable-5-1 bundle drops. The
    /// captured schema stands in only where this tool accepts every argument it
    /// declares; Agent keeps its own (two arguments more than the reference's) and
    /// so does every tool this build adds. A schema that is this build's own is
    /// shaped the way the reference's zod output is (<see cref="NormalizeSchema"/>),
    /// so the wire reads the same whichever tool answered. MCP tools pass through:
    /// their schemas are the server's.
    /// </remarks>
    public static IReadOnlyList<ITool> Apply(
        IEnumerable<ITool> tools,
        bool coordinatorMode = false,
        PromptModelProfile? profile = null,
        string? commitTrailer = null,
        bool forkAvailable = false)
    {
        var form = profile ?? PromptModelProfile.For("");
        var trailer = commitTrailer ?? CommitTrailers.Name(form.Canonical, form.Canonical);
        return [.. tools.Select(tool => Describe(tool, coordinatorMode, form, trailer, forkAvailable))];
    }

    private static ITool Describe(
        ITool tool, bool coordinatorMode, PromptModelProfile profile, string trailer, bool forkAvailable)
    {
        switch (tool.Name)
        {
            case SubagentTool.ToolName:
                // The doc is per model; the schema stays this build's, which adds
                // `name` and `cwd` to the reference's six.
                return new DescribedTool(
                    tool, RunAgentDoc(profile, forkAvailable), schema: NormalizeSchema(tool.InputSchema));
            case JarvisCode.Core.Tools.BuiltIn.SkillTool.ToolName:
            {
                var doc = CapturedToolDocs.Description(tool.Name, profile.Lean)!;
                return new DescribedTool(
                    tool, coordinatorMode ? doc + "\n\n" + SkillCoordinatorSuffix : doc, schema: SchemaFor(tool));
            }
            case "Bash":
                return new DescribedTool(tool, BashDoc(profile, trailer), schema: SchemaFor(tool));
            case "PowerShell":
                return new DescribedTool(
                    tool, Overrides["PowerShell"].Replace(CommitTrailers.Token, trailer, StringComparison.Ordinal),
                    schema: NormalizeSchema(tool.InputSchema));
            case "todo_write":
                return new DescribedTool(tool, Overrides["todo_write"], TodoWriteResult, NormalizeSchema(tool.InputSchema));
        }

        if (CapturedToolDocs.Description(tool.Name, profile.Lean) is { } captured)
        {
            // The reference's doc describes the reference's arguments, so it rides
            // only with the reference's schema; a tool whose arguments still differ
            // keeps its own doc and schema (in the reference's shape) rather than
            // a doc that names parameters it does not take.
            return AcceptsCapturedSchema(tool)
                ? new DescribedTool(tool, captured, schema: CapturedToolDocs.Schema(tool.Name))
                : new DescribedTool(tool, tool.Description, schema: NormalizeSchema(tool.InputSchema));
        }

        if (!tool.Name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return new DescribedTool(tool, tool.Description, schema: NormalizeSchema(tool.InputSchema));
        }

        return tool;
    }

    /// <summary>
    /// The reference Bash doc for this model: lean or classic form, the
    /// dedicated-tools bullet dropped for fable-5-1 (measured: 1121 chars against
    /// opus-5's 1444, the one line apart from the trailer), and the model named in
    /// the commit trailer.
    /// </summary>
    internal static string BashDoc(PromptModelProfile profile, string trailer)
    {
        var doc = CapturedToolDocs.Description("Bash", profile.Lean)!;
        if (profile.Fable51)
        {
            doc = string.Join("\n", doc.Split('\n').Where(static line =>
                !line.TrimStart('-', ' ').StartsWith("IMPORTANT: Avoid using this tool to run", StringComparison.Ordinal)));
        }

        return doc.Replace(CommitTrailers.Token, trailer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The captured schema when this tool accepts every argument it declares,
    /// else this tool's own schema in the reference's shape.
    /// </summary>
    internal static JsonObject SchemaFor(ITool tool) =>
        AcceptsCapturedSchema(tool) ? CapturedToolDocs.Schema(tool.Name)! : NormalizeSchema(tool.InputSchema);

    /// <summary>True when this tool declares every argument the reference's schema does.</summary>
    internal static bool AcceptsCapturedSchema(ITool tool) =>
        CapturedToolDocs.Schema(tool.Name)?["properties"] is JsonObject theirs &&
        tool.InputSchema["properties"] is JsonObject ours &&
        theirs.All(property => ours.ContainsKey(property.Key));

    /// <summary>
    /// The shape zod-to-json-schema gives every reference tool: <c>$schema</c>
    /// first, then type, properties (each with its description first), required,
    /// anything else, and <c>additionalProperties: false</c> last.
    /// </summary>
    internal static JsonObject NormalizeSchema(JsonObject schema)
    {
        var result = new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema" };
        if (schema["type"] is { } type)
        {
            result["type"] = type.DeepClone();
        }

        if (schema["properties"] is JsonObject properties)
        {
            var normalized = new JsonObject();
            foreach (var (name, value) in properties)
            {
                if (value is JsonObject property)
                {
                    var ordered = new JsonObject();
                    if (property["description"] is { } description)
                    {
                        ordered["description"] = description.DeepClone();
                    }

                    foreach (var (key, inner) in property)
                    {
                        if (key != "description")
                        {
                            ordered[key] = inner?.DeepClone();
                        }
                    }

                    normalized[name] = ordered;
                }
                else
                {
                    normalized[name] = value?.DeepClone();
                }
            }

            result["properties"] = normalized;
        }

        foreach (var (key, value) in schema)
        {
            if (key is not ("$schema" or "type" or "properties" or "additionalProperties"))
            {
                result[key] = value?.DeepClone();
            }
        }

        result["additionalProperties"] = schema["additionalProperties"]?.DeepClone() ?? false;
        return result;
    }

    /// <summary>The reference's coordinator-mode suffix on the Skill-tool doc.</summary>
    public const string SkillCoordinatorSuffix =
        "In a coordinator session, the coordinator's own use of this tool is read-only: it loads the skill's " +
        "instructions to inform replies, triage, and coordination but does not run the skill — no fork, no " +
        "permission grants, no hooks, no preamble shell commands. Execution happens in workers: hand the skill " +
        "to one worker, or when its recipe is orchestration, spawn workers per that recipe and synthesize their " +
        "results. Worker skill invocations execute normally. A `<command-name>` block that arrived with only a " +
        "delegation summary (no skill content) does not mean the skill is loaded — calling this tool to load it " +
        "is still appropriate then.";

    /// <summary>
    /// The reference Agent tool doc, measured out of CLI 2.1.257 captures (one per
    /// model class). It has two forms and one optional sentence:
    /// a <b>lean-prompt</b> model gets the compact doc — "## When to use", four
    /// bullets and the background paragraph — and a <b>classic-prompt</b> model
    /// the long form with "## When not to use", "## Usage notes", "## Writing the
    /// prompt" and the examples (the reference's <c>vA()</c> reads the same
    /// lean-prompt predicate the system prompt does). The "Reach for this…"
    /// sentence opens the lean doc's "When to use" exactly when the model is
    /// <em>not</em> under the reduced-delegation stance (<c>hH() === "default"</c>):
    /// fable-5-1, fable-5 and opus-4-8 carry it, opus-5 — whose prompt closes with
    /// "Do not use the Agent tool, workflows, or deep-research unless…" — does not.
    ///
    /// The <c>fork</c> branch is carried in <see cref="ForkAgentDoc"/> and
    /// selected by <paramref name="forkAvailable"/>; the one branch not taken
    /// here is the "pro" plan's spawn warning, declared in
    /// Deltas/reference-surface-deltas.tsv.
    /// </summary>
    public static string RunAgentDoc(PromptModelProfile? profile = null, bool forkAvailable = false)
    {
        profile ??= PromptModelProfile.For("");
        if (forkAvailable)
        {
            // The fork branch rewrites nine places at once rather than adding a
            // section, so both forms are carried whole in ForkAgentDoc.
            return profile.Lean ? ForkAgentDoc.Lean(!profile.HasReducedDelegation) : ForkAgentDoc.Classic;
        }

        if (!profile.Lean)
        {
            return AgentDocClassic;
        }

        return profile.HasReducedDelegation
            ? AgentDocLeanHead + "## When to use\n\n" + AgentDocLeanTail
            : AgentDocLeanHead + "## When to use\n\n" + AgentDocReachSentence + AgentDocLeanTail;
    }

    /// <summary>The lean doc up to its "When to use" heading.</summary>
    internal const string AgentDocLeanHead = """
            Launch a new agent to handle complex, multi-step tasks. Each agent type has specific capabilities and tools available to it.

            Available agent types are listed in <system-reminder> messages in the conversation.

            When using the Agent tool, specify a subagent_type parameter to select which agent type to use. If omitted, the general-purpose agent is used.


            """;

    /// <summary>The sentence the delegation stance adds or withholds.</summary>
    internal const string AgentDocReachSentence =
        "Reach for this when the task matches an available agent type, when you have independent work to run in " +
        "parallel, or when answering would mean reading across several files — delegate it and you keep the " +
        "conclusion, not the file dumps. ";

    /// <summary>The lean doc from its single-fact sentence to the end.</summary>
    internal const string AgentDocLeanTail = """
            For a single-fact lookup where you already know the file, symbol, or value, search directly. Once you've delegated a search, don't also run it yourself — wait for the result.

            - The agent's final report is not shown to the user — relay what matters.
            - Use SendMessage with the agent's ID or name to continue a previously spawned agent with its context intact; a new Agent call starts fresh.
            - Each agent type's model, reasoning effort, and tools come from its definition (`.claude/agents/*.md` frontmatter or SDK `agents`).
            - `isolation: "worktree"` gives the agent its own git worktree (auto-cleaned if unchanged).
            - Subagents run in the background by default; you'll be notified when one completes. Pass `run_in_background: false` only when your very next action depends on the result and nothing else could usefully happen while it runs — otherwise background it so the user can interject. Never fabricate or predict a pending agent's results — the notification is never something you write yourself; if the user asks before it arrives, say it's still running.
            """;

    /// <summary>The classic-prompt models' long form, verbatim.</summary>
    internal const string AgentDocClassic = """
            Launch a new agent to handle complex, multi-step tasks. Each agent type has specific capabilities and tools available to it.

            Available agent types are listed in <system-reminder> messages in the conversation.

            When using the Agent tool, specify a subagent_type parameter to select which agent type to use. If omitted, the general-purpose agent is used.

            ## When not to use

            If the target is already known, use the direct tool: Read for a known path, the Grep tool for a specific symbol or string. Reserve this tool for open-ended questions that span the codebase, or tasks that match an available agent type.

            ## Usage notes

            - Always include a short description summarizing what the agent will do
            - When the agent is done, its final report is not visible to the user. To show the user the result, you should send a text message back to the user with a concise summary of the result.
            - Trust but verify: an agent's summary describes what it intended to do, not necessarily what it did. When an agent writes or edits code, check the actual changes before reporting the work as done.
            - Agents run in the background by default. When an agent runs in the background, you will be automatically notified when it completes — do NOT sleep, poll, or proactively check on its progress. Continue with other work or respond to the user instead.
            - **Foreground vs background**: Pass `run_in_background: false` only when your very next action depends on the agent's result and nothing else could usefully happen while it runs — e.g., a research agent whose finding gates the edit you're about to make. Otherwise let it run in the background (the default) — this includes fire-and-forget work, independent investigations, and anything where the user might hand you something else in the meantime. Wanting the result "next" is not enough on its own.
            - **Don't race**: after launching a background agent, you know nothing about its results. Never fabricate or predict them in any format — not as prose, summary, or structured output. The completion notification arrives in a later turn; it is never something you write yourself. If the user asks before it lands, say the agent is still running — give status, not a guess.
            - To continue a previously spawned agent, use SendMessage with the agent's ID or name as the `to` field — that resumes it with full context. A new Agent call starts a fresh agent with no memory of prior runs, so the prompt must be self-contained.
            - Each agent type's model, reasoning effort, and tool access are set in its definition (`.claude/agents/*.md` frontmatter, or the SDK `agents` option); the `model` parameter here overrides the definition for this one call.
            - Clearly tell the agent whether you expect it to write code or just to do research (search, file reads, web fetches, etc.), since a fresh agent is not aware of the user's intent
            - If the agent description mentions that it should be used proactively, then you should try your best to use it without the user having to ask for it first.
            - If the user specifies that they want you to run agents "in parallel", you MUST send a single message with multiple Agent tool use content blocks. For example, if you need to launch both a build-validator agent and a test-runner agent in parallel, send a single message with both tool calls.
            - With `isolation: "worktree"`, the worktree is automatically cleaned up if the agent makes no changes; otherwise the path and branch are returned in the result.

            ## Writing the prompt

            Brief the agent like a smart colleague who just walked into the room — it hasn't seen this conversation, doesn't know what you've tried, doesn't understand why this task matters.
            - Explain what you're trying to accomplish and why.
            - Describe what you've already learned or ruled out.
            - Give enough context about the surrounding problem that the agent can make judgment calls rather than just following a narrow instruction.
            - If you need a short response, say so ("report in under 200 words").
            - Lookups: hand over the exact command. Investigations: hand over the question — prescribed steps become dead weight when the premise is wrong.

            Terse command-style prompts produce shallow, generic work.

            **Never delegate understanding.** Don't write "based on your findings, fix the bug" or "based on the research, implement it." Those phrases push synthesis onto the agent instead of doing it yourself. Write prompts that prove you understood: include file paths, line numbers, what specifically to change.

            Example usage:

            <example>
            user: "What's left on this branch before we can ship?"
            assistant: <thinking>A survey question across git state, tests, and config. I'll delegate it and ask for a short report so the raw command output stays out of my context.</thinking>
            Agent({
              description: "Branch ship-readiness audit",
              prompt: "Audit what's left before this branch can ship. Check: uncommitted changes, commits ahead of main, whether tests exist, whether the GrowthBook gate is wired up, whether CI-relevant files changed. Report a punch list — done vs. missing. Under 200 words."
            })
            assistant: Ship-readiness audit running in the background.
            <commentary>
            The prompt is self-contained: it states the goal, lists what to check, and caps the response length. The agent runs in the background (the default), so the turn ends here — nothing about its findings is known yet. The report arrives in a SEPARATE turn, as a completion notification from outside; it is never something you write yourself.
            </commentary>
            [later turn — notification arrives as user message]
            assistant: Audit's back. Three blockers: no tests for the new prompt path, GrowthBook gate wired but not in build_flags.yaml, and one uncommitted file.
            </example>

            <example>
            user: "so is the gate wired up or not"
            <commentary>
            User asks mid-wait. The audit was launched to answer exactly this, and it hasn't returned. Give status, not a fabricated result.
            </commentary>
            assistant: Still waiting on the audit — that's one of the things it's checking. Should land shortly.
            </example>

            <example>
            user: "Can you get a second opinion on whether this migration is safe?"
            assistant: <thinking>I'll ask the code-reviewer agent — it won't see my analysis, so it can give an independent read.</thinking>
            Agent({
              description: "Independent migration review",
              subagent_type: "code-reviewer",
              prompt: "Review migration 0042_user_schema.sql for safety. Context: we're adding a NOT NULL column to a 50M-row table. Existing rows get a backfill default. I want a second opinion on whether the backfill approach is safe under concurrent writes — I've checked locking behavior but want independent verification. Report: is this safe, and if not, what specifically breaks?"
            })
            <commentary>
            The agent starts with no context from this conversation, so the prompt briefs it: what to assess, the relevant background, and what form the answer should take.
            </commentary>
            </example>

            """;

    public static readonly IReadOnlyDictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PowerShell"] = """
            Executes a Windows PowerShell command and returns its output.

            This tool runs Windows PowerShell 5.1 (powershell.exe), not cmd.exe or bash. Use PowerShell syntax: `$env:VAR` not `%VAR%`, `;` to chain commands (the `&&` and `||` pipeline operators are not available in 5.1), backtick as the escape character.

            - Each call runs in a fresh shell in the working directory: `cd`, environment variables and functions do not persist between calls.
            - IMPORTANT: Avoid using this tool for file operations unless explicitly instructed or a dedicated tool cannot do the job. File search: use glob (NOT Get-ChildItem -Recurse). Content search: use grep (NOT Select-String). Read files: use Read (NOT Get-Content). Edit files: use Edit. Write files: use Write.
            - Command output is displayed to you, not reliably to the user.
            - Pass `description` — a clear, concise description of what the command does in 5-10 words, in active voice. The user sees it as the command's label. Examples: ls → "List files in current directory"; dotnet test → "Run the test suite"; git push origin master → "Push commits to origin".
            - `timeout` is in milliseconds: default 120000, max 600000. Interactive commands (console prompts, editors) are not supported — they hang until the timeout.
            - `run_in_background` runs the command detached and returns a task id immediately (dev servers, long builds); poll it with TaskOutput, stop it with TaskStop.

            # Git
            - Interactive flags (`-i`, e.g. `git rebase -i`, `git add -i`) are not supported in this environment.
            - Use the `gh` CLI for GitHub operations (PRs, issues, API).
            - Commit or push only when the user asks. If on the default branch, branch first.
            - Prefer creating a NEW commit over amending. When a pre-commit hook fails, the commit did NOT happen — --amend would modify the PREVIOUS commit, so fix the issue, re-stage, and commit again.
            - When staging, prefer adding specific files by name rather than `git add -A` or `git add .`, which can sweep in unrelated or sensitive files (.env, credentials).
            - Never update the git config, never skip hooks (--no-verify), and never run destructive git commands (push --force, reset --hard, checkout ., clean -f) unless the user explicitly asks.
            - End git commit messages with:
            Co-Authored-By: {{TRAILER_MODEL}} <noreply@anthropic.com>
            - End PR bodies with:
            🤖 Generated with [Claude Code](https://claude.com/claude-code)
            """,

        ["todo_write"] = """
            Use this tool to create and manage a structured task list for the current session. Each call replaces the whole list. The user watches the list live, so it doubles as your progress report.

            When to use: complex multi-step work (3 or more distinct steps), non-trivial tasks that need planning, multiple tasks the user listed, or new instructions arriving mid-task. When NOT to use: a single straightforward task, or trivial work where tracking adds nothing — just do it.

            Task states and management:
            - Statuses: pending, in_progress, completed. Keep exactly ONE task in_progress at a time.
            - Mark a task completed IMMEDIATELY when it is done; do not batch up several completions.
            - Only mark a task completed when it is fully done — if tests fail or the implementation is partial, keep it in_progress and add a new task describing what still must be resolved.
            - Break complex work into smaller steps with short, imperative descriptions.
            """,
    };

    /// <summary>
    /// An ITool with the reference description; everything else forwards. When
    /// <paramref name="successText"/> is set, a successful text-only result is
    /// replaced with the reference app's wording (errors pass through).
    /// </summary>
    private sealed class DescribedTool(ITool inner, string description, string? successText = null, JsonObject? schema = null)
        : ITool, IAliasedTool
    {
        public string Name => inner.Name;

        /// <summary>Swapping a tool's doc must not cost it its alias.</summary>
        public IReadOnlyList<string> Aliases =>
            inner is IAliasedTool aliased ? aliased.Aliases : [];

        public string Description => description;

        public JsonObject InputSchema => schema ?? inner.InputSchema;

        public bool IsReadOnly => inner.IsReadOnly;

        public string DescribeCall(JsonObject arguments) => inner.DescribeCall(arguments);

        public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var result = await inner.ExecuteAsync(arguments, context, cancellationToken);
            return successText is not null && result is { IsError: false, Images: null }
                ? ToolResult.Success(successText)
                : result;
        }
    }
}
