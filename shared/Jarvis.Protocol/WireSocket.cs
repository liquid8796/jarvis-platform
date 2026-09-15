using System.Net.WebSockets;
using System.Text.Json;
namespace Jarvis.Protocol;

/// <summary>One receiver, serialized sends, bounded fragmented frames. No compression for secret-bearing traffic.</summary>
public sealed class WireSocket(WebSocket socket)
{
    public const int MaxMessageBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _send = new(1, 1);
    public WebSocketState State => socket.State;

    public async Task SendAsync(WireMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, WireJson.Options);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("Message exceeds 8 MiB.");
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }

    public async Task<WireMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var data = new MemoryStream();
        var buffer = new byte[16 * 1024];
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Only JSON text frames are supported.");
            if (data.Length + part.Count > MaxMessageBytes)
                throw new InvalidDataException("Message exceeds 8 MiB.");
            data.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        var message = JsonSerializer.Deserialize<WireMessage>(data.GetBuffer().AsSpan(0, (int)data.Length), WireJson.Options)
            ?? throw new InvalidDataException("Empty message.");
        if (message.Version != 1) throw new InvalidDataException("Unsupported Jarvis wire version.");
        return message;
    }

    public void Abort()
    {
        try { socket.Abort(); }
        catch (ObjectDisposedException) { /* Concurrent peer disposal is already terminal. */ }
    }
}
