namespace Jarvis.Agent.Core.Autonomous.Execution;

public sealed class ToolRegistryRouter : IToolRouter
{
    private readonly ToolRegistry _registry;
    private readonly IReadOnlyDictionary<string, Func<string, CancellationToken, Task<ToolExecutionResult>>> _handlers;

    public ToolRegistryRouter(
        ToolRegistry registry,
        IReadOnlyDictionary<string, Func<string, CancellationToken, Task<ToolExecutionResult>>> handlers)
    {
        _registry = registry;
        _handlers = handlers;
    }

    public Task<ToolExecutionResult> ExecuteAsync(
        string toolName,
        string payload,
        CancellationToken cancellationToken)
    {
        if (_registry.Find(toolName) is null)
        {
            return Task.FromResult(new ToolExecutionResult(false, string.Empty, "Tool is not registered"));
        }

        return _handlers.TryGetValue(toolName, out var handler)
            ? handler(payload, cancellationToken)
            : Task.FromResult(new ToolExecutionResult(false, string.Empty, "Tool handler is missing"));
    }
}
