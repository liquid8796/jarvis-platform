using System.IO;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

public sealed record MarketplaceInfo(string Name, string Directory, string? Url);

public sealed record MarketplacePlugin(string Name, string? Description, string Directory);

/// <summary>
/// Git-repo plugin marketplaces, the way the reference adds them: a repository is
/// cloned under the app data folder, its plugins are listed from
/// .claude-plugin/marketplace.json when present (each entry pointing at a source
/// folder) or by scanning subfolders that look like plugins, and installing
/// copies the plugin folder into the plugins directory.
/// </summary>
public static class PluginMarketplaces
{
    public static string RootFor(string appRoot) => Path.Combine(appRoot, "marketplaces");

    /// <summary>Clones (or pulls) the repository; returns the marketplace folder or an error.</summary>
    public static (MarketplaceInfo? Marketplace, string? Error) Add(string appRoot, string gitUrl)
    {
        gitUrl = gitUrl.Trim();
        if (gitUrl.Length == 0)
        {
            return (null, "Enter a git URL or owner/repo.");
        }

        // "owner/repo" is GitHub shorthand, like the reference accepts.
        if (!gitUrl.Contains("://") && !gitUrl.StartsWith("git@", StringComparison.Ordinal) &&
            gitUrl.Count(static c => c == '/') == 1)
        {
            gitUrl = $"https://github.com/{gitUrl}.git";
        }

        var name = gitUrl.TrimEnd('/', '\\').Split('/', '\\')[^1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return (null, "That URL has no usable repository name.");
        }

        var root = RootFor(appRoot);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, name);
        var (exit, output) = Directory.Exists(target)
            ? RunGit(target, "pull --ff-only")
            : RunGit(root, $"clone --depth 1 \"{gitUrl}\" \"{target}\"");
        if (exit != 0)
        {
            return (null, output.Trim().Length > 0 ? output.Trim() : "git failed.");
        }

        return (new MarketplaceInfo(name, target, gitUrl), null);
    }

    public static void Remove(string appRoot, string name)
    {
        var target = Path.Combine(RootFor(appRoot), name);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }

    public static IReadOnlyList<MarketplaceInfo> List(string appRoot)
    {
        var root = RootFor(appRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.GetDirectories(root)
            .Select(d => new MarketplaceInfo(Path.GetFileName(d), d, ReadOrigin(d)))
            .OrderBy(static m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The plugins a marketplace offers.</summary>
    public static IReadOnlyList<MarketplacePlugin> Plugins(MarketplaceInfo marketplace)
    {
        var plugins = new List<MarketplacePlugin>();

        // The Claude Code marketplace manifest, when the repo ships one.
        var manifest = Path.Combine(marketplace.Directory, ".claude-plugin", "marketplace.json");
        if (File.Exists(manifest))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(manifest)) is JsonObject root &&
                    root["plugins"] is JsonArray entries)
                {
                    foreach (var entry in entries.OfType<JsonObject>())
                    {
                        var name = entry["name"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        var source = entry["source"] switch
                        {
                            JsonValue value when value.TryGetValue<string>(out var path) => path,
                            JsonObject o => o["source"]?.GetValue<string>() ?? o["path"]?.GetValue<string>(),
                            _ => null,
                        } ?? ".";
                        var directory = Path.GetFullPath(Path.Combine(marketplace.Directory, source.TrimStart('.', '/')));
                        if (Directory.Exists(directory))
                        {
                            plugins.Add(new MarketplacePlugin(name, entry["description"]?.GetValue<string>(), directory));
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }

            if (plugins.Count > 0)
            {
                return plugins;
            }
        }

        // No manifest: the repo root, or each subfolder, that looks like a plugin.
        if (LooksLikePlugin(marketplace.Directory))
        {
            plugins.Add(new MarketplacePlugin(marketplace.Name, null, marketplace.Directory));
        }
        else
        {
            foreach (var dir in Directory.GetDirectories(marketplace.Directory))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith('.') && LooksLikePlugin(dir))
                {
                    plugins.Add(new MarketplacePlugin(name, null, dir));
                }
            }
        }

        return [.. plugins.OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool LooksLikePlugin(string directory) =>
        File.Exists(Path.Combine(directory, ".claude-plugin", "plugin.json")) ||
        File.Exists(Path.Combine(directory, "plugin.json")) ||
        Directory.Exists(Path.Combine(directory, "skills")) ||
        Directory.Exists(Path.Combine(directory, "commands")) ||
        Directory.Exists(Path.Combine(directory, "agents"));

    private static string? ReadOrigin(string directory)
    {
        var (exit, output) = RunGit(directory, "remote get-url origin");
        return exit == 0 ? output.Trim() : null;
    }

    internal static (int ExitCode, string Output) RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return (-1, "git is not available");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);
            return (process.ExitCode, stdout.Length > 0 ? stdout : stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, ex.Message);
        }
    }
}
