using JarvisCode.Core.Agent;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The strings this app took from the reference CLI's own binary, checked
/// against that binary rather than against a remembered quote.
///
/// The desktop app's catalogue (see <see cref="UiStringParityTests"/>) covers
/// what the UI renders; the harness strings ported for teammates, the task board
/// and dynamic workflows only exist inside the CLI, so the check is "this exact
/// text is still in the reference build". A reference rewording fails here
/// instead of silently leaving us quoting a version nobody ships any more.
/// </summary>
public sealed class CliLiteralParityTests
{
    /// <summary>Every ported literal, with the feature it belongs to for the failure message.</summary>
    public static TheoryData<string, string> PortedLiterals
    {
        get
        {
            var data = new TheoryData<string, string>();

            // Teammates: naming rules and their refusals.
            data.Add("agent name regex",
                "name must start with a letter or digit and contain only letters, digits, underscores, or hyphens (max 64 chars)");
            data.Add("reserved recipient refusal",
                "name must not be a reserved recipient (\"main\" or \"team-lead\", in any spelling) or have the shape of an agent id — those already address an agent directly");
            data.Add("Agent name parameter",
                "Name for the spawned agent. Makes it addressable via SendMessage({to: name}) while running.");
            data.Add("tmux on Windows", TeammateBackends.TmuxUnavailableWindows);
            data.Add("agent swarms need tmux", TeammateBackends.SwarmNeedsTmux);

            // The Team Coordination reminder, in the pieces the reference stores.
            data.Add("team coordination heading", "# Team Coordination");
            data.Add("team coordination identity", "You are a teammate in this session's agent team.");
            data.Add("team lead line",
                "**Team Leader:** The team lead's name is \"team-lead\". Send updates and completion notifications to them.");
            data.Add("team config discovery", "Read the team config to discover your teammates' names.");

            // The task board's docs and result lines.
            data.Add("task create summary", TaskBoardSummaries.Create);
            data.Add("task get summary", TaskBoardSummaries.Get);
            data.Add("task list summary", TaskBoardSummaries.List);
            data.Add("task update summary", TaskBoardSummaries.Update);
            data.Add("task list empty", "No tasks found");
            data.Add("task not found", "Task not found");
            data.Add("task status workflow", "Status progresses: `pending` → `in_progress` → `completed`");
            data.Add("teammate workflow section", "## Teammate Workflow");

            // Dynamic workflows: the determinism guards and the refusals.
            data.Add("date guard", WorkflowEngine.DateGuardMessage);
            data.Add("random guard", WorkflowEngine.RandomGuardMessage);
            data.Add("with statement refusal", "'with' statements are not supported in workflow scripts.");
            data.Add("import refusal", "import() is not available in workflow scripts.");
            data.Add("managed settings refusal",
                "Dynamic workflows are disabled by managed settings (`disableWorkflows`).");
            data.Add("not enabled refusal",
                "Dynamic workflows are not enabled for this session (org policy, launch gate, or the \"Dynamic workflows\" setting in /config).");
            data.Add("named-only variable", "CLAUDE_WORKFLOW_NAME_ONLY");
            data.Add("disable-workflows variable", "CLAUDE_CODE_DISABLE_WORKFLOWS");
            data.Add("task budget error", "--task-budget must be a positive integer");

            // The lead/teammate protocol handshakes.
            data.Add("shutdown request sent", "Shutdown request sent to ");
            data.Add("shutdown approved", "Shutdown approved. ");
            data.Add("agent is now exiting", " is now exiting.");
            data.Add("shutdown rejected", "Shutdown rejected. Reason: ");
            data.Add("continuing to work", ". Continuing to work.");
            data.Add("confirmation to team-lead", "Sent confirmation to team-lead.");
            data.Add("only the lead approves plans",
                "Only the team lead can approve plans. Teammates cannot approve their own or other plans.");
            data.Add("plan approved wording",
                " They will receive the approval and can proceed with implementation.");
            data.Add("plan rejected wording", " with feedback: ");
            data.Add("shutdown_request type", "shutdown_request");
            data.Add("plan_approval_response type", "plan_approval_response");

            // Team memory: the two directories and their warnings.
            data.Add("private store label", " (private to this user)");
            data.Add("shared store label", " (shared with all users of this project).");
            data.Add("directories already exist",
                "These directories already exist \u2014 write to them directly with the Write tool " +
                "(do not run mkdir or check for their existence).");
            data.Add("no secrets in team memory",
                "shared with teammates. Never write secrets or credentials to team memory.");
            data.Add("team memory read-only",
                "Team memory is read-only this session \u2014 you cannot persist new memories.");

            // The workflow size guideline.
            data.Add("size guideline default opening", "This session has the default workflow size guideline:");
            data.Add("size guideline configured opening",
                "A workflow size guideline is configured for this session:");
            data.Add("size guideline cap phrase", " \u2014 keep workflows under ");
            data.Add("size guideline not a hard limit",
                "This is a guideline, not a hard limit \u2014 follow it unless the user's prompt calls for a " +
                "different scale.");
            data.Add("size guideline config hint",
                " The user can raise or remove it with \"Dynamic workflow size\" in /config.");
            data.Add("size guideline unrestricted notice",
                "Workflow size is now unrestricted \u2014 no size guideline applies.");

            // agent() options.
            data.Add("agent effort option",
                "opts.effort overrides the reasoning effort for this agent call ('low' | 'medium' | 'high' | " +
                "'xhigh' | 'max')");
            data.Add("agent worktree option", "the worktree is auto-removed if unchanged.");

            // Peer @-mentions.
            data.Add("peer mention resolved opening", "The user @-mentioned the Claude session ");
            data.Add("peer mention exact token",
                " \u2014 that exact name-and-ref token. Do not message it unless the user's message actually " +
                "asks you to.");
            data.Add("peer mention ambiguous opening", ", which matches ");
            data.Add("peer mention unverified names",
                "Session names are self-chosen and unverified, so confirm with the user which one they mean");
            data.Add("peer mention do not guess", "Do not guess between them.");
            data.Add("peer mention overflow", " more with that name (");

            // Workflow progress groups: the event names the runner emits and the
            // saved workflow's phase listing.
            data.Add("workflow_phase event", "workflow_phase");
            data.Add("workflow_agent event", "workflow_agent");
            data.Add("workflow_log event", "workflow_log");
            data.Add("meta phases field", "phases");
            data.Add("saved workflow phase listing", "\n\nPhases:\n");

            // Background agents.
            data.Add("stopped by the user (one)", "\" was stopped by the user.");
            data.Add("stopped by the user (many)", " background agents were stopped by the user:");
            data.Add("previous session report", " background agents from the previous session: ");

            // The turn-recovery nudges. The reference stores each as two adjacent
            // literals joined at build time, so the halves are what can be pinned —
            // which is also why the joined sentence is a declared ported-text delta.
            data.Add("output limit resume (head)",
                "Output token limit hit. Resume directly \u2014 no apology, no recap of what you were doing. ");
            data.Add("output limit resume (tail)",
                "Pick up mid-thought if that is where the cut happened. Break remaining work into smaller pieces.");
            data.Add("truncated resume (head)",
                "Your response above was cut off mid-stream. Resume directly from where it stops \u2014 no apology, no recap. ");
            data.Add("truncated resume (tail)", "If none of it survived, answer the request from the start.");
            data.Add("malformed tool use retry", TurnRecovery.MalformedToolUseRetry);
            data.Add("malformed tool use exhausted", TurnRecovery.MalformedToolUseExhausted);
            data.Add("thinking-only nudge", TurnRecovery.ThinkingOnlyNudge);

            // LLM prompt hooks: the stop-condition wording and the block cap notice,
            // whose three pieces the reference likewise stores separately.
            data.Add("stop condition question",
                "Based on the conversation transcript above, has the following stopping condition been satisfied? Answer based on transcript evidence only.");
            data.Add("stop condition system prompt",
                "You are evaluating a stop-condition hook in Claude Code. Read the conversation transcript carefully, then judge whether the user-provided condition is satisfied.");
            data.Add("generic prompt hook system prompt",
                "You are evaluating a hook condition in Claude Code. Judge whether the user-provided condition is met.");
            data.Add("prompt hook verdict tool", PromptHooks.VerdictToolDescription);
            data.Add("stop hook block cap (head)", "A hook blocked the turn from ending ");
            data.Add("stop hook block cap (tail)",
                " consecutive times \u2014 overriding and ending turn. ");
            data.Add("stop hook block cap (guidance)",
                "For Stop/SubagentStop hooks, check stop_hook_active in the input and return success while it's true. Set CLAUDE_CODE_STOP_HOOK_BLOCK_CAP to raise this limit.");

            // HTTP and MCP hooks: the refusals that decide whether a hook runs.
            data.Add("http hook not supported", "HTTP hooks are not supported for ");
            data.Add("http hook url not allowed", "does not match any pattern in allowedHttpHookUrls");
            data.Add("http hook address blocked",
                "(private/link-local address). Loopback (127.0.0.1, ::1) is allowed for local dev.");
            data.Add("mcp hook no client context", "hook event (no MCP client context)");
            data.Add("mcp hook not connected", "not connected");
            data.Add("http hook allowed env vars", "allowedEnvVars");

            // Goal check-ins: the two bodies and the pause they end on.
            data.Add("goal check-in prefix", "Goal check-in: \u00AB");
            data.Add("goal check-in summary (running)", "Goal check-in: background work still running");
            data.Add("goal check-in summary (gone)", "Goal check-in: background work no longer running");
            data.Add("goal check-in body (gone)",
                " min while background work ran, and that work is no longer running (it finished or was " +
                "stopped without reporting back). Continue toward the goal.");
            data.Add("goal check-in body (running)",
                " min because background work is still running:");
            data.Add("goal check-in advice",
                "Check on their progress (e.g. read their output). If they are progressing, say so briefly " +
                "and keep waiting; if they are stuck or no longer needed, fix or stop them and continue " +
                "toward the goal.");
            data.Add("goal check-in paused (summary)",
                " \u00B7 idle check-ins paused until your next message");
            data.Add("goal check-in paused (body)",
                " Claude Code won't wake this session for another check-in until the user sends a message, " +
                "so say clearly where things stand.");
            data.Add("goal check-in interval variable", "CLAUDE_CODE_GOAL_CHECKIN_MINUTES");

            // The interactive REPL: the reference's own key layer, rendering,
            // dialogs, command surface and startup tips, each read out of the
            // CLI binary at 2.1.257.
            data.Add("keybindings bindings array", "keybindings.json must have a \"bindings\" array");
            data.Add("keybindings format hint", "Use format: { \"bindings\": [ ... ] }");
            data.Add("keybindings must be an array", "\"bindings\" must be an array");
            data.Add("keybindings block structure", "keybindings.json contains invalid block structure");
            data.Add("keybindings block shape hint",
               "Each block must have \"context\" (string) and \"bindings\" (object mapping keys to a string action or null)");
            data.Add("keybindings must contain an array", "keybindings.json must contain an array");
            data.Add("keybindings wrap hint", "Wrap your bindings in [ ]");
            data.Add("keybindings extra plus hint", "Remove extra \"+\" characters");
            data.Add("keybindings duplicate hint",
               "This key appears multiple times in the same context. JSON uses the last value, earlier values are ignored.");
            data.Add("keybindings chat-context hint", "Move this binding to a block with \"context\": \"Chat\"");
            data.Add("keybindings ignored suffix", " \u2014 this binding is ignored");
            data.Add("keybindings reserved ctrl+c", "Cannot be rebound - used for interrupt/exit (hardcoded)");
            data.Add("keybindings reserved ctrl+m",
               "Cannot be rebound - identical to Enter in terminals (both send CR)");
            data.Add("keybindings capslock", "Caps Lock is not delivered to terminal applications");
            data.Add("keybindings schema url", "https://www.schemastore.org/claude-code-keybindings.json");
            data.Add("keybindings docs url", "https://code.claude.com/docs/en/keybindings");
            data.Add("keybindings voice warning",
               " to voice:pushToTalk prints into the input during warmup; use space or a modifier combo like meta+k");
            data.Add("waiting for API response", "Waiting for API response");
            data.Add("will retry in", " \u00B7 will retry in ");
            data.Add("check your network", " \u00B7 check your network");
            data.Add("api error label", "API error");
            data.Add("usage limit label", "usage limit");
            data.Add("compacting conversation", "Compacting conversation");
            data.Add("interrupted row", "What should Claude do instead?");
            data.Add("spinner first verb", "Accomplishing");
            data.Add("spinner last verb", "Zigzagging");
            data.Add("accept edits on", "accept edits on");
            data.Add("plan mode on", "plan mode on");
            data.Add("auto mode on", "auto mode on");
            data.Add("shortcuts hint", "? for shortcuts");
            data.Add("tasks hint", "/tasks to see subagents");
            data.Add("hide diff hint", "/diff to hide diff");
            data.Add("side question hint", "/btw for side question");
            data.Add("shell mode hint", "! for shell mode");
            data.Add("commands hint", "/ for commands");
            data.Add("file paths hint", "@ for file paths");
            data.Add("double tap esc", "double tap esc to clear input");
            data.Add("verbose output hint", " for verbose output");
            data.Add("keybindings customize hint", "/keybindings to customize");
            data.Add("newline hint", "backslash (\\) + return (\u23CE) for newline");
            data.Add("until auto-compact", "% until auto-compact");
            data.Add("context used", "% context used");
            data.Add("run /compact", "Run /compact to compact & continue");
            data.Add("paste again to expand", "paste again to expand");
            data.Add("press up to edit queued", "Press up to edit queued messages");
            data.Add("press again to exit", " again to exit");
            data.Add("do you want to proceed", "Do you want to proceed?");
            data.Add("permission refusal", "No, and tell Claude what to do differently ");
            data.Add("reject placeholder", "tell Claude what to do differently");
            data.Add("accept placeholder", "tell Claude what to do next");
            data.Add("mode label acceptEdits", "accept edits (auto-approve file edits and common file commands)");
            data.Add("mode label bypass", "BYPASS PERMISSIONS (no further prompts)");
            data.Add("mode label auto", "auto (no routine prompts; a reviewer model screens actions)");
            data.Add("mode label plan", "plan mode (research and propose changes without making them)");
            data.Add("mode label default", "default (ask each time)");
            data.Add("switch mode row", "Yes, and switch to ");
            data.Add("dont ask shell row", "Yes, and don't ask again for ");
            data.Add("dont ask any row", "Yes, and don\u2019t ask again for any ${t} command");
            // The placeholder inside this template is a minified local, and 2.1.260
            // renamed it from i to s; the sentence did not move. Pinning the
            // stable half keeps the check about the wording.
            data.Add("dont ask rule row", "Yes, and don\u2019t ask again for: ");
            data.Add("allow reading row", "Yes, allow reading from ");
            data.Add("allow access row", "Yes, and always allow access to ");
            data.Add("ready to code", "Ready to code?");
            data.Add("exit plan mode title", "Exit plan mode?");
            data.Add("plan heading", "Here is Claude's plan:");
            data.Add("plan question",
               "Claude has written up a plan and is ready to execute. Would you like to proceed?");
            data.Add("plan empty question", "Claude wants to exit plan mode");
            data.Add("no plan found", "No plan found. Please write your plan to the plan file first.");
            data.Add("plan too large",
               "(the plan is too large to be shown in full \u2014 approval is withheld; send feedback asking for a shorter plan, or press Esc)");
            data.Add("keep planning row", "No, keep planning");
            data.Add("keep planning placeholder", "Tell Claude what to change");
            data.Add("keep planning description", "shift+tab to approve with this feedback");
            data.Add("auto-accept edits row", "Yes, auto-accept edits");
            data.Add("manually approve row", "Yes, manually approve edits");
            data.Add("plan saved", "Plan saved!");
            data.Add("chat about this", "Chat about this");
            data.Add("ready to submit", "Ready to submit your answers?");
            data.Add("submit answers", "Submit answers");
            data.Add("review your answers", "Review your answers");
            data.Add("not all answered", "You have not answered all questions");
            data.Add("accessing workspace", "Accessing workspace:");
            data.Add("trust confirm", "Yes, I trust this folder");
            data.Add("trust cancel", "No, exit");
            data.Add("trust pre-approved",
               "These will apply without asking. Only proceed if you trust this configuration.");
            data.Add("resume title", "Resume session");
            data.Add("resume refreshing", " \u00B7 Refreshing\u2026");
            data.Add("resume none here", "No conversations found in this project.");
            data.Add("resume none anywhere", "No conversations found.");
            data.Add("resume rename title", "Rename session:");
            data.Add("resume rename placeholder", "Enter new session name");
            data.Add("resume type to search", "Type to search");
            data.Add("resume show all projects", "show all projects");
            data.Add("resume only this repo", "only show current repo");
            data.Add("resume show all branches", "show all branches");
            data.Add("rewind selector", "Restore the code and/or conversation to the point before\u2026");
            data.Add("rewind files notice", "Rewinding does not affect files edited manually or via bash.");
            data.Add("conversation exported", "Conversation exported to:");
            data.Add("clear description",
               "Start a new session with empty context; previous session stays on disk (resumable with /resume)");
            data.Add("rewind description", "Restore the code and/or conversation to a previous point");
            data.Add("tui description", "Set the terminal UI renderer (default | fullscreen)");
            data.Add("usage description", "Show session cost, plan usage, and activity stats");
            data.Add("subtask description", "Send a subagent off with your full context; its result comes back here");
            data.Add("unknown command", "Unknown command: /");
            data.Add("did you mean", ". Did you mean /");
            data.Add("total duration api", "Total duration (API):  ");
            data.Add("total duration wall", "Total duration (wall): ");
            data.Add("usage by model", "Usage by model:");
            data.Add("tip plan mode", "Use Plan Mode to prepare for a complex request before making changes. Press ");
            data.Add("tip config mode", "Use /config to change your default permission mode (including Plan Mode)");
            data.Add("tip worktrees", "Use git worktrees to run multiple Claude sessions in parallel.");
            data.Add("tip theme", "Use /theme to change the color theme");
            data.Add("tip statusline",
               "Use /statusline to set up a custom status line that will display beneath the input box");
            data.Add("tip permissions", "Use /permissions to pre-approve and pre-deny bash, edit, and MCP tools");
            data.Add("tip double esc",
               "Double-tap esc to rewind the code and/or conversation to a previous point in time");
            data.Add("tip rename", "Name your conversations with /rename to find them easily in /resume later");

            return data;
        }
    }

