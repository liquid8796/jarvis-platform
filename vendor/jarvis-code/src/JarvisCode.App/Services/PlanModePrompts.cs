using JarvisCode.Core.Agent;

namespace JarvisCode.App.Services;

/// <summary>
/// The plan-mode reminder, which in the reference is a five-phase workflow
/// rather than a paragraph: explore with parallel agents, design, review, write
/// the plan file, call ExitPlanMode. It rides every message while plan mode is
/// on, in one of three forms — the full workflow, a short reminder once the
/// full one is already in the conversation, and the subagent form, which has no
/// workflow because a subagent does not run one.
/// </summary>
public static class PlanModePrompts
{
    /// <summary>The reference's CLAUDE_CODE_PLAN_V2_EXPLORE_AGENT_COUNT, default 3.</summary>
    public const int DefaultExploreAgentCount = 3;

    /// <summary>The reference's CLAUDE_CODE_PLAN_V2_AGENT_COUNT; 1 outside a team or enterprise plan.</summary>
    public const int DefaultPlanAgentCount = 1;

    public const string ExploreAgentCountVariable = "CLAUDE_CODE_PLAN_V2_EXPLORE_AGENT_COUNT";
    public const string PlanAgentCountVariable = "CLAUDE_CODE_PLAN_V2_AGENT_COUNT";

    /// <summary>The opening sentence, which every form shares but the full one alone excepts the plan file.</summary>
    private const string HeadWithPlanFileException =
        "Plan mode is active. The user indicated that they do not want you to execute yet -- you MUST NOT make " +
        "any edits (with the exception of the plan file mentioned below), run any non-readonly tools (including " +
        "changing configs or making commits), or otherwise make any changes to the system. This supercedes any " +
        "other instructions you have received.";

    private const string HeadForSubagent =
        "Plan mode is active. The user indicated that they do not want you to execute yet -- you MUST NOT make " +
        "any edits, run any non-readonly tools (including changing configs or making commits), or otherwise make " +
        "any changes to the system. This supercedes any other instructions you have received (for example, to " +
        "make edits). Instead, you should:";

    private const string IncrementalLine =
        "You should build your plan incrementally by writing to or editing this file. NOTE that this is the only " +
        "file you are allowed to edit - other than this you are only allowed to take READ-ONLY actions.";

    private const string Phase3 = """
        ### Phase 3: Review
        Goal: Review the plan(s) from Phase 2 and ensure alignment with the user's intentions.
        1. Read the critical files you identified during exploration to deepen your understanding
        2. Ensure that the plans align with the user's original request
        3. Use AskUserQuestion to clarify any remaining questions with the user
        """;

    private const string Phase4 = """
        ### Phase 4: Final Plan
        Goal: Write your final plan to the plan file (the only file you can edit).
        - Begin with a **Context** section: explain why this change is being made — the problem or need it addresses, what prompted it, and the intended outcome
        - Include only your recommended approach, not all alternatives
        - Ensure that the plan file is concise enough to scan quickly, but detailed enough to execute effectively
        - Name the critical files to be modified. For changes that repeat a pattern across many files, describe the pattern once and list a few representative paths — do not enumerate every file or line number
        - Reference existing functions and utilities you found that should be reused, with their file paths
        - Include a verification section describing how to test the changes end-to-end (run the code, use MCP tools, run tests)
        """;

    /// <summary>The end-turn rule: a planning turn ends in a question or in ExitPlanMode.</summary>
    private const string EndTurnRule = """
        At the very end of your turn, once you have asked the user questions and are happy with your final plan file - you should always call ExitPlanMode to indicate to the user that you are done planning.
        This is critical - your turn should only end with either using the AskUserQuestion tool OR calling ExitPlanMode. Do not stop unless it's for these 2 reasons

        **Important:** Use AskUserQuestion ONLY to clarify requirements or choose between approaches. Use ExitPlanMode to request plan approval. Do NOT ask about plan approval in any other way - no text questions, no AskUserQuestion. Phrases like "Is this plan okay?", "Should I proceed?", "How does this plan look?", "Any changes before we start?", or similar MUST use ExitPlanMode.
        """;

    private const string EndTurnRuleSparse =
        "End turns with AskUserQuestion (for clarifications) or ExitPlanMode (for plan approval). " +
        "Never ask about plan approval via text or AskUserQuestion.";

    private const string ClosingNote =
        "NOTE: At any point in time through this workflow you should feel free to ask the user questions or " +
        "clarifications using the AskUserQuestion tool. Don't make large assumptions about user intent. The goal " +
        "is to present a well researched plan to the user, and tie any loose ends before implementation begins.";

    /// <summary>Reads one of the reference's two agent-count overrides.</summary>
    private static int AgentCount(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out int configured) &&
        configured > 0 && configured <= 10
            ? configured
            : fallback;

    /// <summary>The plan-file line, which differs by whether the file is already there.</summary>
    private static string PlanFileInfo(string planFilePath, bool planExists, bool forSubagent)
    {
        var tail = forSubagent ? " if you need to." : ".";
        return planExists
            ? $"A plan file already exists at {planFilePath}. You can read it and make incremental edits using " +
              "the Edit tool" + tail
            : $"No plan file exists yet. You should create your plan at {planFilePath} using the Write tool" + tail;
    }

