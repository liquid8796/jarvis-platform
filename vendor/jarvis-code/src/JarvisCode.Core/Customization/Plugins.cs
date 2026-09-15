namespace JarvisCode.Core.Customization;

public sealed record PluginInfo(
    string Name,
    string Directory,
    int CommandCount,
    int AgentCount,
    int SkillCount,
    bool HasHooks,
    bool HasMcp)
{
    /// <summary>The user-level plugin root, where every plugin lived before scopes existed.</summary>
    public const string UserScope = "user";

    /// <summary>A plugin installed for the project and shared through version control.</summary>
    public const string ProjectScope = "project";

    /// <summary>A plugin installed for the project and kept out of version control.</summary>
    public const string LocalScope = "local";

    /// <summary>Which root the plugin was loaded from: user, project or local.</summary>
    public string Scope { get; init; } = UserScope;

    /// <summary>The plugins root the folder sits in.</summary>
    public string Root { get; init; } = "";
}

/// <summary>
/// A plugin is a folder under the user-level plugins directory bundling
/// commands/, agents/, skills/, hooks.json and mcp.json. Everything it
/// contributes is namespaced as "plugin:name" so plugins never shadow local
/// definitions.
/// </summary>
public static class Plugins
{
    public sealed record PluginContent(
        IReadOnlyList<PluginInfo> Installed,
        IReadOnlyList<CustomCommandDefinition> Commands,
        IReadOnlyList<CustomAgentDefinition> Agents,
        IReadOnlyList<SkillDefinition> Skills,
        IReadOnlyList<string> HookFiles,
        IReadOnlyList<string> McpFiles)
    {
        public static readonly PluginContent Empty = new([], [], [], [], [], []);
    }

    public static PluginContent Load(string pluginsRoot) => Load(pluginsRoot, PluginInfo.UserScope, include: null);

    /// <summary>
    /// Loads one plugins root, stamping every plugin with the scope it belongs to,
    /// and leaving out the plugins <paramref name="include"/> refuses — which is
    /// how a plugin the user switched off contributes nothing to a turn while its
    /// folder stays on disk for the day it is switched back on.
    /// </summary>
    public static PluginContent Load(string pluginsRoot, string scope, Func<PluginInfo, bool>? include)
    {
        string[] directories;
        try
        {
            if (!Directory.Exists(pluginsRoot))
                return PluginContent.Empty;
            directories = Directory.GetDirectories(pluginsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginContent.Empty;
        }

        return LoadDirectories(
            directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => (Name: Path.GetFileName(d), Directory: d)),
            scope,
            pluginsRoot,
            include);
    }

    /// <summary>
    /// Loads plugins whose name and folder are given rather than derived from a
    /// directory listing. A layout that files a plugin under a version or a
    /// marketplace — which is how Claude Code's own installs are laid out — has no
    /// folder whose name is the plugin's, so the name has to travel separately.
    /// </summary>
    public static PluginContent LoadDirectories(
        IEnumerable<(string Name, string Directory)> plugins,
        string scope,
        string root,
        Func<PluginInfo, bool>? include)
    {
        var installed = new List<PluginInfo>();
        var commands = new List<CustomCommandDefinition>();
        var agents = new List<CustomAgentDefinition>();
        var skills = new List<SkillDefinition>();
        var hookFiles = new List<string>();
        var mcpFiles = new List<string>();

        foreach (var (declaredName, directory) in plugins)
        {
            var pluginName = declaredName.Trim().ToLowerInvariant();
            if (pluginName.Length == 0 || pluginName.Any(char.IsWhiteSpace))
                continue;

            var pluginCommands = CustomCommands.LoadDirectory(Path.Combine(directory, "commands"))
                .Select(c => c with { Name = $"{pluginName}:{c.Name}" })
                .ToList();
            var pluginAgents = CustomAgents.LoadDirectory(Path.Combine(directory, "agents"))
                .Select(a => a with { Name = $"{pluginName}:{a.Name}" })
                .ToList();
            var pluginSkills = Skills.LoadDirectory(Path.Combine(directory, "skills"), $"plugin:{pluginName}")
                .Select(s => s with { Name = $"{pluginName}:{s.Name}" })
                .ToList();
            var hooksFile = Path.Combine(directory, "hooks.json");
            var mcpFile = Path.Combine(directory, "mcp.json");
            if (!File.Exists(mcpFile))
            {
                // Claude Code plugins ship the same connector config as ".mcp.json".
                var alternate = Path.Combine(directory, ".mcp.json");
                if (File.Exists(alternate))
                    mcpFile = alternate;
            }
            bool hasHooks = File.Exists(hooksFile);
            bool hasMcp = File.Exists(mcpFile);

            var info = new PluginInfo(
                pluginName, directory,
                pluginCommands.Count, pluginAgents.Count, pluginSkills.Count, hasHooks, hasMcp)
            {
                Scope = scope,
                Root = root,
            };
            if (include is not null && !include(info))
                continue;

            commands.AddRange(pluginCommands);
            agents.AddRange(pluginAgents);
            skills.AddRange(pluginSkills);
            if (hasHooks)
                hookFiles.Add(hooksFile);
            if (hasMcp)
                mcpFiles.Add(mcpFile);
            installed.Add(info);
        }
        return new PluginContent(installed, commands, agents, skills, hookFiles, mcpFiles);
    }

