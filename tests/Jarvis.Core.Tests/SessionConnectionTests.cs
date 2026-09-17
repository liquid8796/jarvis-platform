using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Sessions;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class SessionConnectionTests
{
    [Fact]
    public async Task Separate_wire_sessions_receive_their_own_context_and_do_not_inherit_defaults_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-session-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new AgentSessionStore(Path.Combine(root, "sessions.db"));
            var a = new AgentSessionIdentity("owner", "device", AgentSessionRules.NewSessionId());
            var b = a with { SessionId = AgentSessionRules.NewSessionId() };
            store.Open(a, "A", new WorkspaceDirectories(root));
            store.Open(b, "B", new WorkspaceDirectories(""));
            var gate = new LocalControlGate(); gate.Arm();
            await using var connection = new AgentConnection([new EchoContext()], new Approve(), gate);
            Inject(connection, "_sessions", store);
            Inject(connection, "_deviceId", "device");
            using var socket = new ReplySocket();
            var replyA = await Call(connection, socket, a, "test.context", root);
            var replyB = await Call(connection, socket, b, "test.context", root);
            Assert.False(replyA.IsError, replyA.Text);
            Assert.False(replyB.IsError, replyB.Text);
            var ca = JsonSerializer.Deserialize<ContextView>(replyA.Text, WireJson.Options)!;
            var cb = JsonSerializer.Deserialize<ContextView>(replyB.Text, WireJson.Options)!;
            Assert.Equal(root, ca.Workspace);
            Assert.Empty(cb.Workspace);
            Assert.Equal(a.SessionId, ca.SessionId);
            Assert.Equal(b.SessionId, cb.SessionId);
            Assert.Equal("owner", ca.OwnerId);
            Assert.Equal("device", cb.AgentDeviceId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Forged_owner_and_closed_session_fail_before_the_tool_runs()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-session-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new AgentSessionStore(Path.Combine(root, "sessions.db"));
            var identity = new AgentSessionIdentity("owner", "device", AgentSessionRules.NewSessionId());
            store.Open(identity, "A", new WorkspaceDirectories(root));
            var tool = new EchoContext();
            var gate = new LocalControlGate(); gate.Arm();
            await using var connection = new AgentConnection([tool], new Approve(), gate);
            Inject(connection, "_sessions", store);
            Inject(connection, "_deviceId", "device");
            using var socket = new ReplySocket();
            var forged = await Call(connection, socket, identity with { OwnerId = "another-owner" }, "test.context", root);
            Assert.True(forged.IsError);
            Assert.Contains("SESSION_NOT_FOUND", forged.Text);
            store.Close(identity);
            var closed = await Call(connection, socket, identity, "test.context", root);
            Assert.True(closed.IsError);
            Assert.Contains("SESSION_CLOSED", closed.Text);
            Assert.Equal(0, tool.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Inject(AgentConnection connection, string name, object value)
    {
        var field = typeof(AgentConnection).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(connection, value);
    }
    private static async Task<ToolReply> Call(AgentConnection connection, ReplySocket socket, AgentSessionIdentity identity, string toolId, string defaultWorkspace)
    {
        var call = new WireMessage("call") { Id = Guid.NewGuid().ToString("N"), OwnerId = identity.OwnerId,
            SessionId = identity.SessionId, ToolId = toolId, Arguments = WireJson.Element(new { }), DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10) };
        typeof(AgentConnection).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(connection, [new WireSocket(socket), call, new WorkspaceDirectories(defaultWorkspace), CancellationToken.None]);
        return (await socket.Replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result!;
    }
    private sealed record ContextView(string Workspace, string SessionId, string? OwnerId, string? AgentDeviceId);
    private sealed class EchoContext : IAgentTool
    {
        public int Calls;
        public ToolDescriptor Descriptor { get; } = new("test.context", "test_context", "test", "Return execution identity", WireJson.Element(new { type = "object" }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ToolReply(JsonSerializer.Serialize(new ContextView(context.Workspace, context.SessionId, context.OwnerId, context.AgentDeviceId), WireJson.Options)));
        }
    }
    private sealed class Approve : IApprovalService
    { public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(true); }
    private sealed class ReplySocket : WebSocket
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
        { Replies.Writer.TryWrite(JsonSerializer.Deserialize<WireMessage>(buffer.AsSpan(), WireJson.Options)!); return Task.CompletedTask; }
    }
}
