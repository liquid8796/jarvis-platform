namespace Jarvis.Agent.Core.Autonomous.Runtime;

public sealed class AgentLifecycleCoordinator
{
    public AgentExecutionSnapshot Create(string taskId, string sessionId)
    {
        return new AgentExecutionSnapshot(
            sessionId,
            taskId,
            "CREATED",
            DateTime.UtcNow);
    }

    public AgentExecutionSnapshot Complete(AgentExecutionSnapshot snapshot)
    {
        return snapshot with
        {
            Status = "COMPLETED",
            UpdatedAt = DateTime.UtcNow
        };
    }
}
