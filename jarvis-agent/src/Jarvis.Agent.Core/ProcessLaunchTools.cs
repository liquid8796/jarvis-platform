using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Jarvis.Agent.Core.Auditing;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core;

public sealed partial class ProcessToolSet
{
    private sealed class LaunchProcessTool(ProcessToolSet owner, string operation) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = Describe(operation);

        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(operation switch
                {
                    "launch" => owner.Launch(arguments, context),
                    "get" => owner.GetLaunched(arguments, context),
                    _ => owner.StopLaunched(arguments, context)
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or
                Win32Exception or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                return Task.FromResult(ToolReply.Error(ex.Message));
            }
        }

        private static ToolDescriptor Describe(string operation) => new(
            "process." + operation,
            "process_" + operation,
            "process",
            operation switch
            {
                "launch" => "Launch an executable directly as a session-owned durable process so it is not reaped when a temporary shell exits. A non-elevated Windows process tree remains in an Agent-owned job object and survives temporary transport loss and session stop-work. run_as_administrator uses the standard Windows UAC prompt only, requires both Full permission and the separate local elevation switch, and is tracked as an elevated root process because a medium-integrity Agent cannot place it in the same job object.",
                "get" => "Read status for a session-owned process launched by process_launch.",
                _ => "Request termination of a session-owned process launched by process_launch. Non-elevated trees are terminated through their Agent-owned job object. Windows may refuse termination of an elevated root process from a medium-integrity Agent; status remains running and termination_unconfirmed is reported until it actually exits."
            },
            operation == "launch" ? LaunchSchema() : IdSchema(),
            ReadOnly: operation == "get",
            Sensitive: true);

