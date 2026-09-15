namespace Jarvis.Agent.Core.Autonomous.Execution;

using Jarvis.Agent.Core.Autonomous;

public sealed class ToolRouter : IToolRouter
{
    private readonly ToolRegistry _registry;
    private readonly Func<string, string, CancellationToken, Task<ToolExecutionResult>> _executor;

    public ToolRouter(
        ToolRegistry registry,
        Func<string, string, CancellationToken, Task<ToolExecutionResult>> executor)
    {
        _registry = registry;
        _executor = executor;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolName,
        string payload,
        CancellationToken cancellationToken)
    {
        var tool = _registry.Find(toolName);

        if (tool is null)
        {
            return new ToolExecutionResult(false, string.Empty, $"Tool '{toolName}' not registered");
        }

        return await _executor(tool.Name, payload, cancellationToken);
    }
}
