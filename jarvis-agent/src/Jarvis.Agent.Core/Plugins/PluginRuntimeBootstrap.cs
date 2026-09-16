namespace Jarvis.Agent.Core.Plugins;

/// <summary>
/// Production bootstrap for locally supplied plugin implementations and pinned manifests.
/// Startup discovery is synchronous and deterministic; hot reload is layered on this service.
/// </summary>
public sealed class PluginRuntimeBootstrap
{
    private readonly string _directory;
    private readonly IReadOnlyList<IAgentTool> _implementations;
    private readonly Version _agentVersion;

    public PluginRuntimeBootstrap(string directory, IEnumerable<IAgentTool> implementations, Version agentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(implementations);
        ArgumentNullException.ThrowIfNull(agentVersion);
        _directory = Path.GetFullPath(directory);
        _implementations = implementations.ToArray();
        _agentVersion = agentVersion;
        Snapshot = PluginCatalog.Load(_directory, _implementations, _agentVersion);
    }

    public string DirectoryPath => _directory;
    public PluginCatalogSnapshot Snapshot { get; private set; }

    public PluginCatalogSnapshot Reload()
    {
        Snapshot = PluginCatalog.Load(_directory, _implementations, _agentVersion);
        return Snapshot;
    }
}
