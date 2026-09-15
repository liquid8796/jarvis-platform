using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JarvisCode.Core.BackgroundTasks;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Waits on a background task instead of polling it: returns as soon as the
/// task's output matches a pattern or the task exits, whichever comes first.
/// A timeout is a normal (non-error) result so the model can decide to keep
/// waiting or move on.
/// </summary>
public sealed class MonitorTool : ITool
{
    private const int PollMilliseconds = 500;
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds = 600;
    private const int TailChars = 2_000;

    public string Name => "Monitor";

    public string Description =>
        "Arms a background monitor and returns its taskId immediately. Provide exactly one of command or ws. " +
        "Each stdout line or WebSocket text frame is delivered as an event; process exit or socket close ends " +
        "the watch. timeout_ms defaults to 300000 and is capped at 3600000; persistent runs until TaskStop " +
        "or session end. Use TaskStop to stop a monitor. Do not poll its output in a loop.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("command", SchemaBuilder.String("Shell command or script. Each stdout line is an event; exit ends the watch.")),
            ("ws", SchemaBuilder.Object([
                ("url", SchemaBuilder.String("ASCII ws:// or wss:// URL without userinfo or whitespace.")),
                ("protocols", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }),
            ], "url")),
            ("description", SchemaBuilder.String("Short human-readable description of what you are monitoring (shown in notifications).")),
            ("timeout_ms", new JsonObject { ["type"] = "number", ["minimum"] = 1000, ["default"] = 300000 }),
            ("persistent", SchemaBuilder.Boolean("Run for the lifetime of the session (no timeout). Stop with TaskStop.")),
        ],
        "description");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
    {
        if (arguments["task_id"] is null)
            return $"Monitor({JsonArgs.GetString(arguments, "command") ?? arguments["ws"]?["url"]?.ToString() ?? "?"})";
        var until = JsonArgs.GetString(arguments, "until");
        return until is null
            ? $"Monitor({JsonArgs.GetString(arguments, "task_id") ?? "?"} until exit)"
            : $"Monitor({JsonArgs.GetString(arguments, "task_id") ?? "?"} until /{until}/)";
    }

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        // Preserve execution of old stored calls without advertising the retired
        // blocking-wait shape alongside the reference's command/socket contract.
        if (arguments["task_id"] is null)
            return await BackgroundMonitor.ArmAsync(arguments, context, cancellationToken);
        if (context.BackgroundTasks is null)
            return ToolResult.Error("Background tasks are not available in this context.");

        var taskId = JsonArgs.GetString(arguments, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
            return ToolResult.Error("task_id is required.");
        if (taskId.StartsWith("agent-", StringComparison.Ordinal))
            return ToolResult.Error(
                "monitor watches shell background tasks only. A background agent's report arrives as a task " +
                "notification on its own — continue with other work instead of waiting.");

        Regex? until = null;
        if (JsonArgs.GetString(arguments, "until") is { Length: > 0 } pattern)
        {
            try
            {
                until = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                return ToolResult.Error($"until is not a valid regex: {ex.Message}");
            }
        }

        int timeoutSeconds = Math.Clamp(
            JsonArgs.GetInt(arguments, "timeout_seconds") ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var started = DateTime.UtcNow;

        while (true)
        {
            var info = context.BackgroundTasks.Get(taskId);
            if (info is null)
                return ToolResult.Error($"No background task with id '{taskId}'.");

            if (until is not null && SafeMatches(until, info.Output))
            {
                return ToolResult.Success(context.Truncate(
                    $"{taskId}: output matched /{until}/ after {Elapsed(started)}.\n\n{Tail(info.Output)}",
                    "task output"));
            }

            if (info.Status != BackgroundTaskStatus.Running)
            {
                var status = info.Status == BackgroundTaskStatus.Killed
                    ? "was killed"
                    : $"exited with code {info.ExitCode ?? 0}";
                var matchNote = until is not null ? $" (no match for /{until}/)" : "";
                return ToolResult.Success(context.Truncate(
                    $"{taskId} {status} after {Elapsed(started)}{matchNote}.\n\n{Tail(info.Output)}",
                    "task output"));
            }

            if (DateTime.UtcNow >= deadline)
            {
                return ToolResult.Success(context.Truncate(
                    $"{taskId} is still running after {timeoutSeconds}s (timeout reached, not an error). " +
                    $"Call monitor again to keep waiting, or TaskStop to stop it.\n\n{Tail(info.Output)}",
                    "task output"));
            }

            await Task.Delay(PollMilliseconds, cancellationToken);
        }
    }

    private static bool SafeMatches(Regex regex, string output)
    {
        try
        {
            return regex.IsMatch(output);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Elapsed(DateTime started) =>
        $"{(DateTime.UtcNow - started).TotalSeconds:0}s";

    private static string Tail(string output)
    {
        if (output.Length == 0)
            return "(no output yet)";
        var trimmed = output.TrimEnd();
        return trimmed.Length <= TailChars ? trimmed : "…" + trimmed[^TailChars..];
    }
}
