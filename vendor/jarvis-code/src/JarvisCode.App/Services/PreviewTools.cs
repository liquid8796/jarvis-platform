using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using JarvisCode.Core.BackgroundTasks;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.Services;

/// <summary>One entry from launch.json: how to start (or attach to) a dev server.</summary>
public sealed record LaunchConfiguration(
    string Name, string? RuntimeExecutable, IReadOnlyList<string> RuntimeArgs, int Port, string? Url)
{
    /// <summary>
    /// The reference's tri-state <c>autoPort</c>: <c>true</c> takes a fresh OS-assigned
    /// port when the configured one is busy, <c>false</c> says the port is required, and
    /// an absent field asks the user which of those they meant. Absent is not the same
    /// answer as <c>false</c>, which is why this is nullable.
    /// </summary>
    public bool? AutoPort { get; init; }

    /// <summary>The entry's own <c>env</c>, which the started server inherits.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The entry's <c>program</c> and <c>args</c>. Neither is launched here - a
    /// server is started from runtimeExecutable and runtimeArgs - but both are
    /// read, because the reference searches all four for a port.
    /// </summary>
    public string? Program { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public string PreviewUrl => Url ?? $"http://localhost:{Port}";

    public bool AttachOnly => string.IsNullOrEmpty(RuntimeExecutable);

    public string CommandLine =>
        RuntimeArgs.Count == 0 ? RuntimeExecutable ?? "" : $"{RuntimeExecutable} {string.Join(' ', RuntimeArgs)}";

    /// <summary>
    /// The same entry moved onto the port that was actually secured. A localhost
    /// <c>url</c> moves with it — the reference requires such a url to point at the
    /// entry's own server, so leaving it behind would send the panel to the old port.
    /// </summary>
    public LaunchConfiguration OnPort(int port)
    {
        if (port == Port)
            return this;

        var url = Url;
        if (url is not null
            && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && IsLoopbackHost(parsed.Host))
        {
            url = new UriBuilder(parsed) { Port = port }.Uri.ToString();
        }

        return this with { Port = port, Url = url };
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}

/// <summary>
/// One dev server as the Background tasks panel shows it. <see cref="Status"/>
/// follows the reference's starting → running → error progression; ours reads it
/// off the backing process (a server that has printed nothing yet is still
/// starting) because this app has no separate readiness probe.
/// </summary>
public sealed record PreviewServerInfo(
    string ServerId,
    string Name,
    string Url,
    int? Port,
    PreviewServerStatus Status,
    DateTimeOffset StartedAt);

public enum PreviewServerStatus
{
    Starting,
    Running,
    Error,
    Stopped,
}

/// <summary>
/// The reference app's dev-server preview: preview_start launches a server from
/// launch.json (or opens a URL), the browser panel shows it, preview_logs reads
/// its output, preview_stop kills it, preview_list enumerates. Servers run as
/// background tasks; .jarvis/launch.json is read first, .claude/launch.json as
/// the compatibility fallback.
/// </summary>
public sealed partial class PreviewServers(BackgroundTaskManager tasks, Action<string> openInBrowser)
{
    private sealed record Server(
        string ServerId, string Name, string? TaskId, string Url, DateTimeOffset StartedAt, string? SessionId);

    private readonly List<Server> _servers = [];
    private readonly object _lock = new();
    private int _nextId;

    public static string? FindLaunchFile(string workingDirectory)
    {
        foreach (var candidate in new[] { ".jarvis", ".claude" })
        {
            var path = Path.Combine(workingDirectory, candidate, "launch.json");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public static IReadOnlyList<LaunchConfiguration> ParseLaunchFile(string json)
    {
        var configurations = new List<LaunchConfiguration>();
        if (JsonNode.Parse(json) is not JsonObject root || root["configurations"] is not JsonArray entries)
            return configurations;

        foreach (var entry in entries.OfType<JsonObject>())
        {
            var name = JsonArgs.GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var args = Strings(entry, "runtimeArgs");
            var extra = Strings(entry, "args");
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entry["env"] is JsonObject declared)
            {
                foreach (var (key, value) in declared)
                {
                    if (key.Length > 0 && value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                        env[key] = text;
                }
            }

            configurations.Add(new LaunchConfiguration(
                name,
                JsonArgs.GetString(entry, "runtimeExecutable"),
                args,
                JsonArgs.GetInt(entry, "port") ?? 0,
                JsonArgs.GetString(entry, "url"))
            {
                Program = JsonArgs.GetString(entry, "program"),
                Args = extra,
                // Absent stays absent: the reference's three arms are true, false and
                // "the user has not said", and folding the last onto false would answer
                // a question nobody asked.
                AutoPort = entry["autoPort"] is JsonValue flag && flag.TryGetValue<bool>(out var auto)
                    ? auto
                    : null,
                Env = env,
            });
        }

        return [.. configurations.Select(ResolvePort)];
    }

    /// <summary>
    /// The reference's port chain: the entry's own <c>port</c>, then the port its
    /// <c>url</c> names, then whatever its command spells out, then 3000 for an
    /// entry that has a command at all. An entry with none of those keeps 0,
    /// which is what "attach to whatever the url says" means.
    /// </summary>
    private static LaunchConfiguration ResolvePort(LaunchConfiguration entry)
    {
        if (entry.Port > 0)
        {
            return entry;
        }

        if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var url) && !url.IsDefaultPort)
        {
            return entry with { Port = url.Port };
        }

        if (PortFromCommand(entry) is { } declared)
        {
            return entry with { Port = declared };
        }

        // buildCommand answers null only when neither runtimeExecutable nor
        // program is set; everything else is something the reference would run.
        var hasCommand = !string.IsNullOrEmpty(entry.RuntimeExecutable) ||
                         !string.IsNullOrEmpty(entry.Program);
        return hasCommand ? entry with { Port = 3000 } : entry;
    }

    /// <summary>
    /// The reference's <c>extractPortFromCommand</c>, measured on desktop
    /// 1.46388.2.0. <c>env.PORT</c> wins outright; then the tokens are read as a
    /// command line, where a <c>--port</c> or <c>-p</c> flag takes the next token
    /// or its own <c>=</c> suffix; then three patterns are each tried across
    /// every token before the next pattern is tried at all, which is the reason
    /// this is two passes rather than one.
    /// </summary>
    internal static int? PortFromCommand(LaunchConfiguration entry)
    {
        if (entry.Env.TryGetValue("PORT", out var declared) &&
            int.TryParse(declared, out var fromEnv))
        {
            return fromEnv;
        }

        List<string?> tokens = [entry.Program, entry.RuntimeExecutable, .. entry.RuntimeArgs, .. entry.Args];

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (token is "--port" or "-p" && next is not null && DigitsOnly().IsMatch(next))
            {
                return int.Parse(next, CultureInfo.InvariantCulture);
            }

            if (token is not null && PortFlagWithValue().Match(token) is { Success: true } attached)
            {
                return int.Parse(attached.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        foreach (var pattern in new[] { SpacedPortFlag(), LocalhostPort(), BarePort() })
        {
            foreach (var token in tokens)
            {
                if (token is not null && pattern.Match(token) is { Success: true } hit)
                {
                    return int.Parse(hit.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex(@"^(?:--port|-p)=(\d+)$")]
    private static partial Regex PortFlagWithValue();

    [GeneratedRegex(@"(?:--port|--PORT|-p)\s+(\d+)")]
    private static partial Regex SpacedPortFlag();

    [GeneratedRegex(@"localhost:(\d+)")]
    private static partial Regex LocalhostPort();

    [GeneratedRegex(@":(\d{4,5})(?:\s|$)")]
    private static partial Regex BarePort();

    /// <summary>Every string of a JSON array field, empties dropped.</summary>
    private static List<string> Strings(JsonObject entry, string field) =>
        (entry[field] as JsonArray)?
            .Select(a => a?.GetValue<string>())
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a => a!)
            .ToList() ?? [];

    /// <summary>
    /// The format block a launch.json diagnostic ends with, which is the same
    /// one preview_start's own doc carries.
    /// </summary>
    private static string LaunchJsonHelp =>
        BrowserPaneTools.LaunchJsonShape + "\n" + BrowserPaneTools.LaunchJsonFields;

    /// <summary>
    /// Why a launch.json could not be used, in the reference's own words. Each
    /// case is a different thing for the caller to do, which is why they are
    /// separate sentences rather than one "could not read it".
    /// </summary>
    internal static string NoLaunchFile(string path) =>
        $"No {path} found. Create {path} with this format:\n{LaunchJsonHelp} " +
        "Then call preview_start with the server name.";

    internal static string LaunchFileUnreadable(string path, string code, string message) =>
        $"Found {path} but reading it failed with {code}: {message}. The path exists — do not recreate it.";

    internal static string LaunchFileUnparsable(string path, string detail) =>
        $"Found {path} but it could not be parsed: {detail}. Fix the file to match this format:\n{LaunchJsonHelp}";

    internal static string LaunchFileEmpty(string path) =>
        $"Found {path} but it contains no configurations. Expected format:\n{LaunchJsonHelp}";

    internal static string NoMatchingServer(string path, IEnumerable<string> available) =>
        $"No matching server in {path}. Available servers: {string.Join(", ", available)}.";

    internal static string AmbiguousServers(IEnumerable<string> names) =>
        $"Multiple server configurations found: {string.Join(", ", names)}. Specify which server to start " +
        "by passing the name parameter (e.g., preview_start with name: \"frontend\" or name: \"backend\"). " +
        "To start all servers, call preview_start separately for each.";

    internal const string NoWorkingDirectory =
        "This session has no working directory, so dev-server configurations can't be resolved. Ask the user " +
        "to open a project folder, or pass a url to preview_start if browser preview is enabled.";

    internal static string ConfigurationHasNoCommand(string path) =>
        "The resolved launch configuration has no command. Check the configuration's \"runtimeExecutable\" " +
        $"or \"program\" field in {path}, then try again.";

    public ToolResult Start(string? name, string? url, string workingDirectory, string? sessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            var direct = Register(name ?? url, taskId: null, url, sessionId);
            openInBrowser(url);
            return ToolResult.Success($"Preview open at {url} (serverId {direct}).");
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return ToolResult.Error(NoWorkingDirectory);
        }

        var launchFile = FindLaunchFile(workingDirectory);
        if (launchFile is null)
        {
            return ToolResult.Error(NoLaunchFile(Path.Combine(workingDirectory, ".jarvis", "launch.json")));
        }

        string text;
        try
        {
            text = File.ReadAllText(launchFile);
        }
        catch (IOException ex)
        {
            return ToolResult.Error(LaunchFileUnreadable(launchFile, ex.GetType().Name, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Error(LaunchFileUnreadable(launchFile, nameof(UnauthorizedAccessException), ex.Message));
        }

        IReadOnlyList<LaunchConfiguration> configurations;
        try
        {
            configurations = ParseLaunchFile(text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ToolResult.Error(LaunchFileUnparsable(launchFile, ex.Message));
        }

        if (configurations.Count == 0)
        {
            return ToolResult.Error(LaunchFileEmpty(launchFile));
        }

        LaunchConfiguration configuration;
        if (string.IsNullOrWhiteSpace(name))
        {
            // One configuration needs no name; several do, and the caller is
            // told which names there are rather than being asked to guess.
            if (configurations.Count > 1)
            {
                return ToolResult.Error(AmbiguousServers(configurations.Select(c => c.Name)));
            }

            configuration = configurations[0];
        }
        else if (configurations.FirstOrDefault(c =>
                     c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            configuration = named;
        }
        else
        {
            return ToolResult.Error(NoMatchingServer(launchFile, configurations.Select(c => c.Name)));
        }

        if (!configuration.AttachOnly && string.IsNullOrWhiteSpace(configuration.RuntimeExecutable))
        {
            return ToolResult.Error(ConfigurationHasNoCommand(launchFile));
        }

        lock (_lock)
        {
            var existing = _servers.FirstOrDefault(s => s.Name.Equals(configuration.Name, StringComparison.OrdinalIgnoreCase)
                && (s.TaskId is null || tasks.Get(s.TaskId)?.Status == BackgroundTaskStatus.Running));
            if (existing is not null)
            {
                openInBrowser(existing.Url);
                return ToolResult.Success(
                    $"{configuration.Name} is already running (serverId {existing.ServerId}); preview open at {existing.Url}.");
            }
        }

        // The port is settled before anything is launched: a server started on a busy
        // port dies in its own log, where the reference answers the caller directly.
        var environment = new Dictionary<string, string>(configuration.Env, StringComparer.Ordinal);
        if (!configuration.AttachOnly)
        {
            var resolved = PreviewPorts.Resolve(
                configuration.Port,
                configuration.AutoPort,
                RunningPorts(),
                sessionId,
                DisplayPath(launchFile, workingDirectory),
                PreviewPortProbe.Bind,
                PreviewPortProbe.Occupant);
            if (!resolved.Ok)
            {
                return ToolResult.Error(resolved.Error!);
            }

            if (resolved.Port != configuration.Port)
            {
                configuration = configuration.OnPort(resolved.Port);
                // The assigned port reaches the server the way the reference's own
                // autoPort advice says it does.
                environment["PORT"] = resolved.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        string? startedTaskId = null;
        if (!configuration.AttachOnly)
        {
            startedTaskId = tasks.Start(
                configuration.CommandLine, workingDirectory, sessionId, environment: environment);
        }

        var serverId = Register(configuration.Name, startedTaskId, configuration.PreviewUrl, sessionId);
        openInBrowser(configuration.PreviewUrl);
        return ToolResult.Success(
            (configuration.AttachOnly
                ? $"Attached to {configuration.Name}"
                : $"Started {configuration.Name} ({configuration.CommandLine})") +
            $" — serverId {serverId}, preview open at {configuration.PreviewUrl}. " +
            "Use preview_logs to check for build errors.");
    }

    private string Register(string name, string? taskId, string url, string? sessionId = null)
    {
        lock (_lock)
        {
            var serverId = $"preview-{++_nextId}";
            _servers.Add(new Server(serverId, name, taskId, url, DateTimeOffset.Now, sessionId));
            return serverId;
        }
    }

    /// <summary>
    /// The servers this session started, for the Background tasks panel. Stopped
    /// ones are dropped, the way the reference's panel filters its own list.
    /// </summary>
    public IReadOnlyList<PreviewServerInfo> Snapshot(string? sessionId)
    {
        List<Server> servers;
        lock (_lock)
        {
            servers = [.. _servers.Where(s => sessionId is null || s.SessionId is null ||
                                              string.Equals(s.SessionId, sessionId, StringComparison.Ordinal))];
        }

        var infos = new List<PreviewServerInfo>();
        foreach (var server in servers)
        {
            var status = ServerStatus(server);
            if (status == PreviewServerStatus.Stopped)
            {
                continue;
            }

            infos.Add(new PreviewServerInfo(
                server.ServerId, server.Name, server.Url, PortOf(server.Url), status, server.StartedAt));
        }

        return infos;
    }

    private PreviewServerStatus ServerStatus(Server server)
    {
        // A URL-only preview has no process to watch, so it is simply up.
        if (server.TaskId is null)
        {
            return PreviewServerStatus.Running;
        }

        if (tasks.Get(server.TaskId) is not { } task)
        {
            return PreviewServerStatus.Stopped;
        }

        return task.Status switch
        {
            BackgroundTaskStatus.Running => task.Output.Length == 0
                ? PreviewServerStatus.Starting
                : PreviewServerStatus.Running,
            BackgroundTaskStatus.Completed when task.ExitCode is not 0 => PreviewServerStatus.Error,
            _ => PreviewServerStatus.Stopped,
        };
    }

    private static int? PortOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : null;

    /// <summary>
    /// The servers the port check compares against: the reference filters its own list
    /// to the running and starting ones, so a server that has already exited is not
    /// reported as holding a port.
    /// </summary>
    private IReadOnlyList<PreviewServerRef> RunningPorts()
    {
        List<Server> servers;
        lock (_lock)
        {
            servers = [.. _servers];
        }

        var running = new List<PreviewServerRef>();
        foreach (var server in servers)
        {
            if (ServerStatus(server) is PreviewServerStatus.Stopped or PreviewServerStatus.Error)
                continue;
            if (PortOf(server.Url) is { } port)
                running.Add(new PreviewServerRef(server.ServerId, server.Name, port, server.SessionId));
        }

        return running;
    }

    /// <summary>
    /// How the launch file is named back to the model: relative to the working
    /// directory with forward slashes, so the advice says ".jarvis/launch.json" or
    /// ".claude/launch.json" — whichever was actually read.
    /// </summary>
    internal static string DisplayPath(string launchFile, string workingDirectory)
    {
        try
        {
            var relative = Path.GetRelativePath(workingDirectory, launchFile);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            // An unrelatable path is named in full rather than not at all.
        }

        return launchFile.Replace('\\', '/');
    }

    public ToolResult Stop(string serverId)
    {
        Server? server;
        lock (_lock)
        {
            server = _servers.FirstOrDefault(s => s.ServerId == serverId);
        }

        if (server is null)
            return ToolResult.Error($"Unknown serverId '{serverId}'. Use preview_list to see servers.");
        if (server.TaskId is null)
            return ToolResult.Success($"{serverId} had no server process (URL-only preview); nothing to stop.");
        return tasks.Kill(server.TaskId)
            ? ToolResult.Success($"Stopped {server.Name} ({serverId}).")
            : ToolResult.Success($"{server.Name} ({serverId}) was not running anymore.");
    }

    /// <summary>The reference's preview_logs default: "Max lines to return (default: 50)".</summary>
    /// <summary>
    /// What preview_logs returns when the call names no line count, and the cap
    /// it clamps to. The reference's doc says 50 and its handler uses 200 either
    /// way; the handler is what the model actually gets, so 200 is what this
    /// returns and the doc stays the reference's.
    /// </summary>
    public const int DefaultLogLines = 200;

    /// <summary>
    /// The words the reference's <c>level: "error"</c> filter keeps, and the
    /// whole list — a warning is not one of them, so a warn-only build stays out
    /// of an error-filtered read.
    /// </summary>
    private static readonly string[] ErrorWords = ["error", "exception", "failed", "fatal"];

    public ToolResult Logs(string serverId, int lines, string? level, string? search)
    {
        Server? server;
        lock (_lock)
        {
            server = _servers.FirstOrDefault(s => s.ServerId == serverId);
        }

        if (server is null)
            return ToolResult.Error($"Unknown serverId '{serverId}'. Use preview_list to see servers.");
        if (server.TaskId is null || tasks.Get(server.TaskId) is not { } task)
            return ToolResult.Success("(no server process, so no logs)");

        // A dev server colours its output whether or not anything can render it, and the escapes
        // would be read as text on the other side of this.
        IEnumerable<string> output = AnsiText.Strip(task.Output).Split('\n');
        if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
            output = output.Where(static l =>
                ErrorWords.Any(word => l.Contains(word, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(search))
            output = output.Where(l => l.Contains(search, StringComparison.OrdinalIgnoreCase));

        var tail = output.TakeLast(Math.Clamp(lines, 1, DefaultLogLines)).ToList();
        var status = task.Status == BackgroundTaskStatus.Running
            ? "running"
            : $"exited ({task.ExitCode?.ToString() ?? "killed"})";
        return ToolResult.Success(
            $"{server.Name} ({serverId}) — {status}\n" +
            (tail.Count == 0 ? "(no matching output)" : string.Join('\n', tail)));
    }

    /// <summary>The preview server (if any) that a background task belongs to.</summary>
    public (string ServerId, string Name, string Url)? FindByTask(string taskId)
    {
        lock (_lock)
        {
            var server = _servers.FirstOrDefault(s => s.TaskId == taskId);
            return server is null ? null : (server.ServerId, server.Name, server.Url);
        }
    }

    /// <summary>The most recently started server, for the pane's "Show dev server logs".</summary>
    public string? LatestServerId
    {
        get
        {
            lock (_lock)
            {
                return _servers.Count == 0 ? null : _servers[^1].ServerId;
            }
        }
    }

    /// <summary>
    /// Reads the autoVerify flag from the project's launch.json (.jarvis first,
    /// .claude as the compatibility fallback) — the reference's auto-verify
    /// setting, which it likewise stores in launch.json.
    /// </summary>
    public static bool ReadAutoVerify(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory) || FindLaunchFile(workingDirectory) is not { } launchFile)
        {
            return false;
        }

        try
        {
            // On unless the file turns it off: the reference reads `autoVerify !== false`
            // from a launch.json that parsed and holds at least one usable
            // configuration, and answers false only when there is no config file.
            var text = File.ReadAllText(launchFile);
            if (JsonNode.Parse(text) is not JsonObject root || ParseLaunchFile(text).Count == 0)
            {
                return false;
            }

            return root["autoVerify"]?.GetValue<bool>() != false;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Merges autoVerify into the existing launch.json, or creates a minimal .jarvis one.</summary>
    public static void WriteAutoVerify(string workingDirectory, bool enabled)
    {
        var launchFile = FindLaunchFile(workingDirectory);
        JsonObject root;
        if (launchFile is not null)
        {
            root = JsonNode.Parse(File.ReadAllText(launchFile)) as JsonObject ?? [];
        }
        else
        {
            var directory = Path.Combine(workingDirectory, ".jarvis");
            Directory.CreateDirectory(directory);
            launchFile = Path.Combine(directory, "launch.json");
            root = new JsonObject { ["version"] = "0.0.1", ["configurations"] = new JsonArray() };
        }

        root["autoVerify"] = enabled;
        File.WriteAllText(launchFile, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public ToolResult ListServers()
    {
        lock (_lock)
        {
            if (_servers.Count == 0)
                return ToolResult.Success("No preview servers started in this session.");
            var builder = new StringBuilder();
            foreach (var server in _servers)
            {
                var status = server.TaskId is null
                    ? "url-only"
                    : tasks.Get(server.TaskId)?.Status.ToString().ToLowerInvariant() ?? "gone";
                builder.AppendLine($"{server.ServerId}: {server.Name} — {server.Url} ({status})");
            }

            return ToolResult.Success(builder.ToString().TrimEnd());
        }
    }
}
