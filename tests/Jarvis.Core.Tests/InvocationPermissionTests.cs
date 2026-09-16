using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
namespace Jarvis.Core.Tests;

public sealed class InvocationPermissionTests
{
    private sealed class Approval(bool answer = true) : IApprovalService
    {
        public int Calls;
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement args, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(answer); }
    }
    private sealed class Tool(string id = "shell.PowerShell", bool wait = false) : IAgentTool
    {
        public ToolDescriptor Descriptor => new(id, id.Replace(".", "__"), "shell", "Synthetic tool",
            WireJson.Element(new { type = "object", additionalProperties = false }), false, true);
        public int Calls;
        public AgentExecutionContext? Context;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Context = context; Started.TrySetResult();
            if (wait) await Task.Delay(Timeout.Infinite, ct);
            return new ToolReply("synthetic success");
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
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Replies.Writer.TryWrite(JsonSerializer.Deserialize<WireMessage>(buffer.AsSpan(), WireJson.Options)!);
            return Task.CompletedTask;
        }
    }
    private static Task<WireMessage> Invoke(AgentConnection connection, Socket socket, string toolId,
        JsonElement? arguments = null, DateTimeOffset? deadline = null)
    {
        var call = new WireMessage("call") { Id = Guid.NewGuid().ToString("N"), ToolId = toolId,
            Arguments = arguments ?? WireJson.Element(new { }), DeadlineUtc = deadline ?? DateTimeOffset.UtcNow.AddSeconds(15) };
        typeof(AgentConnection).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(connection, [new WireSocket(socket), call, new WorkspaceDirectories(Path.GetTempPath()), CancellationToken.None]);
        return socket.Replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }
    [Theory] [InlineData(true, 0)] [InlineData(false, 1)]
    public async Task Actual_dispatch_uses_saved_tool_consent(bool full, int expectedApprovals)
    {
        var tool = new Tool(); var approval = new Approval(); var gate = new LocalControlGate(); gate.Arm();
        var policy = new ToolPermissionPolicy(full ? [tool.Descriptor.Id] : []);
        await using var connection = new AgentConnection([tool], approval, gate, policy);
        using var socket = new Socket(); var response = await Invoke(connection, socket, tool.Descriptor.Id);
        Assert.False(response.Result!.IsError); Assert.Equal(expectedApprovals, approval.Calls);
        Assert.Equal(1, tool.Calls); Assert.Equal(full, tool.Context!.FullPermission);
    }
    [Fact] public async Task Permanent_constrained_process_approval_bypasses_future_prompt_for_exact_tool()
    {
        var tool = new Tool("process.start"); var approval = new Approval(false); var gate = new LocalControlGate(); gate.Arm();
        var policy = new ToolPermissionPolicy([tool.Descriptor.Id]);
        var replace = typeof(ToolPermissionPolicy).GetMethod("ReplaceAlwaysApprovedConstrainedTools", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(replace); replace.Invoke(policy, [new[] { tool.Descriptor.Id }]);
        await using var connection = new AgentConnection([tool], approval, gate, policy);
        using var socket = new Socket(); var response = await Invoke(connection, socket, tool.Descriptor.Id);
        Assert.False(response.Result!.IsError); Assert.Equal(0, approval.Calls); Assert.Equal(1, tool.Calls);
        Assert.True(tool.Context!.FullPermission);
    }
    [Fact] public async Task Grant_for_one_tool_does_not_authorize_another()
    {
        var tool = new Tool("shell.Bash"); var approval = new Approval(false); var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection([tool], approval, gate, new(["shell.PowerShell"]));
        using var socket = new Socket(); var response = await Invoke(connection, socket, tool.Descriptor.Id);
        Assert.True(response.Result!.IsError); Assert.Equal(1, approval.Calls); Assert.Equal(0, tool.Calls);
    }
    [Fact] public async Task Full_permission_cannot_bypass_pause_schema_or_deadline()
    {
        var tool = new Tool(); var approval = new Approval(); var gate = new LocalControlGate();
        await using var connection = new AgentConnection([tool], approval, gate, new([tool.Descriptor.Id]));
        using var socket = new Socket();
        Assert.True((await Invoke(connection, socket, tool.Descriptor.Id)).Result!.IsError);
        gate.Arm();
        Assert.True((await Invoke(connection, socket, tool.Descriptor.Id, WireJson.Element(new { fullPermission = true }))).Result!.IsError);
        Assert.True((await Invoke(connection, socket, tool.Descriptor.Id, deadline: DateTimeOffset.UtcNow.AddSeconds(-1))).Result!.IsError);
        Assert.Equal(0, tool.Calls); Assert.Equal(0, approval.Calls);
    }
    [Fact] public async Task Revocation_cancels_running_and_queued_calls_without_rearming()
    {
        var tool = new Tool(wait: true); var approval = new Approval(); var gate = new LocalControlGate(); gate.Arm();
        var policy = new ToolPermissionPolicy([tool.Descriptor.Id]);
        await using var connection = new AgentConnection([tool], approval, gate, policy);
        using var one = new Socket(); using var two = new Socket();
        var running = Invoke(connection, one, tool.Descriptor.Id);
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = Invoke(connection, two, tool.Descriptor.Id);
        policy.Replace([]);
        Assert.True((await running).Result!.IsError); Assert.True((await queued).Result!.IsError);
        Assert.Equal(1, tool.Calls); Assert.Equal(0, approval.Calls); Assert.True(gate.IsArmed);
        connection.Pause(); Assert.False(gate.IsArmed);
    }
    [Fact] public async Task Unknown_installed_tool_cannot_be_added_by_a_saved_grant()
    {
        var approval = new Approval(); var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection([], approval, gate, new(["future.tool"]));
        using var socket = new Socket();
        Assert.True((await Invoke(connection, socket, "future.tool")).Result!.IsError); Assert.Equal(0, approval.Calls);
    }
}
