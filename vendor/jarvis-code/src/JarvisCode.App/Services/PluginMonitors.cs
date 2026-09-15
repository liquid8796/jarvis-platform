using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace JarvisCode.App.Services;

/// <summary>
/// One monitor a plugin declares. The shape is the reference's own zod schema
/// (CLI 2.1.257 at byte offset ~181,975,000: <c>name</c>, <c>command</c>,
/// <c>description</c> and <c>when</c>), whose <c>when</c> is either
/// <c>always</c> or <c>on-skill-invoke:&lt;skill&gt;</c>.
/// </summary>
public sealed record PluginMonitor(
    string PluginName,
    string Name,
    string Command,
    string Description,
    string When)
{
    /// <summary>The reference's default arm trigger.</summary>
    public const string Always = "always";

    /// <summary>Its other trigger, which arms the first time a named skill is dispatched.</summary>
    public const string OnSkillInvokePrefix = "on-skill-invoke:";

    /// <summary>The skill this monitor waits for, or null when it arms at session start.</summary>
    public string? SkillName =>
        When.StartsWith(OnSkillInvokePrefix, StringComparison.Ordinal)
            ? When[OnSkillInvokePrefix.Length..]
            : null;

    /// <summary>The dedupe key: a monitor's name is unique inside its plugin.</summary>
    public string Key => $"{PluginName}:{Name}";
}

/// <summary>
/// Plugin monitors — the reference's "Background watch scripts the host arms as
/// persistent Monitor tasks (unsandboxed, same trust tier as hooks) so plugins need
/// not instruct the model to arm them". A plugin declares them in
/// <c>plugin.json</c>'s <c>monitors</c> field, which is either the monitors array
/// itself or a path to a JSON file holding it; with the field absent,
/// <c>monitors/monitors.json</c> at the plugin root is loaded if present.
///
/// The parsing and the substitution are pure so they are unit-tested;
/// <see cref="PluginMonitorRunner"/> is the process half.
/// </summary>
public static class PluginMonitors
{
    /// <summary>The file the reference loads when the manifest declares no monitors.</summary>
    public static readonly string DefaultRelativePath = Path.Combine("monitors", "monitors.json");

    /// <summary>The reference's own refusal when a monitor row carries no command.</summary>
    public const string NoCommand = "No command is declared for this monitor.";

    /// <summary>The detail page's two field labels.</summary>
    public const string RunsLabel = "Runs";

    public const string ShellCommandLabel = "Shell command";

    /// <summary>The reference's Contents heading for the section.</summary>
    public const string SectionHeading = "Monitors";

    /// <summary>The "Runs" value: the arm trigger, worded the way the reference words it.</summary>
    public static string RunsValue(PluginMonitor monitor) =>
        monitor.SkillName is { Length: > 0 } skill ? $"{PluginMonitor.OnSkillInvokePrefix}{skill}" : PluginMonitor.Always;

    /// <summary>
    /// The monitors a plugin folder declares. A row without a name or a command is
    /// dropped, and — as the reference's schema requires — a name may appear only
    /// once per plugin, the first winning.
    /// </summary>
    public static IReadOnlyList<PluginMonitor> Load(string pluginDirectory)
    {
        var pluginName = Path.GetFileName(pluginDirectory.TrimEnd('\\', '/')).ToLowerInvariant();
        var json = ReadDeclaration(pluginDirectory);
        return json is null ? [] : Parse(pluginName, json);
    }

    /// <summary>
    /// The JSON array the plugin declares: the manifest's <c>monitors</c> field when
    /// it is one, the file it names when it is a path, and otherwise the default file.
    /// </summary>
    public static string? ReadDeclaration(string pluginDirectory)
    {
        var manifest = Path.Combine(pluginDirectory, "plugin.json");
        try
        {
            if (File.Exists(manifest))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("monitors", out var monitors))
                {
                    if (monitors.ValueKind == JsonValueKind.Array)
                    {
                        return monitors.GetRawText();
                    }

                    if (monitors.ValueKind == JsonValueKind.String &&
                        monitors.GetString() is { Length: > 0 } relative)
                    {
                        var named = Path.Combine(pluginDirectory, relative);
                        return File.Exists(named) ? File.ReadAllText(named) : null;
                    }
                }
            }

            var fallback = Path.Combine(pluginDirectory, DefaultRelativePath);
            return File.Exists(fallback) ? File.ReadAllText(fallback) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads one monitors array; separated out so it is unit-testable.</summary>
    public static IReadOnlyList<PluginMonitor> Parse(string pluginName, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var monitors = new List<PluginMonitor>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = Text(element, "name");
                var command = Text(element, "command");
                if (name.Length == 0 || command.Length == 0 || !seen.Add(name))
                {
                    continue;
                }

                var when = Text(element, "when");
                monitors.Add(new PluginMonitor(
                    pluginName,
                    name,
                    command,
                    Text(element, "description"),
                    IsValidWhen(when) ? when : PluginMonitor.Always));
            }

