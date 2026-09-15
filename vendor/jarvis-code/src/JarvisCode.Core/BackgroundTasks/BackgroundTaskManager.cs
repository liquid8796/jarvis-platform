using System.Diagnostics;
using System.Text;

namespace JarvisCode.Core.BackgroundTasks;

public enum BackgroundTaskStatus
{
    Running,
    Completed,
    Killed,
}

public sealed record BackgroundTaskInfo(
    string Id,
    string Command,
    BackgroundTaskStatus Status,
    int? ExitCode,
    string Output,
    string? SessionId = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? Description = null,
    /// <summary>What kind of work this is ("Workflow", …); null is a shell command.</summary>
    string? Kind = null,
    /// <summary>The file the task's output is mirrored to, when the manager has an output directory.</summary>
    string? OutputFile = null);

/// <summary>
/// Runs shell commands detached from the agent turn (dev servers, long builds).
/// Output is collected continuously with a bounded tail; tasks survive across
/// turns and are killed when the app shuts down.
/// </summary>
public sealed class BackgroundTaskManager : IDisposable
{
    private const int MaxOutputChars = 200_000;
    private const int TrimmedOutputChars = 150_000;

    private readonly Dictionary<string, TaskEntry> _tasks = [];
    private readonly object _lock = new();
    private int _nextId;

    /// <summary>
    /// Where each task's output is mirrored as <c>{id}.output</c> — the reference's
    /// per-session <c>tasks/</c> directory, which its background-shell result names
    /// so the model can Read interim output. Null keeps output in memory only.
    /// </summary>
    public Func<string, string?>? OutputDirectoryFor { get; set; }

    /// <summary>The output file a task of this id would write, or null without a directory.</summary>
    public string? OutputFileFor(string taskId, string? workingDirectory)
    {
        var directory = workingDirectory is null ? null : OutputDirectoryFor?.Invoke(workingDirectory);
        return directory is null ? null : Path.Combine(directory, taskId + ".output");
    }

    /// <summary>
    /// Fires once when a task's process exits — completed, failed, or killed —
    /// with a snapshot of its final state. Raised on the process-exit thread;
    /// UI subscribers must marshal themselves. Observational and optional.
    /// </summary>
    public event Action<BackgroundTaskInfo>? TaskExited;

    private sealed class TaskEntry
    {
        public required string Id { get; init; }
        public required string Command { get; init; }

        /// <summary>Null for an adopted task: its process belongs to whoever handed it over.</summary>
        public Process? Process { get; init; }

        /// <summary>How to stop an adopted task, since this manager does not own its process.</summary>
        public Action? KillRequested { get; init; }

        public string? SessionId { get; init; }
        public string? Description { get; init; }
        public string? Kind { get; init; }
        public StringBuilder Output { get; } = new();
        public string? OutputFile { get; set; }
        public BackgroundTaskStatus Status { get; set; } = BackgroundTaskStatus.Running;
        public int? ExitCode { get; set; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public DateTimeOffset? CompletedAt { get; set; }
    }

    public string Start(
        string command, string workingDirectory, string? sessionId = null, string? description = null,
        IReadOnlyDictionary<string, string>? environment = null, string? kind = null,
        Action<string, string>? onStdout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (environment is not null)
        {
            // Added to the inherited block rather than replacing it: a dev server needs
            // PATH and the rest of the user's environment as much as it needs PORT.
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }
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

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        TaskEntry entry;
        lock (_lock)
        {
            entry = new TaskEntry
            {
                Id = $"task-{++_nextId}",
                Command = command,
                Process = process,
                SessionId = sessionId,
                Description = description,
                Kind = kind,
            };
            _tasks[entry.Id] = entry;
            entry.OutputFile = OpenOutputFile(entry.Id, workingDirectory);
        }

        process.OutputDataReceived += (_, e) =>
        {
            Append(entry, e.Data);
            if (e.Data is { } line) onStdout?.Invoke(entry.Id, line);
        };
        process.ErrorDataReceived += (_, e) => Append(entry, e.Data);
        process.Exited += (_, _) =>
        {
            BackgroundTaskInfo snapshot;
            lock (_lock)
            {
                if (entry.Status == BackgroundTaskStatus.Running)
                {
                    entry.Status = BackgroundTaskStatus.Completed;
                    try
                    {
                        entry.ExitCode = process.ExitCode;
                    }
                    catch (InvalidOperationException)
                    {
                        // Process handle already released.
                    }
                }

                entry.CompletedAt ??= DateTimeOffset.Now;
                snapshot = Snapshot(entry);
            }

            TaskExited?.Invoke(snapshot);
        };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return entry.Id;
    }

