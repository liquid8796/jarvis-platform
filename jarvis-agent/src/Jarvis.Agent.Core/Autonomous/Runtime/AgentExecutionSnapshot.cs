namespace Jarvis.Agent.Core.Autonomous.Runtime;

public sealed record AgentExecutionSnapshot(
    string SessionId,
    string TaskId,
    string Status,
    DateTime UpdatedAt);
