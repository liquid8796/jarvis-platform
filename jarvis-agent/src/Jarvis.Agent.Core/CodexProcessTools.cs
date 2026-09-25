using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core;

public sealed partial class ProcessToolSet
{
    private sealed class CodexProcessTool(ProcessToolSet owner, string operation) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = operation == "exec_command"
            ? new(
                "unified_exec.exec_command",
                "exec_command",
                "command",
                "Execute a shell command in an owned PTY. Returns output when the command exits quickly or a numeric session_id for continued interaction with write_stdin. Commands run only on this agent and retain the existing local approval, Arm, timeout, ownership and descendant-process controls.",
                ExecSchema(),
                ReadOnly: false,
                Sensitive: true)
            : new(
                "unified_exec.write_stdin",
                "write_stdin",
                "process",
                "Write characters to or poll an owned exec_command session. Pass chars='' to poll for new output; pass a single Ctrl+C character (\\u0003) to cancel the owned process tree.",
                StdinSchema(),
                ReadOnly: false,
                Sensitive: true);

        public async Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            try
            {
                return operation == "exec_command"
                    ? await owner.ExecuteCommandAsync(args, context, ct).ConfigureAwait(false)
                    : await owner.WriteStdinAsync(args, context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or Win32Exception or PlatformNotSupportedException)
            {
                return ToolReply.Error(ex.Message);
            }
        }

