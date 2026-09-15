using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// Plan mode's write carve-out, the reference app's plan-file flow: while plan
/// mode is on, Write and Edit exist but accept only the session's
/// plan file; once the user approves ExitPlanMode (the gate leaves Plan mid-
/// turn) they behave normally again for the rest of the turn.
/// </summary>
public static class PlanModeTools
{
    /// <summary>The session's plan file, kept inside the workspace like the reference.</summary>
    public static string PlanFilePath(string workingDirectory, string sessionId) =>
        Path.Combine(workingDirectory, ".jarvis", "plans", sessionId + ".md");

    public static ITool Scope(ITool inner, UiPermissionGate gate, string planFilePath) =>
        new PlanScopedWriteTool(inner, gate, planFilePath);

    /// <summary>
    /// The model-invoked way into plan mode (the reference EnterPlanMode): flips
    /// the gate to Plan, which the live registry answers by swapping in the plan
    /// tool set before the next model call.
    /// </summary>
    public static ITool CreateEnterTool(UiPermissionGate gate, string planFilePath) =>
        new EnterPlanModeTool(gate, planFilePath);

    private sealed class EnterPlanModeTool(UiPermissionGate gate, string planFilePath) : ITool
    {
        public string Name => "EnterPlanMode";

        public string Description => """
            Use this tool proactively when you're about to start a non-trivial implementation task. Getting user sign-off on your approach before writing code prevents wasted effort and ensures alignment. This tool transitions you into plan mode where you can explore the codebase and design an implementation approach for user approval.

            ## When to Use This Tool

            **Prefer using EnterPlanMode** for implementation tasks unless they're simple. Use it when ANY of these conditions apply:

            1. **New Feature Implementation**: Adding meaningful new functionality
               - Example: "Add a logout button" - where should it go? What should happen on click?
               - Example: "Add form validation" - what rules? What error messages?

            2. **Multiple Valid Approaches**: The task can be solved in several different ways
               - Example: "Add caching to the API" - could use Redis, in-memory, file-based, etc.
               - Example: "Improve performance" - many optimization strategies possible

            3. **Code Modifications**: Changes that affect existing behavior or structure
               - Example: "Update the login flow" - what exactly should change?
               - Example: "Refactor this component" - what's the target architecture?

            4. **Architectural Decisions**: The task requires choosing between patterns or technologies
               - Example: "Add real-time updates" - WebSockets vs SSE vs polling
               - Example: "Implement state management" - Redux vs Context vs custom solution

            5. **Multi-File Changes**: The task will likely touch more than 2-3 files
               - Example: "Refactor the authentication system"
               - Example: "Add a new API endpoint with tests"

            6. **Unclear Requirements**: You need to explore before understanding the full scope
               - Example: "Make the app faster" - need to profile and identify bottlenecks
               - Example: "Fix the bug in checkout" - need to investigate root cause

            7. **User Preferences Matter**: The implementation could reasonably go multiple ways
               - If you would use AskUserQuestion to clarify the approach, use EnterPlanMode instead
               - Plan mode lets you explore first, then present options with context

            ## When NOT to Use This Tool

            Only skip EnterPlanMode for simple tasks:
            - Single-line or few-line fixes (typos, obvious bugs, small tweaks)
            - Adding a single function with clear requirements
            - Tasks where the user has given very specific, detailed instructions
            - Pure research/exploration tasks

            ## Examples

            ### GOOD - Use EnterPlanMode:
            User: "Add user authentication to the app"
            - Requires architectural decisions (session vs JWT, where to store tokens, middleware structure)

            User: "Optimize the database queries"
            - Multiple approaches possible, need to profile first, significant impact

            User: "Implement dark mode"
            - Architectural decision on theme system, affects many components

            User: "Add a delete button to the user profile"
            - Seems simple but involves: where to place it, confirmation dialog, API call, error handling, state updates

            User: "Update the error handling in the API"
            - Affects multiple files, user should approve the approach

            ### BAD - Don't use EnterPlanMode:
            User: "Fix the typo in the README"
            - Straightforward, no planning needed

            User: "Add a console.log to debug this function"
            - Simple, obvious implementation

            User: "What files handle routing?"
            - Research task, not implementation planning

            ## Important Notes

            - This tool REQUIRES user approval - they must consent to entering plan mode
            - If unsure whether to use it, err on the side of planning - it's better to get alignment upfront than to redo work
            - Users appreciate being consulted before significant changes are made to their codebase
            """;

        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

        // "This tool REQUIRES user approval - they must consent to entering plan
        // mode": the reference asks before switching the session, so this one is
        // not read-only and the gate raises its card.
        public bool IsReadOnly => false;

        public string DescribeCall(JsonObject arguments) => "EnterPlanMode()";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (gate.Mode == PermissionMode.Plan)
                return Task.FromResult(ToolResult.Error("The session is already in plan mode."));

            // Entering remembers the mode to come back to, so an approved plan
            // returns the session to it instead of to a fixed one.
            // The gate knows the plan file (PlanFilePath) and asks about every
            // other write itself, so plan mode grants no blanket rule.
            gate.EnterPlanMode([]);
            return Task.FromResult(ToolResult.Success(
                "Entered plan mode. You are now restricted to read-only tools, plus Write/Edit " +
                $"scoped to the plan file at {planFilePath}. Research the task, write your implementation " +
                "plan to that file, then present it with ExitPlanMode for the user's approval. " +
                "You MUST NOT make any edits or run any non-readonly tools until the plan is approved."));
        }
    }

    /// <summary>
    /// A live registry: the plan set while the gate stays in Plan mode, the full
    /// set the moment an approved ExitPlanMode flips it — the reference app's
    /// post-approval context refresh, without restarting the turn.
    /// </summary>
    public sealed class ModeSwitchedRegistry(
        UiPermissionGate gate, IToolRegistry planRegistry, IToolRegistry fullRegistry) : IToolRegistry
    {
        private IToolRegistry Current =>
            gate.Mode == PermissionMode.Plan ? planRegistry : fullRegistry;

        public IReadOnlyList<ITool> All => Current.All;

        public ITool? Find(string name) => Current.Find(name);
    }

    private sealed class PlanScopedWriteTool(ITool inner, UiPermissionGate gate, string planFilePath) : ITool
    {
        public string Name => inner.Name;

        public string Description => inner.Description;

        public JsonObject InputSchema => inner.InputSchema;

        public bool IsReadOnly => inner.IsReadOnly;

        public string DescribeCall(JsonObject arguments) => inner.DescribeCall(arguments);

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (gate.Mode == PermissionMode.Plan)
            {
                var raw = arguments["file_path"]?.GetValue<string>() ?? "";
                string resolved;
                try
                {
                    resolved = context.ResolvePath(raw);
                }
                catch (ArgumentException)
                {
                    return Task.FromResult(ToolResult.Error("file_path is required."));
                }

                if (!string.Equals(resolved, Path.GetFullPath(planFilePath), StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ToolResult.Error(
                        $"In plan mode the only file you may write or edit is the plan file at {planFilePath}. " +
                        "Other than this you are only allowed to take READ-ONLY actions. Present the plan with " +
                        "ExitPlanMode to leave plan mode before changing anything else."));
                }
            }

            return inner.ExecuteAsync(arguments, context, cancellationToken);
        }
    }
}
