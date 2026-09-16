using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class DynamicAgentConnectionTests
{
    private sealed class Approval : IApprovalService
    {
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class Tool(string output, JsonElement schema) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.dynamic", "test_dynamic", "test", "Dynamic test tool", schema, true);
        public int Calls;
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ToolReply(output));
        }
    }

    private sealed class Socket : WebSocket
    {
        public Channel<WireMessage> Replies { get; } = Channel.CreateUnbounded<WireMessage>();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Replies.Writer.TryWrite(JsonSerializer.Deserialize<WireMessage>(buffer.AsSpan(), WireJson.Options)!);
            return Task.CompletedTask;
        }
    }

    private static JsonElement Schema(string required) => WireJson.Element(new
    {
        type = "object",
        properties = new Dictionary<string, object> { [required] = new { type = "string" } },
        required = new[] { required },
        additionalProperties = false
    });

    private static Task<WireMessage> Invoke(AgentConnection connection, Socket socket, JsonElement arguments,
        long? expectedCatalogGeneration = null, string? expectedCatalogDigest = null)
    {
        var message = new WireMessage("call")
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolId = "test.dynamic",
            Arguments = arguments,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(15),
            SessionId = "session-1",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ExpectedCatalogGeneration = expectedCatalogGeneration,
            ExpectedCatalogDigest = expectedCatalogDigest
        };
        typeof(AgentConnection).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(connection, [new WireSocket(socket), message, new WorkspaceDirectories(Path.GetTempPath()), CancellationToken.None]);
        return socket.Replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Invocation_uses_latest_registry_tool_and_schema()
    {
        var first = new Tool("first", Schema("value"));
        var second = new Tool("second", Schema("message"));
        var registry = new DynamicToolRegistry([first]);
        var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection(registry, new Approval(), gate);
        using var socket = new Socket();

        var firstReply = await Invoke(connection, socket, WireJson.Element(new { value = "ok" }));
        Assert.Equal("first", firstReply.Result!.Text);

        connection.ToolRegistry.Replace([second]);
        var staleReply = await Invoke(connection, socket, WireJson.Element(new { value = "old" }));
        var secondReply = await Invoke(connection, socket, WireJson.Element(new { message = "ok" }));

        Assert.True(staleReply.Result!.IsError);
        Assert.Contains("schema", staleReply.Result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("second", secondReply.Result!.Text);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Single(connection.Descriptors);
    }

    [Fact]
    public async Task Registry_change_pushes_live_catalog_notification()
    {
        var first = new Tool("first", Schema("value"));
        var second = new Tool("second", Schema("message"));
        var registry = new DynamicToolRegistry([first]);
        var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection(registry, new Approval(), gate);
        using var socket = new Socket();
        typeof(AgentConnection).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(connection, new WireSocket(socket));

        var snapshot = registry.Replace([second]);
        var notification = await socket.Replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("catalog.changed", notification.Type);
        Assert.Equal(snapshot.Generation, notification.CatalogGeneration);
        Assert.Equal(snapshot.Digest, notification.CatalogDigest);
        Assert.Single(notification.CatalogTools!);
    }

    [Fact]
    public async Task Invocation_rejects_stale_catalog_identity_before_tool_execution()
    {
        var first = new Tool("first", Schema("value"));
        var second = new Tool("second", Schema("message"));
        var registry = new DynamicToolRegistry([first]);
        var expectedGeneration = registry.Snapshot.Generation;
        var expectedDigest = registry.Snapshot.Digest;
        var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection(registry, new Approval(), gate);
        using var socket = new Socket();

        registry.Replace([second]);
        var reply = await Invoke(connection, socket, WireJson.Element(new { message = "ok" }), expectedGeneration, expectedDigest);

        Assert.True(reply.Result!.IsError);
        Assert.Contains("catalog", reply.Result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, second.Calls);
    }
}
