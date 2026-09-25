using System.Text;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

internal sealed record RemoteStepResult(bool Success, string Output, bool Truncated = false, int? ExitCode = null,
    string? Error = null);

/// <summary>Waits for an owned exec_command session; a session_id alone is never a successful build.</summary>
internal static class RemoteProcessRunner
{
    public static async Task<RemoteStepResult> RunAsync(RemoteTaskStep step, AgentExecutionContext context,
        Func<string, JsonElement, AgentExecutionContext, CancellationToken, Task<ToolReply>> invoke,
        Func<string, AgentExecutionContext, Task> cancelOwnedJob, CancellationToken ct)
    {
        var started = await invoke(step.ToolId, step.Arguments, context, ct);
        if (started.IsError) return new(false, started.Text, Error: started.Text);
        var output = new StringBuilder();
        var truncated = false;
        var completed = false;
        long? sessionId = null;
        int? exitCode = null;
        try
        {
            using var initial = JsonDocument.Parse(started.Text);
            Consume(initial.RootElement, output, ref truncated, ref sessionId, ref exitCode);
            while (sessionId is not null)
            {
                ct.ThrowIfCancellationRequested();
                var reply = await invoke("unified_exec.write_stdin",
                    WireJson.Element(new { session_id = sessionId.Value, chars = "", yield_time_ms = 100, max_output_tokens = 10000 }), context, ct);
                if (reply.IsError) throw new InvalidOperationException(reply.Text);
                using var json = JsonDocument.Parse(reply.Text);
                sessionId = null;
                Consume(json.RootElement, output, ref truncated, ref sessionId, ref exitCode);
            }
            completed = true;
            return new(exitCode == 0, output.ToString(), truncated, exitCode,
                exitCode == 0 ? null : "Process failed or completion is unknown (exit code " + exitCode + ").");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            return new(false, output.ToString(), truncated, exitCode, ex is OperationCanceledException
                ? "Process cancelled or deadline exceeded; partial output retained."
                : "Process observation failed: " + ex.GetType().Name);
        }
        finally
        {
            if (!completed && sessionId is not null) await cancelOwnedJob(sessionId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), context);
        }
    }

    private static void Consume(JsonElement root, StringBuilder output, ref bool truncated, ref long? sessionId, ref int? exitCode)
    {
        if (root.TryGetProperty("output", out var outputNode)) output.Append(outputNode.GetString() ?? "");
        if (output.Length > RemoteTaskRules.OutputLimit)
        {
            output.Remove(0, output.Length - RemoteTaskRules.OutputLimit);
            truncated = true;
        }
        truncated |= root.TryGetProperty("original_token_count", out _);
        if (root.TryGetProperty("session_id", out var sessionNode) && sessionNode.ValueKind == JsonValueKind.Number)
            sessionId = sessionNode.GetInt64();
        if (root.TryGetProperty("exit_code", out var exitNode) && exitNode.ValueKind == JsonValueKind.Number)
            exitCode = exitNode.GetInt32();
    }
}
