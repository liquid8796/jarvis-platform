namespace Jarvis.Agent.Core.Autonomous.Runtime;

using Jarvis.Agent.Core.Autonomous;

public sealed class AutonomousTaskRunner
{
    private readonly AgentExecutionLoop _executionLoop;

    public AutonomousTaskRunner(AgentExecutionLoop executionLoop)
    {
        _executionLoop = executionLoop;
    }

    public async Task<AutonomousTaskResult> RunAsync(
        AgentTask task,
        CancellationToken cancellationToken = default)
    {
        var result = await _executionLoop.ExecuteAsync(task, cancellationToken);

        return new AutonomousTaskResult(
            result.Status == AgentTaskStatus.Completed,
            result.Id,
            result.Artifacts.Select(x => x.Name).ToArray());
    }
}
