using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Jint;
using Jint.Runtime;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.ToolPrograms;

public sealed record SessionToolReplLimits(
    int MaxCodeChars = 100_000,
    int MaxSessions = 64,
    int MaxCellsPerSession = 64,
    int MaxToolCallsPerCell = 64,
    int MaxOutputCharsPerCell = 256_000,
    int MaxImagesPerCell = 8,
    int MaxImageBase64CharsPerCell = 32 * 1024 * 1024,
    int MaxStatementsPerCell = 100_000,
    long MaxMemoryBytesPerSession = 32 * 1024 * 1024,
    TimeSpan? MaxCellElapsed = null)
{
    public TimeSpan CellElapsedLimit => MaxCellElapsed ?? TimeSpan.FromMinutes(10);
}

/// <summary>
/// Persistent, session-bound JavaScript tool runtime. JavaScript itself has no direct OS, CLR,
/// filesystem, process or network capability. The only effectful bridge is the dynamically rebuilt
/// tools object, and every nested call re-enters AgentConnection's guarded invocation path.
/// </summary>
public sealed class SessionToolReplHost : IAsyncDisposable
{
    public const string ExecId = "tool_repl.exec";
    public const string WaitId = "tool_repl.wait";
    public const string SleepId = "tool_repl.sleep";
    public const string ResetId = "tool_repl.reset";

    private static readonly HashSet<string> BlockedNestedTools = new(StringComparer.Ordinal)
    {
        ExecId, WaitId, SleepId, ResetId, "tool_script.run", "tool_program.run"
    };

    private readonly GuardedToolInvoker _invoke;
    private readonly Func<DynamicToolSnapshot> _catalog;
    private readonly SessionToolReplLimits _limits;
    private readonly ConcurrentDictionary<AgentSessionIdentity, ReplSession> _sessions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sessionSync = new();

