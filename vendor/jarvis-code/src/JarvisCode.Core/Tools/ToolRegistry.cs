namespace JarvisCode.Core.Tools;

/// <summary>Lookup of the tools exposed to the model in a session.</summary>
public interface IToolRegistry
{
    IReadOnlyList<ITool> All { get; }

    ITool? Find(string name);
}

/// <summary>
/// A tool the model may also call by another name. The reference declares these
/// as a tool's `aliases`; an alias resolves but is never advertised, so the tool
/// list keeps one entry.
/// </summary>
public interface IAliasedTool
{
    IReadOnlyList<string> Aliases { get; }
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _byName;
    private readonly Dictionary<string, ITool> _byAlias;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _byAlias = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in _byName.Values)
        {
            if (tool is not IAliasedTool aliased)
                continue;
            foreach (var alias in aliased.Aliases)
            {
                // A real tool of that name always wins the lookup.
                if (!_byName.ContainsKey(alias))
                    _byAlias[alias] = tool;
            }
        }
    }

    public IReadOnlyList<ITool> All => [.. _byName.Values];

    public ITool? Find(string name) =>
        _byName.GetValueOrDefault(name) ?? _byAlias.GetValueOrDefault(name);
}
