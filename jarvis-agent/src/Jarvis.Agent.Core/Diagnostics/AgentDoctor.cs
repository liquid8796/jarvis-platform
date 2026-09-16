using System.Text.Json;
using Jarvis.Agent.Core.Plugins;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Diagnostics;

public sealed record AgentDoctorInput(
    string PackageVersion,
    Version AssemblyVersion,
    DynamicToolRegistry Registry,
    ToolPermissionPolicy Permissions,
    string PluginDirectory,
    IEnumerable<IAgentTool> PluginImplementations,
    string TaskStorageRoot,
    int OwnedProcessCount,
    bool ComputerUseReady,
    bool BrowserIntegrationReady,
    bool PermissionStoreHealthy = true,
    string PermissionStoreStatus = "ok");

public sealed record AgentDoctorSnapshot
{
    public DateTimeOffset TimestampUtc { get; init; }
    public string PackageVersion { get; init; } = "";
    public string AssemblyVersion { get; init; } = "";
    public bool VersionDrift { get; init; }
    public int ProtocolVersion { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public long CatalogGeneration { get; init; }
    public string CatalogDigest { get; init; } = "";
    public int ToolCount { get; init; }
    public bool PluginHealthy { get; init; }
    public string PluginStatus { get; init; } = "";
    public int PluginManifestCount { get; init; }
    public int PluginToolCount { get; init; }
    public int PluginSkillRootCount { get; init; }
    public int PluginMcpDependencyCount { get; init; }
    public bool PermissionStoreHealthy { get; init; }
    public string PermissionStoreStatus { get; init; } = "";
    public int FullPermissionCount { get; init; }
    public int ActiveCapabilityLeaseCount { get; init; }
    public bool TaskStoreHealthy { get; init; }
    public string TaskStoreStatus { get; init; } = "";
    public int TaskSnapshotCount { get; init; }
    public int OwnedProcessCount { get; init; }
    public bool ComputerUseReady { get; init; }
    public bool BrowserIntegrationReady { get; init; }
}

/// <summary>Read-only, redacted operational diagnostics. No tool arguments/results, credentials or local paths are returned.</summary>
public static class AgentDoctor
{
    public static AgentDoctorSnapshot Capture(AgentDoctorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var catalog = input.Registry.Snapshot;
        var (pluginHealthy, pluginStatus, pluginManifests, pluginTools, pluginSkillRoots, pluginMcpDependencies) = PluginHealth(input);
        var (taskHealthy, taskStatus, taskCount) = TaskHealth(input.TaskStorageRoot);
        return new AgentDoctorSnapshot
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            PackageVersion = input.PackageVersion,
            AssemblyVersion = input.AssemblyVersion.ToString(),
            VersionDrift = HasVersionDrift(input.PackageVersion, input.AssemblyVersion),
            ProtocolVersion = AgentProtocolVersion.Current,
            Capabilities = AgentProtocolCapabilities.Agent,
            CatalogGeneration = catalog.Generation,
            CatalogDigest = catalog.Digest,
            ToolCount = catalog.Tools.Count,
            PluginHealthy = pluginHealthy,
            PluginStatus = pluginStatus,
            PluginManifestCount = pluginManifests,
            PluginToolCount = pluginTools,
            PluginSkillRootCount = pluginSkillRoots,
            PluginMcpDependencyCount = pluginMcpDependencies,
            PermissionStoreHealthy = input.PermissionStoreHealthy,
            PermissionStoreStatus = input.PermissionStoreStatus,
            FullPermissionCount = input.Permissions.FullPermissionTools.Count,
            ActiveCapabilityLeaseCount = input.Permissions.ActiveLeases.Count,
            TaskStoreHealthy = taskHealthy,
            TaskStoreStatus = taskStatus,
            TaskSnapshotCount = taskCount,
            OwnedProcessCount = Math.Max(0, input.OwnedProcessCount),
            ComputerUseReady = input.ComputerUseReady,
            BrowserIntegrationReady = input.BrowserIntegrationReady
        };
    }

    private static (bool Healthy, string Status, int Manifests, int Tools, int SkillRoots, int McpDependencies) PluginHealth(AgentDoctorInput input)
    {
        try
        {
            var snapshot = PluginCatalog.Load(input.PluginDirectory, input.PluginImplementations, input.AssemblyVersion);
            return (true, "ok", snapshot.Manifests.Count, snapshot.Tools.Count,
                snapshot.SkillRoots.Values.Sum(x => x.Count), snapshot.McpDependencies.Values.Sum(x => x.Count));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return (false, "error:" + ex.GetType().Name, 0, 0, 0, 0);
        }
    }

    private static (bool Healthy, string Status, int Count) TaskHealth(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root)) return (false, "unconfigured", 0);
            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full)) return (true, "empty", 0);
            var paths = Directory.EnumerateFiles(full, "*.json", SearchOption.AllDirectories).Take(129).ToArray();
            if (paths.Length > 128) return (false, "capacity-exceeded", 128);
            foreach (var path in paths)
            {
                var info = new FileInfo(path);
                if (info.Length > 8 * 1024 * 1024) return (false, "oversized-snapshot", paths.Length);
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object) return (false, "invalid-snapshot", paths.Length);
            }
            return (true, "ok", paths.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return (false, "error:" + ex.GetType().Name, 0);
        }
    }

    private static bool HasVersionDrift(string packageVersion, Version assemblyVersion)
    {
        if (!Version.TryParse(packageVersion, out var package)) return true;
        return package.Major != assemblyVersion.Major || package.Minor != assemblyVersion.Minor ||
            package.Build != assemblyVersion.Build;
    }
}