    public SessionToolReplHost(GuardedToolInvoker invoke, Func<DynamicToolSnapshot> catalog,
        SessionToolReplLimits? limits = null)
    {
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _limits = limits ?? new SessionToolReplLimits();
        if (_limits.MaxCodeChars < 1 || _limits.MaxSessions < 1 || _limits.MaxCellsPerSession < 1 ||
            _limits.MaxToolCallsPerCell < 1 || _limits.MaxOutputCharsPerCell < 1 ||
            _limits.MaxImagesPerCell < 1 || _limits.MaxImageBase64CharsPerCell < 1 ||
            _limits.MaxStatementsPerCell < 1 || _limits.MaxMemoryBytesPerSession < 1 ||
            _limits.CellElapsedLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public IReadOnlyList<IAgentTool> CreateTools() =>
    [
        new ReplExecTool(this, _limits.MaxCodeChars),
        new ReplWaitTool(this),
        new ReplSleepTool(),
        new ReplResetTool(this)
    ];

    public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context,
        CancellationToken cancellationToken)
    {
        var identity = context.RequireSessionIdentity();
        var code = arguments.GetProperty("code").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || code.Length > _limits.MaxCodeChars)
            throw new ArgumentException($"code must contain 1..{_limits.MaxCodeChars} characters.");
        var yield = arguments.TryGetProperty("yield_time_ms", out var yieldNode) ? yieldNode.GetInt32() : 10_000;
        var maxTokens = arguments.TryGetProperty("max_output_tokens", out var tokensNode) ? tokensNode.GetInt32() : 10_000;
        var session = GetSession(identity);
        return await session.StartAsync(code, context, yield, maxTokens, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolReply> WaitAsync(JsonElement arguments, AgentExecutionContext context,
        CancellationToken cancellationToken)
    {
        var identity = context.RequireSessionIdentity();
        var cellId = arguments.GetProperty("cell_id").GetString() ?? string.Empty;
        var yield = arguments.TryGetProperty("yield_time_ms", out var yieldNode) ? yieldNode.GetInt32() : 5_000;
        var maxTokens = arguments.TryGetProperty("max_output_tokens", out var tokensNode) ? tokensNode.GetInt32() : 10_000;
        var terminate = arguments.TryGetProperty("terminate", out var terminateNode) && terminateNode.GetBoolean();
        if (!_sessions.TryGetValue(identity, out var session))
            throw new KeyNotFoundException("No Tool REPL exists for this session. Call exec first.");
        return await session.WaitAsync(cellId, yield, maxTokens, terminate, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolReply> ResetAsync(AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var identity = context.RequireSessionIdentity();
        if (!_sessions.TryGetValue(identity, out var session))
            return new ToolReply(WireJson.Element(new { reset = true, generation = 0, cancelledCells = 0 }).GetRawText());
        var result = await session.ResetAsync(cancellationToken).ConfigureAwait(false);
        return new ToolReply(WireJson.Element(new
        {
            reset = true,
            generation = result.Generation,
            cancelledCells = result.CancelledCells
        }).GetRawText());
    }

    public void Reset(AgentSessionIdentity identity)
    {
        if (_sessions.TryRemove(identity, out var session)) _ = session.DisposeAsync().AsTask();
    }

    public void CancelAll(string reason)
    {
        foreach (var session in _sessions.Values) session.CancelActive(reason);
    }

    private ReplSession GetSession(AgentSessionIdentity identity)
    {
        if (_sessions.TryGetValue(identity, out var current)) return current;
        lock (_sessionSync)
        {
            if (_sessions.TryGetValue(identity, out current)) return current;
            if (_sessions.Count >= _limits.MaxSessions)
            {
                foreach (var pair in _sessions.Where(pair => !pair.Value.HasRunningCell)
                             .OrderBy(pair => pair.Value.LastUsedAt)
                             .Take(Math.Max(1, _sessions.Count - _limits.MaxSessions + 1)).ToArray())
                    if (_sessions.TryRemove(pair.Key, out var removed)) _ = removed.DisposeAsync().AsTask();
            }
            if (_sessions.Count >= _limits.MaxSessions)
                throw new InvalidOperationException("Tool REPL session capacity is full. Reset or close an idle Jarvis session.");
            current = new ReplSession(_invoke, _catalog, _limits, _lifetime.Token);
            if (!_sessions.TryAdd(identity, current))
            {
                _ = current.DisposeAsync().AsTask();
                return _sessions[identity];
            }
            return current;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var sessions = _sessions.ToArray();
        _sessions.Clear();
        foreach (var session in sessions) await session.Value.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class ReplSession : IAsyncDisposable
    {
        private readonly GuardedToolInvoker _invoke;
        private readonly Func<DynamicToolSnapshot> _catalog;
        private readonly SessionToolReplLimits _limits;
        private readonly CancellationToken _hostLifetime;
        private readonly SemaphoreSlim _engineLock = new(1, 1);
        private readonly object _sync = new();
        private readonly Dictionary<string, ReplCell> _cells = new(StringComparer.Ordinal);
        private CancellationTokenSource _generationStop = new();
        private Engine? _engine;
        private int _generation = 1;
        private bool _disposed;

        public DateTimeOffset LastUsedAt { get; private set; } = DateTimeOffset.UtcNow;
        public bool HasRunningCell { get { lock (_sync) return _cells.Values.Any(cell => !cell.IsTerminal); } }

        public ReplSession(GuardedToolInvoker invoke, Func<DynamicToolSnapshot> catalog,
            SessionToolReplLimits limits, CancellationToken hostLifetime)
        {
            _invoke = invoke;
            _catalog = catalog;
            _limits = limits;
            _hostLifetime = hostLifetime;
        }

        public async Task<ToolReply> StartAsync(string code, AgentExecutionContext context, int yieldTimeMs,
            int maxOutputTokens, CancellationToken requestCancellation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LastUsedAt = DateTimeOffset.UtcNow;
            ReplCell cell;
            CancellationTokenSource cellStop;
            lock (_sync)
            {
                PruneCells();
                if (_cells.Count >= _limits.MaxCellsPerSession)
                    throw new InvalidOperationException("Tool REPL cell history is full because too many cells are still active or unread.");
                var id = "cell_" + Guid.NewGuid().ToString("N");
                cellStop = CancellationTokenSource.CreateLinkedTokenSource(
                    _hostLifetime, context.SessionCancellation, _generationStop.Token);
                cellStop.CancelAfter(_limits.CellElapsedLimit);
                cell = new ReplCell(id, _generation, _limits, cellStop);
                _cells.Add(id, cell);
                cell.Runner = Task.Run(() => RunCellAsync(cell, code, context), CancellationToken.None);
            }

            try
            {
                if (yieldTimeMs > 0)
                {
                    var completed = await Task.WhenAny(cell.Runner!, Task.Delay(yieldTimeMs, requestCancellation)).ConfigureAwait(false);
                    if (completed == cell.Runner) await ObserveRunnerAsync(cell.Runner!).ConfigureAwait(false);
                }
                else if (cell.Runner!.IsCompleted) await ObserveRunnerAsync(cell.Runner).ConfigureAwait(false);
                requestCancellation.ThrowIfCancellationRequested();
                return cell.ReadDelta(maxOutputTokens);
            }
            catch (OperationCanceledException)
            {
                cell.Cancel("Initial exec request was cancelled.");
                throw;
            }
        }

        public async Task<ToolReply> WaitAsync(string cellId, int yieldTimeMs, int maxOutputTokens,
            bool terminate, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LastUsedAt = DateTimeOffset.UtcNow;
            ReplCell cell;
            lock (_sync)
                cell = _cells.TryGetValue(cellId, out var found) ? found :
                    throw new KeyNotFoundException("Tool REPL cell was not found in this session.");
            if (terminate) cell.Cancel("Terminated by wait request.");
            var before = cell.Version;
            if (!cell.IsTerminal && yieldTimeMs > 0)
                await cell.WaitForChangeAsync(before, TimeSpan.FromMilliseconds(yieldTimeMs), cancellationToken).ConfigureAwait(false);
            if (cell.Runner?.IsCompleted == true) await ObserveRunnerAsync(cell.Runner).ConfigureAwait(false);
            return cell.ReadDelta(maxOutputTokens);
        }

        public async Task<(int Generation, int CancelledCells)> ResetAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReplCell[] cells;
            CancellationTokenSource previous;
            int generation;
            lock (_sync)
            {
                previous = _generationStop;
                _generationStop = new CancellationTokenSource();
                generation = ++_generation;
                cells = _cells.Values.Where(cell => !cell.IsTerminal).ToArray();
                foreach (var cell in cells) cell.Cancel("Tool REPL reset.");
                _engine = null;
                _cells.Clear();
                LastUsedAt = DateTimeOffset.UtcNow;
            }
            previous.Cancel();
            previous.Dispose();
            var runners = cells.Select(cell => cell.Runner).Where(task => task is not null).Cast<Task>().ToArray();
            if (runners.Length > 0)
                try { await Task.WhenAll(runners).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            return (generation, cells.Length);
        }

        public void CancelActive(string reason)
        {
            lock (_sync)
                foreach (var cell in _cells.Values.Where(cell => !cell.IsTerminal)) cell.Cancel(reason);
        }

        private async Task RunCellAsync(ReplCell cell, string code, AgentExecutionContext context)
        {
            await _engineLock.WaitAsync(cell.Token).ConfigureAwait(false);
            var resetEngine = false;
            try
            {
                cell.MarkRunning();
                var engine = _engine ??= CreateEngine();
                var hostFailure = new StrongBox<Exception?>();
                var nestedCall = 0;

                async Task<string> InvokeToolAsync(string toolId, string argumentsJson)
                {
                    try
                    {
                        cell.Token.ThrowIfCancellationRequested();
                        var snapshot = _catalog();
                        if (!snapshot.Tools.TryGetValue(toolId, out var tool) ||
                            BlockedNestedTools.Contains(toolId) || AgentSessionRules.IsTool(toolId) || tool is ICompositeAgentTool)
                            throw new InvalidOperationException("Tool is not callable from the Session Tool REPL: " + toolId);
                        var callNumber = Interlocked.Increment(ref nestedCall);
                        if (callNumber > _limits.MaxToolCallsPerCell)
                            throw new InvalidOperationException("Session Tool REPL nested tool-call budget exceeded.");
                        if (argumentsJson is null || argumentsJson.Length > 256_000)
                            throw new ArgumentException("Nested tool arguments JSON exceeds the bounded size.");
                        JsonElement arguments;
                        try
                        {
                            using var document = JsonDocument.Parse(argumentsJson);
                            if (document.RootElement.ValueKind != JsonValueKind.Object)
                                throw new ArgumentException("Nested tool arguments must be a JSON object.");
                            arguments = document.RootElement.Clone();
                        }
                        catch (JsonException ex)
                        {
                            throw new ArgumentException("Nested tool arguments JSON is invalid.", ex);
                        }

                        var childContext = context with
                        {
                            CallId = context.CallId + ":repl:" + cell.Id + ":" + callNumber,
                            SessionCancellation = cell.Token,
                            FullPermission = false,
                            RetainResources = null
                        };
                        var reply = await _invoke(toolId, arguments, childContext, cell.Token).ConfigureAwait(false);
                        if (reply.IsError) throw new InvalidOperationException("Nested tool failed: " + reply.Text);
                        cell.AddImages(reply.Images);
                        cell.SetWidget(reply.Widget);
                        return JsonSerializer.Serialize(new
                        {
                            text = reply.Text,
                            imageCount = reply.Images?.Count ?? 0,
                            widgetTitle = reply.Widget?.Title
                        }, WireJson.Options);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref hostFailure.Value, ex, null);
                        throw;
                    }
                }

                Task SleepAsync(int milliseconds)
                {
                    if (milliseconds is < 0 or > 60_000)
                        throw new ArgumentOutOfRangeException(nameof(milliseconds), "sleep milliseconds must be 0..60000.");
                    return Task.Delay(milliseconds, cell.Token);
                }

                engine.SetValue("__invokeTool", new Func<string, string, Task<string>>(InvokeToolAsync));
                engine.SetValue("__emitText", new Action<string>(cell.AppendText));
                engine.SetValue("__emitTable", new Action<string>(cell.AppendTable));
                engine.SetValue("__emitImage", new Action<string>(_ => cell.Touch()));
                engine.SetValue("__sleep", new Func<int, Task>(SleepAsync));
                engine.Execute(BuildBindings(_catalog()));

                var wrapped = "(async () => {\n" + code +
                              "\n})().then(value => value === undefined ? null : JSON.stringify(value));";
                var result = await engine.EvaluateAsync(wrapped, cancellationToken: cell.Token).ConfigureAwait(false);
                if (hostFailure.Value is not null)
                {
                    resetEngine = true;
                    ExceptionDispatchInfo.Capture(hostFailure.Value).Throw();
                }
                var value = result.ToObject()?.ToString();
                cell.Complete(value is null or "null" ? null : value, nestedCall);
            }
            catch (Exception ex) when (ex is OperationCanceledException or PromiseRejectedException or JavaScriptException or
                                       InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                resetEngine = true;
                cell.Fail(ex is OperationCanceledException ? "Cell cancelled or timed out." : ex.Message, nestedCall: null);
            }
            catch (Exception ex)
            {
                resetEngine = true;
                cell.Fail("Cell failed: " + ex.GetType().Name, nestedCall: null);
            }
            finally
            {
                if (resetEngine) _engine = null;
                _engineLock.Release();
            }
        }

        private Engine CreateEngine() => new(options =>
        {
            options.LimitMemory(_limits.MaxMemoryBytesPerSession);
            options.MaxStatements(_limits.MaxStatementsPerCell);
            options.TimeoutInterval(_limits.CellElapsedLimit);
        });

        private string BuildBindings(DynamicToolSnapshot snapshot)
        {
            var tools = snapshot.Tools.Values
                .Where(tool => !BlockedNestedTools.Contains(tool.Descriptor.Id) &&
                               !AgentSessionRules.IsTool(tool.Descriptor.Id) && tool is not ICompositeAgentTool)
                .GroupBy(tool => tool.Descriptor.Name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single().Descriptor)
                .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
                .Select(descriptor => JsonSerializer.Serialize(descriptor.Name) +
                    ": async (args = {}) => __replDecode(await __invokeTool(" +
                    JsonSerializer.Serialize(descriptor.Id) + ", JSON.stringify(args ?? {})))")
                .ToArray();
            return """
                globalThis.__replString = value => {
                  if (typeof value === "string") return value;
                  if (value === undefined) return "undefined";
                  try { return JSON.stringify(value, null, 2); } catch (_) { return String(value); }
                };
                globalThis.__replDecode = raw => {
                  const envelope = JSON.parse(raw);
                  let value;
                  try { value = JSON.parse(envelope.text); } catch (_) { value = envelope.text; }
                  if (value && typeof value === "object" && !Array.isArray(value)) {
                    if (envelope.imageCount) value.__imageCount = envelope.imageCount;
                    if (envelope.widgetTitle) value.__widgetTitle = envelope.widgetTitle;
                    return value;
                  }
                  return envelope.imageCount || envelope.widgetTitle
                    ? { value, __imageCount: envelope.imageCount, __widgetTitle: envelope.widgetTitle }
                    : value;
                };
                globalThis.text = value => __emitText(__replString(value));
                globalThis.table = value => __emitTable(__replString(value));
                globalThis.image = value => __emitImage(__replString(value));
                globalThis.sleep = milliseconds => __sleep(milliseconds);
                globalThis.tools = Object.freeze({
                """ + string.Join(",\n", tools) + "\n});";
        }

        private void PruneCells()
        {
            if (_cells.Count < _limits.MaxCellsPerSession) return;
            foreach (var cell in _cells.Values.Where(cell => cell.IsTerminal && cell.IsFullyRead)
                         .OrderBy(cell => cell.CompletedAt ?? DateTimeOffset.MaxValue)
                         .Take(Math.Max(1, _cells.Count - _limits.MaxCellsPerSession + 1)).ToArray())
                _cells.Remove(cell.Id);
        }

        private static async Task ObserveRunnerAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            ReplCell[] cells;
            lock (_sync)
            {
                cells = _cells.Values.ToArray();
                foreach (var cell in cells) cell.Cancel("Tool REPL session disposed.");
                _cells.Clear();
            }
            _generationStop.Cancel();
            var runners = cells.Select(cell => cell.Runner).Where(task => task is not null).Cast<Task>().ToArray();
            if (runners.Length > 0)
                try { await Task.WhenAll(runners).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            foreach (var cell in cells) cell.Dispose();
            _generationStop.Dispose();
            _engineLock.Dispose();
        }
    }

    private sealed class ReplCell : IDisposable
    {
        private readonly object _sync = new();
        private readonly SessionToolReplLimits _limits;
        private readonly StringBuilder _output = new();
        private readonly List<WireImage> _images = [];
        private TaskCompletionSource<long> _changed = NewSignal();
        private long _version;
        private int _readOffset;
        private int _imageReadOffset;
        private bool _resultRead;
        private bool _widgetRead;
        private string? _result;
        private string? _error;
        private WidgetArtifact? _widget;
        private bool _truncated;
        private int _imageChars;
        private int? _toolCalls;
        private string _status = "QUEUED";

        public ReplCell(string id, int generation, SessionToolReplLimits limits, CancellationTokenSource stop)
        {
            Id = id;
            Generation = generation;
            _limits = limits;
            Stop = stop;
            StartedAt = DateTimeOffset.UtcNow;
        }

        public string Id { get; }
        public int Generation { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset? CompletedAt { get; private set; }
        public CancellationTokenSource Stop { get; }
        public CancellationToken Token => Stop.Token;
        public Task? Runner { get; set; }
        public long Version { get { lock (_sync) return _version; } }
        public bool IsTerminal { get { lock (_sync) return _status is "SUCCEEDED" or "FAILED" or "CANCELLED"; } }
        public bool IsFullyRead
        {
            get
            {
                lock (_sync)
                    return IsTerminalUnsafe() && _readOffset >= _output.Length && _imageReadOffset >= _images.Count &&
                           (_result is null || _resultRead) && (_widget is null || _widgetRead);
            }
        }

        public void MarkRunning() { lock (_sync) { _status = "RUNNING"; SignalUnsafe(); } }

        public void AppendText(string text) => Append(text, table: false);
        public void AppendTable(string text) => Append(text, table: true);

        private void Append(string text, bool table)
        {
            text ??= "null";
            lock (_sync)
            {
                var prefix = _output.Length == 0 ? string.Empty : Environment.NewLine;
                var payload = table ? "```json\n" + text + "\n```" : text;
                var remaining = _limits.MaxOutputCharsPerCell - _output.Length;
                if (remaining <= 0) _truncated = true;
                else
                {
                    var combined = prefix + payload;
                    if (combined.Length > remaining)
                    {
                        _output.Append(combined.AsSpan(0, remaining));
                        _truncated = true;
                    }
                    else _output.Append(combined);
                }
                SignalUnsafe();
            }
        }

        public void AddImages(IReadOnlyList<WireImage>? images)
        {
            if (images is null || images.Count == 0) return;
            lock (_sync)
            {
                foreach (var image in images)
                {
                    if (_images.Count >= _limits.MaxImagesPerCell ||
                        _imageChars + image.Base64.Length > _limits.MaxImageBase64CharsPerCell)
                        throw new InvalidOperationException("Session Tool REPL image output budget exceeded.");
                    _images.Add(image);
                    _imageChars += image.Base64.Length;
                }
                SignalUnsafe();
            }
        }

        public void SetWidget(WidgetArtifact? widget)
        {
            if (widget is null) return;
            lock (_sync)
            {
                _widget ??= widget;
                SignalUnsafe();
            }
        }

        public void Complete(string? result, int toolCalls)
        {
            lock (_sync)
            {
                _result = result;
                _toolCalls = toolCalls;
                _status = "SUCCEEDED";
                CompletedAt = DateTimeOffset.UtcNow;
                SignalUnsafe();
            }
        }

        public void Fail(string error, int? nestedCall)
        {
            lock (_sync)
            {
                _error = error.Length > 4000 ? error[..4000] : error;
                _toolCalls = nestedCall;
                _status = Stop.IsCancellationRequested ? "CANCELLED" : "FAILED";
                CompletedAt = DateTimeOffset.UtcNow;
                SignalUnsafe();
            }
        }

        public void Cancel(string reason)
        {
            lock (_sync)
            {
                if (IsTerminalUnsafe()) return;
                _error ??= reason;
            }
            try { Stop.Cancel(); } catch (ObjectDisposedException) { }
            Touch();
        }

        public void Touch() { lock (_sync) SignalUnsafe(); }

        public async Task WaitForChangeAsync(long version, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task signal;
            lock (_sync)
            {
                if (_version != version || IsTerminalUnsafe()) return;
                signal = _changed.Task;
            }
            try { await signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }

        public ToolReply ReadDelta(int maxOutputTokens)
        {
            lock (_sync)
            {
                var maxChars = Math.Clamp(maxOutputTokens * 4, 400, 400_000);
                var available = _output.Length - _readOffset;
                var take = Math.Min(available, maxChars);
                var output = take == 0 ? string.Empty : _output.ToString(_readOffset, take);
                _readOffset += take;
                var images = _images.Skip(_imageReadOffset).ToArray();
                _imageReadOffset = _images.Count;
                var result = IsTerminalUnsafe() && !_resultRead ? _result : null;
                if (IsTerminalUnsafe()) _resultRead = true;
                var widget = !_widgetRead ? _widget : null;
                if (widget is not null) _widgetRead = true;
                var body = WireJson.Element(new
                {
                    cell_id = Id,
                    generation = Generation,
                    status = _status,
                    output,
                    result,
                    error = _error,
                    started_at = StartedAt,
                    completed_at = CompletedAt,
                    truncated = _truncated || available > take,
                    tool_calls = _toolCalls
                });
                return new ToolReply(body.GetRawText(), _status is "FAILED" or "CANCELLED", images, widget);
            }
        }

        private bool IsTerminalUnsafe() => _status is "SUCCEEDED" or "FAILED" or "CANCELLED";
        private void SignalUnsafe()
        {
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult(++_version);
        }
        private static TaskCompletionSource<long> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            try { Stop.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private sealed class ReplExecTool(SessionToolReplHost host, int maxCodeChars) : ICompositeAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(ExecId, "exec", "workflow",
            "Run JavaScript in this Jarvis session's persistent Tool REPL. Use globalThis for cross-cell state. The dynamic tools object exposes current non-composite Agent tools by public name; every nested call still passes schema, Arm/Pause, permission, approval, ownership and resource checks. text(), table(), image() and sleep() are available. Long cells return a cell_id for wait.",
            WireJson.Element(new
            {
                type = "object",
                properties = new
                {
                    code = new { type = "string", minLength = 1, maxLength = maxCodeChars },
                    yield_time_ms = new { type = "integer", minimum = 0, maximum = 30_000, @default = 10_000 },
                    max_output_tokens = new { type = "integer", minimum = 100, maximum = 100_000, @default = 10_000 }
                },
                required = new[] { "code" },
                additionalProperties = false
            }), ReadOnly: true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            host.ExecuteAsync(arguments, context, cancellationToken);
    }

    private sealed class ReplWaitTool(SessionToolReplHost host) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(WaitId, "wait", "workflow",
            "Read new output from one Session Tool REPL cell, wait for a change, or terminate that cell. Output and images are delivered incrementally and remain owner/device/session bound.",
            WireJson.Element(new
            {
                type = "object",
                properties = new
                {
                    cell_id = new { type = "string", pattern = "^cell_[a-f0-9]{32}$" },
                    yield_time_ms = new { type = "integer", minimum = 0, maximum = 300_000, @default = 5_000 },
                    max_output_tokens = new { type = "integer", minimum = 100, maximum = 100_000, @default = 10_000 },
                    terminate = new { type = "boolean", @default = false }
                },
                required = new[] { "cell_id" },
                additionalProperties = false
            }), ReadOnly: true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            host.WaitAsync(arguments, context, cancellationToken);
    }

    private sealed class ReplSleepTool : ICompositeAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(SleepId, "sleep", "workflow",
            "Wait for a bounded duration without holding a normal Agent execution slot. The wait is cancelled by session stop, Pause or request cancellation.",
            WireJson.Element(new
            {
                type = "object",
                properties = new { duration_ms = new { type = "integer", minimum = 0, maximum = 60_000 } },
                required = new[] { "duration_ms" },
                additionalProperties = false
            }), ReadOnly: true);
        public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            _ = context.RequireSessionIdentity();
            var duration = arguments.GetProperty("duration_ms").GetInt32();
            var timer = Stopwatch.StartNew();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.SessionCancellation);
            await Task.Delay(duration, stop.Token).ConfigureAwait(false);
            return new ToolReply(WireJson.Element(new { slept_ms = timer.ElapsedMilliseconds }).GetRawText());
        }
    }

    private sealed class ReplResetTool(SessionToolReplHost host) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(ResetId, "repl_reset", "workflow",
            "Cancel this Jarvis session's active Tool REPL cells, discard cell history and persistent JavaScript state, and start a fresh REPL generation. Other sessions are unaffected.",
            WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), ReadOnly: true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            host.ResetAsync(context, cancellationToken);
    }
}
