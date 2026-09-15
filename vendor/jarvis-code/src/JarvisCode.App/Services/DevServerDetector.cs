using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>
/// The "Detect dev server" scan behind the Browser pane's empty state. The
/// reference asks Claude to read the project; ours is a deliberate local port —
/// a deterministic scan of the usual markers (package.json scripts + framework
/// deps, dotnet launchSettings, Django/Flask), instant and offline, feeding the
/// same "Dev server detected → Use this → saved to launch.json" flow. Pure
/// functions over file contents so it unit-tests without a project.
/// </summary>
public static class DevServerDetector
{
    /// <summary>Framework → default dev port, matched against package.json dependencies.</summary>
    private static readonly (string Dependency, int Port)[] FrameworkPorts =
    [
        ("next", 3000),
        ("react-scripts", 3000),
        ("@remix-run", 3000),
        ("nuxt", 3000),
        ("gatsby", 8000),
        ("@angular/cli", 4200),
        ("astro", 4321),
        ("@sveltejs/kit", 5173),
        ("vite", 5173),
    ];

    public static LaunchConfiguration? Scan(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var packageJson = Path.Combine(workingDirectory, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                if (FromPackageJson(File.ReadAllText(packageJson), PickNodeRunner(workingDirectory)) is { } node)
                {
                    return node;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // fall through to the other detectors
            }
        }

        if (File.Exists(Path.Combine(workingDirectory, "manage.py")))
        {
            return new LaunchConfiguration("django", "python", ["manage.py", "runserver"], 8000, null);
        }

        foreach (var project in SafeEnumerate(workingDirectory, "*.csproj"))
        {
            var launchSettings = Path.Combine(Path.GetDirectoryName(project)!, "Properties", "launchSettings.json");
            var port = File.Exists(launchSettings) ? PortFromLaunchSettings(ReadOrEmpty(launchSettings)) : null;
            if (port is { } found)
            {
                return new LaunchConfiguration(
                    Path.GetFileNameWithoutExtension(project), "dotnet",
                    ["run", "--project", Path.GetFileName(project)], found, null);
            }
        }

        return null;
    }

    /// <summary>pnpm/yarn/bun by lockfile; npm otherwise.</summary>
    public static string PickNodeRunner(string workingDirectory)
    {
        if (File.Exists(Path.Combine(workingDirectory, "pnpm-lock.yaml")))
        {
            return "pnpm";
        }

        if (File.Exists(Path.Combine(workingDirectory, "yarn.lock")))
        {
            return "yarn";
        }

        if (File.Exists(Path.Combine(workingDirectory, "bun.lockb")) ||
            File.Exists(Path.Combine(workingDirectory, "bun.lock")))
        {
            return "bun";
        }

        return "npm";
    }

    /// <summary>Reads package.json: a dev/start/serve script + a port from the framework or the script text.</summary>
    public static LaunchConfiguration? FromPackageJson(string json, string runner)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["scripts"] is not JsonObject scripts)
        {
            return null;
        }

        var script = new[] { "dev", "start", "serve" }
            .FirstOrDefault(name => scripts[name] is JsonValue);
        if (script is null)
        {
            return null;
        }

        var command = scripts[script]!.GetValue<string>();
        var port = PortFromScript(command) ?? PortFromDependencies(root) ?? 3000;
        return new LaunchConfiguration(script, runner, ["run", script], port, null);
    }

    /// <summary>--port 4000 / --port=4000 / -p 4000 in the script text.</summary>
    public static int? PortFromScript(string script)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            script, @"(?:--port[= ]|-p )(\d{2,5})");
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
    }

    private static int? PortFromDependencies(JsonObject packageJson)
    {
        var names = new[] { "dependencies", "devDependencies" }
            .Select(key => packageJson[key] as JsonObject)
            .Where(static o => o is not null)
            .SelectMany(static o => o!.Select(static entry => entry.Key))
            .ToList();
        foreach (var (dependency, port) in FrameworkPorts)
        {
            if (names.Any(n => n == dependency || n.StartsWith(dependency, StringComparison.Ordinal)))
            {
                return port;
            }
        }

        return null;
    }

    /// <summary>First applicationUrl port in a dotnet launchSettings.json.</summary>
    public static int? PortFromLaunchSettings(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["profiles"] is not JsonObject profiles)
            {
                return null;
            }

            foreach (var profile in profiles.Select(static p => p.Value).OfType<JsonObject>())
            {
                var urls = profile["applicationUrl"]?.GetValue<string>() ?? "";
                foreach (var url in urls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "http")
                    {
                        return uri.Port;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>Writes the detected configuration to .jarvis/launch.json (creating the folder).</summary>
    public static void WriteLaunchFile(string workingDirectory, LaunchConfiguration configuration)
    {
        var directory = Path.Combine(workingDirectory, ".jarvis");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "launch.json"), BuildLaunchJson(configuration));
    }

    public static string BuildLaunchJson(LaunchConfiguration configuration)
    {
        var entry = new JsonObject
        {
            ["name"] = configuration.Name,
            ["runtimeExecutable"] = configuration.RuntimeExecutable,
            ["runtimeArgs"] = new JsonArray([.. configuration.RuntimeArgs.Select(static a => (JsonNode)a)]),
            ["port"] = configuration.Port,
        };
        if (configuration.Url is not null)
        {
            entry["url"] = configuration.Url;
        }

        var root = new JsonObject
        {
            ["version"] = "0.0.1",
            ["configurations"] = new JsonArray(entry),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<string> SafeEnumerate(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
