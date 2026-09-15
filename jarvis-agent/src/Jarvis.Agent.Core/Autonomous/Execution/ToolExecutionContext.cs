namespace Jarvis.Agent.Core.Autonomous.Execution;

public sealed record ToolExecutionContext(
    string TaskId,
    string ToolName,
    string Payload,
    DateTime StartedAt);
