namespace JarvisCode.App.Services;

/// <summary>
/// <c>/batch</c>: the reference's parallel work orchestration command, ported from
/// CLI 2.1.257 (its <c>kt()</c> registration at 194054034, the prompt builder
/// <c>Gn</c>, the worker template <c>Hn</c>, and the two refusals <c>Wn</c>/<c>qn</c>).
///
/// It is a prompt, not a piece of machinery: the reference registers it with
/// <c>disableModelInvocation</c> and <c>userInvocable</c>, and everything it asks
/// for — plan mode, foreground research subagents, then one background agent per
/// unit in its own worktree opening a PR — is a tool call the model makes. Every
/// tool it names exists here under the same name, so the text rides verbatim; the
/// interpolation holes are the reference's own (<c>hA</c> EnterPlanMode,
/// <c>Ki</c> AskUserQuestion, <c>ry</c> ExitPlanMode, <c>_t</c> Agent, <c>co</c>
/// Skill), resolved against this build's registry.
/// </summary>
internal static class BatchCommand
{
    /// <summary>The reference's <c>bt</c>: the fewest units a batch is decomposed into.</summary>
    public const int MinUnits = 5;

    /// <summary>The reference's <c>vt</c>: the most.</summary>
    public const int MaxUnits = 30;

    private const string EnterPlanModeTool = "EnterPlanMode";
    private const string ExitPlanModeTool = "ExitPlanMode";
    private const string AskUserQuestionTool = "AskUserQuestion";
    private const string AgentTool = "Agent";
    private const string SkillTool = "Skill";

    /// <summary>The row the "/" menu shows.</summary>
    public const string MenuDescription = "Plan a large change; background agents each open a PR";

    /// <summary>The description the command surface carries.</summary>
    public const string Description =
        "Research and plan a large-scale change, then execute it in parallel across " +
        "5–30 isolated worktree agents that each open a PR.";

    public const string ArgumentHint = "<instruction>";

    /// <summary>The reference's <c>qn</c>: /batch typed with nothing after it.</summary>
    public const string NoInstruction = """
        Provide an instruction describing the batch change you want to make.

        Examples:
          /batch migrate from react to vue
          /batch replace all uses of lodash with native equivalents
          /batch add type annotations to all untyped function parameters
        """;

    /// <summary>The reference's <c>Wn</c>: the working directory is not a repository.</summary>
    public const string NotAGitRepository =
        "This is not a git repository. The `/batch` command requires a git repo because it spawns " +
        "agents in isolated git worktrees and creates PRs from each. Initialize a repo first, or run " +
        "this from inside an existing one.";

    /// <summary>
    /// The reference's <c>Hn</c>: the five steps every worker is given verbatim,
    /// quoted into the coordinator's prompt so each agent carries the same tail.
    /// </summary>
    public static string WorkerInstructions =>
        $$"""
        After you finish implementing the change:
        1. **Code review** — Invoke the `{{SkillTool}}` tool with `skill: "code-review"` to find correctness bugs (it reports findings; it does not edit code). Fix any findings it surfaces before continuing.
        2. **Run unit tests** — Run the project's test suite (check for package.json scripts, Makefile targets, or common commands like `npm test`, `bun test`, `pytest`, `go test`). If tests fail, fix them.
        3. **Test end-to-end** — Follow the e2e test recipe from the coordinator's prompt (below). If the recipe says to skip e2e for this unit, skip it.
        4. **Commit and push** — Commit all changes with a clear message, push the branch, and create a PR with `gh pr create`. Use a descriptive title. If `gh` is not available or the push fails, note it in your final message.
        5. **Report** — End with a single line: `PR: <url>` so the coordinator can track it. If no PR was created, end with `PR: none — <reason>`.
        """;

