using System.Text;

namespace JarvisCode.Core.Agent;

/// <summary>Builds the agent system prompt with the current environment baked in.</summary>
public static class SystemPromptBuilder
{
    public static string Build(
        string workingDirectory,
        ProjectInstructions.InstructionSet? projectInstructions = null,
        bool planMode = false,
        IReadOnlyList<Customization.SkillDefinition>? skills = null,
        string? projectMemory = null,
        IReadOnlyList<string>? additionalDirectories = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "You are Jarvis Code, an interactive agentic coding assistant running on Windows. " +
            "You help the user with software engineering tasks: writing and modifying code, debugging, running commands, " +
            "and answering questions about their codebase.");
        builder.AppendLine();
        builder.AppendLine("# Tone and style");
        builder.AppendLine(
            "Be concise and direct; answers are rendered as markdown. Prefer short answers for simple questions. " +
            "When you make changes, briefly state what you changed and why. Do not add code comments that merely " +
            "narrate the change. Never invent APIs: read the relevant files before editing them.");
        builder.AppendLine();
        builder.AppendLine("# Doing tasks");
        builder.AppendLine(
            "Use the available tools to explore before you act: read files before editing, search with glob/grep " +
            "instead of guessing paths. Make focused changes; do not rewrite unrelated code. After changing code, " +
            "verify it when possible (build, run tests) using the PowerShell tool. If a tool call fails, read the error " +
            "and adjust rather than repeating the same call. If the user denies a tool call, ask how to proceed. " +
            "Ground answers about how something is implemented in the actual source files — not in configuration, " +
            "docker-compose, or CI files that merely mention it; in large repositories search broadly before " +
            "concluding that something does not exist.");
        builder.AppendLine();
        builder.AppendLine("# Environment");
        builder.AppendLine($"Working directory: {workingDirectory}");
        if (additionalDirectories is { Count: > 0 })
        {
            builder.AppendLine(
                "Additional working directories (extra sources attached by the user — treat them as part of the " +
                "task context; reach them with absolute paths, since relative paths and the shell resolve against " +
                "the main working directory):");
            foreach (var directory in additionalDirectories)
                builder.AppendLine($"- {directory}");
        }
        builder.AppendLine($"Operating system: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        builder.AppendLine($"Shell for the PowerShell tool: {(OperatingSystem.IsWindows() ? "Windows PowerShell 5.1 (powershell.exe). Use PowerShell syntax, not bash." : "bash")}");
        builder.AppendLine($"Today's date: {DateTime.Now:yyyy-MM-dd}");
        if (planMode)
        {
            builder.AppendLine();
            builder.AppendLine("# Plan mode");
            builder.AppendLine(
                "Plan mode is active: you only have read-only tools. Research the codebase as needed, then present " +
                "a concrete, step-by-step implementation plan (files to change, order of work, risks, how to verify) " +
                "and stop. Do not attempt to modify anything until the user turns plan mode off and approves the plan.");
        }
        if (projectMemory is not null)
        {
            builder.AppendLine();
            builder.AppendLine("# Project memory (persists across sessions)");
            builder.AppendLine(
                "Your memory index for this project, maintained by you through the memory tool. " +
                "Verify facts that may have gone stale before relying on them; record new durable " +
                "facts and correct wrong ones as you work.");
            builder.AppendLine();
            builder.AppendLine(projectMemory);
        }
        if (skills is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("# Available skills");
            builder.AppendLine(
                "Load a skill with the skill tool BEFORE starting a task it covers, then follow its instructions:");
            foreach (var skill in skills)
                builder.AppendLine($"- {skill.Name}: {skill.Description}");
        }
        if (projectInstructions is { IsEmpty: false })
        {
            builder.AppendLine();
            builder.AppendLine("# Project instructions");
            builder.AppendLine(
                "The user maintains these project-specific instructions; follow them when working in this repository.");
            foreach (var file in projectInstructions.Files)
            {
                builder.AppendLine();
                builder.AppendLine($"## From {file.FilePath}");
                builder.AppendLine(file.Content);
            }
        }
        return builder.ToString();
    }

    /// <summary>System prompt for a spawned subagent (explore or general).</summary>
    public static string BuildForSubagent(
        string workingDirectory, string agentType,
        ProjectInstructions.InstructionSet? projectInstructions = null,
        IReadOnlyList<string>? additionalDirectories = null,
        IReadOnlyList<Customization.SkillDefinition>? skills = null)
    {
        var builder = new StringBuilder(
            Build(workingDirectory, projectInstructions, skills: skills, additionalDirectories: additionalDirectories));
        builder.AppendLine();
        builder.AppendLine("# Subagent rules");
        builder.AppendLine(
            "You are a subagent launched by the main agent. The task prompt you received is your entire " +
            "assignment; you cannot ask questions or receive follow-ups. Work autonomously, then reply with one " +
            "final, self-contained report — it is returned to the calling agent as a tool result, so include " +
            "every relevant finding, file path (with line numbers where useful) and conclusion. Do not address the user.");
        if (agentType == SubagentTool.ExploreAgentType)
        {
            builder.AppendLine(
                "You are an explore agent with read-only tools: search and read, never attempt to modify anything.");
        }
        return builder.ToString();
    }
}
