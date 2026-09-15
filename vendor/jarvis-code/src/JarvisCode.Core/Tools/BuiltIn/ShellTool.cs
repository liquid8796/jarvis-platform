using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Which interpreter a <see cref="ShellTool"/> instance runs. The reference
/// ships both on Windows as separate tools — PowerShell as the primary and Bash
/// for POSIX scripts — so this harness registers one instance of each.
/// </summary>
public enum ShellKind
{
    PowerShell,
    Bash,
}

public sealed class ShellTool(ShellKind kind = ShellKind.PowerShell) : ITool
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a finished command's output reads are given to hand over what the
    /// OS already holds. A command settles on its own process exit, never on its
    /// pipes closing — the reference resolves the call from the child's `exit`
    /// event and not from `close` (claude.exe 2.1.258, class YWe) — because a
    /// command may leave a detached descendant that inherited the write end of
    /// those pipes and keeps them open for as long as it runs. The pumps read
    /// continuously, so an ordinary command has only the pipe buffer left to give
    /// up here; past this the reads are abandoned to the process teardown.
    /// </summary>
    private static readonly TimeSpan DrainAfterExit = TimeSpan.FromSeconds(2);

    /// <summary>True when this instance runs bash rather than the platform default.</summary>
    private bool RunsBash => kind == ShellKind.Bash || !OperatingSystem.IsWindows();

    public string Name => kind == ShellKind.Bash ? "Bash" : "PowerShell";

    public string Description => kind == ShellKind.Bash
        ? "Executes a bash command in the working directory and returns stdout/stderr. " +
          "On Windows this runs Git Bash; interactive commands are not supported."
        : OperatingSystem.IsWindows()
        ? "Executes a Windows PowerShell command in the working directory and returns stdout/stderr. " +
          "Use PowerShell syntax. Interactive commands are not supported."
        : "Executes a bash command in the working directory and returns stdout/stderr. " +
          "Interactive commands are not supported.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("command", SchemaBuilder.String("The command to execute")),
            ("timeout", SchemaBuilder.Integer("Optional timeout in milliseconds (max 600000)")),
            ("description", SchemaBuilder.String(
                "Clear, concise description of what this command does in active voice. Never use words like " +
                "\"complex\" or \"risk\" in the description - just describe what it does.\n\nFor simple commands " +
                "(git, npm, standard CLI tools), keep it brief (5-10 words):\n- ls → \"List files in current " +
                "directory\"\n- git status → \"Show working tree status\"\n- npm install → \"Install package " +
                "dependencies\"\n\nFor commands that are harder to parse at a glance (piped commands, obscure " +
                "flags, etc.), add enough context to clarify what it does:\n- find . -name \"*.tmp\" -exec rm {} " +
                "\\; → \"Find and delete all .tmp files recursively\"\n- git reset --hard origin/main → \"Discard " +
                "all local changes and match remote main\"\n- curl -s url | jq '.data[]' → \"Fetch JSON from URL " +
                "and extract data array elements\"")),
            ("run_in_background", SchemaBuilder.Boolean(
                "Set to true to run this command in the background. Use TaskOutput to read the output later.")),
            ("dangerouslyDisableSandbox", SchemaBuilder.Boolean(
                "Set this to true to dangerously override sandbox mode and run commands without sandboxing.")),
        ],
        "command");

    public bool IsReadOnly => false;

    /// <summary>
    /// The reference's background launch result (CLI 2.1.257, at 188616140), on
    /// this port's task id and output file. Its four opening sentences are one
    /// per reason the command ended up in the background; the promise and the
    /// interim-output pointer follow, joined with a single space.
    /// </summary>
    /// <param name="reason">Why the command is in the background.</param>
    public static string BackgroundResult(
        string taskId,
        string? outputFile,
        string workingDirectory,
        string command,
        BackgroundReason reason = BackgroundReason.Requested,
        int? timeoutMs = null,
        string readToolName = "Read")
    {
        var opening = reason switch
        {
            BackgroundReason.ManuallyBackgrounded =>
                $"Command was manually backgrounded by user with ID: {taskId}.",
            BackgroundReason.MovedForMessage =>
                $"Command was moved to the background (ID: {taskId}) so that a message that arrived while it " +
                "was running can reach you; it was not interrupted.",
            BackgroundReason.Timeout =>
                "Command did not complete within its " +
                $"{Math.Max(1, (int)Math.Round((timeoutMs ?? 0) / 1000.0))}s timeout and was moved to the " +
                $"background (ID: {taskId}).",
            _ => $"Command running in background with ID: {taskId}.",
        };

        var parts = new List<string>
        {
            outputFile is null ? opening : opening + $" Output is being written to: {outputFile}.",
        };
        if (reason != BackgroundReason.ManuallyBackgrounded)
        {
            parts.Add("You will be notified when it completes.");
            parts.Add(outputFile is null
                ? $"To check interim output, use TaskOutput with task_id \"{taskId}\"."
                : $"To check interim output, use {readToolName} on that file path.");
        }

        var text = string.Join(" ", parts);
        // The reference's own hint beside the result (its R8): a backgrounded
        // command that changes directory is told the session's cwd did not move.
        if (ChangesDirectory(command))
        {
            text += "\nSession cwd remains " + workingDirectory + "; directory changes made by the " +
                    "backgrounded command do not apply to subsequent commands.";
        }

        return text;
    }

    /// <summary>Why a command is running in the background.</summary>
    public enum BackgroundReason
    {
        /// <summary>The call asked for it (<c>run_in_background</c>).</summary>
        Requested,

        /// <summary>The user moved a running call to the background.</summary>
        ManuallyBackgrounded,

        /// <summary>A queued message ended the turn while the command still ran.</summary>
        MovedForMessage,

        /// <summary>The command outlived its timeout.</summary>
        Timeout,
    }

    /// <summary>
    /// The reference's <c>R8</c>/<c>hO</c>: true when any sub-command's first word
    /// is <c>cd</c>, <c>pushd</c>, <c>popd</c> or <c>chdir</c>.
    /// </summary>
    public static bool ChangesDirectory(string command) =>
        System.Text.RegularExpressions.Regex.Split(command, "&&|[|][|]|[;|\n]")
            .Select(static part => part.Trim())
            .Select(static part => part.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Any(static first => first is "cd" or "pushd" or "popd" or "chdir");

    /// <summary>
    /// Git Bash on Windows, /bin/bash elsewhere. Windows has no bash of its own,
    /// so a Bash call without Git installed fails with the launcher's error
    /// rather than silently running something else.
    /// </summary>
    private static string FindBash()
    {
        if (!OperatingSystem.IsWindows())
            return "/bin/bash";
        foreach (var candidate in new[]
        {
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
        })
        {
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        return "bash.exe";
    }

    public string DescribeCall(JsonObject arguments)
    {
        var command = arguments["command"]?.GetValue<string>() ?? "?";
        return $"Shell({(command.Length > 80 ? command[..80] + "…" : command)})";
    }

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var command = arguments["command"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("command is required.");

        // Claw-code read-only-mode semantics: plan mode may observe but not mutate.
        // The gate is fail-closed — pipes, redirection or an unknown executable all
        // count as potentially mutating. The live check honors a mid-turn approved
        // ExitPlanMode.
        if (context.IsPlanModeActive && !Security.ShellCommandInspector.IsReadOnly(command))
        {
            return ToolResult.Error(
                "Plan mode allows only provably read-only commands (no pipes, redirection or mutating executables). " +
                "Gather information another way, or present the plan and let the user leave plan mode.");
        }

        if (JsonArgs.GetBool(arguments, "run_in_background"))
        {
            if (context.BackgroundTasks is null)
                return ToolResult.Error("Background tasks are not available in this context.");
            var taskId = context.BackgroundTasks.Start(
                command, context.WorkingDirectory, context.SessionId,
                JsonArgs.GetString(arguments, "description"));
            return ToolResult.Success(BackgroundResult(
                taskId,
                context.BackgroundTasks.Get(taskId)?.OutputFile,
                context.WorkingDirectory,
                command,
                readToolName: "Read"));
        }

        var timeout = context.ShellTimeout;
        // The reference's argument is `timeout`; this tool's older `timeout_ms` is still read.
        if ((JsonArgs.GetInt(arguments, "timeout") ?? JsonArgs.GetInt(arguments, "timeout_ms")) is int requestedMs && requestedMs > 0)
            timeout = TimeSpan.FromMilliseconds(Math.Min(requestedMs, (int)MaxTimeout.TotalMilliseconds));

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = context.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!RunsBash)
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
            startInfo.FileName = FindBash();
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        var process = new Process { StartInfo = startInfo };
        var movedToBackground = false;
        try
        {
            try
            {
                if (!process.Start())
                    return ToolResult.Error("Failed to start the shell process.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return ToolResult.Error($"Failed to start the shell process: {ex.Message}");
            }

            process.StandardInput.Close();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            // Output is pumped into buffers rather than read to the end, so a call
            // moved to the background can keep streaming from the same read.
            var stdoutSink = new OutputSink();
            var stderrSink = new OutputSink();
            var stdoutTask = PumpAsync(process.StandardOutput, stdoutSink);
            var stderrTask = PumpAsync(process.StandardError, stderrSink);

            // The reference's "Run in background": while this call runs, the host may
            // ask it to carry on off the turn.
            var moves = context.BackgroundTasks is not null ? context.BackgroundMoves : null;
            var callId = context.CallId;
            var moveToken = moves is not null && callId is not null
                ? moves.Register(callId)
                : CancellationToken.None;
            using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, moveToken);
            try
            {
                await process.WaitForExitAsync(waitSource.Token);
            }
            catch (OperationCanceledException)
            {
                if (moveToken.IsCancellationRequested && context.BackgroundTasks is { } tasks)
                {
                    movedToBackground = true;
                    moves?.Release(callId!);
                    var adopted = tasks.Adopt(
                        command, context.SessionId, JsonArgs.GetString(arguments, "description"),
                        () => TryKill(process));
                    stdoutSink.AttachAndFlush(adopted.Append);
                    stderrSink.AttachAndFlush(adopted.Append);
                    DrainInBackground(process, stdoutTask, stderrTask, adopted);
                    return ToolResult.Success(
                        $"Moved to the background as {adopted.Id}; it keeps running there. " +
                        $"Poll it with TaskOutput(task_id=\"{adopted.Id}\"); stop it with TaskStop.");
                }

                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                    throw;
                // Claw-code test-hang classification: a timed-out test runner is almost
                // always a hung/input-waiting test, not a slow suite — say so.
                return ToolResult.Error(IsTestCommand(command)
                    ? $"Test run hung: no completion after {timeout.TotalSeconds:0}s; the process was terminated. " +
                      $"This usually means a hanging test or one waiting for input — not a slow suite. " +
                      $"Investigate which test hangs (run a subset or add a per-test timeout): {command}"
                    : $"Command timed out after {timeout.TotalSeconds:0}s and was terminated: {command}");
            }
            finally
            {
                if (!movedToBackground && callId is not null)
                {
                    moves?.Release(callId);
                }
            }

            await DrainAsync(stdoutTask, stderrTask);
            var stdout = stdoutSink.Text;
            var stderr = stderrSink.Text;

            var builder = new StringBuilder();
            if (stdout.Length > 0)
                builder.AppendLine(stdout.TrimEnd());
            if (stderr.Length > 0)
            {
                builder.AppendLine("--- stderr ---");
                builder.AppendLine(stderr.TrimEnd());
            }
            if (builder.Length == 0)
                builder.AppendLine("(no output)");
            if (process.ExitCode != 0)
                builder.AppendLine($"(exit code {process.ExitCode})");

            var output = context.Truncate(builder.ToString().TrimEnd(), "command output");
            return process.ExitCode == 0 ? ToolResult.Success(output) : ToolResult.Error(output);
        }
        finally
        {
            // A moved call's process now belongs to the background task.
            if (!movedToBackground)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// A growing output buffer a background task can start reading mid-flight:
    /// attaching flushes what has arrived so far, then every later chunk follows.
    /// </summary>
    private sealed class OutputSink
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();
        private Action<string>? _onChunk;

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _builder.ToString();
                }
            }
        }

        public void Append(string chunk)
        {
            Action<string>? sink;
            lock (_gate)
            {
                _builder.Append(chunk);
                sink = _onChunk;
            }

            sink?.Invoke(chunk);
        }

        public void AttachAndFlush(Action<string> sink)
        {
            // Both the backlog and the hand-over happen under the lock, or a
            // chunk arriving mid-attach could overtake the output before it.
            lock (_gate)
            {
                if (_builder.Length > 0)
                {
                    sink(_builder.ToString());
                }

                _onChunk = sink;
            }
        }
    }

    /// <summary>
    /// Gives the output reads <see cref="DrainAfterExit"/> to finish once the
    /// process is over, then leaves them: a pipe an exited command's descendants
    /// still hold reaches its end only when the last of them does.
    /// </summary>
    private static async Task DrainAsync(Task stdout, Task stderr)
    {
        var reads = Task.WhenAll(stdout, stderr);
        if (await Task.WhenAny(reads, Task.Delay(DrainAfterExit)) == reads)
        {
            await reads;
        }
    }

    private static async Task PumpAsync(StreamReader reader, OutputSink sink)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, CancellationToken.None);
                if (read == 0)
                {
                    return;
                }

                sink.Append(new string(buffer, 0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A killed or timed-out process takes its pipes down under the read;
            // whatever arrived before that is already in the sink.
        }
    }

    /// <summary>Finishes a moved call off the turn: drain the pipes, then settle the task.</summary>
    private static void DrainInBackground(
        Process process,
        Task stdout,
        Task stderr,
        BackgroundTasks.BackgroundTaskManager.AdoptedTask adopted) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
                await DrainAsync(stdout, stderr);
                adopted.Complete(process.ExitCode);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
            {
                adopted.Complete(null);
            }
            finally
            {
                process.Dispose();
            }
        });

    private static readonly string[] TestCommandFragments =
    [
        "dotnet test", "cargo test", "cargo nextest", "npm test", "pnpm test",
        "yarn test", "pytest", "go test", "vstest", "ctest", "gradle test", "mvn test",
    ];

    private static bool IsTestCommand(string command) =>
        TestCommandFragments.Any(fragment => command.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already exited between the timeout and the kill, or handed to a
            // background task that has since finished and disposed it.
        }
    }
}
