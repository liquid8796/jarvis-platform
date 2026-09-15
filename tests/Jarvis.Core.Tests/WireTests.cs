using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Jarvis.Protocol;
namespace Jarvis.Core.Tests;
public sealed class WireTests
{
    [Fact] public async Task Fragmented_json_round_trips()
    {
        using var socket = new FakeSocket(); var json = JsonSerializer.Serialize(new WireMessage("ping") { Timestamp = 42 }, WireJson.Options);
        socket.Parts.Enqueue((Encoding.UTF8.GetBytes(json[..10]), false, WebSocketMessageType.Text));
        socket.Parts.Enqueue((Encoding.UTF8.GetBytes(json[10..]), true, WebSocketMessageType.Text));
        var message = await new WireSocket(socket).ReceiveAsync(CancellationToken.None); Assert.Equal("ping", message!.Type); Assert.Equal(42L, message.Timestamp);
    }
    [Fact] public async Task Binary_frame_is_rejected()
    { using var socket = new FakeSocket(); socket.Parts.Enqueue(([1],true,WebSocketMessageType.Binary)); await Assert.ThrowsAsync<InvalidDataException>(() => new WireSocket(socket).ReceiveAsync(CancellationToken.None)); }
    [Fact] public async Task Incompatible_wire_version_is_rejected()
    { using var socket = new FakeSocket(); socket.Parts.Enqueue((Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"version\":2}"),true,WebSocketMessageType.Text)); await Assert.ThrowsAsync<InvalidDataException>(() => new WireSocket(socket).ReceiveAsync(CancellationToken.None)); }
    [Fact] public async Task Oversized_outbound_frame_is_rejected()
    { using var socket = new FakeSocket(); await Assert.ThrowsAsync<InvalidDataException>(() => new WireSocket(socket).SendAsync(new("result") { Result = new(new string('x',WireSocket.MaxMessageBytes)) },CancellationToken.None)); }
    private sealed class FakeSocket : WebSocket
    {
        public Queue<(byte[] Bytes, bool End, WebSocketMessageType Type)> Parts { get; } = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus,string? description,CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus,string? description,CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); var part=Parts.Dequeue(); part.Bytes.CopyTo(buffer.Array!,buffer.Offset); return Task.FromResult(new WebSocketReceiveResult(part.Bytes.Length,part.Type,part.End)); }
        public override Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType messageType,bool endOfMessage,CancellationToken ct) => Task.CompletedTask;
    }
}
