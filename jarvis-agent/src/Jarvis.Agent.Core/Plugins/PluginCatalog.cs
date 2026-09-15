using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Plugins;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginToolDeclaration
{
    public string Id { get; init; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginManifest
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string MinAgentVersion { get; init; } = "";
    public string EntryFile { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public IReadOnlyList<PluginToolDeclaration> Tools { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<string> Hooks { get; init; } = [];
}

public sealed record PluginCatalogSnapshot(
    IReadOnlyList<PluginManifest> Manifests,
    IReadOnlyDictionary<string, IAgentTool> Tools,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Hooks);

/// <summary>
/// Discovers and validates local plugin metadata. It deliberately does not load
/// assemblies or download code. Tool declarations bind only to implementations
/// already supplied by the local host.
/// </summary>
public static class PluginCatalog
{
    private static readonly HashSet<string> SupportedHooks = new(StringComparer.Ordinal)
    { "interrupt", "stop", "subagentStop" };

    public static PluginCatalogSnapshot Load(string directory, IEnumerable<IAgentTool> implementations, Version agentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(implementations);
        ArgumentNullException.ThrowIfNull(agentVersion);
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) return new([], new Dictionary<string, IAgentTool>(), new Dictionary<string, IReadOnlyList<string>>());

        var available = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var implementation in implementations)
        {
            if (!available.TryAdd(implementation.Descriptor.Id, implementation))
                throw new ArgumentException("Duplicate supplied plugin tool implementation: " + implementation.Descriptor.Id);
        }

        var manifests = new List<PluginManifest>();
        var pluginIds = new HashSet<string>(StringComparer.Ordinal);
        var bound = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        var hooks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(root, "*.plugin.json", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            PluginManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions(WireJson.Options)
                {
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                }) ?? throw new ArgumentException("Plugin manifest is empty: " + Path.GetFileName(path));
            }
            catch (JsonException ex) { throw new ArgumentException("Invalid plugin manifest JSON: " + Path.GetFileName(path), ex); }

            ValidateManifest(root, manifest, agentVersion);
            if (!pluginIds.Add(manifest.Id)) throw new ArgumentException("Duplicate plugin ID: " + manifest.Id);
            foreach (var declaration in manifest.Tools)
            {
                if (string.IsNullOrWhiteSpace(declaration.Id) || declaration.Id.Length > 100)
                    throw new ArgumentException("Plugin tool ID is invalid in " + manifest.Id);
                if (!available.TryGetValue(declaration.Id, out var implementation))
                    throw new ArgumentException("Plugin declares an implementation that is not locally supplied: " + declaration.Id);
                if (!bound.TryAdd(declaration.Id, implementation))
                    throw new ArgumentException("Duplicate plugin tool ID across manifests: " + declaration.Id);
            }
            manifests.Add(manifest);
            hooks.Add(manifest.Id, manifest.Hooks.ToArray());
        }

        return new(manifests, bound, hooks);
    }

    private static void ValidateManifest(string root, PluginManifest manifest, Version agentVersion)
    {
        if (!ValidId(manifest.Id) || !Version.TryParse(manifest.Version, out _))
            throw new ArgumentException("Plugin ID or version is invalid.");
        if (!Version.TryParse(manifest.MinAgentVersion, out var minimum) || minimum > agentVersion)
            throw new ArgumentException("Plugin requires a newer or invalid agent version: " + manifest.MinAgentVersion);
        if (string.IsNullOrWhiteSpace(manifest.EntryFile) || Path.IsPathRooted(manifest.EntryFile))
            throw new ArgumentException("Plugin entryFile must be a relative local path.");
        var entry = Path.GetFullPath(Path.Combine(root, manifest.EntryFile));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!entry.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Plugin entryFile escapes the plugin directory.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Plugin sha256 must contain exactly 64 hexadecimal characters.");
        if (!File.Exists(entry)) throw new ArgumentException("Plugin entryFile does not exist: " + manifest.EntryFile);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))).ToLowerInvariant();
        if (!StringComparer.OrdinalIgnoreCase.Equals(actual, manifest.Sha256))
            throw new ArgumentException("Plugin entryFile hash does not match manifest pin.");
        if (manifest.Hooks.Any(hook => !SupportedHooks.Contains(hook)))
            throw new ArgumentException("Plugin declares an unsupported lifecycle hook.");
        if (manifest.Tools.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Tools.Count)
            throw new ArgumentException("Plugin manifest contains duplicate tool IDs.");
        if (manifest.Permissions.Count > 128 || manifest.Skills.Count > 128 || manifest.Hooks.Count > 16 || manifest.Tools.Count > 128)
            throw new ArgumentException("Plugin manifest exceeds bounded declaration limits.");
    }

    private static bool ValidId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100) return false;
        return value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }
}
