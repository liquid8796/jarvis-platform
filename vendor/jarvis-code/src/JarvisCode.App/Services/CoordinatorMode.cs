using System.Text;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// Coordinator mode, ported from the reference CLI: the session stops doing
/// the work itself and dispatches background workers instead. The coordinator
/// keeps only the delegation tools; workers keep the full set. While
/// dispatched work runs and the coordinator sits idle, a periodic check-in
/// message lets it react to progress.
/// </summary>
internal static class CoordinatorMode
{
    /// <summary>The tools the coordinator itself keeps.</summary>
    public static readonly string[] CoordinatorTools =
        ["todo_write", "Skill", "Agent", "TaskOutput", "TaskStop", "SendMessage"];

    public const string CheckInTitle = "Coordinator check-in: dispatched work still running";

    /// <summary>How many running workers the check-in lists before folding the rest.</summary>
    public const int CheckInListCap = 10;

    public static bool IsCoordinatorTool(string name)
        => Array.IndexOf(CoordinatorTools, name) >= 0;

    /// <summary>
    /// The system-prompt section describing the split, with the reference's
    /// worker-tools context adapted to our tool names.
    /// </summary>
    public static string BuildPrompt(IEnumerable<string> workerToolNames, IEnumerable<string> mcpToolNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Coordinator mode");
        builder.AppendLine();
        builder.AppendLine(
            "This session runs in coordinator mode: delegate the actual work to workers instead of " +
            "doing it yourself. Spawn workers with Agent (run_in_background: true for anything " +
            "that should not block the conversation), follow a finished worker up with SendMessage, " +
            "stop one with TaskStop, and keep the todo list current with todo_write.");
        builder.AppendLine();
        builder.AppendLine("Workers spawned via the Agent tool have access to these tools:");
        foreach (var name in workerToolNames)
        {
            builder.Append("- ").AppendLine(name);
        }

        var mcp = mcpToolNames.ToList();
        if (mcp.Count > 0)
        {
            builder.AppendLine();
            builder.Append("Workers also have access to MCP tools from connected MCP servers: ")
                .Append(string.Join(", ", mcp)).AppendLine(".");
        }

        builder.AppendLine();
        builder.AppendLine(
            "Workers have access to standard tools, MCP tools from configured MCP servers, and " +
            "project skills via the skill tool. Delegate skill invocations that need worker tools " +
            "to workers by including \"Use the /<name> skill\" in the worker prompt.");
        builder.AppendLine();
        builder.Append(
            "The skill tool loads a skill's full instructions inline (read-only): read skills to " +
            "inform how you reply, triage, and coordinate. Execution happens in workers — hand the " +
            "skill to one when following it needs tools you don't have, or, when the skill's recipe " +
            "is orchestration, spawn workers per that recipe and synthesize their results.");
        return builder.ToString();
    }

    /// <summary>
    /// The idle-time check-in, listing up to <see cref="CheckInListCap"/>
    /// running workers. Returns null when nothing is running.
    /// </summary>
    public static string? BuildCheckIn(IReadOnlyList<WorkerInfo> workers)
    {
        var running = workers.Where(static w => w.Status == WorkerStatus.Running).ToList();
        if (running.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append(CheckInTitle).AppendLine(". If no response is needed, ignore this check-in.");
        builder.AppendLine("Still running:");
        foreach (var worker in running.Take(CheckInListCap))
        {
            builder.Append("- ").Append(worker.Id).Append(" (").Append(worker.AgentType).Append("): ")
                .AppendLine(worker.PromptPreview);
        }

        if (running.Count > CheckInListCap)
        {
            builder.Append("- …and ").Append(running.Count - CheckInListCap).AppendLine(" more");
        }

        return builder.ToString().TrimEnd();
    }
}
