using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

public interface IBrowserRuntimeClient : IDisposable
{
    event Action<string>? ApplicationStopRequested;
    BrowserRuntimeHandshake? Handshake { get; }
    bool IsReady { get; }
    Task<ToolReply> ExecuteAsync(BrowserRuntimeRequest request, CancellationToken cancellationToken);
    Task EndApplicationSessionAsync(string sessionId, bool close, CancellationToken cancellationToken);
}

public sealed class BrowserRuntimeClient : IBrowserRuntimeClient
{
    private readonly string _servicePipeName;
    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new(StringComparer.Ordinal);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readerLoop;
    private Process? _startedService;
    private int _disposed;

    public BrowserRuntimeClient(string? servicePipeName = null)
    {
        _servicePipeName = servicePipeName ?? BrowserIntegration.ServicePipeName;
    }

    public event Action<string>? ApplicationStopRequested;
    public BrowserRuntimeHandshake? Handshake { get; private set; }
    public bool IsReady => _pipe is { IsConnected: true } && Handshake is not null;

    public async Task<ToolReply> ExecuteAsync(BrowserRuntimeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync(new JsonObject
        {
            ["kind"] = "execute",
            ["request"] = JsonNode.Parse(JsonSerializer.Serialize(request, BrowserRuntimeProtocol.Json))
        }, cancellationToken).ConfigureAwait(false);
        var reply = response["reply"]?.Deserialize<BrowserRuntimeReply>(BrowserRuntimeProtocol.Json)
            ?? throw new InvalidDataException("Browser service returned no reply.");
        return new ToolReply(reply.Text, reply.IsError, reply.Images);
    }

    public async Task EndApplicationSessionAsync(string sessionId, bool close, CancellationToken cancellationToken)
    {
        await SendAsync(new JsonObject
        {
            ["kind"] = "endSession",
            ["sessionId"] = sessionId,
            ["close"] = close
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject> SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        message["id"] = id;
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate browser runtime request id.");
        try
        {
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer!.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _write.Release(); }
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsReady) return;
        await _connect.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady) return;
            CleanupConnection();
            if (!await TryConnectAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false))
            {
                StartService();
                if (!await TryConnectAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Browser service unavailable. Reinstall or rebuild the Agent browser companions.");
            }

            _readerLoop = Task.Run(ReadLoopAsync);
            var hello = await SendHelloAsync(cancellationToken).ConfigureAwait(false);
            if (hello.ProtocolVersion != BrowserRuntimeProtocol.Version)
                throw new InvalidOperationException($"Unsupported browser service protocol {hello.ProtocolVersion}.");
            Handshake = hello;
        }
        finally { _connect.Release(); }
    }

    private async Task<bool> TryConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", _servicePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeoutStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutStop.CancelAfter(timeout);
        try
        {
            await pipe.ConnectAsync(timeoutStop.Token).ConfigureAwait(false);
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            return false;
        }
        catch (IOException)
        {
            pipe.Dispose();
            return false;
        }
    }

    private void StartService()
    {
        if (_startedService is { HasExited: false }) return;
        var executable = BrowserIntegration.ResolveCompanionExecutable(BrowserIntegration.BrowserServiceExeName);
        _startedService = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"--service-pipe \"{_servicePipeName}\" --extension-pipe \"{BrowserIntegration.ExtensionPipeName}\" --parent {Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        });
        if (_startedService is null) throw new InvalidOperationException("Could not start the browser service.");
    }

    private async Task<BrowserRuntimeHandshake> SendHelloAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var hello = new JsonObject
            {
                ["kind"] = "hello",
                ["id"] = id,
                ["protocolVersion"] = BrowserRuntimeProtocol.Version,
                ["capabilities"] = new JsonArray(BrowserRuntimeProtocol.Capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            };
            await _writer!.WriteLineAsync(hello.ToJsonString()).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return response["handshake"]?.Deserialize<BrowserRuntimeHandshake>(BrowserRuntimeProtocol.Json)
                ?? throw new InvalidDataException("Browser service handshake missing.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (_reader is not null && await _reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var node = JsonNode.Parse(line);
                if (node is not JsonObject message) continue;
                if (message["kind"]?.GetValue<string>() == "event")
                {
                    if (message["event"]?.GetValue<string>() == "sessionStop" &&
                        message["sessionId"]?.GetValue<string>() is { Length: > 0 } sessionId)
                        ApplicationStopRequested?.Invoke(sessionId);
                    continue;
                }
                if (message["id"]?.GetValue<string>() is { } id && _pending.TryGetValue(id, out var pending))
                {
                    if (message["ok"]?.GetValue<bool>() == false)
                        pending.TrySetException(new InvalidOperationException(message["error"]?.GetValue<string>() ?? "Browser service request failed."));
                    else pending.TrySetResult(message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
        {
        }
        finally
        {
            Handshake = null;
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("Browser service disconnected."));
        }
    }

    private void CleanupConnection()
    {
        Handshake = null;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _reader = null;
        _writer = null;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(BrowserRuntimeClient));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CleanupConnection();
        _connect.Dispose();
        _write.Dispose();
    }
}
