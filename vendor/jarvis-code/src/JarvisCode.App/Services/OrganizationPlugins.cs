using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>
/// The four values a tool policy can take, in the reference's own spellings
/// (c90ee77c5 <c>kt</c>). Anything else on disk reads as <see cref="Blocked"/>,
/// which is the reference's own coercion.
/// </summary>
public static class ToolPolicyValues
{
    public const string Allow = "allow";
    public const string Ask = "ask";
    public const string AskSession = "ask-session";
    public const string Blocked = "blocked";

    public static readonly IReadOnlyList<string> All = [Allow, Ask, AskSession, Blocked];

    /// <summary>The label the picker prints for each value.</summary>
    public static string Label(string value) => value switch
    {
        Allow => "Always allow",
        Ask => "Ask each time",
        AskSession => "Ask once per session",
        _ => "Blocked",
    };

    /// <summary>The reference refuses anything outside the set and stores "blocked".</summary>
    public static string Normalize(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal) ? value : Blocked;

    /// <summary>The reference's parse error for a value outside the set.</summary>
    public const string NotAccepted = "not an accepted value (expected allow, ask or blocked)";
}

/// <summary>
/// The tool policies an organization's plugin folder carries, stored the
/// reference's way — <c>{"mcpServers": {"{server}": {"toolPolicy": {"{tool}":
/// "allow"}}}}</c> — and translated into the rules this app's permission gate
/// already speaks. Policy is keyed by server name and applies even if the plugin
/// that ships it is later updated.
/// </summary>
public static class ToolPolicies
{
    /// <summary>The rule lines a policy set becomes: allow and blocked are ordinary rules.</summary>
    public static IReadOnlyList<string> ToRuleLines(
        IReadOnlyDictionary<string, Dictionary<string, string>> policies)
    {
        var lines = new List<string>();
        foreach (var (server, tools) in policies.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var (tool, value) in tools.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                var wireName = WireName(server, tool);
                switch (ToolPolicyValues.Normalize(value))
                {
                    case ToolPolicyValues.Allow:
                        lines.Add($"allow {wireName}");
                        break;
                    case ToolPolicyValues.Blocked:
                        lines.Add($"deny {wireName}");
                        break;
                }
            }
        }
        return lines;
    }

    /// <summary>
    /// The tools "Ask each time" covers: they are put to the user every time and
    /// the answer is never remembered, which is the one state no rule line says.
    /// </summary>
    public static IReadOnlyCollection<string> AlwaysAskTools(
        IReadOnlyDictionary<string, Dictionary<string, string>> policies)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (server, tools) in policies)
        {
            foreach (var (tool, value) in tools)
            {
                if (ToolPolicyValues.Normalize(value) == ToolPolicyValues.Ask)
                    names.Add(WireName(server, tool));
            }
        }
        return names;
    }

    /// <summary>The wire name a policy row addresses: the MCP tool as the model sees it.</summary>
    public static string WireName(string server, string tool) =>
        JarvisCode.Core.Mcp.McpBuiltInServers.WireName(server, tool);

    /// <summary>Reads the reference's document shape into the flat map this app stores.</summary>
    public static Dictionary<string, Dictionary<string, string>> FromDocument(JsonNode? root)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (root?["mcpServers"] is not JsonObject servers)
            return result;
        foreach (var (server, node) in servers)
        {
            if (node is not JsonObject entry || entry["toolPolicy"] is not JsonObject tools)
                continue;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (tool, value) in tools)
            {
                map[tool] = ToolPolicyValues.Normalize(
                    value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null);
            }
            result[server] = map;
        }
        return result;
    }

    /// <summary>Writes the map back in the reference's document shape.</summary>
    public static JsonObject ToDocument(IReadOnlyDictionary<string, Dictionary<string, string>> policies)
    {
        var servers = new JsonObject();
        foreach (var (server, tools) in policies.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var policy = new JsonObject();
            foreach (var (tool, value) in tools.OrderBy(t => t.Key, StringComparer.Ordinal))
                policy[tool] = ToolPolicyValues.Normalize(value);
            servers[server] = new JsonObject { ["toolPolicy"] = policy };
        }
        return new JsonObject { ["mcpServers"] = servers };
    }

    /// <summary>
    /// The servers the scanned plugins really ship, which is what tells a live
    /// policy from a "Pending" one — the reference's own split.
    /// </summary>
    public static (IReadOnlyList<string> Detected, IReadOnlyList<string> Pending) Split(
        IReadOnlyCollection<string> detectedServerNames,
        IReadOnlyDictionary<string, Dictionary<string, string>> policies)
    {
        var detected = detectedServerNames
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var pending = policies.Keys
            .Where(name => !detectedServerNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (detected, pending);
    }
}

/// <summary>One plugin the organization folder scan found.</summary>
public sealed record OrgPluginEntry(
    string Name,
    string Directory,
    PluginManifest? Manifest,
    string ManifestStatus,
    string? ManifestDetail,
    int SkillCount,
    int AgentCount,
    int CommandCount,
    bool HasHooks,
    IReadOnlyList<string> McpServerNames)
{
    /// <summary>The reference's Ft: a manifest that will not parse is an error, missing pieces a warning.</summary>
    public string Health =>
        ManifestStatus != "ok" ? "error"
        : McpServerNames.Count == 0 && SkillCount + AgentCount + CommandCount == 0 && !HasHooks ? "warn"
        : "ok";
}

