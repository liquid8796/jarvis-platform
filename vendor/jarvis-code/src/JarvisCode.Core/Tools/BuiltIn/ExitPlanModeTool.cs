using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// The user's verdict on a presented plan. <paramref name="EditedPlan"/> is the
/// reference's planWasEdited path: the user may change the plan in the approval
/// card, and what they approved is what goes back to the model.
/// </summary>
public sealed record PlanApprovalDecision(
    bool Approved, string? Feedback = null, string? EditedPlan = null);

/// <summary>
/// The reference app's ExitPlanMode tool: reads the plan the model wrote to the
/// session's plan file and asks the user to approve leaving plan mode. The host
/// renders the approval through <see cref="ToolExecutionContext.PlanApprovalAsync"/>
/// and owns switching the permission mode when the user says yes.
/// </summary>
public sealed class ExitPlanModeTool : ITool
{
    public string Name => "ExitPlanMode";

    public string Description => """
        Use this tool when you are in plan mode and have finished writing your plan to the plan file and are ready for user approval.

        ## How This Tool Works
        - You should have already written your plan to the plan file specified in the plan mode system message
        - This tool does NOT take the plan content as a parameter - it will read the plan from the file you wrote
        - This tool simply signals that you're done planning and ready for the user to review and approve
        - The user will see the contents of your plan file when they review it

        ## When to Use This Tool
        IMPORTANT: Only use this tool when the task requires planning the implementation steps of a task that requires writing code. For research tasks where you're gathering information, searching files, reading files or in general trying to understand the codebase - do NOT use this tool.

        ## Before Using This Tool
        Ensure your plan is complete and unambiguous:
        - If you have unresolved questions about requirements or approach, use AskUserQuestion first (in earlier phases)
        - Once your plan is finalized, use THIS tool to request approval

        **Important:** Do NOT use AskUserQuestion to ask "Is this plan okay?" or "Should I proceed?" - that's exactly what THIS tool does. ExitPlanMode inherently requests user approval of your plan.

        ## Examples

        1. Initial task: "Search for and understand the implementation of vim mode in the codebase" - Do not use the exit plan mode tool because you are not planning the implementation steps of a task.
        2. Initial task: "Help me implement yank mode for vim" - Use the exit plan mode tool after you have finished planning the implementation steps of the task.
        3. Initial task: "Add a new feature to handle user authentication" - If unsure about auth method (OAuth, JWT, etc.), use AskUserQuestion first, then use exit plan mode tool after clarifying the approach.
        """;

    public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    // Read-only in the tool sense: it mutates nothing itself, so it survives the
    // plan-mode registry filter and runs without a permission prompt — the
    // approval card IS the permission.
    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) => "ExitPlanMode()";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.PlanFilePath is null || context.PlanApprovalAsync is null)
            return ToolResult.Error(
                "You are not in plan mode, so there is no plan to approve. Continue with the task.");

        string plan;
        try
        {
            plan = File.Exists(context.PlanFilePath) ? File.ReadAllText(context.PlanFilePath).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Could not read the plan file: {ex.Message}");
        }

        // A teammate does not ask the user: it raises the plan with the team
        // lead and keeps working, the way the reference's awaitingLeaderApproval
        // teammate does. The lead answers with a plan_approval_response.
        if (context.Team is { IsLeader: false } team &&
            context.PlanApprovals is { } approvals &&
            context.ReportToLeadAsync is { } reportToLead)
        {
            var requestId = approvals.Raise(team.AgentName, plan);
            var request = new JsonObject
            {
                ["type"] = "plan_approval_request",
                ["request_id"] = requestId,
                ["from"] = team.AgentName,
                ["plan"] = plan,
            };
            await reportToLead(team.AgentName, request.ToJsonString(), "plan approval request");
            context.TeamStore?.SetAwaitingApproval(team.TeamName, team.AgentName, awaiting: true);
            return ToolResult.Success(
                Agent.TeamProtocolMessages.PlanAwaitingLeader(requestId, context.PlanFilePath));
        }

        var decision = await context.PlanApprovalAsync(plan, cancellationToken);
        if (!decision.Approved)
        {
            var feedback = string.IsNullOrWhiteSpace(decision.Feedback) ? "" : $" Feedback: {decision.Feedback}";
            return ToolResult.Error(
                "The user rejected the plan and wants to stay in plan mode." + feedback +
                " Refine the plan in the plan file based on the feedback, then call ExitPlanMode again.");
        }

        // A subagent is told the plan was approved and that the implementing is
        // not its job — the session that spawned it does that.
        if (context.IsSubagent)
        {
            return ToolResult.Success(
                "User has approved the plan. There is nothing else needed from you now. " +
                "Please respond with \"ok\"");
        }

        var approved = decision.EditedPlan?.Trim() is { Length: > 0 } edited ? edited : plan;
        if (approved.Length == 0)
        {
            return ToolResult.Success("User has approved exiting plan mode. You can now proceed.");
        }

        if (decision.EditedPlan is not null && approved != plan)
        {
            // What the user approved is what the plan file should hold.
            try
            {
                File.WriteAllText(context.PlanFilePath, approved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The answer below still carries the approved plan verbatim.
            }
        }

        // The suffix only makes sense where the Agent tool can actually spawn one.
        var teammates = context.Subagents is not null
            ? "\n\nIf this plan can be broken down into multiple independent tasks, consider " +
              "spawning named teammates with the Agent tool (pass a `name`) to parallelize the work."
            : "";

        return ToolResult.Success(
            "User has approved your plan. You can now start coding. Start with updating your todo list " +
            "if applicable\n\nYour plan has been saved to: " + context.PlanFilePath + "\n" +
            "You can refer back to it if needed during implementation." + teammates +
            "\n\n## Approved Plan" + (approved != plan ? " (edited by user)" : "") + ":\n" + approved);
    }
}