    /// <summary>
    /// Appends <paramref name="extra"/> to <paramref name="first"/>, dropping any
    /// plugin whose name the first already carries — the same first-wins rule
    /// <see cref="LoadAll"/> applies across roots, for content that was not loaded
    /// from a root.
    /// </summary>
    public static PluginContent Combine(PluginContent first, PluginContent extra)
    {
        var taken = new HashSet<string>(first.Installed.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var added = extra.Installed.Where(p => !taken.Contains(p.Name)).ToList();
        if (added.Count == 0)
            return first;

        var keep = new HashSet<string>(added.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        bool Mine(string name)
        {
            var colon = name.IndexOf(':');
            return colon > 0 && keep.Contains(name[..colon]);
        }

        var directories = new HashSet<string>(added.Select(p => p.Directory), StringComparer.OrdinalIgnoreCase);
        bool FromAdded(string file) =>
            Path.GetDirectoryName(file) is { } dir && directories.Contains(dir);

        return new PluginContent(
            [.. first.Installed, .. added],
            [.. first.Commands, .. extra.Commands.Where(c => Mine(c.Name))],
            [.. first.Agents, .. extra.Agents.Where(a => Mine(a.Name))],
            [.. first.Skills, .. extra.Skills.Where(s => Mine(s.Name))],
            [.. first.HookFiles, .. extra.HookFiles.Where(FromAdded)],
            [.. first.McpFiles, .. extra.McpFiles.Where(FromAdded)]);
    }

    /// <summary>
    /// Loads several roots in the order given and merges them. A plugin name that
    /// appears in more than one root is taken from the first root that carries it,
    /// so a user-level install shadows a project one the way a user skill shadows
    /// a project skill.
    /// </summary>
    public static PluginContent LoadAll(
        IEnumerable<(string Root, string Scope)> roots, Func<PluginInfo, bool>? include = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<PluginInfo>();
        var commands = new List<CustomCommandDefinition>();
        var agents = new List<CustomAgentDefinition>();
        var skills = new List<SkillDefinition>();
        var hookFiles = new List<string>();
        var mcpFiles = new List<string>();
        foreach (var (root, scope) in roots)
        {
            var content = Load(root, scope, info => seen.Add(info.Name) && (include is null || include(info)));
            installed.AddRange(content.Installed);
            commands.AddRange(content.Commands);
            agents.AddRange(content.Agents);
            skills.AddRange(content.Skills);
            hookFiles.AddRange(content.HookFiles);
            mcpFiles.AddRange(content.McpFiles);
        }
        return installed.Count == 0
            ? PluginContent.Empty
            : new PluginContent(installed, commands, agents, skills, hookFiles, mcpFiles);
    }
}