    /// <summary>The reference's <c>Gn(e)</c>: the coordinator prompt for one instruction.</summary>
    public static string Prompt(string instruction) =>
        $$"""
        # Batch: Parallel Work Orchestration

        You are orchestrating a large, parallelizable change across this codebase.

        ## User Instruction

        {{instruction}}

        ## Phase 1: Research and Plan (Plan Mode)

        Call the `{{EnterPlanModeTool}}` tool now to enter plan mode, then:

        1. **Understand the scope.** Launch one or more subagents (in the foreground — you need their results) to deeply research what this instruction touches. Find all the files, patterns, and call sites that need to change. Understand the existing conventions so the migration is consistent.

        2. **Decompose into independent units.** Break the work into {{MinUnits}}–{{MaxUnits}} self-contained units. Each unit must:
           - Be independently implementable in an isolated git worktree (no shared state with sibling units)
           - Be mergeable on its own without depending on another unit's PR landing first
           - Be roughly uniform in size (split large units, merge trivial ones)

           Scale the count to the actual work: few files → closer to {{MinUnits}}; hundreds of files → closer to {{MaxUnits}}. Prefer per-directory or per-module slicing over arbitrary file lists.

        3. **Determine the e2e test recipe.** Figure out how a worker can verify its change actually works end-to-end — not just that unit tests pass. Look for:
           - A `claude-in-chrome` skill or browser-automation tool (for UI changes: click through the affected flow, screenshot the result)
           - A `tmux` or CLI-verifier skill (for CLI changes: launch the app interactively, exercise the changed behavior)
           - A dev-server + curl pattern (for API changes: start the server, hit the affected endpoints)
           - An existing e2e/integration test suite the worker can run

           If you cannot find a concrete e2e path, use the `{{AskUserQuestionTool}}` tool to ask the user how to verify this change end-to-end. Offer 2–3 specific options based on what you found (e.g., "Screenshot via chrome extension", "Run `bun run dev` and curl the endpoint", "No e2e — unit tests are sufficient"). Do not skip this — the workers cannot ask the user themselves.

           Write the recipe as a short, concrete set of steps that a worker can execute autonomously. Include any setup (start a dev server, build first) and the exact command/interaction to verify.

        4. **Write the plan.** In your plan file, include:
           - A summary of what you found during research
           - A numbered list of work units — for each: a short title, the list of files/directories it covers, and a one-line description of the change
           - The e2e test recipe (or "skip e2e because …" if the user chose that)
           - The exact worker instructions you will give each agent (the shared template)

        5. Call `{{ExitPlanModeTool}}` to present the plan for approval.

        ## Phase 2: Spawn Workers (After Plan Approval)

        Once the plan is approved, spawn one background agent per work unit using the `{{AgentTool}}` tool. **All agents must use `isolation: "worktree"` and `run_in_background: true`.** Launch them all in a single message block so they run in parallel.

        For each agent, the prompt must be fully self-contained. Include:
        - The overall goal (the user's instruction)
        - This unit's specific task (title, file list, change description — copied verbatim from your plan)
        - Any codebase conventions you discovered that the worker needs to follow
        - The e2e test recipe from your plan (or "skip e2e because …")
        - The worker instructions below, copied verbatim:

        ```
        {{WorkerInstructions}}
        ```

        Use `subagent_type: "general-purpose"` unless a more specific agent type fits.

        ## Phase 3: Track Progress

        After launching all workers, render an initial status table:

        | # | Unit | Status | PR |
        |---|------|--------|----|
        | 1 | <title> | running | — |
        | 2 | <title> | running | — |

        As background-agent completion notifications arrive, parse the `PR: <url>` line from each agent's result and re-render the table with updated status (`done` / `failed`) and PR links. Keep a brief failure note for any agent that did not produce a PR.

        When all agents have reported, render the final table and a one-line summary (e.g., "22/24 units landed as PRs").
        """;

    /// <summary>
    /// What a typed <c>/batch</c> sends, in the reference's own order: no
    /// instruction refuses first, then a working directory that is not a
    /// repository, and only then the orchestration prompt.
    /// </summary>
    public static string Compose(string instruction, bool insideGitRepository)
    {
        var trimmed = instruction.Trim();
        if (trimmed.Length == 0)
        {
            return NoInstruction;
        }

        return insideGitRepository ? Prompt(trimmed) : NotAGitRepository;
    }

    /// <summary>True when the composed text is the prompt rather than one of the two refusals.</summary>
    public static bool IsPrompt(string composed) => composed.StartsWith("# Batch:", StringComparison.Ordinal);

    /// <summary>
    /// The reference's <c>rh()</c> check, without shelling out: a <c>.git</c>
    /// directory — or the <c>.git</c> file a linked worktree carries — at this
    /// directory or above it.
    /// </summary>
    public static bool InsideGitRepository(string? workingDirectory)
    {
        try
        {
            var directory = workingDirectory is { Length: > 0 }
                ? new System.IO.DirectoryInfo(workingDirectory)
                : null;
            while (directory is not null)
            {
                var marker = System.IO.Path.Combine(directory.FullName, ".git");
                if (System.IO.Directory.Exists(marker) || System.IO.File.Exists(marker))
                {
                    return true;
                }

                directory = directory.Parent;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable path is not a repository as far as this check is concerned.
        }

        return false;
    }
}
