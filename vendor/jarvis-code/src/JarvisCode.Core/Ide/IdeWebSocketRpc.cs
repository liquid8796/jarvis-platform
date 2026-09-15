using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Ide;

/// <summary>Local IDE MCP WebSocket transport; measured from installed CLI 2.1.260's ws-ide branch.</summary>
internal sealed class IdeWebSocketRpc : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writes = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> _pending = new();
    private Task? _reader;
    private long _id;
    internal Action<string, JsonNode?>? NotificationReceived { get; set; }
    internal bool IsConnected => _socket.State == WebSocketState.Open && _reader?.IsCompleted == false;

    public async Task ConnectAsync(Uri uri, string? token, CancellationToken cancellationToken)
    {
        if (!uri.IsLoopback || uri.Scheme != "ws") throw new InvalidOperationException("IDE WebSockets must use local loopback.");
        _socket.Options.Proxy = null;
        _socket.Options.AddSubProtocol("mcp");
        _socket.Options.SetRequestHeader("User-Agent", "Jarvis Code");
        if (!string.IsNullOrEmpty(token)) _socket.Options.SetRequestHeader("X-Claude-Code-Ide-Authorization", token);
        await _socket.ConnectAsync(uri, cancellationToken);
        _reader = ReadAsync();
        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "Jarvis Code", ["version"] = "1.0" },
        }, cancellationToken);
        await NotifyAsync("notifications/initialized", new JsonObject(), cancellationToken);
    }

    public async Task<JsonNode> RequestAsync(string method, JsonObject parameters, CancellationToken token)
    {
        if (_reader?.IsCompleted != false) throw new IOException("IDE WebSocket connection is closed.");
        var id = Interlocked.Increment(ref _id);
        var waiter = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            if (_reader.IsCompleted) throw new IOException("IDE WebSocket connection is closed.");
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, token);
            return await waiter.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "Request cancelled" }, timeout.Token); }
            catch (Exception error) when (error is IOException or WebSocketException or OperationCanceledException) { }
            throw;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, JsonObject parameters, CancellationToken token) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, token);

    private async Task SendAsync(JsonObject value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 8 * 1024 * 1024) throw new IOException("IDE request exceeds 8 MiB.");
        await _writes.WaitAsync(token);
        try { await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
        finally { _writes.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception failure = new IOException("IDE WebSocket connection closed.");
        try
        {
            var buffer = new byte[16384];
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, _lifetime.Token);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 8 * 1024 * 1024)
                        throw new IOException("Invalid IDE WebSocket message.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                var value = JsonNode.Parse(message.ToArray())?.AsObject() ?? throw new IOException("Invalid IDE JSON-RPC response.");
                if (value["method"]?.GetValue<string>() is { } method)
                {
                    if (value["id"] is { } serverId)
                    {
                        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = serverId.DeepClone() };
                        if (method == "ping") reply["result"] = new JsonObject();
                        else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unsupported IDE server request" };
                        await SendAsync(reply, _lifetime.Token);
                    }
                    else NotificationReceived?.Invoke(method, value["params"]?.DeepClone());
                }
                else if (value["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id) && _pending.TryGetValue(id, out var waiter))
                {
                    if (value["error"] is { } error) waiter.TrySetException(new IOException(error["message"]?.ToString() ?? "IDE request failed."));
                    else waiter.TrySetResult(value["result"]?.DeepClone() ?? new JsonObject());
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally { foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("IDE connection closed.", failure)); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _socket.Abort();
        if (_reader is not null) await _reader;
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