        private static JsonElement ExecSchema() => WireJson.Element(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["cmd"] = new { type = "string", minLength = 1, maxLength = 16000, description = "Shell command to execute." },
                ["workdir"] = new { type = "string", minLength = 1, description = "Optional working directory. Relative paths use the selected workspace." },
                ["shell"] = new { type = "string", minLength = 1, maxLength = 1024, description = "Optional shell executable or shell name. Defaults to PowerShell on Windows and bash elsewhere." },
                ["login"] = new { type = "boolean", @default = true, description = "Use login/profile shell behavior where supported." },
                ["tty"] = new { type = "boolean", @default = true, description = "Use Windows ConPTY for interactive terminal semantics." },
                ["yield_time_ms"] = new { type = "integer", minimum = 0, maximum = 30000, @default = 10000, description = "Wait this long for initial output or completion before returning a session_id." },
                ["max_output_tokens"] = new { type = "integer", minimum = 100, maximum = 100000, @default = 10000, description = "Approximate output-token budget for this response." },
                ["sandbox_permissions"] = new { type = "string", @enum = new[] { "use_default", "require_escalated" }, @default = "use_default" },
                ["justification"] = new { type = "string", maxLength = 2000, description = "Reason for escalated execution when requested." },
                ["prefix_rule"] = new { type = "array", maxItems = 32, items = new { type = "string", maxLength = 512 }, description = "Optional command-prefix metadata used by approval clients." }
            },
            required = new[] { "cmd" },
            additionalProperties = false
        });

        private static JsonElement StdinSchema() => WireJson.Element(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["session_id"] = new { type = "integer", minimum = 1, description = "Numeric session_id returned by exec_command." },
                ["chars"] = new { type = "string", maxLength = 65536, @default = "", description = "Characters to write. Empty polls; a single Ctrl+C character cancels the session." },
                ["yield_time_ms"] = new { type = "integer", minimum = 0, maximum = 300000, description = "How long to wait for new output. Defaults to 250ms after input and 5000ms while polling." },
                ["max_output_tokens"] = new { type = "integer", minimum = 100, maximum = 100000, @default = 10000 }
            },
            required = new[] { "session_id" },
            additionalProperties = false
        });
    }

    private async Task<ToolReply> ExecuteCommandAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var command = args.GetProperty("cmd").GetString();
        if (string.IsNullOrWhiteSpace(command) || command.Length > 16000)
            throw new ArgumentException("cmd must contain 1..16000 characters.");
        var escalation = args.TryGetProperty("sandbox_permissions", out var permission) ? permission.GetString() : "use_default";
        if (escalation == "require_escalated" && !context.FullPermission)
            throw new UnauthorizedAccessException("require_escalated needs local Full permission or an approved scoped capability lease.");

        var cwd = WorkspaceDirectories.Normalize(args.TryGetProperty("workdir", out var requested)
            ? WorkspaceDirectories.ResolvePath(requested.GetString(), context.Workspace)
            : WorkspaceDirectories.ResolvePath(null, context.Workspace));
        var shell = args.TryGetProperty("shell", out var shellNode) ? shellNode.GetString() : null;
        var login = !args.TryGetProperty("login", out var loginNode) || loginNode.GetBoolean();
        var tty = !args.TryGetProperty("tty", out var ttyNode) || ttyNode.GetBoolean();
        var yield = args.TryGetProperty("yield_time_ms", out var yieldNode) ? yieldNode.GetInt32() : 10000;
        var maxTokens = args.TryGetProperty("max_output_tokens", out var tokenNode) ? tokenNode.GetInt32() : 10000;

        ManagedJob job;
        long sessionId;
        lock (_startLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            context.SessionCancellation.ThrowIfCancellationRequested();
            var maximum = _settings().MaxProcessJobs;
            if (_jobs.Values.Count(j => !j.Done) >= maximum)
                return ToolReply.Error($"PROCESS_LIMIT: {maximum} owned process jobs are already running.");

            var argv = ShellArgv(command, shell, login);
            if (tty)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PTY mode currently requires Windows ConPTY.");
                job = new ConPtyJob(argv, cwd, timeout: 1800, environment: null, columns: 100, rows: 32);
            }
            else job = new PipedJob(argv, cwd, timeout: 1800, environment: null);

            job.BindOwner(context);
            if (!_jobs.TryAdd(job.Id, job)) { job.Dispose(); throw new InvalidOperationException("Job ID collision."); }
            sessionId = Interlocked.Increment(ref _nextCodexSessionId);
            _codexSessions[sessionId] = job.Id;
            _codexReadCursors[sessionId] = 0;
            PruneCompletedJobs();
        }

        return await SnapshotAsync(sessionId, job, yield, maxTokens, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> WriteStdinAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sessionId = args.GetProperty("session_id").GetInt64();
        if (!_codexSessions.TryGetValue(sessionId, out var jobId) || !_jobs.TryGetValue(jobId, out var job) || !job.BelongsTo(context))
            return ToolReply.Error("No owned exec_command session with this session_id.");

        var chars = args.TryGetProperty("chars", out var charsNode) ? charsNode.GetString() ?? "" : "";
        if (chars.Length > 65536) throw new ArgumentException("chars exceeds 65536 characters.");
        if (chars == "\u0003") job.Cancel();
        else if (chars.Length > 0)
        {
            if (job.Done) throw new InvalidOperationException("Process has already exited.");
            await job.WriteStdinAsync(chars, close: false, ct).ConfigureAwait(false);
        }

        var yield = args.TryGetProperty("yield_time_ms", out var yieldNode)
            ? yieldNode.GetInt32()
            : chars.Length == 0 ? 5000 : 250;
        var maxTokens = args.TryGetProperty("max_output_tokens", out var tokenNode) ? tokenNode.GetInt32() : 10000;
        return await SnapshotAsync(sessionId, job, yield, maxTokens, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> SnapshotAsync(long sessionId, ManagedJob job, int yieldMs, int maxTokens, CancellationToken ct)
    {
        var cursor = _codexReadCursors.GetOrAdd(sessionId, 0);
        var deadline = Stopwatch.StartNew();
        long? firstOutputAt = null;
        JsonElement root;
        while (true)
        {
            using var snapshot = JsonDocument.Parse(job.Snapshot(cursor));
            root = snapshot.RootElement.Clone();
            var hasOutput = !string.IsNullOrEmpty(root.GetProperty("output").GetString());
            if (hasOutput) firstOutputAt ??= deadline.ElapsedMilliseconds;
            var outputSettled = firstOutputAt is { } outputAt && deadline.ElapsedMilliseconds - outputAt >= 100;
            if (root.GetProperty("done").GetBoolean() || outputSettled || deadline.ElapsedMilliseconds >= yieldMs) break;
            await Task.Delay(Math.Min(25, Math.Max(1, yieldMs - (int)deadline.ElapsedMilliseconds)), ct).ConfigureAwait(false);
        }

        var nextCursor = root.GetProperty("cursor").GetInt64();
        _codexReadCursors[sessionId] = nextCursor;
        var outputText = root.GetProperty("output").GetString() ?? "";
        var rawOutputLength = outputText.Length;
        var originalTokenCount = Math.Max(0, (rawOutputLength + 3) / 4);
        var budgetChars = checked(maxTokens * 4);
        if (outputText.Length > budgetChars)
        {
            var half = Math.Max(1, (budgetChars - 80) / 2);
            outputText = outputText[..half] + "\n...[output truncated by max_output_tokens]...\n" + outputText[^half..];
        }
        var done = root.GetProperty("done").GetBoolean();
        var exitCode = root.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : (int?)null;
        var mayHaveMore = rawOutputLength >= 32000;
        if (done && !mayHaveMore)
        {
            _codexSessions.TryRemove(sessionId, out _);
            _codexReadCursors.TryRemove(sessionId, out _);
        }

        var body = new Dictionary<string, object?>
        {
            ["output"] = outputText,
            ["wall_time_seconds"] = Math.Round((DateTimeOffset.UtcNow - job.Started).TotalSeconds, 3)
        };
        if (outputText.Length > 0) body["chunk_id"] = $"{sessionId}:{nextCursor}";
        if (!done || mayHaveMore) body["session_id"] = sessionId;
        if (exitCode is not null) body["exit_code"] = exitCode;
        if (originalTokenCount > maxTokens || root.GetProperty("truncated").GetBoolean())
            body["original_token_count"] = originalTokenCount;
        return new ToolReply(JsonSerializer.Serialize(body, WireJson.Options));
    }

    private void PruneCompletedJobs()
    {
        foreach (var old in _jobs.Values.Where(job => job.Done).OrderBy(job => job.Started).Take(Math.Max(0, _jobs.Count - 50)))
        {
            if (!_jobs.TryRemove(old.Id, out var removed)) continue;
            foreach (var session in _codexSessions.Where(pair => pair.Value == old.Id).Select(pair => pair.Key).ToArray())
            {
                _codexSessions.TryRemove(session, out _);
                _codexReadCursors.TryRemove(session, out _);
            }
            removed.Dispose();
        }
    }

    private static string[] ShellArgv(string command, string? requestedShell, bool login)
    {
        var shell = string.IsNullOrWhiteSpace(requestedShell)
            ? OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash"
            : requestedShell.Trim();
        var name = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();
        if (name is "powershell" or "pwsh")
            return login ? [shell, "-NoLogo", "-Command", command] : [shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command];
        if (name is "cmd") return [shell, "/d", "/s", "/c", command];
        return [shell, login ? "-lc" : "-c", command];
    }
}
