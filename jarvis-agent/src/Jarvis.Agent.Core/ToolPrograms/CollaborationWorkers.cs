using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Jint;
using Jint.Runtime;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.ToolPrograms;

public sealed record CollaborationWorkerLimits(
    int MaxWorkersPerSession = 8,
    int MaxRetainedWorkersPerSession = 32,
    int MaxCodeChars = 100_000,
    int MaxMessagesPerWorker = 128,
    int MaxMessageChars = 64_000,
    int MaxToolCallsPerWorker = 256,
    int MaxOutputCharsPerWorker = 256_000,
    int MaxImagesPerWorker = 8,
    int MaxImageBase64CharsPerWorker = 32 * 1024 * 1024,
    int MaxStatementsPerWorker = 250_000,
    long MaxMemoryBytesPerWorker = 32 * 1024 * 1024,
    TimeSpan? MaxWorkerElapsed = null)
{
    public TimeSpan WorkerElapsedLimit => MaxWorkerElapsed ?? TimeSpan.FromMinutes(30);
}

/// <summary>
/// Session-scoped collaboration actors. Workers run bounded JavaScript concurrently, communicate
/// through bounded mailboxes, and can affect the machine only by re-entering the guarded
/// installed-tool invoker. They are intentionally not represented as hidden LLM agents.
/// </summary>
public sealed class CollaborationWorkerHost : IAsyncDisposable
{
    public const string SpawnId = "collaboration.worker_spawn";
    public const string SendId = "collaboration.worker_send";
    public const string WaitId = "collaboration.worker_wait";
    public const string ListId = "collaboration.worker_list";
    public const string StopId = "collaboration.worker_stop";

    private static readonly HashSet<string> BlockedNestedTools = new(StringComparer.Ordinal)
    {
        SpawnId, SendId, WaitId, ListId, StopId,
        SessionToolReplHost.ExecId, SessionToolReplHost.WaitId, SessionToolReplHost.SleepId,
        SessionToolReplHost.ResetId, "tool_script.run", "tool_program.run"
    };

    private readonly GuardedToolInvoker _invoke;
    private readonly Func<DynamicToolSnapshot> _catalog;
    private readonly CollaborationWorkerLimits _limits;
    private readonly ConcurrentDictionary<AgentSessionIdentity, WorkerGroup> _groups = new();
    private readonly CancellationTokenSource _lifetime = new();

    public CollaborationWorkerHost(GuardedToolInvoker invoke, Func<DynamicToolSnapshot> catalog,
        CollaborationWorkerLimits? limits = null)
    {
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _limits = limits ?? new CollaborationWorkerLimits();
        if (_limits.MaxWorkersPerSession < 1 ||
            _limits.MaxRetainedWorkersPerSession < _limits.MaxWorkersPerSession ||
            _limits.MaxCodeChars < 1 || _limits.MaxMessagesPerWorker < 1 || _limits.MaxMessageChars < 1 ||
            _limits.MaxToolCallsPerWorker < 1 || _limits.MaxOutputCharsPerWorker < 1 ||
            _limits.MaxImagesPerWorker < 1 || _limits.MaxImageBase64CharsPerWorker < 1 ||
            _limits.MaxStatementsPerWorker < 1 || _limits.MaxMemoryBytesPerWorker < 1 ||
            _limits.WorkerElapsedLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public IReadOnlyList<IAgentTool> CreateTools() =>
    [
        new WorkerSpawnTool(this, _limits.MaxCodeChars, _limits.MaxMessageChars),
        new WorkerSendTool(this, _limits.MaxMessageChars),
        new WorkerWaitTool(this),
        new WorkerListTool(this),
        new WorkerStopTool(this)
    ];

    public async Task<ToolReply> SpawnAsync(JsonElement args, AgentExecutionContext context,
        CancellationToken requestCancellation)
    {
        var identity = context.RequireSessionIdentity();
        var label = RequiredString(args, "label");
        if (label.Length > 120) throw new ArgumentException("label must contain at most 120 characters.");
        var code = RequiredString(args, "code");
        if (code.Length > _limits.MaxCodeChars)
            throw new ArgumentException($"code must contain at most {_limits.MaxCodeChars} characters.");
        var initialMessage = OptionalString(args, "initial_message");
        if (initialMessage?.Length > _limits.MaxMessageChars)
            throw new ArgumentException($"initial_message must contain at most {_limits.MaxMessageChars} characters.");
        var yieldMs = args.TryGetProperty("yield_time_ms", out var yieldNode) ? yieldNode.GetInt32() : 250;
        var group = _groups.GetOrAdd(identity, _ => new WorkerGroup(_invoke, _catalog, _limits, _lifetime.Token));
        return await group.SpawnAsync(label, code, initialMessage, context, yieldMs, requestCancellation)
            .ConfigureAwait(false);
    }

    public Task<ToolReply> SendAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var group = RequireGroup(context.RequireSessionIdentity());
        var id = RequiredString(args, "worker_id");
        var message = RequiredString(args, "message", allowEmpty: true);
        if (message.Length > _limits.MaxMessageChars)
            throw new ArgumentException($"message must contain at most {_limits.MaxMessageChars} characters.");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(group.Send(id, message));
    }

    public async Task<ToolReply> WaitAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var group = RequireGroup(context.RequireSessionIdentity());
        var id = RequiredString(args, "worker_id");
        var yieldMs = args.TryGetProperty("yield_time_ms", out var yieldNode) ? yieldNode.GetInt32() : 5_000;
        var maxTokens = args.TryGetProperty("max_output_tokens", out var tokenNode) ? tokenNode.GetInt32() : 10_000;
        return await group.WaitAsync(id, yieldMs, maxTokens, ct).ConfigureAwait(false);
    }

