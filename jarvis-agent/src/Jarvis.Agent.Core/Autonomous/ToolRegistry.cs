namespace Jarvis.Agent.Core.Autonomous;

public sealed record ToolDefinition(
    string Name,
    string Category,
    string Permission,
    TimeSpan Timeout);

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ToolDefinition tool)
        => _tools[tool.Name] = tool;

    public ToolDefinition? Find(string name)
        => _tools.GetValueOrDefault(name);

    public IReadOnlyCollection<ToolDefinition> All()
        => _tools.Values;
}
