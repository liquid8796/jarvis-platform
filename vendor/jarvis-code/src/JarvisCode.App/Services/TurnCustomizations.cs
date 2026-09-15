using System.IO;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>Explicit per-invocation customization scope; null preserves desktop composition.</summary>
public sealed record TurnCustomizations
{
    public bool DisableAutomaticDiscovery { get; init; }
    public bool DisableHooks { get; init; }
    public bool DisableSkills { get; init; }
    public bool DisableAgents { get; init; }
    public bool DisableLsp { get; init; }
    public bool DisableWorkflows { get; init; }
    public bool DisableOutputStyles { get; init; }
    public bool DisableAttribution { get; init; }
    public bool DisablePrefetch { get; init; }
    public IReadOnlySet<string>? SettingSources { get; init; }
    public Plugins.PluginContent Plugins { get; init; } = Core.Customization.Plugins.PluginContent.Empty;
    public IReadOnlyList<CustomAgentDefinition> Agents { get; init; } = [];
    public IReadOnlyList<SkillDefinition>? Skills { get; init; }
    public string? OutputStyleOverride { get; init; }
    public IReadOnlyList<(string Plugin, string Path)> PluginOutputStylePaths { get; init; } = [];
    public IReadOnlyList<(string Plugin, string Path)> PluginWorkflowPaths { get; init; } = [];
    public IReadOnlyList<string> PluginLspFiles { get; init; } = [];

    public IReadOnlyDictionary<string, string> WorkflowDefinitions()
    {
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (plugin, path) in PluginWorkflowPaths)
            foreach (var file in File.Exists(path) ? new[] { path } : Directory.EnumerateFiles(path, "*.js", SearchOption.AllDirectories))
                definitions.TryAdd(plugin + ":" + Path.GetFileNameWithoutExtension(file), file);
        return definitions;
    }

    public IEnumerable<string> AutomaticHookFiles(string cwd, ProfilePaths paths)
    {
        if (DisableAutomaticDiscovery) yield break;
        foreach (var file in new[] { paths.UserHooksFile, paths.SettingsFile })
            if (AllowsFile(file, cwd, paths) && File.Exists(file)) yield return file;
        foreach (var name in new[] { "hooks.json", "settings.json", "settings.local.json" })
        {
            var native = Path.Combine(cwd, ".jarvis", name);
            var reference = Path.Combine(cwd, ".claude", name);
            var chosen = File.Exists(native) ? native : reference;
            if (AllowsFile(chosen, cwd, paths) && File.Exists(chosen)) yield return chosen;
        }
    }

    public IReadOnlyList<SkillDefinition> ResolveSkills(string cwd, ProfilePaths paths, UiSettings settings)
    {
        if (Skills is not null) return Skills;
        var automatic = DisableAutomaticDiscovery || DisableSkills ? [] : SkillCatalog.LoadAll(cwd, paths);
        var explicitSkills = Plugins.Skills.Concat(Plugins.Commands.Select(command => new SkillDefinition(
            command.Name, command.Description, "You", command.Template, "", DateTimeOffset.MinValue, "plugin")
            { DescriptionDeclared = command.DescriptionDeclared }));
        return [.. explicitSkills.Concat(automatic.Where(skill => AllowsFile(skill.FilePath, cwd, paths)))
            .DistinctBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public bool AllowsFile(string? path, string cwd, ProfilePaths paths)
    {
        if (SettingSources is null || string.IsNullOrWhiteSpace(path)) return true;
        var normalized = Path.GetFullPath(path);
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (Under(normalized, paths.Root) || Under(normalized, home)) return SettingSources.Contains("user");
        if (Under(normalized, cwd))
        {
            var local = Path.GetFileName(normalized).Equals("settings.local.json", StringComparison.OrdinalIgnoreCase)
                || Under(normalized, Path.Combine(cwd, ".jarvis.local"));
            return SettingSources.Contains(local ? "local" : "project");
        }
        return true; // bundled resources live beside the installation, not in a settings tier
    }

    private static bool Under(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
