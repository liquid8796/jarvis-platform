using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>
/// The plugins Claude Code has installed, read live — the same compatibility this
/// app already extends to <c>~/.claude/skills</c>, <c>.claude/commands</c>,
/// <c>.claude/launch.json</c> and <c>CLAUDE.md</c>. Without it a user whose skills
/// arrive through a plugin (its marketplaces are where most third-party skills
/// live) sees them in Claude Code and not here, for no reason they could act on.
///
/// The layout is not this app's, so the folders cannot simply be listed:
/// <c>~/.claude/plugins</c> holds <c>cache/</c>, <c>marketplaces/</c> and an
/// <c>installed_plugins.json</c> whose entries carry the real
/// <c>installPath</c> — <c>cache/{marketplace}/{plugin}/{version}</c>. The plugin's
/// name is the part of its key before the <c>@</c>, and whether it is switched on
/// is Claude Code's own <c>settings.json</c> <c>enabledPlugins</c> map.
///
/// Read-only: this app installs, updates and removes plugins under its own roots.
/// </summary>
public static class ClaudeCodePlugins
{
    /// <summary>The scope these are stamped with, so a row can say where it came from.</summary>
    public const string Scope = "claude-code";

    public static string PluginsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plugins");

    private static string SettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static string InstalledFile => Path.Combine(PluginsDirectory, "installed_plugins.json");

    /// <summary>
    /// The plugin name and directory of every entry Claude Code has installed and
    /// left enabled. A key with no <c>@</c>, a missing install path and a directory
    /// that is no longer on disk are each skipped rather than surfaced as a plugin
    /// that would contribute nothing.
    /// </summary>
    public static IReadOnlyList<(string Name, string Directory)> Installed() =>
        Installed(InstalledFile, SettingsFile);

    /// <summary>The same read against explicit files, which is what the tests drive.</summary>
    internal static IReadOnlyList<(string Name, string Directory)> Installed(
        string installedFile, string settingsFile)
    {
        if (ReadJson(installedFile) is not JsonObject root || root["plugins"] is not JsonObject entries)
            return [];

        var enabled = EnabledKeys(settingsFile);
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            // "watch@claude-video" — the marketplace rides the key, the name leads it.
            var at = key.IndexOf('@');
            var name = (at >= 0 ? key[..at] : key).Trim();
            if (name.Length == 0 || !enabled(key))
                continue;

            // One key can carry several scope entries; the first that is on disk wins,
            // the way a user-level install wins over a project one here.
            foreach (var install in value as JsonArray ?? [])
            {
                if (install is not JsonObject record)
                    continue;
                // Read as a value rather than asserted to be one: GetValue<string> on a
                // number throws, and this file is not this app's to trust.
                if (record["installPath"] is not JsonValue node
                    || !node.TryGetValue<string>(out var path)
                    || string.IsNullOrWhiteSpace(path)
                    || !Directory.Exists(path))
                {
                    continue;
                }
                if (seen.Add(name))
                    found.Add((name, path));
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Everything those plugins contribute, stamped with this scope so the Plugins
    /// page can name where a row came from and this app's own disable switch can
    /// still turn one off.
    /// </summary>
    public static Plugins.PluginContent Load(Func<PluginInfo, bool>? include = null) =>
        Environment.GetEnvironmentVariable("JARVIS_PARITY_ISOLATED") == "1"
            ? Plugins.PluginContent.Empty
            : Plugins.LoadDirectories(Installed(), Scope, PluginsDirectory, include);

    /// <summary>
    /// Claude Code's <c>enabledPlugins</c> map. A plugin missing from it is treated as
    /// enabled, which is how a freshly installed one behaves there; only an explicit
    /// <c>false</c> switches one off.
    /// </summary>
    private static Func<string, bool> EnabledKeys(string settingsFile)
    {
        if (ReadJson(settingsFile) is not JsonObject settings || settings["enabledPlugins"] is not JsonObject map)
            return static _ => true;

        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in map)
        {
            if (value is JsonValue flag && flag.TryGetValue<bool>(out var on) && !on)
                off.Add(key);
        }

        return key => !off.Contains(key);
    }

    private static JsonNode? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A file this app does not own being unreadable is not this app's error.
            return null;
        }
    }
}