    private static string Phase1(bool withAgents)
    {
        if (!withAgents)
        {
            return """
                ### Phase 1: Initial Understanding
                Goal: Gain a comprehensive understanding of the user's request by reading through code and asking them questions.

                1. Focus on understanding the user's request and the code associated with their request. Actively search for existing functions, utilities, and patterns that can be reused — avoid proposing new code when suitable implementations already exist.

                2. Read and explore the relevant files directly to efficiently understand the codebase.
                """;
        }

        int count = AgentCount(ExploreAgentCountVariable, DefaultExploreAgentCount);
        var agent = SubagentTool.ExploreAgentType;
        return $"""
            ### Phase 1: Initial Understanding
            Goal: Gain a comprehensive understanding of the user's request by reading through code and asking them questions. Critical: In this phase you should only use the {agent} subagent type.

            1. Focus on understanding the user's request and the code associated with their request. Actively search for existing functions, utilities, and patterns that can be reused — avoid proposing new code when suitable implementations already exist.

            2. **Launch up to {count} {agent} agents IN PARALLEL** (single message, multiple tool calls) to efficiently explore the codebase.
               - Use 1 agent when the task is isolated to known files, the user provided specific file paths, or you're making a small targeted change.
               - Use multiple agents when: the scope is uncertain, multiple areas of the codebase are involved, or you need to understand existing patterns before planning.
               - Quality over quantity - {count} agents maximum, but you should try to use the minimum number of agents necessary (usually just 1)
               - If using multiple agents: Provide each agent with a specific search focus or area to explore. Example: One agent searches for existing implementations, another explores related components, a third investigating testing patterns
            """;
    }

    private static string Phase2(bool withAgents)
    {
        if (!withAgents)
        {
            return """
                ### Phase 2: Design
                Goal: Design an implementation approach based on the user's intent and your exploration results from Phase 1.

                - Provide comprehensive background context from Phase 1 exploration including filenames and code path traces
                - Describe requirements and constraints
                - Produce a detailed implementation plan
                """;
        }

        int count = AgentCount(PlanAgentCountVariable, DefaultPlanAgentCount);
        var agent = SubagentTool.PlanAgentType;
        var multiple = count > 1
            ? $"""
                - **Multiple agents**: Use up to {count} agents for complex tasks that benefit from different perspectives

                Examples of when to use multiple agents:
                - The task touches multiple parts of the codebase
                - It's a large refactor or architectural change
                - There are many edge cases to consider
                - You'd benefit from exploring different approaches

                Example perspectives by task type:
                - New feature: simplicity vs performance vs maintainability
                - Bug fix: root cause vs workaround vs prevention
                - Refactoring: minimal change vs clean architecture

                """
            : "";

        return $"""
            ### Phase 2: Design
            Goal: Design an implementation approach.

            Launch {agent} agent(s) to design the implementation based on the user's intent and your exploration results from Phase 1.

            You can launch up to {count} agent(s) in parallel.

            **Guidelines:**
            - **Default**: Launch at least 1 Plan agent for most tasks - it helps validate your understanding and consider alternatives
            - **Skip agents**: Only for truly trivial tasks (typo fixes, single-line changes, simple renames)
            {multiple}
            In the agent prompt:
            - Provide comprehensive background context from Phase 1 exploration including filenames and code path traces
            - Describe requirements and constraints
            - Request a detailed implementation plan
            """;
    }

    /// <summary>
    /// The full workflow, sent while plan mode is on. <paramref name="withAgents"/>
    /// is the reference's own gate: the phases only tell the model to fan out
    /// when the session actually has the Agent tool.
    /// </summary>
    public static string Full(
        string planFilePath, bool planExists, bool withAgents, string? customInstructions = null)
    {
        var head =
            HeadWithPlanFileException + "\n\n" +
            "## Plan File Info:\n" +
            PlanFileInfo(planFilePath, planExists, forSubagent: false) + "\n" +
            IncrementalLine + "\n\n" +
            "## Plan Workflow\n\n";

        // A session that was given its own planning instructions runs those in
        // place of the five phases, and still ends by calling ExitPlanMode.
        if (customInstructions is { Length: > 0 } instructions)
        {
            return head + instructions + "\n\n### Call ExitPlanMode\n" + EndTurnRule;
        }

        return head +
            Phase1(withAgents) + "\n\n" +
            Phase2(withAgents) + "\n\n" +
            Phase3 + "\n\n" +
            Phase4 + "\n\n" +
            "### Phase 5: Call ExitPlanMode\n" +
            EndTurnRule + "\n\n" +
            ClosingNote;
    }

    /// <summary>
    /// The short form for later messages of the same session: the full workflow
    /// is already in the conversation, so this only keeps the rules in view.
    /// </summary>
    public static string Sparse(string planFilePath, bool customInstructions = false) =>
        "Plan mode still active (see full instructions earlier in conversation). Read-only except plan file " +
        $"({planFilePath}). " +
        (customInstructions ? "Follow the plan workflow described earlier." : "Follow 5-phase workflow.") +
        " " + EndTurnRuleSparse;

    /// <summary>
    /// The subagent form: a subagent inherits the restriction but not the
    /// workflow, since it is not the one presenting a plan.
    /// </summary>
    public static string ForSubagent(string planFilePath, bool planExists) =>
        HeadForSubagent + "\n\n" +
        "## Plan File Info:\n" +
        PlanFileInfo(planFilePath, planExists, forSubagent: true) + "\n" +
        IncrementalLine + "\n" +
        "Answer the user's query comprehensively, using the AskUserQuestion tool if you need to ask the user " +
        "clarifying questions. If you do use the AskUserQuestion, make sure to ask all clarifying questions you " +
        "need to fully understand the user's intent before proceeding.";
}