    /// <summary>Our own copies of the four short tool descriptions.</summary>
    private static class TaskBoardSummaries
    {
        public const string Create = "Create a new task in the task list";
        public const string Get = "Get a task by ID from the task list";
        public const string List = "List all tasks in the task list";
        public const string Update = "Update a task in the task list";
    }

    [ReferenceCliTheory]
    [MemberData(nameof(PortedLiterals))]
    public void Ported_literal_is_still_in_the_reference_binary(string what, string literal)
    {
        Assert.True(ReferenceCorpora.Cli.Contains(literal),
            $"the reference CLI {ReferenceInstall.CliVersion} no longer contains the {what} literal:\n" +
            $"  {literal}\n" +
            "It was reworded or removed upstream — re-find it in the binary and update our copy.");
    }

    [ReferenceCliFact]
    public void The_task_board_docs_are_the_reference_text()
    {
        // The long docs are checked by a distinctive line each, because the
        // reference assembles them from conditional segments.
        foreach (var line in new[]
        {
            "Use this tool to create a structured task list for your current coding session.",
            "- **activeForm** (optional): Present continuous form shown in the spinner when the task is in_progress",
            "Use this tool to retrieve a task by its ID from the task list.",
            "- To see what tasks are available to work on (status: 'pending', no owner, not blocked)",
            "**Mark tasks as resolved:**",
            "- Setting status to `deleted` permanently removes the task",
        })
        {
            Assert.True(ReferenceCorpora.Cli.Contains(line), $"reference doc line missing upstream: {line}");
        }

        // And ours must still carry them, with our own tool names substituted.
        var create = new TaskCreateTool().Description;
        var update = new TaskUpdateTool().Description;
        Assert.Contains("Use this tool to create a structured task list for your current coding session.", create);
        Assert.Contains("- Check TaskList first to avoid creating duplicate tasks", create);
        Assert.Contains("- Setting status to `deleted` permanently removes the task", update);
        Assert.Contains("Make sure to read a task's latest state using `TaskGet` before updating it.", update);
    }
}
