namespace Jarvis.Agent.Core.Autonomous.Execution;

public sealed class AgentExecutor
{
    private readonly IToolRouter _toolRouter;

    public AgentExecutor(IToolRouter toolRouter)
    {
        _toolRouter = toolRouter;
    }

    public Task<ToolExecutionResult> ExecuteAsync(
        string tool,
        string input,
        CancellationToken cancellationToken = default)
    {
        return _toolRouter.ExecuteAsync(tool, input, cancellationToken);
    }
}
