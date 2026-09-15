namespace Jarvis.Agent.Core.Autonomous.Execution;

public interface IToolRouter
{
    Task<ToolExecutionResult> ExecuteAsync(
        string toolName,
        string payload,
        CancellationToken cancellationToken);
}

public sealed record ToolExecutionRequest(
    string ToolName,
    string Payload);

public sealed record ToolExecutionResult(
    bool Success,
    string Output,
    string? Error = null);