    public ToolReply List(AgentExecutionContext context)
    {
        var identity = context.RequireSessionIdentity();
        return _groups.TryGetValue(identity, out var group)
            ? group.List()
            : JsonReply(new { workers = Array.Empty<object>(), running = 0, retained = 0 });
    }

    public async Task<ToolReply> StopAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var group = RequireGroup(context.RequireSessionIdentity());
        var id = RequiredString(args, "worker_id");
        return await group.StopAsync(id, ct).ConfigureAwait(false);
    }

    public void Reset(AgentSessionIdentity identity)
    {
        if (_groups.TryRemove(identity, out var group)) _ = group.DisposeAsync().AsTask();
    }

    public void CancelAll(string reason)
    {
        foreach (var group in _groups.Values) group.CancelAll(reason);
    }

    private WorkerGroup RequireGroup(AgentSessionIdentity identity) =>
        _groups.TryGetValue(identity, out var group)
            ? group
            : throw new KeyNotFoundException("No collaboration workers exist in this session.");

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var groups = _groups.ToArray();
        _groups.Clear();
        foreach (var group in groups) await group.Value.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class WorkerGroup : IAsyncDisposable
    {
        private readonly GuardedToolInvoker _invoke;
        private readonly Func<DynamicToolSnapshot> _catalog;
        private readonly CollaborationWorkerLimits _limits;
        private readonly CancellationToken _hostLifetime;
        private readonly object _sync = new();
        private readonly Dictionary<string, WorkerState> _workers = new(StringComparer.Ordinal);
        private bool _disposed;

        public WorkerGroup(GuardedToolInvoker invoke, Func<DynamicToolSnapshot> catalog,
            CollaborationWorkerLimits limits, CancellationToken hostLifetime)
        {
            _invoke = invoke;
            _catalog = catalog;
            _limits = limits;
            _hostLifetime = hostLifetime;
        }

        public async Task<ToolReply> SpawnAsync(string label, string code, string? initialMessage,
            AgentExecutionContext context, int yieldMs, CancellationToken requestCancellation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            WorkerState worker;
            lock (_sync)
            {
                PruneUnsafe();
                if (_workers.Values.Count(item => !item.IsTerminal) >= _limits.MaxWorkersPerSession)
                    throw new AgentRequestException("WORKER_LIMIT",
                        $"This session already has {_limits.MaxWorkersPerSession} active collaboration workers.");
                if (_workers.Count >= _limits.MaxRetainedWorkersPerSession)
                    throw new AgentRequestException("WORKER_HISTORY_FULL",
                        "Stop or fully read completed workers before starting another worker.");
                var stop = CancellationTokenSource.CreateLinkedTokenSource(
                    _hostLifetime, context.SessionCancellation);
                stop.CancelAfter(_limits.WorkerElapsedLimit);
                worker = new WorkerState("worker_" + Guid.NewGuid().ToString("N"), label, _limits, stop);
                _workers.Add(worker.Id, worker);
                if (initialMessage is not null) worker.Send(initialMessage);
                worker.Runner = Task.Run(() => RunWorkerAsync(worker, code, context), CancellationToken.None);
            }

            if (yieldMs > 0)
            {
                var completed = await Task.WhenAny(worker.Runner!, Task.Delay(yieldMs, requestCancellation))
                    .ConfigureAwait(false);
                if (completed == worker.Runner) await ObserveRunnerAsync(worker.Runner!).ConfigureAwait(false);
            }
            requestCancellation.ThrowIfCancellationRequested();
            return yieldMs == 0 ? worker.Snapshot() : worker.ReadDelta(10_000);
        }

        public ToolReply Send(string id, string message)
        {
            var worker = Get(id);
            if (worker.IsTerminal)
                throw new AgentRequestException("WORKER_TERMINAL", "The collaboration worker has already finished.");
            worker.Send(message);
            return JsonReply(new { worker_id = id, accepted = true, queued_messages = worker.QueuedMessages });
        }

        public async Task<ToolReply> WaitAsync(string id, int yieldMs, int maxTokens, CancellationToken ct)
        {
            var worker = Get(id);
            var version = worker.Version;
            if (!worker.IsTerminal && yieldMs > 0)
                await worker.WaitForChangeAsync(version, TimeSpan.FromMilliseconds(yieldMs), ct).ConfigureAwait(false);
            if (worker.Runner?.IsCompleted == true) await ObserveRunnerAsync(worker.Runner).ConfigureAwait(false);
            return worker.ReadDelta(maxTokens);
        }

        public ToolReply List()
        {
            WorkerState[] workers;
            lock (_sync) workers = _workers.Values.OrderBy(item => item.StartedAt).ToArray();
            return JsonReply(new
            {
                workers = workers.Select(item => item.Summary()).ToArray(),
                running = workers.Count(item => !item.IsTerminal),
                retained = workers.Length
            });
        }

        public async Task<ToolReply> StopAsync(string id, CancellationToken ct)
        {
            var worker = Get(id);
            worker.Cancel("Stopped by worker_stop.");
            if (worker.Runner is { } runner)
                try { await runner.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            return JsonReply(new { worker_id = id, stopped = true, status = worker.Status });
        }

        public void CancelAll(string reason)
        {
            WorkerState[] workers;
            lock (_sync) workers = _workers.Values.Where(item => !item.IsTerminal).ToArray();
            foreach (var worker in workers) worker.Cancel(reason);
        }

        private WorkerState Get(string id)
        {
            lock (_sync)
                return _workers.TryGetValue(id, out var worker)
                    ? worker
                    : throw new KeyNotFoundException("Collaboration worker was not found in this session.");
        }

        private async Task RunWorkerAsync(WorkerState worker, string code, AgentExecutionContext context)
        {
            var nestedCalls = 0;
            try
            {
                worker.MarkRunning();
                var hostFailure = new StrongBox<Exception?>();
                async Task<string> InvokeToolAsync(string toolId, string argumentsJson)
                {
                    try
                    {
                        worker.Token.ThrowIfCancellationRequested();
                        var snapshot = _catalog();
                        if (!snapshot.Tools.TryGetValue(toolId, out var tool) || BlockedNestedTools.Contains(toolId) ||
                            AgentSessionRules.IsTool(toolId) || tool is ICompositeAgentTool)
                            throw new InvalidOperationException("Tool is not callable from a collaboration worker: " + toolId);
                        var callNumber = Interlocked.Increment(ref nestedCalls);
                        if (callNumber > _limits.MaxToolCallsPerWorker)
                            throw new InvalidOperationException("Collaboration worker nested tool-call budget exceeded.");
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
                            CallId = context.CallId + ":worker:" + worker.Id + ":" + callNumber,
                            SessionCancellation = worker.Token,
                            FullPermission = false,
                            RetainResources = null
                        };
                        var reply = await _invoke(toolId, arguments, childContext, worker.Token).ConfigureAwait(false);
                        if (reply.IsError) throw new InvalidOperationException("Nested tool failed: " + reply.Text);
                        worker.AddImages(reply.Images);
                        worker.SetWidget(reply.Widget);
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

                async Task<string?> ReceiveAsync(int timeoutMs)
                {
                    if (timeoutMs is < 0 or > 300_000)
                        throw new ArgumentOutOfRangeException(nameof(timeoutMs), "receive timeout must be 0..300000 milliseconds.");
                    return await worker.ReceiveAsync(timeoutMs).ConfigureAwait(false);
                }

                Task SleepAsync(int milliseconds)
                {
                    if (milliseconds is < 0 or > 60_000)
                        throw new ArgumentOutOfRangeException(nameof(milliseconds), "sleep milliseconds must be 0..60000.");
                    return Task.Delay(milliseconds, worker.Token);
                }

                var engine = new Engine(options =>
                {
                    options.LimitMemory(_limits.MaxMemoryBytesPerWorker);
                    options.MaxStatements(_limits.MaxStatementsPerWorker);
                    options.TimeoutInterval(_limits.WorkerElapsedLimit);
                });
                engine.SetValue("__invokeTool", new Func<string, string, Task<string>>(InvokeToolAsync));
                engine.SetValue("__receive", new Func<int, Task<string?>>(ReceiveAsync));
                engine.SetValue("__emitText", new Action<string>(worker.AppendText));
                engine.SetValue("__emitTable", new Action<string>(worker.AppendTable));
                engine.SetValue("__sleep", new Func<int, Task>(SleepAsync));
                engine.Execute(BuildBindings(_catalog()));
                var wrapped = "(async () => {\n" + code +
                              "\n})().then(value => value === undefined ? null : JSON.stringify(value));";
                var result = await engine.EvaluateAsync(wrapped, cancellationToken: worker.Token).ConfigureAwait(false);
                if (hostFailure.Value is not null) ExceptionDispatchInfo.Capture(hostFailure.Value).Throw();
                var value = result.ToObject()?.ToString();
                worker.Complete(value is null or "null" ? null : value, nestedCalls);
            }
            catch (Exception ex) when (ex is OperationCanceledException or PromiseRejectedException or JavaScriptException or
                                       InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                worker.Fail(ex is OperationCanceledException ? "Worker cancelled or timed out." : ex.Message, nestedCalls);
            }
            catch (Exception ex)
            {
                worker.Fail("Worker failed: " + ex.GetType().Name, nestedCalls);
            }
        }

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
                    ": async (args = {}) => __workerDecode(await __invokeTool(" +
                    JsonSerializer.Serialize(descriptor.Id) + ", JSON.stringify(args ?? {})))")
                .ToArray();
            return """
                globalThis.__workerString = value => {
                  if (typeof value === "string") return value;
                  if (value === undefined) return "undefined";
                  try { return JSON.stringify(value, null, 2); } catch (_) { return String(value); }
                };
                globalThis.__workerDecode = raw => {
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
                globalThis.text = value => __emitText(__workerString(value));
                globalThis.table = value => __emitTable(__workerString(value));
                globalThis.sleep = milliseconds => __sleep(milliseconds);
                globalThis.receive = async (timeoutMs = 0) => {
                  const raw = await __receive(timeoutMs);
                  return raw === null || raw === undefined ? null : JSON.parse(raw);
                };
                globalThis.tools = Object.freeze({
                """ + string.Join(",\n", tools) + "\n});";
        }

        private void PruneUnsafe()
        {
            if (_workers.Count < _limits.MaxRetainedWorkersPerSession) return;
            foreach (var worker in _workers.Values.Where(item => item.IsTerminal && item.IsFullyRead)
                         .OrderBy(item => item.CompletedAt ?? DateTimeOffset.MaxValue)
                         .Take(Math.Max(1, _workers.Count - _limits.MaxRetainedWorkersPerSession + 1)).ToArray())
            {
                _workers.Remove(worker.Id);
                worker.Dispose();
            }
        }

        private static async Task ObserveRunnerAsync(Task task)
        {
            try { await task.ConfigureAwait(false); } catch (Exception) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            WorkerState[] workers;
            lock (_sync)
            {
                workers = _workers.Values.ToArray();
                _workers.Clear();
            }
            foreach (var worker in workers) worker.Cancel("Collaboration worker group disposed.");
            var runners = workers.Select(item => item.Runner).Where(task => task is not null).Cast<Task>().ToArray();
            if (runners.Length > 0)
                try { await Task.WhenAll(runners).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            foreach (var worker in workers) worker.Dispose();
        }
    }

    private sealed class WorkerState : IDisposable
    {
        private readonly object _sync = new();
        private readonly CollaborationWorkerLimits _limits;
        private readonly Channel<WorkerMessage> _mailbox;
        private readonly StringBuilder _output = new();
        private readonly List<WireImage> _images = [];
        private TaskCompletionSource<long> _changed = NewSignal();
        private long _version;
        private long _messageSequence;
        private int _readOffset;
        private int _imageReadOffset;
        private int _imageChars;
        private bool _resultRead;
        private bool _widgetRead;
        private string? _result;
        private string? _error;
        private WidgetArtifact? _widget;
        private bool _truncated;
        private int? _toolCalls;
        private string _status = "QUEUED";

        public WorkerState(string id, string label, CollaborationWorkerLimits limits, CancellationTokenSource stop)
        {
            Id = id;
            Label = label;
            _limits = limits;
            Stop = stop;
            StartedAt = DateTimeOffset.UtcNow;
            _mailbox = Channel.CreateBounded<WorkerMessage>(new BoundedChannelOptions(limits.MaxMessagesPerWorker)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public string Id { get; }
        public string Label { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset? CompletedAt { get; private set; }
        public CancellationTokenSource Stop { get; }
        public CancellationToken Token => Stop.Token;
        public Task? Runner { get; set; }
        public long Version { get { lock (_sync) return _version; } }
        public string Status { get { lock (_sync) return _status; } }
        public int QueuedMessages => _mailbox.Reader.Count;
        public bool IsTerminal { get { lock (_sync) return IsTerminalUnsafe(); } }
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

        public void Send(string message)
        {
            var envelope = new WorkerMessage(Interlocked.Increment(ref _messageSequence), message, DateTimeOffset.UtcNow);
            if (!_mailbox.Writer.TryWrite(envelope))
                throw new AgentRequestException("WORKER_MAILBOX_FULL", "The collaboration worker mailbox is full.");
            Touch();
        }

        public async Task<string?> ReceiveAsync(int timeoutMs)
        {
            if (_mailbox.Reader.TryRead(out var immediate)) return JsonSerializer.Serialize(immediate, WireJson.Options);
            if (timeoutMs == 0) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(timeoutMs);
            try
            {
                var message = await _mailbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                return JsonSerializer.Serialize(message, WireJson.Options);
            }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                return null;
            }
        }

        public object Summary()
        {
            lock (_sync)
                return new
                {
                    worker_id = Id,
                    label = Label,
                    status = _status,
                    started_at = StartedAt,
                    completed_at = CompletedAt,
                    queued_messages = QueuedMessages,
                    tool_calls = _toolCalls,
                    error = _error
                };
        }

        public void AppendText(string text) => Append(text, false);
        public void AppendTable(string text) => Append(text, true);
        private void Append(string text, bool table)
        {
            text ??= "null";
            lock (_sync)
            {
                var prefix = _output.Length == 0 ? string.Empty : Environment.NewLine;
                var payload = table ? "```json\n" + text + "\n```" : text;
                var remaining = _limits.MaxOutputCharsPerWorker - _output.Length;
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
                    if (_images.Count >= _limits.MaxImagesPerWorker ||
                        _imageChars + image.Base64.Length > _limits.MaxImageBase64CharsPerWorker)
                        throw new InvalidOperationException("Collaboration worker image output budget exceeded.");
                    _images.Add(image);
                    _imageChars += image.Base64.Length;
                }
                SignalUnsafe();
            }
        }

        public void SetWidget(WidgetArtifact? widget)
        {
            if (widget is null) return;
            lock (_sync) { _widget ??= widget; SignalUnsafe(); }
        }

        public void Complete(string? result, int calls)
        {
            lock (_sync)
            {
                _result = result;
                _toolCalls = calls;
                _status = "SUCCEEDED";
                CompletedAt = DateTimeOffset.UtcNow;
                _mailbox.Writer.TryComplete();
                SignalUnsafe();
            }
        }

        public void Fail(string error, int calls)
        {
            lock (_sync)
            {
                _error = error.Length > 4_000 ? error[..4_000] : error;
                _toolCalls = calls;
                _status = Stop.IsCancellationRequested ? "CANCELLED" : "FAILED";
                CompletedAt = DateTimeOffset.UtcNow;
                _mailbox.Writer.TryComplete();
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

        public async Task WaitForChangeAsync(long version, TimeSpan timeout, CancellationToken ct)
        {
            Task signal;
            lock (_sync)
            {
                if (_version != version || IsTerminalUnsafe()) return;
                signal = _changed.Task;
            }
            try { await signal.WaitAsync(timeout, ct).ConfigureAwait(false); }
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
                    worker_id = Id,
                    label = Label,
                    status = _status,
                    output,
                    result,
                    error = _error,
                    started_at = StartedAt,
                    completed_at = CompletedAt,
                    queued_messages = QueuedMessages,
                    tool_calls = _toolCalls,
                    truncated = _truncated || available > take
                });
                return new ToolReply(body.GetRawText(), _status is "FAILED" or "CANCELLED", images, widget);
            }
        }

        public ToolReply Snapshot()
        {
            lock (_sync)
            {
                var body = WireJson.Element(new
                {
                    worker_id = Id,
                    label = Label,
                    status = _status,
                    output = string.Empty,
                    result = (string?)null,
                    error = _error,
                    started_at = StartedAt,
                    completed_at = CompletedAt,
                    queued_messages = QueuedMessages,
                    tool_calls = _toolCalls,
                    truncated = false
                });
                return new ToolReply(body.GetRawText(), _status is "FAILED" or "CANCELLED");
            }
        }

        private void Touch() { lock (_sync) SignalUnsafe(); }
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
            _mailbox.Writer.TryComplete();
            try { Stop.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private sealed record WorkerMessage(long Sequence, string Message, DateTimeOffset SentAt);

    private static ToolReply JsonReply(object value) => new(JsonSerializer.Serialize(value, WireJson.Options));
    private static string RequiredString(JsonElement args, string name, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            throw new ArgumentException(name + " is required.");
        var value = node.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(value)) throw new ArgumentException(name + " is required.");
        return value;
    }
    private static string? OptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private sealed class WorkerSpawnTool(CollaborationWorkerHost host, int maxCodeChars, int maxMessageChars) : ICompositeAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(SpawnId, "worker_spawn", "workflow",
            "Start a bounded collaboration worker in this Jarvis session. A worker is a concurrent JavaScript actor, not a hidden model: it can receive messages, emit incremental output and call current non-composite tools through the same schema, Arm/Pause, permission, approval, ownership and resource checks. Available globals: tools, receive(timeoutMs), text, table and sleep.",
            WireJson.Element(new
            {
                type = "object",
                properties = new
                {
                    label = new { type = "string", minLength = 1, maxLength = 120 },
                    code = new { type = "string", minLength = 1, maxLength = maxCodeChars },
                    initial_message = new { type = "string", maxLength = maxMessageChars },
                    yield_time_ms = new { type = "integer", minimum = 0, maximum = 30_000, @default = 250 }
                },
                required = new[] { "label", "code" },
                additionalProperties = false
            }), ReadOnly: false, Sensitive: true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            host.SpawnAsync(arguments, context, cancellationToken);
    }

    private sealed class WorkerSendTool(CollaborationWorkerHost host, int maxMessageChars) : ICompositeAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(SendId, "worker_send", "workflow",
            "Send one bounded message to a running collaboration worker owned by this session.",
            WireJson.Element(new
            {
                type = "object",
                properties = new
                {
                    worker_id = new { type = "string", pattern = "^worker_[a-f0-9]{32}$" },
                    message = new { type = "string", maxLength = maxMessageChars }
                },
                required = new[] { "worker_id", "message" },
                additionalProperties = false
            }), ReadOnly: true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            host.SendAsync(arguments, context, cancellationToken);
    }

    private sealed class WorkerWaitTool(CollaborationWorkerHost host) : ICompositeAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(WaitId, "worker_wait", "workflow",
            "Read new output from a session-owned collaboration worker or wait for its next change. Output, images and widgets are delivered incrementally.",
            WireJson.Element(new
            {
                type = "object",
                properties = new
                {
                    worker_id = new { type = "string", pattern = "^worker_[a-f0-9]{32}$" },
                    yield_time_ms = new { type = "integer", minimum = 0, maximum = 300_000, @default = 5_000 },
                    max_output_tokens = new { type = "integer", minimum = 100, maximum = 100_000, @default = 10_000 }
                },
                required = new[] { "worker_id" },
                additionalProperties = false
            }), ReadOnly: true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            host.WaitAsync(arguments, context, cancellationToken);
    }

    private sealed class WorkerListTool(CollaborationWorkerHost host) : ICompositeAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(ListId, "worker_list", "workflow",
            "List collaboration workers owned by this Jarvis session, including status and mailbox counts.",
            WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), ReadOnly: true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(host.List(context));
    }

    private sealed class WorkerStopTool(CollaborationWorkerHost host) : ICompositeAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(StopId, "worker_stop", "workflow",
            "Cancel one collaboration worker owned by this Jarvis session. Other workers and sessions are unaffected.",
            WireJson.Element(new
            {
                type = "object",
                properties = new { worker_id = new { type = "string", pattern = "^worker_[a-f0-9]{32}$" } },
                required = new[] { "worker_id" },
                additionalProperties = false
            }), ReadOnly: true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            host.StopAsync(arguments, context, cancellationToken);
    }
}