/// <summary>What one scan of the mount folder found.</summary>
public sealed record OrgPluginScan(
    IReadOnlyList<OrgPluginEntry> Plugins,
    int SkippedEntries,
    string? ReadError)
{
    public static readonly OrgPluginScan Empty = new([], 0, null);

    /// <summary>The reference's count line, or its two empty states.</summary>
    public string Summary =>
        ReadError is { } error ? $"Read failed: {error}"
        : Plugins.Count == 0 && SkippedEntries > 0
            ? $"No plugins found ({SkippedEntries} non-plugin {(SkippedEntries == 1 ? "entry" : "entries")})"
        : Plugins.Count == 0 ? "No organization plugins found"
        : $"{Plugins.Count} {(Plugins.Count == 1 ? "plugin" : "plugins")} found";

    /// <summary>The reference's parenthetical after the count, when anything was skipped.</summary>
    public string? SkippedNote => SkippedEntries == 0 || Plugins.Count == 0
        ? null
        : $"({SkippedEntries} non-plugin {(SkippedEntries == 1 ? "entry" : "entries")} in folder skipped)";
}

/// <summary>
/// The Organization plugins page's folder scan. The reference has a
/// device-management tool mount plugin bundles into a read-only folder and loads
/// them at launch; here the user points at that folder and the same scan reads
/// it — every subdirectory that looks like a plugin, with its manifest status
/// and the MCP servers it declares, which is what the tool policy is keyed on.
/// </summary>
public static class OrganizationPluginScanner
{
    public static OrgPluginScan Scan(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return OrgPluginScan.Empty;

        string[] directories;
        try
        {
            if (!Directory.Exists(folder))
                return new OrgPluginScan([], 0, "the folder does not exist");
            directories = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new OrgPluginScan([], 0, ex.Message);
        }

        // The mount folder is a plugins root: one load reads every plugin under it,
        // and each subdirectory is matched to what that load found for it.
        var content = Plugins.Load(folder);
        var byName = content.Installed.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        var plugins = new List<OrgPluginEntry>();
        var skipped = 0;
        foreach (var directory in directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.'))
            {
                skipped++;
                continue;
            }
            byName.TryGetValue(name, out var info);
            var entry = Read(directory, name, info);
            if (entry is null)
                skipped++;
            else
                plugins.Add(entry);
        }
        return new OrgPluginScan(plugins, skipped, null);
    }

    /// <summary>
    /// Reads one candidate folder, or null when nothing in it makes it a plugin:
    /// a folder is one when it declares a manifest or contributes something a
    /// turn would load.
    /// </summary>
    public static OrgPluginEntry? Read(string directory, string name, PluginInfo? info = null)
    {
        var (status, detail) = PluginManifests.Status(directory);
        var servers = ServerNames(directory);
        var carriesSomething = info is not null &&
            (info.SkillCount + info.AgentCount + info.CommandCount > 0 || info.HasHooks || info.HasMcp);
        if (status == "missing" && !carriesSomething)
            return null;

        return new OrgPluginEntry(
            name, directory, PluginManifests.Read(directory), status, detail,
            info?.SkillCount ?? 0, info?.AgentCount ?? 0, info?.CommandCount ?? 0,
            info?.HasHooks ?? false, servers);
    }

    /// <summary>The MCP servers a plugin's own .mcp.json declares — what the policy is keyed on.</summary>
    private static IReadOnlyList<string> ServerNames(string directory)
    {
        var file = Path.Combine(directory, ".mcp.json");
        if (!File.Exists(file))
            return [];
        return [.. JarvisCode.Core.Mcp.McpConfig.LoadSingleFile(file)
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The reference's "Manifest {status}: {detail}" line, or its "No plugin.json".</summary>
    public static string ManifestLine(OrgPluginEntry entry) =>
        entry.ManifestStatus == "missing"
            ? "No plugin.json"
            : $"Manifest {entry.ManifestStatus}: {entry.ManifestDetail ?? entry.ManifestStatus}";

    /// <summary>
    /// The reference's <c>$t</c>: how long ago the folder was last read, through
    /// Intl.RelativeTimeFormat at numeric "auto" and style "narrow".
    /// </summary>
    public static string LastSynced(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null)
            return "never";
        var seconds = Math.Max(0, (int)Math.Round((now - at.Value).TotalSeconds));
        if (seconds < 45)
            return "now";
        var minutes = (int)Math.Round(seconds / 60.0);
        if (minutes < 60)
            return $"{minutes}m ago";
        var hours = (int)Math.Round(minutes / 60.0);
        if (hours < 24)
            return $"{hours}h ago";
        var days = (int)Math.Round(hours / 24.0);
        return days == 1 ? "yesterday" : $"{days}d ago";
    }
}
