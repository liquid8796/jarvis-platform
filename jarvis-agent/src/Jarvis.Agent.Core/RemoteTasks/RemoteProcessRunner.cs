using System.Text;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

internal sealed record RemoteStepResult(bool Success, string Output, bool Truncated = false, int? ExitCode = null,
    string? Error = null, bool KnownCompletion = false, bool RetainedOutputTruncated = false);

/// <summary>Waits for owned process completion; a jobId alone is never a successful build.</summary>
internal static class RemoteProcessRunner
{
    public static async Task<RemoteStepResult> RunAsync(RemoteTaskStep step, AgentExecutionContext context,
        Func<string, JsonElement, AgentExecutionContext, CancellationToken, Task<ToolReply>> invoke,
        Func<string, AgentExecutionContext, Task> cancelOwnedJob, CancellationToken ct,
        Func<RemoteTaskEvent, Task>? report = null, string? outputPath = null)
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
        FileStream? retained = null;
        var retainedBytes = 0;
        var retainedTruncated = false;
        try
        {
            if (outputPath is not null) retained = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            if (report is not null) await report(new() { Type = "process.started", StepId = step.Id, ToolId = step.ToolId, JobId = jobId });
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var reply = await invoke("process.read", WireJson.Element(new { jobId, cursor }), context, ct);
                if (reply.IsError) throw new InvalidOperationException(reply.Text);
                using var json = JsonDocument.Parse(reply.Text);
                var root = json.RootElement;
                var text = root.GetProperty("output").GetString() ?? "";
                if (text.Length > 0)
                {
                    if (retained is not null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        var count = Math.Min(bytes.Length, Math.Max(0, 8 * 1024 * 1024 - retainedBytes));
                        if (count < bytes.Length)
                            while (count > 0 && (bytes[count] & 0xc0) == 0x80) count--;
                        if (count > 0) { await retained.WriteAsync(bytes.AsMemory(0, count), ct); retainedBytes += count; }
                        if (count < bytes.Length) { truncated = true; retainedTruncated = true; }
                    }
                    if (report is not null) await report(new() { Type = "process.output", StepId = step.Id, ToolId = step.ToolId,
                        JobId = jobId, Stream = "combined", Text = text, Cursor = root.GetProperty("cursor").GetInt64() });
                }
                output.Append(text);
                if (output.Length > RemoteTaskRules.OutputLimit)
                { output.Remove(0, output.Length - RemoteTaskRules.OutputLimit); truncated = true; }
                truncated |= root.GetProperty("truncated").GetBoolean();
                retainedTruncated |= root.GetProperty("truncated").GetBoolean();
                cursor = root.GetProperty("cursor").GetInt64();
                if (root.GetProperty("done").GetBoolean())
                {
                    if (root.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number) exitCode = code.GetInt32();
                    // A snapshot returns at most 32K characters: drain the final pages before finishing.
                    if (text.Length == 0) { completed = true; break; }
                }
                else await Task.Delay(100, ct);
            }
            if (retained is not null) await retained.FlushAsync(ct);
            if (report is not null) await report(new() { Type = "process.exited", StepId = step.Id, ToolId = step.ToolId,
                JobId = jobId, ExitCode = exitCode });
            return new(exitCode == 0, output.ToString(), truncated, exitCode,
                exitCode == 0 ? null : "Process failed or completion is unknown (exit code " + exitCode + ").",
                KnownCompletion: exitCode is not null, RetainedOutputTruncated: retainedTruncated);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            return new(false, output.ToString(), truncated, exitCode, ex is OperationCanceledException
                ? "Process cancelled or deadline exceeded; partial output retained."
                : "Process observation failed: " + ex.GetType().Name, RetainedOutputTruncated: true);
        }
        finally
        {
            // This cleanup can only cancel the job ID returned by this invocation, even after pause.
            try { if (!completed) await cancelOwnedJob(jobId, context); }
            finally { retained?.Dispose(); }
        }
    }
}
