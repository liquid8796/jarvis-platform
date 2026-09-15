using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>What a plugin's manifest (.claude-plugin/plugin.json or plugin.json) declares.</summary>
public sealed record PluginManifest(
    string? Name,
    string? Description,
    string? Version,
    string? Author,
    string? Category,
    IReadOnlyList<string> Keywords,
    string? Homepage);

/// <summary>Reads plugin manifests, tolerating the two places and the two author shapes they come in.</summary>
public static class PluginManifests
{
    /// <summary>The manifest paths, in the order the reference looks.</summary>
    public static readonly IReadOnlyList<string> RelativePaths =
        [Path.Combine(".claude-plugin", "plugin.json"), "plugin.json"];

    public static string? ManifestPath(string pluginDirectory) =>
        RelativePaths.Select(p => Path.Combine(pluginDirectory, p)).FirstOrDefault(File.Exists);

    /// <summary>The manifest, or null when there is none or it does not parse.</summary>
    public static PluginManifest? Read(string pluginDirectory) =>
        ManifestPath(pluginDirectory) is { } path ? Parse(path) : null;

    /// <summary>
    /// Whether a manifest exists and parses. The organization scan prints this as
    /// "No plugin.json" or "Manifest {status}: {detail}".
    /// </summary>
    public static (string Status, string? Detail) Status(string pluginDirectory)
    {
        var path = ManifestPath(pluginDirectory);
        if (path is null)
            return ("missing", null);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject)
                return ("invalid", "not a JSON object");
            return ("ok", null);
        }
        catch (JsonException ex)
        {
            return ("invalid", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("invalid", ex.Message);
        }
    }

    public static PluginManifest? Parse(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                return null;
            return Parse(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static PluginManifest Parse(JsonObject root)
    {
        var author = root["author"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject o => o["name"]?.GetValue<string>(),
            _ => null,
        };
        var keywords = (root["keywords"] as JsonArray ?? root["tags"] as JsonArray)?
            .Select(k => k?.GetValue<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!)
            .ToList() ?? [];
        return new PluginManifest(
            Text(root, "name"), Text(root, "description"), Text(root, "version"), author,
            Text(root, "category"), keywords, Text(root, "homepage"));
    }

    private static string? Text(JsonObject root, string key) =>
        root[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0
            ? text.Trim()
            : null;
}

/// <summary>Where a skill or plugin is installed: the three roots the install scopes name.</summary>
public enum InstallScope
{
    /// <summary>"Install for me" — the profile's own directory, every project.</summary>
    User,

    /// <summary>"Install for project (shared)" — {cwd}/.jarvis, checked in.</summary>
    Project,

    /// <summary>"Install for project (personal)" — {cwd}/.jarvis.local, gitignored.</summary>
    Local,
}

/// <summary>
/// The install-scope menu the Directory offers, in the reference's order. The
/// reference names its own folders (~/.claude, .claude, .claude.local); these
/// name the folders this app installs to, which is the whole point of the line.
/// </summary>
public static class InstallScopes
{
    /// <summary>The project subdirectory a shared install writes under.</summary>
    public const string ProjectDirectoryName = ".jarvis";

    /// <summary>The project subdirectory a personal install writes under; git ignores it.</summary>
    public const string LocalDirectoryName = ".jarvis.local";

    public static readonly IReadOnlyList<InstallScope> All =
        [InstallScope.User, InstallScope.Project, InstallScope.Local];

    public static string Label(InstallScope scope) => scope switch
    {
        InstallScope.Project => "Install for project (shared)",
        InstallScope.Local => "Install for project (personal)",
        _ => "Install for me",
    };

    /// <summary>The line under each label, naming the folder this app really writes to.</summary>
    public static string Description(InstallScope scope, string userDirectory) => scope switch
    {
        InstallScope.Project => $"Installs to {ProjectDirectoryName}/ — shared with your team via git.",
        InstallScope.Local => $"Installs to {LocalDirectoryName}/ — personal, gitignored.",
        _ => $"Installs to {userDirectory} — available in all projects on this machine.",
    };

    /// <summary>The skills directory a scope installs into.</summary>
    public static string SkillsRoot(InstallScope scope, ProfilePaths paths, string cwd) => scope switch
    {
        InstallScope.Project => Path.Combine(cwd, ProjectDirectoryName, "skills"),
        InstallScope.Local => Path.Combine(cwd, LocalDirectoryName, "skills"),
        _ => paths.UserSkillsDirectory,
    };

    /// <summary>The plugins directory a scope installs into.</summary>
    public static string PluginsRoot(InstallScope scope, ProfilePaths paths, string cwd) => scope switch
    {
        InstallScope.Project => Path.Combine(cwd, ProjectDirectoryName, "plugins"),
        InstallScope.Local => Path.Combine(cwd, LocalDirectoryName, "plugins"),
        _ => paths.PluginsDirectory,
    };

    public static string ScopeName(InstallScope scope) => scope switch
    {
        InstallScope.Project => PluginInfo.ProjectScope,
        InstallScope.Local => PluginInfo.LocalScope,
        _ => PluginInfo.UserScope,
    };

    /// <summary>The reference's Scope cell: printed only for a project install.</summary>
    public static string? ScopeCell(string scope) => scope switch
    {
        PluginInfo.ProjectScope => "Project (shared)",
        PluginInfo.LocalScope => "Project (personal)",
        _ => null,
    };
}

/// <summary>The plugin roots a session reads, in the order that decides which copy of a name wins.</summary>
public static class PluginRoots
{
    /// <summary>User first, then the project's shared root, then its personal one.</summary>
    public static IReadOnlyList<(string Root, string Scope)> For(ProfilePaths paths, string? cwd)
    {
        var roots = new List<(string, string)> { (paths.PluginsDirectory, PluginInfo.UserScope) };
        if (!string.IsNullOrEmpty(cwd))
        {
            roots.Add((InstallScopes.PluginsRoot(InstallScope.Project, paths, cwd), PluginInfo.ProjectScope));
            roots.Add((InstallScopes.PluginsRoot(InstallScope.Local, paths, cwd), PluginInfo.LocalScope));
        }
        return roots;
    }
}

/// <summary>Where an installed plugin came from, kept beside the plugin roots.</summary>
public sealed record InstalledPluginRecord(string? Marketplace, string? Version, DateTimeOffset InstalledAt, string? Source);

/// <summary>
/// <c>installed.json</c> in a plugins root: which marketplace each plugin was
/// installed from and at what version, which is what "Check for updates" and
/// the marketplace's remove confirm read.
/// </summary>
public sealed class InstalledPluginsIndex(string root)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; } = Path.Combine(root, "installed.json");

    public IReadOnlyDictionary<string, InstalledPluginRecord> All()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, InstalledPluginRecord>(StringComparer.OrdinalIgnoreCase);
            var parsed =
                JsonSerializer.Deserialize<Dictionary<string, InstalledPluginRecord>>(File.ReadAllText(FilePath), Options);
            return new Dictionary<string, InstalledPluginRecord>(parsed ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, InstalledPluginRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public InstalledPluginRecord? Get(string pluginName) =>
        All().TryGetValue(pluginName, out var record) ? record : null;

    public void Set(string pluginName, InstalledPluginRecord record)
    {
        var all = new Dictionary<string, InstalledPluginRecord>(All(), StringComparer.OrdinalIgnoreCase)
        {
            [pluginName] = record,
        };
        Write(all);
    }

    public void Remove(string pluginName)
    {
        var all = new Dictionary<string, InstalledPluginRecord>(All(), StringComparer.OrdinalIgnoreCase);
        if (all.Remove(pluginName))
            Write(all);
    }

    private void Write(Dictionary<string, InstalledPluginRecord> all)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Options));
    }
}

