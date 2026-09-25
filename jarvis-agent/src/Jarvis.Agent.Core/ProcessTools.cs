using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Jarvis.Protocol;
using Microsoft.Win32.SafeHandles;

namespace Jarvis.Agent.Core;

/// <summary>Owned build/test and interactive process jobs. Never attaches to unrelated machine processes.</summary>
public sealed partial class ProcessToolSet : IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedJob> _jobs = new();
    private readonly ConcurrentDictionary<long, string> _codexSessions = new();
    private readonly ConcurrentDictionary<long, long> _codexReadCursors = new();
    private long _nextCodexSessionId;
    private readonly object _startLock = new();
    private readonly Func<AgentExecutionSettings> _settings;
    private bool _disposed;
    public ProcessToolSet(Func<AgentExecutionSettings>? settings = null) => _settings = settings ?? (() => new AgentExecutionSettings());
    public int RunningCount => _jobs.Values.Count(job => !job.Done);
    public int RunningForSession(AgentSessionIdentity identity) => _jobs.Values.Count(job => !job.Done && job.BelongsTo(identity));
    public void StopSession(AgentSessionIdentity identity)
    {
        lock (_startLock) foreach (var job in _jobs.Values.Where(job => job.BelongsTo(identity))) job.Cancel();
    }

    public IEnumerable<IAgentTool> Tools =>
    [
        new CodexProcessTool(this, "exec_command"),
        new CodexProcessTool(this, "write_stdin")
    ];

    public void StopAll()
    {
        lock (_startLock) foreach (var job in _jobs.Values) job.Cancel();
    }

    public void Dispose()
    {
        lock (_startLock)
        {
            if (_disposed) return; _disposed = true;
            foreach (var job in _jobs.Values) job.Dispose();
        }
    }

    private sealed class ProcessTool(ProcessToolSet owner, string operation) : IAgentTool
    {
        public ToolDescriptor Descriptor => new(
            "process." + operation,
            "process__" + operation,
            "process",
            operation switch
            {
                "start" => "Start an owned build/test shell command. This compatibility tool invokes a shell; prefer process.spawn argv for exact execution. Returns a job ID immediately.",
                "spawn" => "Spawn an owned process from an argv array with optional environment overrides, working directory and Windows ConPTY. No shell is inserted.",
                "read" => "Read bounded output, structured stdout/stderr/pty events and exit status of a process created by this agent. Cursor is a character offset.",
                "write_stdin" => "Write text to stdin of an owned running process, optionally closing stdin afterwards.",
                "resize_pty" => "Resize a running owned Windows ConPTY process.",
                _ => "Cancel only a process created by this agent, including descendants owned by its job object."
            },
            Schema(operation),
            operation == "read",
            operation is "start" or "spawn" or "write_stdin" or "resize_pty");

        private static JsonElement Schema(string op) => WireJson.Element(op switch
        {
            "start" => new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["command"] = new { type = "string", minLength = 1, maxLength = 16000 },
                    ["timeoutSeconds"] = new { type = "integer", minimum = 1, maximum = 1800, @default = 600 },
                    ["workingDirectory"] = new { type = "string", minLength = 1, description = "Optional starting directory. Defaults to the selected workspace." }
                },
                required = new[] { "command" }, additionalProperties = false
            },
            "spawn" => new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["argv"] = new { type = "array", minItems = 1, maxItems = 128, items = new { type = "string", maxLength = 8192 } },
                    ["timeoutSeconds"] = new { type = "integer", minimum = 1, maximum = 1800, @default = 600 },
                    ["workingDirectory"] = new { type = "string", minLength = 1 },
                    ["environment"] = new { type = "object", maxProperties = 128, additionalProperties = new { type = "string", maxLength = 32768 } },
                    ["pty"] = new { type = "boolean", @default = false, description = "Use Windows ConPTY. Unsupported on non-Windows hosts." },
                    ["columns"] = new { type = "integer", minimum = 20, maximum = 500, @default = 100 },
                    ["rows"] = new { type = "integer", minimum = 5, maximum = 200, @default = 32 }
                },
                required = new[] { "argv" }, additionalProperties = false
            },
            "write_stdin" => new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["jobId"] = new { type = "string" },
                    ["text"] = new { type = "string", maxLength = 65536 },
                    ["close"] = new { type = "boolean", @default = false }
                },
                required = new[] { "jobId", "text" }, additionalProperties = false
            },
            "resize_pty" => new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["jobId"] = new { type = "string" },
                    ["columns"] = new { type = "integer", minimum = 20, maximum = 500 },
                    ["rows"] = new { type = "integer", minimum = 5, maximum = 200 }
                },
                required = new[] { "jobId", "columns", "rows" }, additionalProperties = false
            },
            _ => new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["jobId"] = new { type = "string" },
                    ["cursor"] = new { type = "integer", minimum = 0 }
                },
                required = new[] { "jobId" }, additionalProperties = false
            }
        });

        public async Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (operation is "start" or "spawn") return Start(args, context);

                var id = args.GetProperty("jobId").GetString() ?? "";
                if (!owner._jobs.TryGetValue(id, out var found) || !found.BelongsTo(context)) return ToolReply.Error("No owned job with this ID in this session.");

                if (operation == "cancel")
                {
                    found.Cancel();
                    return new ToolReply(found.Snapshot(args.TryGetProperty("cursor", out var cancelCursor) ? cancelCursor.GetInt64() : 0));
                }
                if (operation == "write_stdin")
                {
                    var text = args.GetProperty("text").GetString() ?? "";
                    if (text.Length > 65536) throw new ArgumentException("stdin text exceeds 65536 characters.");
                    await found.WriteStdinAsync(text, args.TryGetProperty("close", out var close) && close.GetBoolean(), ct);
                    return new ToolReply(JsonSerializer.Serialize(new { jobId = id, written = text.Length, closed = args.TryGetProperty("close", out close) && close.GetBoolean() }, WireJson.Options));
                }
                if (operation == "resize_pty")
                {
                    var columns = args.GetProperty("columns").GetInt32();
                    var rows = args.GetProperty("rows").GetInt32();
                    if (columns is < 20 or > 500 || rows is < 5 or > 200) throw new ArgumentException("PTY dimensions are out of range.");
                    found.ResizePty((short)columns, (short)rows);
                    return new ToolReply(JsonSerializer.Serialize(new { jobId = id, columns, rows }, WireJson.Options));
                }

                var cursor = args.TryGetProperty("cursor", out var c) ? c.GetInt64() : 0;
                return new ToolReply(found.Snapshot(cursor));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or Win32Exception or PlatformNotSupportedException)
            {
                return ToolReply.Error(ex.Message);
            }
        }

        private ToolReply Start(JsonElement args, AgentExecutionContext context)
        {
            lock (owner._startLock)
            {
                ObjectDisposedException.ThrowIf(owner._disposed, owner);
                context.SessionCancellation.ThrowIfCancellationRequested();
                var maximum = owner._settings().MaxProcessJobs;
                if (owner._jobs.Values.Count(j => !j.Done) >= maximum) return ToolReply.Error($"PROCESS_LIMIT: {maximum} owned process jobs are already running.");

                var timeout = args.TryGetProperty("timeoutSeconds", out var n) ? n.GetInt32() : 600;
                if (timeout is < 1 or > 1800) throw new ArgumentException("timeoutSeconds must be 1..1800.");
                var cwd = WorkspaceDirectories.Normalize(args.TryGetProperty("workingDirectory", out var requested)
                    ? WorkspaceDirectories.ResolvePath(requested.GetString(), context.Workspace) : WorkspaceDirectories.ResolvePath(null, context.Workspace));

                ManagedJob job;
                if (operation == "start")
                {
                    var command = args.GetProperty("command").GetString();
                    if (string.IsNullOrWhiteSpace(command) || command.Length > 16000) throw new ArgumentException("Invalid command.");
                    job = new PipedJob(ShellArgv(command), cwd, timeout, null);
                }
                else
                {
                    var argv = ParseArgv(args);
                    var environment = ParseEnvironment(args);
                    var pty = args.TryGetProperty("pty", out var ptyValue) && ptyValue.GetBoolean();
                    if (pty)
                    {
                        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PTY mode currently requires Windows ConPTY.");
                        var columns = (short)(args.TryGetProperty("columns", out var cols) ? cols.GetInt32() : 100);
                        var rows = (short)(args.TryGetProperty("rows", out var r) ? r.GetInt32() : 32);
                        if (columns is < 20 or > 500 || rows is < 5 or > 200) throw new ArgumentException("PTY dimensions are out of range.");
                        job = new ConPtyJob(argv, cwd, timeout, environment, columns, rows);
                    }
                    else job = new PipedJob(argv, cwd, timeout, environment);
                }

                job.BindOwner(context);
                if (!owner._jobs.TryAdd(job.Id, job)) { job.Dispose(); throw new InvalidOperationException("Job ID collision."); }
                foreach (var old in owner._jobs.Values.Where(j => j.Done).OrderBy(j => j.Started).Take(Math.Max(0, owner._jobs.Count - 50)))
                    if (owner._jobs.TryRemove(old.Id, out var removed)) removed.Dispose();
                return new ToolReply(JsonSerializer.Serialize(new { jobId = job.Id, status = "running", pty = job.IsPty }, WireJson.Options));
            }
        }

        private static string[] ParseArgv(JsonElement args)
        {
            if (!args.TryGetProperty("argv", out var value) || value.ValueKind != JsonValueKind.Array) throw new ArgumentException("argv must be an array.");
            var argv = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : throw new ArgumentException("argv items must be strings.")).ToArray();
            if (argv.Length is < 1 or > 128 || string.IsNullOrWhiteSpace(argv[0]) || argv.Any(x => x.Length > 8192)) throw new ArgumentException("Invalid argv.");
            return argv;
        }

        private static Dictionary<string, string>? ParseEnvironment(JsonElement args)
        {
            if (!args.TryGetProperty("environment", out var value)) return null;
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("environment must be an object.");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (result.Count >= 128 || string.IsNullOrWhiteSpace(property.Name) || property.Name.Contains('=') || property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Invalid environment override.");
                var text = property.Value.GetString() ?? "";
                if (text.Length > 32768) throw new ArgumentException("Environment value is too long.");
                result[property.Name] = text;
            }
            return result;
        }

        private static string[] ShellArgv(string command) => OperatingSystem.IsWindows()
            ? ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command]
            : ["/bin/bash", "-lc", command];
    }

    private abstract class ManagedJob : IDisposable
    {
        private const int LogLimit = 128 * 1024;
        private readonly object _sync = new();
        private readonly StringBuilder _log = new();
        private readonly List<OutputChunk> _events = [];
        private long _offset;
        private bool _done;
        private int? _exitCode;
        private string? _ownerId, _deviceId, _sessionId;
        private bool _sessionless;
        private CancellationTokenRegistration _sessionCancellation;
        private IDisposable? _resourceLease;
        public bool BelongsTo(AgentExecutionContext context)
        {
            if (_ownerId != context.OwnerId || _deviceId != context.AgentDeviceId) return false;
            return _sessionless ? AgentSessionRules.IsEphemeralExecutionId(context.SessionId) : _sessionId == context.SessionId;
        }
        public bool BelongsTo(AgentSessionIdentity identity) => !_sessionless && _sessionId == identity.SessionId && _ownerId == identity.OwnerId && _deviceId == identity.DeviceId;
        public void BindOwner(AgentExecutionContext context)
        {
            lock (_sync)
            {
                _ownerId = context.OwnerId; _deviceId = context.AgentDeviceId;
                _sessionless = AgentSessionRules.IsEphemeralExecutionId(context.SessionId);
                _sessionId = _sessionless ? null : context.SessionId;
                _resourceLease = context.RetainResources?.Invoke();
                if (_done) { _resourceLease?.Dispose(); _resourceLease = null; }
                if (!_done) _sessionCancellation = context.SessionCancellation.UnsafeRegister(static job => ((ManagedJob)job!).Cancel(), this);
            }
        }

        protected ManagedJob(bool isPty) => IsPty = isPty;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public bool IsPty { get; }
        public bool Done { get { lock (_sync) return _done; } }

        protected void Add(string stream, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_sync)
            {
                var start = _offset + _log.Length;
                _log.Append(text);
                _events.Add(new OutputChunk(start, stream, text));
                if (_log.Length > LogLimit)
                {
                    var trim = _log.Length - LogLimit;
                    _log.Remove(0, trim);
                    _offset += trim;
                    _events.RemoveAll(e => e.Start + e.Text.Length <= _offset);
                }
            }
        }

        protected void Complete(int? exitCode)
        {
            lock (_sync) { _exitCode = exitCode; _done = true; _sessionCancellation.Unregister(); _resourceLease?.Dispose(); _resourceLease = null; }
        }

        public string Snapshot(long cursor)
        {
            lock (_sync)
            {
                var absoluteEnd = _offset + _log.Length;
                if (cursor < 0 || cursor > absoluteEnd) throw new ArgumentException("Invalid log cursor.");
                var actualStart = Math.Max(cursor, _offset);
                var index = checked((int)(actualStart - _offset));
                var length = Math.Min(32000, _log.Length - index);
                var text = _log.ToString(index, length);
                var actualEnd = actualStart + text.Length;
                var events = _events.Where(e => e.Start < actualEnd && e.Start + e.Text.Length > actualStart)
                    .Select(e =>
                    {
                        var from = (int)Math.Max(0, actualStart - e.Start);
                        var to = (int)Math.Min(e.Text.Length, actualEnd - e.Start);
                        return new { stream = e.Stream, text = e.Text[from..to] };
                    }).Where(e => e.text.Length > 0).ToArray();
                return JsonSerializer.Serialize(new
                {
                    jobId = Id,
                    done = _done,
                    exitCode = _exitCode,
                    pty = IsPty,
                    truncated = cursor < _offset,
                    cursor = actualEnd,
                    output = text,
                    events
                }, WireJson.Options);
            }
        }

        public abstract Task WriteStdinAsync(string text, bool close, CancellationToken ct);
        public virtual void ResizePty(short columns, short rows) => throw new InvalidOperationException("This job is not a PTY process.");
        public abstract void Cancel();
        public abstract void Dispose();
        private sealed record OutputChunk(long Start, string Stream, string Text);
    }

    private sealed class PipedJob : ManagedJob
    {
        private readonly Process _process;
        private readonly OwnedProcessLease _lease;
        private readonly CancellationTokenSource _stop;

        public PipedJob(IReadOnlyList<string> argv, string cwd, int timeout, IReadOnlyDictionary<string, string>? environment) : base(false)
        {
            _stop = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var start = new ProcessStartInfo(argv[0])
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            for (var i = 1; i < argv.Count; i++) start.ArgumentList.Add(argv[i]);
            if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            _process = new Process { StartInfo = start };
            _lease = new OwnedProcessLease();
            try { _process.Start(); _lease.Attach(_process); }
            catch { _lease.Dispose(); _process.Dispose(); _stop.Dispose(); throw; }
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            var output = PumpAsync(_process.StandardOutput, "stdout");
            var error = PumpAsync(_process.StandardError, "stderr");
            try { await _process.WaitForExitAsync(_stop.Token); }
            catch (OperationCanceledException)
            {
                Kill(); Add("system", "\n[jarvis: job cancelled or timed out]\n");
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            }
            finally
            {
                try { _process.StandardInput.Close(); } catch (Exception) { }
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
                var exitCode = _process.HasExited ? _process.ExitCode : (int?)null;
                _lease.Dispose();
                _process.Dispose();
                _stop.Dispose();
                Complete(exitCode);
            }
        }

        private async Task PumpAsync(StreamReader stream, string name)
        {
            while (await stream.ReadLineAsync() is { } line)
                Add(name, line + Environment.NewLine);
        }

        public override async Task WriteStdinAsync(string text, bool close, CancellationToken ct)
        {
            if (Done) throw new InvalidOperationException("Process has already exited.");
            await _process.StandardInput.WriteAsync(text.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
            if (close) _process.StandardInput.Close();
        }

        public override void Cancel()
        {
            if (Done) return;
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            Kill();
        }

        private void Kill()
        {
            _lease.Stop();
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }

        public override void Dispose() => Cancel();
    }

    private sealed class ConPtyJob : ManagedJob
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CreateSuspended = 0x00000004;
        private static readonly IntPtr PseudoConsoleAttribute = (IntPtr)0x20016;

        private IntPtr _console;
        private FileStream? _writer;
        private FileStream? _reader;
        private readonly Process _process;
        private readonly OwnedProcessLease _lease;
        private readonly CancellationTokenSource _stop;
        private int _cancelled;
        private readonly SemaphoreSlim _inputSerial = new(1, 1);
        private readonly object _consoleSync = new();

        public ConPtyJob(IReadOnlyList<string> argv, string cwd, int timeout, IReadOnlyDictionary<string, string>? environment, short columns, short rows) : base(true)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY requires Windows.");
            _stop = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            _lease = new OwnedProcessLease();
            SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
            IntPtr attributeList = IntPtr.Zero, environmentBlock = IntPtr.Zero;
            ProcessInformation pi = default;
            try
            {
                if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
                var hr = CreatePseudoConsole(new Coord { X = columns, Y = rows }, inputRead, outputWrite, 0, out _console);
                if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed.");

                var size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributeList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size) ||
                    !UpdateProcThreadAttribute(attributeList, 0, PseudoConsoleAttribute, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Pseudo-console attribute failed.");

                environmentBlock = BuildEnvironmentBlock(environment);
                var startup = new StartupInfoEx { AttributeList = attributeList };
                startup.StartupInfo.Cb = Marshal.SizeOf<StartupInfoEx>();
                // Explicit NULL handles keep redirected parent stdio out of the pseudoconsole child.
                startup.StartupInfo.Flags = 0x00000100; // STARTF_USESTDHANDLES
                var flags = ExtendedStartupInfoPresent | CreateSuspended | (environmentBlock != IntPtr.Zero ? CreateUnicodeEnvironment : 0);
                if (!CreateProcessW(null, BuildCommandLine(argv), IntPtr.Zero, IntPtr.Zero, false, flags, environmentBlock, cwd, ref startup, out pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{argv[0]}'.");

                _process = Process.GetProcessById(pi.ProcessId);
                _lease.Attach(_process);
                if (ResumeThread(pi.ThreadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume PTY process.");

                inputRead.Dispose(); inputRead = null;
                outputWrite.Dispose(); outputWrite = null;
                _writer = new FileStream(inputWrite, FileAccess.Write); inputWrite = null;
                _reader = new FileStream(outputRead, FileAccess.Read); outputRead = null;
                // Release the host's artificial reference only after the child has its console.
                // The host now flushes its final frame and closes output naturally when clients exit.
                hr = ConptyReleasePseudoConsole(_console);
                if (hr != 0) throw new Win32Exception(hr, "ConptyReleasePseudoConsole failed.");
            }
            catch
            {
                if (pi.ProcessHandle != IntPtr.Zero) TerminateProcess(pi.ProcessHandle, 1);
                if (_console != IntPtr.Zero) { ClosePseudoConsole(_console); _console = IntPtr.Zero; }
                _lease.Dispose(); _stop.Dispose();
                throw;
            }
            finally
            {
                inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
                if (pi.ThreadHandle != IntPtr.Zero) CloseHandle(pi.ThreadHandle);
                if (pi.ProcessHandle != IntPtr.Zero) CloseHandle(pi.ProcessHandle);
                if (attributeList != IntPtr.Zero) { DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
                if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
            }
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            var output = PumpAsync();
            try { await _process.WaitForExitAsync(_stop.Token); }
            catch (OperationCanceledException)
            {
                Kill(); Add("system", "\n[jarvis: PTY cancelled or timed out]\n");
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            }
            finally
            {
                try { await output.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    Add("system", "\n[jarvis: final PTY drain timed out; closing remaining console clients]\n");
                    Kill();
                }
                // Output is drained concurrently; never close the console merely because the root exited.
                CloseConsole();
                try { await output.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { Add("system", "\n[jarvis: PTY output cleanup incomplete]\n"); }
                var exitCode = _process.HasExited ? _process.ExitCode : (int?)null;
                _writer?.Dispose(); _reader?.Dispose();
                _lease.Dispose(); _process.Dispose(); _stop.Dispose();
                Complete(exitCode);
            }
        }

        private async Task PumpAsync()
        {
            // Read raw pipe chunks instead of waiting for a large character read to fill.
            // ConPTY blocks startup on DA1; a headless terminal must answer that query promptly.
            var decoder = new UTF8Encoding(false).GetDecoder();
            var handshake = new PtyStartupHandshake();
            var bytes = new byte[4096]; var chars = new char[4096];
            try
            {
                int read;
                while ((read = await _reader!.ReadAsync(bytes.AsMemory())) > 0)
                {
                    var count = decoder.GetChars(bytes.AsSpan(0, read), chars, false);
                    var text = new string(chars, 0, count);
                    Add("pty", text);
                    if (handshake.Observe(text))
                        await WriteInputAsync(PtyStartupHandshake.Response, false, _stop.Token);
                }
                var tail = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, true);
                if (tail > 0) Add("pty", new string(chars, 0, tail));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }

        public override Task WriteStdinAsync(string text, bool close, CancellationToken ct)
        {
            if (Done) throw new InvalidOperationException("Process has already exited.");
            return WriteInputAsync(text, close, ct);
        }
        private async Task WriteInputAsync(string text, bool close, CancellationToken ct)
        {
            await _inputSerial.WaitAsync(ct);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await _writer!.WriteAsync(bytes, ct);
                await _writer.FlushAsync(ct);
                if (close) _writer.Dispose();
            }
            finally { _inputSerial.Release(); }
        }

        public override void ResizePty(short columns, short rows)
        {
            lock (_consoleSync)
            {
                if (Done || _console == IntPtr.Zero) throw new InvalidOperationException("PTY is no longer running.");
                var hr = ResizePseudoConsole(_console, new Coord { X = columns, Y = rows });
                if (hr != 0) throw new Win32Exception(hr, "ResizePseudoConsole failed.");
            }
        }

        public override void Cancel()
        {
            if (Done || Interlocked.Exchange(ref _cancelled, 1) != 0) return;
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            Kill();
        }

        private void Kill()
        {
            _lease.Stop();
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }

        private void CloseConsole()
        {
            lock (_consoleSync)
            {
                var console = Interlocked.Exchange(ref _console, IntPtr.Zero);
                if (console != IntPtr.Zero) ClosePseudoConsole(console);
            }
        }

        public override void Dispose() => Cancel();

        private static string BuildCommandLine(IReadOnlyList<string> argv) => string.Join(" ", argv.Select(QuoteWindowsArgument));

        private static string QuoteWindowsArgument(string arg)
        {
            if (arg.Length > 0 && !arg.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return arg;
            var sb = new StringBuilder().Append('"');
            var slashes = 0;
            foreach (var ch in arg)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"')
                {
                    sb.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                if (slashes > 0) { sb.Append('\\', slashes); slashes = 0; }
                sb.Append(ch);
            }
            if (slashes > 0) sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
        {
            if (overrides is null || overrides.Count == 0) return IntPtr.Zero;
            var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key && entry.Value is string value) environment[key] = value;
            foreach (var pair in overrides) environment[pair.Key] = pair.Value;
            var block = string.Join('\0', environment.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            return Marshal.StringToHGlobalUni(block);
        }

        [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X; public short Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
        {
            public int Cb; public IntPtr Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, Reserved2; public IntPtr ReservedPtr, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
        {
            public IntPtr ProcessHandle; public IntPtr ThreadHandle; public int ProcessId; public int ThreadId;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);
        [DllImport("conpty.dll", EntryPoint = "ConptyCreatePseudoConsole")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("conpty.dll", EntryPoint = "ConptyResizePseudoConsole")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("conpty.dll", EntryPoint = "ConptyClosePseudoConsole")] private static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("conpty.dll")] private static extern int ConptyReleasePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessW(string? applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