    /// <summary>
    /// Takes over a process someone else started — the reference's "Run in
    /// background", where a tool call that is already running carries on off the
    /// turn. The caller keeps reading the process and feeds the handle; this
    /// manager only lists it, bounds its output, and relays a stop request.
    /// </summary>
    public AdoptedTask Adopt(
        string command, string? sessionId, string? description, Action killRequested, string? kind = null,
        string? workingDirectory = null)
    {
        TaskEntry entry;
        lock (_lock)
        {
            entry = new TaskEntry
            {
                Id = $"task-{++_nextId}",
                Command = command,
                Process = null,
                KillRequested = killRequested,
                SessionId = sessionId,
                Description = description,
                Kind = kind,
            };
            _tasks[entry.Id] = entry;
            entry.OutputFile = OpenOutputFile(entry.Id, workingDirectory);
        }

        return new AdoptedTask(this, entry.Id);
    }

    /// <summary>A task this manager lists but does not run; its owner feeds it.</summary>
    public sealed class AdoptedTask(BackgroundTaskManager manager, string id)
    {
        public string Id => id;

        /// <summary>Adds output as it arrives, bounded like any other task's.</summary>
        public void Append(string text) => manager.AppendAdopted(id, text);

        /// <summary>The process ended; a null exit code reads as killed.</summary>
        public void Complete(int? exitCode) => manager.CompleteAdopted(id, exitCode);
    }

    private void AppendAdopted(string id, string text)
    {
        if (text.Length == 0)
            return;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var entry))
                return;
            entry.Output.Append(text);
            TrimLocked(entry);
            MirrorLocked(entry, text);
        }
    }

    private void CompleteAdopted(string id, int? exitCode)
    {
        BackgroundTaskInfo snapshot;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var entry))
                return;
            if (entry.Status == BackgroundTaskStatus.Running)
            {
                entry.Status = exitCode is null ? BackgroundTaskStatus.Killed : BackgroundTaskStatus.Completed;
                entry.ExitCode = exitCode;
            }

            entry.CompletedAt ??= DateTimeOffset.Now;
            snapshot = Snapshot(entry);
        }

        TaskExited?.Invoke(snapshot);
    }

    private void Append(TaskEntry entry, string? line)
    {
        if (line is null)
            return;
        lock (_lock)
        {
            entry.Output.AppendLine(line);
            TrimLocked(entry);
            MirrorLocked(entry, line + "\n");
        }
    }

    /// <summary>Creates the task's output file when a directory is configured; null when it cannot be.</summary>
    private string? OpenOutputFile(string taskId, string? workingDirectory)
    {
        var path = OutputFileFor(taskId, workingDirectory);
        if (path is null)
            return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Appends to the task's output file, forgetting the file on the first failure.</summary>
    private static void MirrorLocked(TaskEntry entry, string text)
    {
        if (entry.OutputFile is not { } path)
            return;
        try
        {
            File.AppendAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry.OutputFile = null;
        }
    }

    private static void TrimLocked(TaskEntry entry)
    {
        if (entry.Output.Length <= MaxOutputChars)
            return;
        var tail = entry.Output.ToString(entry.Output.Length - TrimmedOutputChars, TrimmedOutputChars);
        entry.Output.Clear();
        entry.Output.Append("[… earlier output trimmed]\n").Append(tail);
    }

    public BackgroundTaskInfo? Get(string id)
    {
        lock (_lock)
        {
            return _tasks.TryGetValue(id, out var entry) ? Snapshot(entry) : null;
        }
    }

    public IReadOnlyList<BackgroundTaskInfo> List()
    {
        lock (_lock)
        {
            return [.. _tasks.Values.Select(Snapshot)];
        }
    }

    public bool Kill(string id)
    {
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var entry) || entry.Status != BackgroundTaskStatus.Running)
                return false;
            entry.Status = BackgroundTaskStatus.Killed;
            entry.CompletedAt = DateTimeOffset.Now;
            if (entry.Process is { } process)
            {
                TryKill(process);
            }
            else
            {
                entry.KillRequested?.Invoke();
            }

            return true;
        }
    }

    private static BackgroundTaskInfo Snapshot(TaskEntry entry) =>
        new(entry.Id, entry.Command, entry.Status, entry.ExitCode, entry.Output.ToString(), entry.SessionId,
            entry.StartedAt, entry.CompletedAt, entry.Description, entry.Kind, entry.OutputFile);

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited.
        }
    }

    public void Dispose()
    {
        List<TaskEntry> entries;
        lock (_lock)
        {
            entries = [.. _tasks.Values];
            _tasks.Clear();
        }
        foreach (var entry in entries)
        {
            var wasRunning = false;
            lock (_lock)
            {
                if (entry.Status == BackgroundTaskStatus.Running)
                {
                    wasRunning = true;
                    entry.Status = BackgroundTaskStatus.Killed;
                }
            }

            if (entry.Process is null)
            {
                // Adopted: its owner holds the process, so ask rather than kill.
                if (wasRunning) entry.KillRequested?.Invoke();
                continue;
            }

            TryKill(entry.Process);
            // The async output readers must wind down before Dispose, otherwise
            // their callbacks race a disposed Process and crash the host process.
            try
            {
                entry.Process.WaitForExit(3000);
                entry.Process.CancelOutputRead();
                entry.Process.CancelErrorRead();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process may never have started or already be fully reaped.
            }
            entry.Process.Dispose();
        }
    }
}