        private static JsonElement LaunchSchema() => WireJson.Element(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["file_path"] = new { type = "string", minLength = 1, maxLength = 32768 },
                ["arguments"] = new { type = "array", maxItems = 128, items = new { type = "string", maxLength = 8192 } },
                ["working_directory"] = new { type = "string", minLength = 1, maxLength = 32768 },
                ["environment"] = new { type = "object", maxProperties = 128, additionalProperties = new { type = "string", maxLength = 32768 } },
                ["timeout_seconds"] = new { type = "integer", minimum = 0, maximum = 604800, @default = 0,
                    description = "Optional lifetime limit. Use 0 for no timeout; ownership and explicit stop/close/Agent-exit controls still apply." },
                ["run_as_administrator"] = new { type = "boolean", @default = false },
                ["window_style"] = new { type = "string", @enum = new[] { "normal", "minimized", "maximized", "hidden" }, @default = "normal" }
            },
            required = new[] { "file_path" },
            additionalProperties = false
        });

        private static JsonElement IdSchema() => WireJson.Element(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["launch_id"] = new { type = "string", pattern = "^lp_[a-f0-9]{32}$" }
            },
            required = new[] { "launch_id" },
            additionalProperties = false
        });
    }

    private ToolReply Launch(JsonElement arguments, AgentExecutionContext context)
    {
        context.RequireSessionIdentity();
        context.SessionCancellation.ThrowIfCancellationRequested();

        var rawPath = arguments.GetProperty("file_path").GetString() ?? "";
        var filePath = Path.GetFullPath(WorkspaceDirectories.ResolvePath(rawPath, context.Workspace));
        if (!Path.IsPathFullyQualified(filePath) || !File.Exists(filePath))
            throw new ArgumentException("file_path must resolve to an existing executable file.");
        var runAsAdministrator = arguments.TryGetProperty("run_as_administrator", out var elevationNode) && elevationNode.GetBoolean();
        if (runAsAdministrator)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows UAC elevation is available only on Windows.");
            if (!context.FullPermission) throw new UnauthorizedAccessException("run_as_administrator requires Full permission for process.launch.");
            if (!context.WindowsElevationAllowed || !_allowWindowsUacElevation())
                throw new UnauthorizedAccessException("Enable Windows UAC elevation in Tool permissions before using run_as_administrator.");
        }
        var environment = ParseLaunchEnvironment(arguments);
        if (runAsAdministrator && environment.Count > 0)
            throw new ArgumentException("Custom environment variables are not supported with Windows UAC launch.");
        var timeout = arguments.TryGetProperty("timeout_seconds", out var timeoutNode) ? timeoutNode.GetInt32() : 0;
        var workingDirectory = arguments.TryGetProperty("working_directory", out var cwdNode)
            ? WorkspaceDirectories.Normalize(WorkspaceDirectories.ResolvePath(cwdNode.GetString(), context.Workspace))
            : Path.GetDirectoryName(filePath)!;
        if (!Directory.Exists(workingDirectory))
            throw new ArgumentException("working_directory must resolve to an existing directory.");
        var style = arguments.TryGetProperty("window_style", out var styleNode) ? styleNode.GetString() ?? "normal" : "normal";
        LaunchedProcess process;
        lock (_startLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var maximum = _settings().MaxProcessJobs;
            if (RunningCount >= maximum)
                return ToolReply.Error($"PROCESS_LIMIT: {maximum} owned process jobs are already running.");
            process = new LaunchedProcess(filePath, ParseLaunchArguments(arguments), workingDirectory, environment,
                timeout, runAsAdministrator, style, context, CompletedLaunchedProcess);
            if (!_launched.TryAdd(process.Id, process))
            {
                process.Dispose();
                throw new InvalidOperationException("Launch ID collision.");
            }
        }
        _audit.Write(new(DateTimeOffset.UtcNow, "PROCESS_STARTED", "Session-owned process started.",
            "process.launch", context.SessionId, context.CallId, process.ProcessId));
        return new ToolReply(process.Snapshot());
    }

    private ToolReply GetLaunched(JsonElement arguments, AgentExecutionContext context)
    {
        context.RequireSessionIdentity();
        var process = OwnedLaunched(arguments, context);
        return new ToolReply(process.Snapshot());
    }

    private ToolReply StopLaunched(JsonElement arguments, AgentExecutionContext context)
    {
        context.RequireSessionIdentity();
        var process = OwnedLaunched(arguments, context);
        process.Stop("explicit_stop");
        var code = process.TerminationUnconfirmed ? "PROCESS_STOP_FAILED" : "PROCESS_STOP_REQUESTED";
        var message = process.TerminationUnconfirmed
            ? "Windows did not confirm termination of the session-owned process."
            : "Session-owned process stop requested.";
        _audit.Write(new(DateTimeOffset.UtcNow, code, message,
            "process.stop", context.SessionId, context.CallId, process.ProcessId));
        return new ToolReply(process.Snapshot());
    }

    private LaunchedProcess OwnedLaunched(JsonElement arguments, AgentExecutionContext context)
    {
        var id = arguments.GetProperty("launch_id").GetString() ?? "";
        if (!_launched.TryGetValue(id, out var process) || !process.BelongsTo(context))
            throw new InvalidOperationException("No owned launched process with this launch_id in this session.");
        return process;
    }

    private void CompletedLaunchedProcess(LaunchedProcess process)
    {
        var code = process.StopReason switch
        {
            "timeout" => "PROCESS_TIMEOUT",
            null => "PROCESS_EXITED",
            _ => "PROCESS_STOPPED"
        };
        var detail = process.StopReason switch
        {
            "timeout" => "Session-owned process reached its timeout.",
            null => "Session-owned process exited naturally.",
            _ => "Session-owned process stopped: " + process.StopReason + "."
        };
        _audit.Write(new(DateTimeOffset.UtcNow, code, detail,
            "process.launch", process.SessionId, null, process.ProcessId));
        foreach (var old in _launched.Values.Where(item => item.Done).OrderBy(item => item.StartedUtc)
                     .Take(Math.Max(0, _launched.Count - 50)).ToArray())
            if (_launched.TryRemove(old.Id, out var removed)) removed.Dispose();
    }

    private static IReadOnlyList<string> ParseLaunchArguments(JsonElement arguments) =>
        arguments.TryGetProperty("arguments", out var node)
            ? node.EnumerateArray().Select(item => item.GetString() ?? "").ToArray()
            : [];

    private static Dictionary<string, string> ParseLaunchEnvironment(JsonElement arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!arguments.TryGetProperty("environment", out var node)) return result;
        foreach (var property in node.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Contains('='))
                throw new ArgumentException("Invalid environment variable name.");
            result[property.Name] = property.Value.GetString() ?? "";
        }
        return result;
    }

    private sealed class LaunchedProcess : IDisposable
    {
        private readonly object _sync = new();
        private readonly Process _process;
        private readonly OwnedProcessLease? _lease;
        private readonly CancellationTokenSource _stop = new();
        private readonly Action<LaunchedProcess> _completed;
        private readonly bool _sessionless;
        private readonly string? _ownerId;
        private readonly string? _deviceId;
        private readonly string? _ownedSessionId;
        private bool _done;
        private int? _exitCode;
        private string? _stopReason;
        private bool _terminationUnconfirmed;
        private int _disposeRequested;
        private int _resourcesDisposed;

        public LaunchedProcess(string filePath, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, int timeoutSeconds, bool elevated, string windowStyle,
            AgentExecutionContext context, Action<LaunchedProcess> completed)
        {
            Elevated = elevated;
            _completed = completed;
            _ownerId = context.OwnerId;
            _deviceId = context.AgentDeviceId;
            _sessionless = AgentSessionRules.IsEphemeralExecutionId(context.SessionId);
            _ownedSessionId = _sessionless ? null : context.SessionId;
            SessionId = context.SessionId;
            var start = new ProcessStartInfo(filePath)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = elevated,
                Verb = elevated ? "runas" : "",
                CreateNoWindow = false,
                WindowStyle = windowStyle switch
                {
                    "minimized" => ProcessWindowStyle.Minimized,
                    "maximized" => ProcessWindowStyle.Maximized,
                    "hidden" => ProcessWindowStyle.Hidden,
                    _ => ProcessWindowStyle.Normal
                }
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            if (!elevated) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try
            {
                if (!_process.Start()) throw new InvalidOperationException("Windows did not start the requested process.");
                if (!elevated)
                {
                    _lease = new OwnedProcessLease();
                    _lease.Attach(_process);
                }
            }
            catch
            {
                _lease?.Dispose();
                _process.Dispose();
                _stop.Dispose();
                throw;
            }
            ProcessId = _process.Id;
            StartedUtc = DateTimeOffset.UtcNow;
            _ = MonitorAsync(timeoutSeconds);
        }

        public string Id { get; } = "lp_" + Guid.NewGuid().ToString("N");
        public int ProcessId { get; }
        public DateTimeOffset StartedUtc { get; }
        public string SessionId { get; }
        public bool Elevated { get; }
        public bool Done { get { lock (_sync) return _done; } }
        public string? StopReason { get { lock (_sync) return _stopReason; } }
        public bool TerminationUnconfirmed { get { lock (_sync) return _terminationUnconfirmed; } }

        public bool BelongsTo(AgentExecutionContext context)
        {
            if (_ownerId != context.OwnerId || _deviceId != context.AgentDeviceId) return false;
            return _sessionless ? AgentSessionRules.IsEphemeralExecutionId(context.SessionId) : _ownedSessionId == context.SessionId;
        }

        public bool BelongsTo(AgentSessionIdentity identity) => !_sessionless && _ownedSessionId == identity.SessionId &&
            _ownerId == identity.OwnerId && _deviceId == identity.DeviceId;

        public void Stop(string reason)
        {
            lock (_sync)
            {
                if (_done) return;
                _stopReason ??= reason;
            }
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            TryKill();
        }

        public string Snapshot()
        {
            var rootExited = false;
            try { rootExited = _process.HasExited; }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { rootExited = Done; }
            var activeProcesses = SafeActiveProcessCount(rootExited);
            lock (_sync)
                return JsonSerializer.Serialize(new
                {
                    launch_id = Id,
                    process_id = ProcessId,
                    running = !_done,
                    exit_code = _exitCode,
                    elevated = Elevated,
                    started_at = StartedUtc,
                    stop_reason = _stopReason,
                    termination_unconfirmed = _terminationUnconfirmed,
                    root_exited = rootExited,
                    active_process_count = activeProcesses
                }, WireJson.Options);
        }

        private async Task MonitorAsync(int timeoutSeconds)
        {
            using var deadline = new CancellationTokenSource();
            if (timeoutSeconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var terminationAttempted = false;
            try
            {
                while (true)
                {
                    var timedOut = timeoutSeconds > 0 && deadline.IsCancellationRequested;
                    var stopRequested = _stop.IsCancellationRequested;
                    if ((timedOut || stopRequested) && !terminationAttempted)
                    {
                        lock (_sync)
                            if (_stopReason is null && timedOut)
                                _stopReason = "timeout";
                        TryKill();
                        terminationAttempted = true;
                    }
                    var rootExited = false;
                    try { rootExited = _process.HasExited; } catch (InvalidOperationException) { rootExited = true; }
                    if (rootExited && SafeActiveProcessCount(rootExited) == 0) break;
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
            finally
            {
                int? exit = null;
                try { if (_process.HasExited) exit = _process.ExitCode; } catch (Exception) { }
                lock (_sync)
                {
                    _exitCode = exit;
                    _terminationUnconfirmed = false;
                    _done = true;
                }
                _lease?.Dispose();
                _completed(this);
                if (Volatile.Read(ref _disposeRequested) == 1) DisposeResources();
            }
        }

        private uint SafeActiveProcessCount(bool rootExited)
        {
            if (_lease is null) return rootExited ? 0u : 1u;
            try { return _lease.ActiveProcessCount; }
            catch (Win32Exception) { return rootExited ? 0u : 1u; }
        }

        private void TryKill()
        {
            var confirmed = false;
            var jobTerminationFailed = false;
            if (_lease is not null)
            {
                try { _lease.Stop(); confirmed = true; }
                catch (Win32Exception) { jobTerminationFailed = true; }
            }

            // A successful job-object termination is authoritative for the complete
            // non-elevated tree. Only fall back to the root-process API when that request
            // failed, or for elevated launches which cannot join the Agent's job object.
            if (_lease is null || jobTerminationFailed)
            {
                try
                {
                    if (_process.HasExited) confirmed = ObservedStopped();
                    else { _process.Kill(entireProcessTree: true); confirmed = true; }
                }
                catch (InvalidOperationException) { confirmed = ObservedStopped(); }
                catch (Win32Exception) { }
            }
            if (confirmed)
            {
                lock (_sync) _terminationUnconfirmed = false;
                return;
            }

            lock (_sync) _terminationUnconfirmed = !ObservedStopped();
        }

        private bool ObservedStopped()
        {
            try
            {
                var rootExited = _process.HasExited;
                return rootExited && (_lease is null || _lease.ActiveProcessCount == 0);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposeRequested, 1);
            Stop("disposed");
            if (Done) DisposeResources();
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
            _process.Dispose();
            _stop.Dispose();
        }
    }
}
