namespace Jarvis.Agent.Core.Autonomous.Execution;

public sealed record ToolExecutionArtifact(
    string ToolName,
    bool Success,
    string Output,
    DateTimeOffset CreatedAt);