/// <summary>What an uploaded plugin archive turned out to hold.</summary>
public sealed record PluginUploadPreview(string? Name, string? Description);

/// <summary>The result of a plugin upload.</summary>
public sealed record PluginUploadOutcome(string Kind, string? PluginName, string? Message)
{
    public const string Ok = "ok";
    public const string Conflict = "conflict";
    public const string Invalid = "invalid";
}

/// <summary>
/// The plugins the app has, across the three roots, with the enabled switch the
/// reference's plugin toggle flips: a disabled plugin stays on disk and
/// contributes nothing to a turn.
/// </summary>
public static class PluginLibrary
{
    /// <summary>The extensions Upload plugin accepts.</summary>
    public static readonly IReadOnlyList<string> UploadExtensions = [".zip", ".plugin"];

    /// <summary>The reference's manifest rule, refused verbatim when an archive breaks it.</summary>
    public const string ManifestRule =
        "The archive must contain a .claude-plugin/plugin.json manifest, or a top-level SKILL.md that declares the plugin’s components";

    /// <summary>The key a plugin's enabled state is filed under: a project copy is its own switch.</summary>
    public static string Key(PluginInfo plugin) => $"{plugin.Scope}:{plugin.Name}";

    public static bool IsEnabled(UiSettings ui, PluginInfo plugin) => !ui.DisabledPlugins.Contains(Key(plugin));

