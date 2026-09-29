using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Auditing;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class AgentAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Persistent_audit_is_reason_coded_bounded_and_does_not_store_raw_session_ids()
    {
        var session = "js_" + new string('a', 32);
        using var log = new FileAgentAuditLog(_root);
        log.Write("PROCESS_TIMEOUT", "process", "Process reached its timeout.",
            "process.launch", session, "call-1", 42);
        var records = log.Query(10, "PROCESS_TIMEOUT", "process.launch", session);
        var record = Assert.Single(records);
        Assert.Equal("process", record.Category);
        Assert.Equal(42, record.ProcessId);
        Assert.NotEqual(session, record.SessionId);
        var persisted = string.Join("\n", Directory.GetFiles(_root, "*.jsonl").Select(File.ReadAllText));
        Assert.DoesNotContain(session, persisted, StringComparison.Ordinal);
        Assert.Contains("PROCESS_TIMEOUT", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_query_defaults_to_the_current_session_and_all_sessions_requires_full_permission()
    {
        var session = "js_" + new string('b', 32);
        using var log = new FileAgentAuditLog(_root);
        log.Write("PAUSED", "security", "Paused.", sessionId: session);
        log.Write("OTHER", "security", "Other.", sessionId: "js_" + new string('c', 32));
        var tools = new AuditToolSet(log);
        var query = Assert.Single(tools.Tools);
        var context = new AgentExecutionContext(Path.GetTempPath(), "call", session)
        { OwnerId = "owner", AgentDeviceId = "device" };
        var scoped = await query.ExecuteAsync(WireJson.Element(new { limit = 10 }), context, CancellationToken.None);
        Assert.False(scoped.IsError, scoped.Text);
        using (var json = JsonDocument.Parse(scoped.Text))
            Assert.Single(json.RootElement.GetProperty("records").EnumerateArray());
        var denied = await query.ExecuteAsync(WireJson.Element(new { all_sessions = true }), context, CancellationToken.None);
        Assert.True(denied.IsError);
        var all = await query.ExecuteAsync(WireJson.Element(new { all_sessions = true }),
            context with { FullPermission = true }, CancellationToken.None);
        Assert.False(all.IsError, all.Text);
        using var allJson = JsonDocument.Parse(all.Text);
        Assert.Equal(2, allJson.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task Connection_audit_does_not_persist_tool_exception_text_or_arguments()
    {
        const string secret = "audit-secret-must-not-be-persisted";
        using var log = new FileAgentAuditLog(_root);
        var tool = new RejectingTool(secret);
        var gate = new LocalControlGate();
        gate.Arm();
        await using var connection = new AgentConnection([tool], new Approval(), gate,
            new ToolPermissionPolicy([tool.Descriptor.Id]), audit: log);
        using var socket = new AuditSocket();
        var call = new WireMessage("call")
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolId = tool.Descriptor.Id,
            Arguments = WireJson.Element(new { marker = secret }),
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(15),
            SessionId = "legacy-audit-session"
        };

        typeof(AgentConnection).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(connection, [new WireSocket(socket), call,
                new WorkspaceDirectories(Path.GetTempPath()), CancellationToken.None]);
        var response = await socket.Replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.Result!.IsError);
        Assert.Contains(secret, response.Result.Text, StringComparison.Ordinal);
        var persisted = string.Join("\n", Directory.GetFiles(_root, "*.jsonl").Select(File.ReadAllText));
        Assert.DoesNotContain(secret, persisted, StringComparison.Ordinal);
        Assert.Contains("The tool call was rejected because its arguments were invalid.", persisted,
            StringComparison.Ordinal);
    }

    private sealed class Approval : IApprovalService
    {
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RejectingTool(string secret) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "test.audit_rejection", "audit_rejection", "test", "Reject for audit coverage.",
            WireJson.Element(new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["marker"] = new { type = "string" }
                },
                required = new[] { "marker" },
                additionalProperties = false
            }),
            ReadOnly: false,
            Sensitive: true);

        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context,
            CancellationToken cancellationToken) =>
            Task.FromException<ToolReply>(new ArgumentException("Rejected marker: " + secret));
    }

    private sealed class AuditSocket : WebSocket
    {
        public Channel<WireMessage> Replies { get; } = Channel.CreateUnbounded<WireMessage>();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            Replies.Writer.TryWrite(JsonSerializer.Deserialize<WireMessage>(buffer.AsSpan(), WireJson.Options)!);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
