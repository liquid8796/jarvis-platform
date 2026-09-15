namespace Jarvis.Agent.Core.Autonomous;

public sealed class AgentExecutionLoop
{
    private readonly IAgentPlanner _planner;
    private readonly IAgentExecutor _executor;
    private readonly IAgentVerifier _verifier;

    public AgentExecutionLoop(
        IAgentPlanner planner,
        IAgentExecutor executor,
        IAgentVerifier verifier)
    {
        _planner = planner;
        _executor = executor;
        _verifier = verifier;
    }

    public async Task<AgentTask> ExecuteAsync(
        AgentTask task,
        CancellationToken cancellationToken = default)
    {
        task.Status = AgentTaskStatus.Running;

        foreach (var action in _planner.Plan(task))
        {
            var result = await _executor.ExecuteAsync(action, cancellationToken);
            task.Artifacts.Add(new AgentArtifact(
                "execution-result",
                action.Name,
                result.Output));

            if (!result.Success)
            {
                task.Status = AgentTaskStatus.Failed;
                return task;
            }
        }

        task.Status = _verifier.Verify(task)
            ? AgentTaskStatus.Completed
            : AgentTaskStatus.Failed;

        return task;
    }
}