    public static void SetEnabled(UiSettingsStore store, PluginInfo plugin, bool enabled)
    {
        if (enabled)
            store.Current.DisabledPlugins.Remove(Key(plugin));
        else if (!store.Current.DisabledPlugins.Contains(Key(plugin)))
            store.Current.DisabledPlugins.Add(Key(plugin));
        store.Save();
    }

    /// <summary>Every plugin in every root, disabled ones included — what the Plugins page lists.</summary>
    public static Plugins.PluginContent LoadAllScopes(ProfilePaths paths, string? cwd) =>
        Plugins.Combine(Plugins.LoadAll(PluginRoots.For(paths, cwd)), ClaudeCodePlugins.Load());

    /// <summary>The plugins a turn should see: every root, enabled ones only.</summary>
    public static Plugins.PluginContent LoadActive(ProfilePaths paths, string? cwd, UiSettings ui) =>
        Plugins.Combine(
            Plugins.LoadAll(PluginRoots.For(paths, cwd), plugin => IsEnabled(ui, plugin)),
            ClaudeCodePlugins.Load(plugin => IsEnabled(ui, plugin)));

    /// <summary>
    /// The user-level plugins a session reads: this app's own root, then the ones
    /// Claude Code has installed. This app's copy of a name wins, so installing a
    /// plugin here takes over from the one Claude Code has.
    /// </summary>
    public static Plugins.PluginContent LoadUser(ProfilePaths paths) =>
        Plugins.Combine(Plugins.Load(paths.PluginsDirectory), ClaudeCodePlugins.Load());

    /// <summary>Copies a plugin folder into a root under a name; the index remembers where it came from.</summary>
    public static string Install(string sourceDirectory, string root, string name, InstalledPluginRecord? record)
    {
        var target = Path.Combine(root, name);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        CopyDirectory(sourceDirectory, target);
        if (record is not null)
            new InstalledPluginsIndex(root).Set(name, record);
        return target;
    }

    public static void Uninstall(PluginInfo plugin)
    {
        if (Directory.Exists(plugin.Directory))
            Directory.Delete(plugin.Directory, recursive: true);
        if (plugin.Root.Length > 0)
            new InstalledPluginsIndex(plugin.Root).Remove(plugin.Name);
    }

