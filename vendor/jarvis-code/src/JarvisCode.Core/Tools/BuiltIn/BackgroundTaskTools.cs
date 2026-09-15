using System.Text.Json.Nodes;
using JarvisCode.Core.BackgroundTasks;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>Reads status and accumulated output of a background shell task.</summary>
public sealed class TaskOutputTool : ITool
{
    public string Name => "TaskOutput";

    public string Description =>
        "Returns the status and collected output of a background task started with shell(run_in_background=true). " +
        "Call without task_id to list all background tasks.";

    /// <summary>The reference's default wait when `timeout` is not given.</summary>
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>The reference's ceiling on one wait.</summary>
    public const int MaxTimeoutMs = 600_000;

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("task_id", SchemaBuilder.String("The task ID to get output from")),
            ("block", SchemaBuilder.Boolean("Whether to wait for completion (default: true)")),
            ("timeout", SchemaBuilder.Integer("Max wait time in ms (default: 30000)")),
        ],
        "task_id", "block", "timeout");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"TaskOutput({JsonArgs.GetString(arguments, "task_id") ?? "all"})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.BackgroundTasks is null)
            return ToolResult.Error("Background tasks are not available in this context.");

        var taskId = JsonArgs.GetString(arguments, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            var tasks = context.BackgroundTasks.List();
            if (tasks.Count == 0)
                return ToolResult.Success("No background tasks.");
            var lines = tasks.Select(t =>
                $"{t.Id}  {StatusLabel(t)}  {(t.Command.Length > 60 ? t.Command[..60] + "…" : t.Command)}");
            return ToolResult.Success(string.Join('\n', lines));
        }

        var info = context.BackgroundTasks.Get(taskId);
        if (info is null)
            return ToolResult.Error($"No background task with id '{taskId}'.");

        // The reference's block/timeout: wait for the task to finish, up to the
        // timeout, then report whatever it has. `block` defaults to true.
        bool block = arguments["block"] is null || JsonArgs.GetBool(arguments, "block");
        int timeoutMs = Math.Clamp(JsonArgs.GetInt(arguments, "timeout") ?? DefaultTimeoutMs, 0, MaxTimeoutMs);
        if (block && info.Status == BackgroundTaskStatus.Running)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (info is not null && info.Status == BackgroundTaskStatus.Running && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, timeoutMs)), cancellationToken).ConfigureAwait(false);
                info = context.BackgroundTasks.Get(taskId);
            }

            if (info is null)
                return ToolResult.Error($"No background task with id '{taskId}'.");
        }

        var output = info.Output.Length == 0 ? "(no output yet)" : info.Output.TrimEnd();
        var header = $"{info.Id} [{StatusLabel(info)}] {info.Command}";
        if (block && info.Status == BackgroundTaskStatus.Running)
        {
            header += $"\n(still running after {timeoutMs}ms; call again to keep waiting)";
        }

        return ToolResult.Success(context.Truncate($"{header}\n\n{output}", "task output"));
    }

    private static string StatusLabel(BackgroundTaskInfo info) => info.Status switch
    {
        BackgroundTaskStatus.Running => "running",
        BackgroundTaskStatus.Killed => "killed",
        _ => $"completed, exit {info.ExitCode ?? 0}",
    };
}

/// <summary>Terminates a background shell task.</summary>
public sealed class TaskKillTool : ITool
{
    public string Name => "TaskStop";

    public string Description =>
        "Kills a running background task (and its process tree) or stops a running background agent.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("task_id", SchemaBuilder.String("The ID of the background task to stop")),
            ("shell_id", SchemaBuilder.String("Deprecated: use task_id instead")),
        ]);

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments) =>
        $"TaskKill({JsonArgs.GetString(arguments, "task_id") ?? "?"})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        // The reference still reads its deprecated shell_id alias.
        var taskId = JsonArgs.GetString(arguments, "task_id") ?? JsonArgs.GetString(arguments, "shell_id");
        if (string.IsNullOrWhiteSpace(taskId))
            return Task.FromResult(ToolResult.Error("task_id is required."));
        if (taskId.StartsWith("agent-", StringComparison.Ordinal))
        {
            return Task.FromResult(context.Workers?.Kill(taskId) == true
                ? ToolResult.Success($"Stopping {taskId}; its final task notification will report it as stopped.")
                : ToolResult.Error($"No running background agent {taskId}."));
        }

        if (context.BackgroundTasks is null)
            return Task.FromResult(ToolResult.Error("Background tasks are not available in this context."));
        return Task.FromResult(context.BackgroundTasks.Kill(taskId)
            ? ToolResult.Success($"Killed {taskId}.")
            : ToolResult.Error($"No running background task with id '{taskId}'."));
    }
}
