namespace Jarvis.Agent.Core.Autonomous.Execution;

using Jarvis.Agent.Core.Autonomous;

public sealed class AgentToolExecutor : IAgentExecutor
{
    private readonly IToolRouter _toolRouter;

    public AgentToolExecutor(IToolRouter toolRouter)
    {
        _toolRouter = toolRouter;
    }

    public async Task<AgentActionResult> ExecuteAsync(
        AgentAction action,
        CancellationToken cancellationToken)
    {
        var result = await _toolRouter.ExecuteAsync(
            action.Name,
            action.Payload,
            cancellationToken);

        return new AgentActionResult(
            result.Success,
            result.Output);
    }
}
