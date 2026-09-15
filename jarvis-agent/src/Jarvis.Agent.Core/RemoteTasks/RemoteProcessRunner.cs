using System.Text;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

internal sealed record RemoteStepResult(bool Success, string Output, bool Truncated = false, int? ExitCode = null,
    string? Error = null);

/// <summary>Waits for owned process completion; a jobId alone is never a successful build.</summary>
internal static class RemoteProcessRunner
{
    public static async Task<RemoteStepResult> RunAsync(RemoteTaskStep step, AgentExecutionContext context,
        Func<string, JsonElement, AgentExecutionContext, CancellationToken, Task<ToolReply>> invoke,
        Func<string, AgentExecutionContext, Task> cancelOwnedJob, CancellationToken ct)
    {
        var start = await invoke(step.ToolId, step.Arguments, context, ct);
        if (start.IsError) return new(false, start.Text, Error: start.Text);
        using var started = JsonDocument.Parse(start.Text);
        var jobId = started.RootElement.GetProperty("jobId").GetString()
            ?? throw new InvalidDataException("Process did not return a job ID.");
        var output = new StringBuilder();
        var truncated = false;
        var completed = false;
        long cursor = 0;
        int? exitCode = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var reply = await invoke("process.read", WireJson.Element(new { jobId, cursor }), context, ct);
                if (reply.IsError) throw new InvalidOperationException(reply.Text);
                using var json = JsonDocument.Parse(reply.Text);
                var root = json.RootElement;
                var text = root.GetProperty("output").GetString() ?? "";
                output.Append(text);
                if (output.Length > RemoteTaskRules.OutputLimit)
                { output.Remove(0, output.Length - RemoteTaskRules.OutputLimit); truncated = true; }
                truncated |= root.GetProperty("truncated").GetBoolean();
                cursor = root.GetProperty("cursor").GetInt64();
                if (root.GetProperty("done").GetBoolean())
                {
                    if (root.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number) exitCode = code.GetInt32();
                    // A snapshot returns at most 32K characters: drain the final pages before finishing.
                    if (text.Length == 0) { completed = true; break; }
                }
                else await Task.Delay(100, ct);
            }
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
            // This cleanup can only cancel the job ID returned by this invocation, even after pause.
            if (!completed) await cancelOwnedJob(jobId, context);
        }
    }
}
