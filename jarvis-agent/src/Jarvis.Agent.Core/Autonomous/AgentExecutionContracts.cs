namespace Jarvis.Agent.Core.Autonomous;

public enum AgentTaskStatus
{
    Created,
    Running,
    Completed,
    Failed
}

public sealed record AgentTask(string Id, string Goal)
{
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Created;
    public List<AgentArtifact> Artifacts { get; } = [];
}

public sealed record AgentArtifact(string Type, string Name, string? Content = null);

public interface IAgentPlanner
{
    IReadOnlyList<AgentAction> Plan(AgentTask task);
}

public interface IAgentExecutor
{
    Task<AgentActionResult> ExecuteAsync(AgentAction action, CancellationToken cancellationToken);
}

public interface IAgentVerifier
{
    bool Verify(AgentTask task);
}

public sealed record AgentAction(string Name, string Payload);

public sealed record AgentActionResult(bool Success, string Output);