            return monitors;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The reference's <c>when</c> union: <c>always</c>, or
    /// <c>on-skill-invoke:</c> with a skill name after it — its refinement requires
    /// the whole string to be longer than the prefix.
    /// </summary>
    public static bool IsValidWhen(string when) =>
        when == PluginMonitor.Always ||
        (when.StartsWith(PluginMonitor.OnSkillInvokePrefix, StringComparison.Ordinal) &&
         when.Length > PluginMonitor.OnSkillInvokePrefix.Length);

    /// <summary>
    /// The monitors every enabled plugin declares, with the folder each was loaded
    /// from — which is what <c>${CLAUDE_PLUGIN_ROOT}</c> expands to.
    /// </summary>
    public static IReadOnlyList<(PluginMonitor Monitor, string Root)> Discover(
        IEnumerable<string> pluginDirectories) =>
        [.. pluginDirectories.SelectMany(d => Load(d).Select(m => (Monitor: m, Root: d)))];

    /// <summary>
    /// Which of them arm now: everything whose trigger is <c>always</c> at session
    /// start, and the ones waiting on <paramref name="invokedSkill"/> once it is
    /// dispatched.
    /// </summary>
    public static IReadOnlyList<(PluginMonitor Monitor, string Root)> ArmedBy(
        IEnumerable<(PluginMonitor Monitor, string Root)> discovered, string? invokedSkill) =>
        [.. discovered.Where(entry => invokedSkill is null
            ? entry.Monitor.SkillName is null
            : string.Equals(entry.Monitor.SkillName, invokedSkill, StringComparison.OrdinalIgnoreCase))];

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    /// The reference's command substitution: <c>${CLAUDE_PLUGIN_ROOT}</c>,
    /// <c>${CLAUDE_PLUGIN_DATA}</c>, <c>${CLAUDE_PROJECT_DIR}</c> and any
    /// <c>${ENV_VAR}</c>. An unset name expands to nothing rather than being left
    /// in the command.
    /// </summary>
    public static string Expand(
        string command, string pluginRoot, string pluginData, string projectDir,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLAUDE_PLUGIN_ROOT"] = pluginRoot,
            ["CLAUDE_PLUGIN_DATA"] = pluginData,
            ["CLAUDE_PROJECT_DIR"] = projectDir,
        };

        return System.Text.RegularExpressions.Regex.Replace(
            command,
            @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
            match =>
            {
                var name = match.Groups[1].Value;
                return known.TryGetValue(name, out var value) ? value : environment(name) ?? "";
            });
    }
}

/// <summary>
/// Arms a session's plugin monitors and turns each stdout line into a task
/// notification, which is what the reference's host does with them: "Each stdout
/// line is delivered to the model as a &lt;task_notification&gt; event; the process
/// runs for the session lifetime."
/// </summary>
public sealed class PluginMonitorRunner : IDisposable
{
    private readonly Dictionary<string, Process> _running = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Called with the monitor, the session it was armed for, and one stdout line.</summary>
    public Action<PluginMonitor, string, string>? LineReceived { get; set; }

    /// <summary>The monitors this runner has armed, by their dedupe key.</summary>
    public IReadOnlyCollection<string> Armed
    {
        get
        {
            lock (_lock)
            {
                return [.. _running.Keys];
            }
        }
    }

    /// <summary>
    /// Arms one monitor unless it is already running — the reference dedupes by the
    /// monitor's name so a plugin reload or a repeat skill invoke does not spawn a
    /// second process. Returns true when a process was started.
    /// </summary>
    public bool Arm(
        PluginMonitor monitor, string pluginRoot, string pluginData, string projectDir, string sessionId)
    {
        lock (_lock)
        {
            if (_running.ContainsKey(monitor.Key))
            {
                return false;
            }
        }

        var command = PluginMonitors.Expand(monitor.Command, pluginRoot, pluginData, projectDir);
        var startInfo = new ProcessStartInfo
        {
            // The reference runs a monitor in the session cwd.
            WorkingDirectory = Directory.Exists(projectDir) ? projectDir : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } line)
                {
                    LineReceived?.Invoke(monitor, sessionId, line);
                }
            };
            process.Exited += (_, _) =>
            {
                lock (_lock)
                {
                    _running.Remove(monitor.Key);
                }
            };
            if (!process.Start())
            {
                return false;
            }

            process.BeginOutputReadLine();
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }

        lock (_lock)
        {
            _running[monitor.Key] = process;
        }

        return true;
    }

    public void Dispose()
    {
        List<Process> processes;
        lock (_lock)
        {
            processes = [.. _running.Values];
            _running.Clear();
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                           or System.ComponentModel.Win32Exception)
            {
                // The process is already gone.
            }

            process.Dispose();
        }
    }
}
