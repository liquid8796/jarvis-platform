using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.LanguageServers;

/// <summary>LSP 3.17 base protocol: UTF-8 Content-Length framing, requests, notifications and cancellation.</summary>
internal sealed class LspJsonRpcConnection : IAsyncDisposable
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _write = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly Func<string, JsonNode?, Task<JsonNode?>> _serverRequest;
    private readonly Action<string, JsonNode?> _notification;
    private readonly Task _reader;
    private long _nextId;
    private Exception? _failure;

    public LspJsonRpcConnection(Stream input, Stream output,
        Func<string, JsonNode?, Task<JsonNode?>> serverRequest, Action<string, JsonNode?> notification)
    {
        _input = input; _output = output; _serverRequest = serverRequest; _notification = notification;
        _reader = ReadLoopAsync();
    }

    public bool IsClosed => _reader.IsCompleted;

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_failure is { } failure) throw new IOException("Language server connection closed.", failure);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            // The reader can finish between the first state check and registration.
            if (_failure is { } closed) throw new IOException("Language server connection closed.", closed);
            await SendAsync(new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, token);
            try { return await completion.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                using var cancelTimeout = new CancellationTokenSource(1000);
                try { await NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id }, cancelTimeout.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                throw;
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken token) =>
        SendAsync(new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, token);

    private async Task SendAsync(JsonObject message, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await _write.WaitAsync(token);
        try
        {
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            await _output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"), sending.Token);
            await _output.WriteAsync(payload, sending.Token);
            await _output.FlushAsync(sending.Token);
        }
        // An interrupted write may contain half a frame: close the connection rather than reusing corrupted framing.
        catch { await _lifetime.CancelAsync(); throw; }
        finally { _write.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var header = new List<byte>();
                var one = new byte[1];
                while (true)
                {
                    if (await _input.ReadAsync(one, _lifetime.Token) == 0) throw new EndOfStreamException("Language server closed stdout.");
                    header.Add(one[0]);
                    if (header.Count > 65536) throw new InvalidDataException("LSP header exceeds 64 KiB.");
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var lengths = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")
                    .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (lengths.Length != 1 || !int.TryParse(lengths[0][15..].Trim(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var size) || size <= 0 || size > MaxMessageBytes)
                    throw new InvalidDataException("Invalid LSP Content-Length.");
                var body = new byte[size];
                await _input.ReadExactlyAsync(body, _lifetime.Token);
                var message = JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("Invalid JSON-RPC message.");
                if (message["method"] is JsonValue methodValue)
                {
                    var method = methodValue.GetValue<string>();
                    if (message["id"] is { } serverId)
                    {
                        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = serverId.DeepClone() };
                        try { response["result"] = await _serverRequest(method, message["params"]); }
                        catch (NotSupportedException error)
                        { response["error"] = new JsonObject { ["code"] = -32601, ["message"] = error.Message }; }
                        await SendAsync(response, _lifetime.Token);
                    }
                    else _notification(method, message["params"]);
                }
                else if (message["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id) && _pending.TryGetValue(id, out var waiter))
                {
                    if (message["error"] is JsonObject error)
                        waiter.TrySetException(new IOException($"LSP error {error["code"]}: {error["message"]}"));
                    else if (message.ContainsKey("result")) waiter.TrySetResult(message["result"]?.DeepClone());
                    else waiter.TrySetException(new InvalidDataException("LSP response contains neither result nor error."));
                }
            }
        }
        catch (Exception error) { _failure = error; }
        finally
        {
            _failure ??= new IOException("Language server connection closed.");
            foreach (var waiter in _pending.Values) waiter.TrySetException(new IOException("Language server connection closed.", _failure));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _reader;
        _lifetime.Dispose();
    }
}
