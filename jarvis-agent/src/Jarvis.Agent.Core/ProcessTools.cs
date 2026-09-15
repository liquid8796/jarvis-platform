using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jarvis.Protocol;
namespace Jarvis.Agent.Core;

/// <summary>Owned build/test jobs with bounded logs. No attaching to unrelated machine processes.</summary>
public sealed class ProcessToolSet : IDisposable
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly object _startLock = new();
    public IEnumerable<IAgentTool> Tools => [new ProcessTool(this, "start"), new ProcessTool(this, "read"), new ProcessTool(this, "cancel")];
    public void StopAll()
    {
        foreach (var job in _jobs.Values) job.Cancel();
    }
    public void Dispose() { StopAll(); foreach (var job in _jobs.Values) job.Dispose(); }
    private sealed class ProcessTool(ProcessToolSet owner, string operation) : IAgentTool
    {
        public ToolDescriptor Descriptor => new("process." + operation, "process__" + operation, "process",
            operation switch
            {
                "start" => "Start a build/test shell command with an optional workingDirectory. Project folders are not a sandbox. Requires local approval unless this tool has saved Full permission. Returns a job ID immediately; read logs with process__read.",
                "read" => "Read bounded output and exit status of a job created by this agent. Cursor is a character offset.",
                _ => "Cancel only a job created by this agent, including its child processes."
            }, WireJson.Element(operation == "start" ? new
            {
                type = "object", properties = new Dictionary<string, object>
                {
                    ["command"] = new { type = "string", minLength = 1, maxLength = 16000 },
                    ["timeoutSeconds"] = new { type = "integer", minimum = 1, maximum = 1800, @default = 600 },
                    ["workingDirectory"] = new { type = "string", minLength = 1, description = "Optional starting directory, including folders outside the selected projects. Defaults to the primary project." }
                }, required = new[] { "command" }, additionalProperties = false
            } : new
            {
                type = "object", properties = new Dictionary<string, object>
                {
                    ["jobId"] = new { type = "string" }, ["cursor"] = new { type = "integer", minimum = 0 }
                }, required = new[] { "jobId" }, additionalProperties = false
            }), operation == "read", operation == "start");
        public Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (operation == "start")
            {
                lock (owner._startLock)
                {
                if (owner._jobs.Values.Count(j => !j.Done) >= 4) return Task.FromResult(ToolReply.Error("Four jobs are already running."));
                var command = args.GetProperty("command").GetString();
                if (string.IsNullOrWhiteSpace(command) || command.Length > 16000) throw new ArgumentException("Invalid command.");
                var timeout = args.TryGetProperty("timeoutSeconds", out var n) ? n.GetInt32() : 600;
                if (timeout is < 1 or > 1800) throw new ArgumentException("timeoutSeconds must be 1..1800.");
                var cwd = WorkspaceDirectories.Normalize(args.TryGetProperty("workingDirectory", out var requested)
                    ? Path.GetFullPath(requested.GetString() ?? "", context.Workspace) : context.Workspace);
                var job = new Job(command, cwd, timeout);
                if (!owner._jobs.TryAdd(job.Id, job)) throw new InvalidOperationException("Job ID collision.");
                foreach (var old in owner._jobs.Values.Where(j => j.Done).OrderBy(j => j.Started).Take(Math.Max(0, owner._jobs.Count - 50)))
                    if (owner._jobs.TryRemove(old.Id, out var removed)) removed.Dispose();
                return Task.FromResult(new ToolReply(JsonSerializer.Serialize(new { jobId = job.Id, status = "running" }, WireJson.Options)));
                }
            }
            var id = args.GetProperty("jobId").GetString() ?? "";
            if (!owner._jobs.TryGetValue(id, out var found)) return Task.FromResult(ToolReply.Error("No owned job with this ID."));
            if (operation == "cancel") found.Cancel();
            var cursor = args.TryGetProperty("cursor", out var c) ? c.GetInt64() : 0;
            return Task.FromResult(new ToolReply(found.Snapshot(cursor)));
        }
    }
    private sealed class Job : IDisposable
    {
        private const int Limit = 128 * 1024;
        private readonly object _sync = new();
        private readonly StringBuilder _log = new();
        private long _offset;
        private readonly Process _process;
        private readonly OwnedProcessLease _lease;
        private readonly CancellationTokenSource _stop;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public bool Done { get; private set; }
        public int? ExitCode { get; private set; }
        public Job(string command, string cwd, int timeout)
        {
            _stop = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash")
            { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command"); }
            else start.ArgumentList.Add("-lc");
            start.ArgumentList.Add(command);
            _process = new Process { StartInfo = start };
            _lease = new OwnedProcessLease();
            try { _process.Start(); _lease.Attach(_process); }
            catch { _lease.Dispose(); _process.Dispose(); _stop.Dispose(); throw; }
            _ = RunAsync();
        }
        private async Task RunAsync()
        {
            var output = PumpAsync(_process.StandardOutput); var error = PumpAsync(_process.StandardError);
            try { await _process.WaitForExitAsync(_stop.Token); }
            catch (OperationCanceledException)
            {
                Kill(); Add("\n[jarvis: job cancelled or timed out]\n");
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            }
            finally
            {
                _lease.Dispose();
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
                lock (_sync) { ExitCode = _process.HasExited ? _process.ExitCode : null; Done = true; }
                _process.Dispose(); _stop.Dispose();
            }
        }
        private async Task PumpAsync(StreamReader stream)
        {
            var chunk = new char[2048];
            int n; while ((n = await stream.ReadAsync(chunk.AsMemory())) > 0) Add(new string(chunk, 0, n));
        }
        private void Add(string text)
        {
            lock (_sync)
            {
                _log.Append(text);
                if (_log.Length > Limit) { var trim = _log.Length - Limit; _log.Remove(0, trim); _offset += trim; }
            }
        }
        public string Snapshot(long cursor)
        {
            lock (_sync)
            {
                if (cursor < 0 || cursor > _offset + _log.Length) throw new ArgumentException("Invalid log cursor.");
                var index = (int)Math.Max(0, cursor - _offset);
                var text = _log.ToString(index, Math.Min(32000, _log.Length - index));
                return JsonSerializer.Serialize(new { jobId = Id, done = Done, exitCode = ExitCode,
                    truncated = cursor < _offset, cursor = _offset + index + text.Length, output = text }, WireJson.Options);
            }
        }
        public void Cancel() { lock (_sync) if (!Done) { try { _stop.Cancel(); Kill(); } catch (ObjectDisposedException) { } } }
        private void Kill() { _lease.Stop(); try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        public void Dispose() { Cancel(); /* RunAsync releases handles after draining output. */ }
    }
}
