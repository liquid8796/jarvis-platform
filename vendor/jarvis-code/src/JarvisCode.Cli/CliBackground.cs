using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.Cli;

internal sealed record BackgroundSession(string Id, int Pid, long StartedUtcTicks, string WorkingDirectory,
    string Title, string Status = "running")
{
    public string? ConversationId { get; init; }
    public string? WorktreePath { get; init; }
    public string? WorktreeBranch { get; init; }
    public bool Pinned { get; init; }
    public bool Background { get; init; } = true;
}

/// <summary>Detached local CLI hosts. Credentials cross the startup pipe only, never the registry or argv.</summary>
internal static class CliBackground
{
    internal static string Root => Path.Combine(ProfilePaths.Create(Environment.GetEnvironmentVariable("JARVISCODE_PROFILE")).Root, "cli-background");

    internal static string SessionDirectory(string id, string? root = null)
    {
        if (!Guid.TryParse(id, out var parsed)) throw new CliError("A background session UUID is required.");
        return Path.Combine(root ?? Root, parsed.ToString());
    }

    internal static ProcessStartInfo SelfStart()
    {
        var executable = Environment.ProcessPath ?? throw new CliError("Cannot locate the CLI executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Cli).Assembly.Location);
        return start;
    }

    public static async Task<int> StartAsync(CliOptions options, CancellationToken cancellationToken, Action<string>? announce = null)
    {
        if (options.NoSessionPersistence) throw new CliError("--bg requires session persistence.");
        if (options.InputFormat == "stream-json") throw new CliError("--bg takes a prompt; attach to the background session to send additional messages.");
        if (options.Prompt.Length == 0 && Console.IsInputRedirected && !options.Resume && !options.Continue)
            options = options with { Prompt = (await Console.In.ReadToEndAsync(cancellationToken)).Trim() };
        if (options.Prompt.Length == 0 && !options.Resume && !options.Continue)
            throw new CliError("Provide a prompt or a session to resume with --bg.");
        string id;
        using (var services = CliServices.Create(options))
        {
            var resumed = await services.ResolveSessionAsync(options, Environment.CurrentDirectory, cancellationToken);
            id = options.Resume || options.Continue ? resumed.Id : Guid.NewGuid().ToString();
            // Older desktop IDs are not UUIDs; preserve conversation identity separately.
            var registrationId = Guid.TryParse(id, out _) ? id :
                Registrations().FirstOrDefault(entry => entry.ConversationId == id)?.Id ?? Guid.NewGuid().ToString();
            if (Load(registrationId) is { } existing && IsRunning(existing))
            {
                var copy = Guid.NewGuid().ToString();
                options = options with { ForkSession = true, SessionId = copy };
                Console.Error.WriteLine($"Session {id} is already running; starting a copy as {copy}.");
                id = copy;
            }
            else if (options.Resume || options.Continue)
                options = options with { Resume = true, Continue = false, ResumeValue = resumed.Id };
            options = options with { SessionId = Guid.TryParse(id, out _) ? id : null };
            id = Guid.TryParse(id, out _) ? id : registrationId;
        }
        var directory = SessionDirectory(id);
        Directory.CreateDirectory(Path.Combine(directory, "inbox"));
        var start = SelfStart();
        start.WorkingDirectory = Environment.CurrentDirectory;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.ArgumentList.Add("--internal-background");
        start.ArgumentList.Add(id);
        using var child = DetachedProcessStart.Start(start) ?? throw new CliError("Background session could not start.");
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(options with { Background = false }));
        child.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ready = await child.StandardOutput.ReadLineAsync(timeout.Token);
        if (ready != "ready")
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            throw new CliError("Background session failed to initialize: " + await child.StandardError.ReadToEndAsync(cancellationToken));
        }
        if (announce is null) Console.WriteLine(id); else announce(id);
        return 0;
    }

    public static async Task<int> RunChildAsync(string id, CancellationToken cancellationToken)
    {
        var directory = SessionDirectory(id);
        var line = await Console.In.ReadLineAsync(cancellationToken);
        var options = JsonSerializer.Deserialize<CliOptions>(line ?? "") ?? throw new CliError("Missing background startup options.");
        Directory.CreateDirectory(Path.Combine(directory, "inbox"));
        var stopPath = Path.Combine(directory, "stop");
        File.Delete(stopPath);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var metadata = new BackgroundSession(id, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            Environment.CurrentDirectory, options.SessionName ?? (options.Prompt.Length > 100 ? options.Prompt[..100] : options.Prompt))
        { ConversationId = options.ForkSession ? options.SessionId : options.ResumeValue ?? options.SessionId ?? id };
        Save(metadata);
        await Console.Out.WriteLineAsync("ready");
        await Console.Out.FlushAsync(cancellationToken);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "output.jsonl"), FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);
        using var inboxReader = new InboxReader(directory, options.Prompt, lifetime.Token);
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    if (File.Exists(stopPath)) { lifetime.Cancel(); break; }
                    await Task.Delay(200, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        var original = Environment.CurrentDirectory;
        CliWorkspace? workspace = null;
        try
        {
            workspace = options.Worktree
                ? await CliWorkspace.CreateAsync(original, options.WorktreeName, lifetime.Token) : null;
            if (workspace is not null)
            {
                Environment.CurrentDirectory = workspace.Path;
                metadata = metadata with { WorktreePath = workspace.Path, WorktreeBranch = workspace.Branch };
                Save(metadata);
            }
            var exit = await PrintRunner.RunAsync(options with { Print = true, Prompt = "", InputFormat = "stream-json",
                OutputFormat = "stream-json", Background = false }, lifetime.Token, inboxReader);
            Save(metadata with { Status = exit == 0 ? "completed" : "failed" });
            return exit;
        }
        catch (OperationCanceledException) { Save(metadata with { Status = "stopped" }); return 130; }
        catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); Save(metadata with { Status = "failed" }); return 1; }
        finally
        {
            Environment.CurrentDirectory = original;
            if (workspace is not null) await workspace.DisposeAsync();
            lifetime.Cancel();
            await watcher;
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
    }

    internal static bool IsRunning(BackgroundSession session)
    {
        if (session.Status != "running") return false;
        try
        {
            using var process = Process.GetProcessById(session.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == session.StartedUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static void Save(BackgroundSession session)
    {
        var file = Path.Combine(SessionDirectory(session.Id), "session.json");
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(session));
        File.Move(temporary, file, overwrite: true);
    }

    internal static BackgroundSession? Load(string id)
    {
        var file = Path.Combine(SessionDirectory(id), "session.json");
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<BackgroundSession>(File.ReadAllText(file)); }
        catch (JsonException) { return null; }
    }

    internal static BackgroundSession[] Registrations() => Directory.Exists(Root)
        ? Directory.GetDirectories(Root).Select(Path.GetFileName).Where(id => Guid.TryParse(id, out _))
            .Select(id => Load(id!)).OfType<BackgroundSession>().ToArray() : [];

    public static async Task<int> CommandAsync(string name, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (name == "agents")
        {
            if (!arguments.Contains("--json") && !Console.IsInputRedirected && !Console.IsOutputRedirected)
                return await AgentFleet.RunAsync(arguments, cancellationToken);
            var sessions = await ListedSessionsAsync(arguments.Contains("--all"), cancellationToken);
            var cwdIndex = arguments.ToList().IndexOf("--cwd");
            if (cwdIndex >= 0 && cwdIndex + 1 < arguments.Count)
            {
                var selected = Path.GetFullPath(arguments[cwdIndex + 1]);
                sessions = [.. sessions.Where(session => Path.GetFullPath(session.WorkingDirectory).Equals(selected, StringComparison.OrdinalIgnoreCase))];
            }
            if (arguments.Contains("--json")) Console.WriteLine(JsonSerializer.Serialize(sessions.Select(session =>
                session with { Status = IsRunning(session) ? "running" : session.Status == "running" ? "exited" : session.Status })));
            else foreach (var session in sessions) Console.WriteLine($"{session.Id}  {(IsRunning(session) ? "running" : session.Status)}  {session.Title}");
            return 0;
        }
        var id = arguments.FirstOrDefault(value => !value.StartsWith('-')) ?? throw new CliError($"Usage: jarvis {name} <id>");
        var metadata = Load(id) ?? throw new CliError("No background session found: " + id);
        var directory = SessionDirectory(id);
        if (name is "stop" or "kill")
        {
            if (IsRunning(metadata))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "stop", cancellationToken);
                for (var count = 0; count < 100 && IsRunning(metadata); count++) await Task.Delay(100, cancellationToken);
                if (IsRunning(metadata))
                {
                    using var process = Process.GetProcessById(metadata.Pid);
                    if (process.StartTime.ToUniversalTime().Ticks == metadata.StartedUtcTicks) process.Kill(entireProcessTree: true);
                }
            }
            Save(metadata with { Status = "stopped" });
            Console.WriteLine("Stopped " + id);
            return 0;
        }
        if (name == "rm")
        {
            if (IsRunning(metadata)) throw new CliError("Stop the background session before removing it.");
            if (metadata.WorktreePath is { } worktree && Directory.Exists(worktree))
            {
                var worktreeRoot = Path.GetFullPath(Path.Combine(metadata.WorkingDirectory, ".jarvis", "worktrees"));
                if (!Path.GetFullPath(worktree).StartsWith(worktreeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new CliError("The stored worktree is outside the session's worktree directory.");
                var head = await CliWorkspace.GitAsync(worktree, cancellationToken, "rev-parse", "HEAD");
                var status = await CliWorkspace.GitAsync(worktree, cancellationToken, "status", "--porcelain", "--untracked-files=all");
                var ahead = await CliWorkspace.GitAsync(worktree, cancellationToken, "rev-list", "HEAD", "--not", "--remotes");
                if (head.Code != 0 || status.Code != 0 || ahead.Code != 0) throw new CliError("Could not verify worktree state before removal.");
                var token = head.Output.Trim() + "@" + id;
                var discardIndex = arguments.ToList().IndexOf("--discard-unpushed");
                var discard = discardIndex >= 0 && discardIndex + 1 < arguments.Count && arguments[discardIndex + 1] == token;
                if ((status.Output.Length > 0 || ahead.Output.Length > 0) && !discard)
                    throw new CliError("Worktree contains changes or unpushed commits. To discard this exact revision, use --discard-unpushed " + token);
                var removal = await CliWorkspace.GitAsync(metadata.WorkingDirectory, cancellationToken,
                    ["worktree", "remove", .. discard ? new[] { "--force" } : Array.Empty<string>(), worktree]);
                if (removal.Code != 0) throw new CliError("Could not remove worktree: " + removal.Error.Trim());
                if (metadata.WorktreeBranch is { } branch)
                    await CliWorkspace.GitAsync(metadata.WorkingDirectory, cancellationToken, "branch", discard ? "-D" : "-d", branch);
            }
            Directory.Delete(directory, recursive: true);
            Console.WriteLine("Removed background registration and logs for " + id + ". Conversation history is retained.");
            return 0;
        }
        if (name == "respawn")
            return await StartAsync(new CliOptions { Prompt = "", Resume = true, ResumeValue = metadata.ConversationId ?? id, Background = true }, cancellationToken);
        if (name == "logs")
        {
            var file = Path.Combine(directory, "output.jsonl");
            if (arguments.Contains("--follow") || arguments.Contains("-f"))
                await FollowAsync(directory, metadata, waitForResult: false, cancellationToken, offset: 0);
            else if (File.Exists(file)) Console.Write(await ReadLogAsync(file, cancellationToken));
            return 0;
        }
        if (name == "attach")
        {
            var file = Path.Combine(directory, "output.jsonl");
            var existingLog = File.Exists(file) ? await ReadLogAsync(file, cancellationToken) : "";
            Console.Write(existingLog);
            if (!IsRunning(metadata))
            {
                var options = new CliOptions { Prompt = "", Resume = true, ResumeValue = metadata.ConversationId ?? id };
                using var services = CliServices.Create(options);
                using var repl = new InteractiveRepl(services, options);
                return await repl.RunAsync(cancellationToken);
            }
            var previousFrames = existingLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(ParseFrame).OfType<JsonObject>().ToArray();
            var pendingPermission = previousFrames.LastOrDefault(frame => frame["type"]?.GetValue<string>() is "result" or "control_request");
            if (pendingPermission?["type"]?.GetValue<string>() == "control_request" &&
                pendingPermission["request"]?["subtype"]?.GetValue<string>() == "can_use_tool")
            {
                var offset = new FileInfo(file).Length;
                await AnswerPermissionAsync(directory, pendingPermission, cancellationToken);
                await FollowAsync(directory, metadata, true, cancellationToken, offset);
            }
            Console.WriteLine("Attached to " + id + ". Enter a message, or /detach to leave it running.");
            while (IsRunning(metadata) && !cancellationToken.IsCancellationRequested)
            {
                var input = await Console.In.ReadLineAsync(cancellationToken);
                if (input is null or "/detach") break;
                var pending = Path.Combine(directory, "inbox", Guid.NewGuid().ToString("N"));
                var offset = new FileInfo(file).Length;
                await File.WriteAllTextAsync(pending + ".tmp", UserFrame(input), cancellationToken);
                File.Move(pending + ".tmp", pending + ".json");
                await FollowAsync(directory, metadata, waitForResult: true, cancellationToken, offset);
            }
            return 0;
        }
        throw new CliError("Unknown background command: " + name);
    }

    internal static async Task<BackgroundSession[]> ListedSessionsAsync(bool includeCompleted, CancellationToken cancellationToken)
    {
        var paths = ProfilePaths.Create(Environment.GetEnvironmentVariable("JARVISCODE_PROFILE"));
        var sessions = Registrations().Where(session => includeCompleted || IsRunning(session)).ToList();
        var summaries = await new Core.Sessions.JsonSessionStore(Path.Combine(paths.SessionsDirectory, "code")).ListAsync(cancellationToken);
        foreach (var peer in Core.Agent.LocalSessionMailbox.ReadPeers(paths.Root))
        {
            if (sessions.Any(session => (session.ConversationId ?? session.Id) == peer.SessionId)) continue;
            var summary = summaries.FirstOrDefault(session => session.Id == peer.SessionId);
            sessions.Add(new BackgroundSession(peer.SessionId, peer.ProcessId, peer.ProcessStartedUtcTicks,
                summary?.WorkingDirectory ?? "", peer.Title) { ConversationId = peer.SessionId, Background = false });
        }
        return [.. sessions.OrderByDescending(session => session.Pinned).ThenByDescending(session => session.StartedUtcTicks)];
    }

    private static async Task FollowAsync(string directory, BackgroundSession metadata, bool waitForResult, CancellationToken cancellationToken, long? offset = null)
    {
        var file = Path.Combine(directory, "output.jsonl");
        using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        if (offset is { } position) stream.Seek(position, SeekOrigin.Begin);
        else stream.Seek(0, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        while (IsRunning(metadata))
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) { await Task.Delay(100, cancellationToken); continue; }
            Console.WriteLine(line);
            var frame = ParseFrame(line);
            if (waitForResult && frame?["type"]?.GetValue<string>() == "control_request" &&
                frame["request"]?["subtype"]?.GetValue<string>() == "can_use_tool")
                await AnswerPermissionAsync(directory, frame, cancellationToken);
            if (waitForResult && line.StartsWith('{'))
                try { if (JsonNode.Parse(line)?["type"]?.GetValue<string>() == "result") return; } catch (JsonException) { }
        }
    }

    internal static async Task<string> ReadLogAsync(string file, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static JsonObject? ParseFrame(string line)
    {
        try { return line.TrimStart().StartsWith('{') ? JsonNode.Parse(line) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    private static async Task AnswerPermissionAsync(string directory, JsonObject frame, CancellationToken cancellationToken)
    {
        var id = frame["request_id"]?.GetValue<string>();
        if (!Guid.TryParse(id, out var requestId)) return;
        var replyFile = Path.Combine(directory, "permission-" + requestId.ToString("N") + ".answered");
        if (File.Exists(replyFile)) return;
        Console.Write("Allow " + frame["request"]?["tool_name"]?.GetValue<string>() + " " +
            frame["request"]?["input"]?.ToJsonString() + "? [y/N] ");
        var answer = (await Console.In.ReadLineAsync(cancellationToken))?.Trim().ToLowerInvariant();
        var allowed = answer is "y" or "yes";
        var payload = new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject
        {
            ["subtype"] = "success", ["request_id"] = id,
            ["response"] = new JsonObject { ["behavior"] = allowed ? "allow" : "deny",
                ["message"] = allowed ? null : "The attached user denied this tool call." },
        } };
        var path = Path.Combine(directory, "inbox", Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path + ".tmp", payload.ToJsonString(), cancellationToken);
        File.Move(path + ".tmp", path + ".json");
        await File.WriteAllTextAsync(replyFile, allowed ? "allow" : "deny", cancellationToken);
    }

    internal static string UserFrame(string prompt) => new JsonObject
    {
        ["type"] = "user", ["uuid"] = Guid.NewGuid().ToString(),
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt },
    }.ToJsonString();

    private sealed class InboxReader(string directory, string initialPrompt, CancellationToken lifetime) : TextReader
    {
        private string? _initial = initialPrompt.Length > 0 ? UserFrame(initialPrompt) : null;
        public override string? ReadLine() => ReadLineAsync(lifetime).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _initial, null) is { } first) return first;
            while (!cancellationToken.IsCancellationRequested)
            {
                var file = Directory.EnumerateFiles(Path.Combine(directory, "inbox"), "*.json")
                    .OrderBy(File.GetCreationTimeUtc).FirstOrDefault();
                if (file is not null)
                {
                    var content = await File.ReadAllTextAsync(file, cancellationToken);
                    File.Delete(file);
                    return content;
                }
                await Task.Delay(100, cancellationToken);
            }
            return null;
        }
    }

    public static async Task<int> StartTmuxAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var self = SelfStart();
        static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var command = string.Join(' ', new[] { self.FileName }.Concat(self.ArgumentList)
            .Concat(arguments.Where(argument => argument != "--tmux")).Select(Quote));
        var start = new ProcessStartInfo("tmux") { UseShellExecute = false };
        foreach (var argument in new[] { "new-session", "-s", "jarvis-" + Guid.NewGuid().ToString("N")[..10],
                     "-c", Environment.CurrentDirectory, command }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new CliError("tmux could not start. Install tmux and retry.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