    /// <summary>The last time anything in the plugin folder changed.</summary>
    public static DateTimeOffset? UpdatedAt(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;
            DateTime latest = Directory.GetLastWriteTime(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var at = File.GetLastWriteTime(file);
                if (at > latest)
                    latest = at;
            }
            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool HasUploadExtension(string fileName) =>
        UploadExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The reference's manifest rule read off an archive: it must contain a
    /// .claude-plugin/plugin.json manifest, or a top-level SKILL.md that declares
    /// the plugin's components. Returns the folder prefix inside the zip.
    /// </summary>
    internal static (string Prefix, bool SkillOnly)? Layout(ZipArchive archive)
    {
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(n => !n.EndsWith('/')).ToList();
        foreach (var candidate in new[] { ".claude-plugin/plugin.json", "plugin.json" })
        {
            var root = names.FirstOrDefault(n => n.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (root is not null)
                return ("", false);
            var nested = names.FirstOrDefault(n =>
                n.EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase) &&
                n.Count(c => c == '/') == candidate.Count(c => c == '/') + 1);
            if (nested is not null)
                return (nested[..(nested.IndexOf('/') + 1)], false);
        }
        if (names.Any(n => n.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            return ("", true);
        var nestedSkill = names.FirstOrDefault(n =>
            n.Count(c => c == '/') == 1 && n.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
        return nestedSkill is null ? null : (nestedSkill[..(nestedSkill.IndexOf('/') + 1)], true);
    }

    /// <summary>The name and description an archive would install as.</summary>
    public static PluginUploadPreview? Preview(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (Layout(archive) is not { } layout)
                return null;
            if (layout.SkillOnly)
            {
                var entry = archive.GetEntry(layout.Prefix + "SKILL.md")!;
                using var reader = new StreamReader(entry.Open());
                var (name, description) = SkillLibrary.NameAndDescription(reader.ReadToEnd());
                return new PluginUploadPreview(name, description);
            }
            var manifestEntry = archive.GetEntry(layout.Prefix + ".claude-plugin/plugin.json")
                ?? archive.GetEntry(layout.Prefix + "plugin.json")!;
            using var manifestReader = new StreamReader(manifestEntry.Open());
            if (JsonNode.Parse(manifestReader.ReadToEnd()) is not JsonObject root)
                return null;
            var manifest = PluginManifests.Parse(root);
            return new PluginUploadPreview(manifest.Name, manifest.Description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The folder name an archive installs under: its declared name, made safe.</summary>
    public static string? InstallName(string zipPath, PluginUploadPreview? preview)
    {
        var name = (preview?.Name ?? Path.GetFileNameWithoutExtension(zipPath)).Trim().ToLowerInvariant();
        name = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray())
            .Trim('-');
        return name.Length == 0 ? null : name;
    }

    /// <summary>Installs an uploaded archive into a root under its declared name.</summary>
    public static PluginUploadOutcome Upload(string zipPath, string root, bool overwrite)
    {
        if (!HasUploadExtension(zipPath))
            return new PluginUploadOutcome(PluginUploadOutcome.Invalid, null, ManifestRule);
        var preview = Preview(zipPath);
        if (preview is null)
            return new PluginUploadOutcome(PluginUploadOutcome.Invalid, null, ManifestRule);
        var name = InstallName(zipPath, preview);
        if (name is null)
            return new PluginUploadOutcome(PluginUploadOutcome.Invalid, null, ManifestRule);
        var target = Path.Combine(root, name);
        if (Directory.Exists(target) && !overwrite)
            return new PluginUploadOutcome(PluginUploadOutcome.Conflict, name, null);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var layout = Layout(archive)!.Value;
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);
            foreach (var entry in archive.Entries)
            {
                var full = entry.FullName.Replace('\\', '/');
                if (full.EndsWith('/') || !full.StartsWith(layout.Prefix, StringComparison.Ordinal))
                    continue;
                var relative = full[layout.Prefix.Length..];
                if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
                    continue;
                // A bare SKILL.md upload becomes a one-skill plugin, which is how the
                // reference reads "a top-level SKILL.md that declares the components".
                if (layout.SkillOnly)
                    relative = "skills/" + name + "/" + relative;
                var destination = Path.GetFullPath(Path.Combine(target, relative));
                if (!destination.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
            new InstalledPluginsIndex(root).Set(name, new InstalledPluginRecord(null, null, DateTimeOffset.Now, zipPath));
            return new PluginUploadOutcome(PluginUploadOutcome.Ok, name, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new PluginUploadOutcome(PluginUploadOutcome.Invalid, name, ex.Message);
        }
    }

    internal static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

/// <summary>The "Show" filter of the Plugins list: all, enabled or disabled.</summary>
public enum PluginShow
{
    All,
    Enabled,
    Disabled,
}

/// <summary>The sort orders the Plugins list offers.</summary>
public enum PluginSort
{
    Updated,
    Name,
    MostUsedByMe,
}

/// <summary>One row of the Plugins list.</summary>
public sealed record PluginRow(
    PluginInfo Plugin,
    PluginManifest? Manifest,
    DateTimeOffset? UpdatedAt,
    bool Enabled,
    IReadOnlyList<string> SkillNames,
    int? OwnUses90d,
    SkillCreator CreatedBy,
    InstalledPluginRecord? Installed)
{
    public string Name => Plugin.Name;

    public string DisplayName => Manifest?.Name is { Length: > 0 } declared ? declared : Plugin.Name;

    public string? Author => Manifest?.Author;

    public string? Description => Manifest?.Description;

    /// <summary>A plugin another user shared; nothing here is shared, so never.</summary>
    public bool IsShared => false;

    /// <summary>The scope suffix the reference prints after a project plugin's name.</summary>
    public string? Suffix => InstallScopes.ScopeCell(Plugin.Scope);
}

/// <summary>The Plugins list split three ways: attention, shared, main.</summary>
public sealed record PluginSections(
    IReadOnlyList<PluginRow> Attention,
    IReadOnlyList<PluginRow> Shared,
    IReadOnlyList<PluginRow> Main);

/// <summary>The rules of the reference's tabbed Plugins list (c76f00e40 <c>Ls</c>).</summary>
public static class PluginListPresentation
{
    /// <summary>Who a plugin is credited to: a local upload is the user's, an Anthropic marketplace's Anthropic's.</summary>
    public static SkillCreator CreatorOf(InstalledPluginRecord? installed)
    {
        if (installed?.Marketplace is not { Length: > 0 } marketplace)
            return SkillCreator.You;
        return marketplace.Contains("anthropic", StringComparison.OrdinalIgnoreCase)
            ? SkillCreator.Anthropic
            : SkillCreator.Org;
    }

    public static PluginRow ToRow(
        PluginInfo plugin, IReadOnlyList<SkillDefinition> skills, UiSettings ui,
        IReadOnlyDictionary<string, InstalledPluginRecord> installed, DateTimeOffset now)
    {
        var own = skills
            .Where(s => s.Name.StartsWith(plugin.Name + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var uses = own
            .Select(s => ui.SkillUsage.TryGetValue(s.Name, out var entry)
                ? SkillListPresentation.OwnUses(entry, now)
                : null)
            .Where(u => u is not null)
            .Sum(u => u!.Value);
        installed.TryGetValue(plugin.Name, out var record);
        return new PluginRow(
            plugin,
            PluginManifests.Read(plugin.Directory),
            PluginLibrary.UpdatedAt(plugin.Directory),
            PluginLibrary.IsEnabled(ui, plugin),
            [.. own.Select(s => s.Name[(plugin.Name.Length + 1)..])],
            uses > 0 ? uses : null,
            CreatorOf(record),
            record);
    }

    /// <summary>The reference's ma: the Show filter.</summary>
    public static IReadOnlyList<PluginRow> Show(IEnumerable<PluginRow> rows, PluginShow show) => show switch
    {
        PluginShow.Enabled => [.. rows.Where(r => r.Enabled)],
        PluginShow.Disabled => [.. rows.Where(r => !r.Enabled)],
        _ => [.. rows],
    };

    /// <summary>The reference's section split: attention first, then shared, then the rest.</summary>
    public static PluginSections Split(IEnumerable<PluginRow> rows, Func<PluginRow, bool>? needsAttention = null)
    {
        var attention = new List<PluginRow>();
        var shared = new List<PluginRow>();
        var main = new List<PluginRow>();
        foreach (var row in rows)
        {
            if (needsAttention is not null && needsAttention(row))
                attention.Add(row);
            else if (row.IsShared)
                shared.Add(row);
            else
                main.Add(row);
        }
        return new PluginSections(attention, shared, main);
    }

    public static PluginSort EffectiveSort(PluginSort requested, bool haveOwnUses) =>
        requested == PluginSort.MostUsedByMe && !haveOwnUses ? PluginSort.Updated : requested;

    public static IReadOnlyList<(PluginSort Value, string Label)> SortOptions(bool haveOwnUses)
    {
        var options = new List<(PluginSort, string)>
        {
            (PluginSort.Updated, "Last edited"),
            (PluginSort.Name, "Name"),
        };
        if (haveOwnUses)
            options.Add((PluginSort.MostUsedByMe, "Most used by me"));
        return options;
    }

    /// <summary>The reference's ns: name, or usage then date, or date then name.</summary>
    public static int Compare(PluginSort sort, PluginRow a, PluginRow b)
    {
        switch (sort)
        {
            case PluginSort.Name:
                return Names(a, b);
            case PluginSort.MostUsedByMe:
                var uses = (b.OwnUses90d ?? -1) - (a.OwnUses90d ?? -1);
                return uses != 0 ? uses : ByUpdated(a, b);
            default:
                return ByUpdated(a, b);
        }
    }

    private static int ByUpdated(PluginRow a, PluginRow b)
    {
        if (a.UpdatedAt == b.UpdatedAt)
            return Names(a, b);
        if (a.UpdatedAt is null)
            return 1;
        if (b.UpdatedAt is null)
            return -1;
        return b.UpdatedAt.Value.CompareTo(a.UpdatedAt.Value);
    }

    private static int Names(PluginRow a, PluginRow b) =>
        string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>A plugin row matches on its name, display name and scope suffix.</summary>
    public static IReadOnlyList<PluginRow> Search(IEnumerable<PluginRow> rows, string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0)
            return [.. rows];
        return [.. rows.Where(r =>
            r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            (r.Suffix?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))];
    }

    /// <summary>"{n} skill(s)" — what a row says under its name when the plugin has no description.</summary>
    public static string SkillCountLabel(int count) => count == 1 ? "1 skill" : $"{count} skills";

    /// <summary>The "Show" menu's rows.</summary>
    public static IReadOnlyList<(PluginShow Value, string Label)> ShowOptions =>
    [
        (PluginShow.All, "All plugins"),
        (PluginShow.Enabled, "Enabled"),
        (PluginShow.Disabled, "Disabled"),
    ];
}

/// <summary>The marketplace-side facts the plugin cards read: sync state, versions, updates.</summary>
public static class MarketplaceSync
{
    /// <summary>What a refresh answered, in the reference's own sentences.</summary>
    public sealed record RefreshOutcome(bool Ok, string Message);

    /// <summary>The reference's four sync-failure reasons.</summary>
    public const string AccessFailed =
        "Repository access failed. The repository may have been made private or is no longer accessible.";

    public const string TooLarge = "This marketplace exceeds size or plugin count limits.";

    public const string ValidationErrors = "Some plugins in this marketplace have validation errors.";

    public const string Temporary = "A temporary error occurred during sync. You can retry.";

    /// <summary>
    /// Pulls the marketplace and says what happened, in the reference's words:
    /// "Already up to date.", "Updated to {sha}." or "Failed to update marketplace."
    /// </summary>
    public static RefreshOutcome Refresh(MarketplaceInfo marketplace)
    {
        var before = SyncedCommit(marketplace.Directory);
        var (exit, _) = PluginMarketplaces.RunGit(marketplace.Directory, "pull --ff-only");
        if (exit != 0)
            return new RefreshOutcome(false, "Failed to update marketplace.");
        var after = SyncedCommit(marketplace.Directory);
        return string.Equals(before, after, StringComparison.Ordinal)
            ? new RefreshOutcome(true, "Already up to date.")
            : new RefreshOutcome(true, $"Updated to {after}.");
    }

    /// <summary>The short SHA the checkout is at.</summary>
    public static string? SyncedCommit(string directory)
    {
        var (exit, output) = PluginMarketplaces.RunGit(directory, "rev-parse --short HEAD");
        return exit == 0 && output.Trim().Length > 0 ? output.Trim() : null;
    }

    /// <summary>When the checkout's HEAD commit was made.</summary>
    public static DateTimeOffset? LastUpdated(string directory)
    {
        var (exit, output) = PluginMarketplaces.RunGit(directory, "log -1 --format=%cI");
        return exit == 0 && DateTimeOffset.TryParse(output.Trim(), out var at) ? at : null;
    }

    /// <summary>
    /// Which of the reference's four sync-failure reasons git's output names: a
    /// repository that cannot be reached is an access failure, a checkout past the
    /// limits a size failure, a manifest that does not parse a validation failure,
    /// and anything else temporary.
    /// </summary>
    public static string FailureReason(string gitOutput)
    {
        var lower = gitOutput.ToLowerInvariant();
        if (lower.Contains("repository not found") || lower.Contains("could not read from remote") ||
            lower.Contains("authentication failed") || lower.Contains("permission denied") || lower.Contains("403"))
            return AccessFailed;
        if (lower.Contains("too large") || lower.Contains("exceeds") || lower.Contains("size limit"))
            return TooLarge;
        if (lower.Contains("validation") || lower.Contains("invalid manifest") || lower.Contains("json"))
            return ValidationErrors;
        return Temporary;
    }

    /// <summary>The version a marketplace plugin's manifest declares.</summary>
    public static string? Version(MarketplacePlugin plugin) => PluginManifests.Read(plugin.Directory)?.Version;

    /// <summary>
    /// The update a plugin could take: the marketplace's version when it differs
    /// from the installed record's. A plugin with no record or no version has
    /// none, which the button reads as "On latest version".
    /// </summary>
    public static (bool Available, string? Version) UpdateFor(InstalledPluginRecord? installed, MarketplacePlugin? offered)
    {
        if (installed is null || offered is null)
            return (false, null);
        var latest = Version(offered);
        if (latest is null || string.Equals(latest, installed.Version, StringComparison.OrdinalIgnoreCase))
            return (false, latest);
        return (true, latest);
    }
}
