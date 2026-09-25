using System.Reflection;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ExecutionConnectionTests
{
    [Fact]
    public async Task Five_independent_calls_run_and_status_has_a_separate_lane()
    {
        var gate = new LocalControlGate(); gate.Arm();
        var held = new HeldTool();
        await using var connection = new AgentConnection([held, new StatusTool()], new Approve(), gate);
        var calls = Enumerable.Range(0, 5).Select(i => Invoke(connection, "test.held", "session-" + i)).ToArray();
        try
        {
            await held.FiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var status = await Invoke(connection, "unified_exec.write_stdin", "observer").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("status-still-responsive", status.Text);
            Assert.Equal(5, Volatile.Read(ref held.Started));
        }
        finally { held.Release.TrySetResult(); await Task.WhenAll(calls); }
    }

    private static Task<ToolReply> Invoke(AgentConnection connection, string id, string session)
    {
        var method = typeof(AgentConnection).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "InvokeInstalledToolAsync" && m.GetParameters().Length == 4);
        return (Task<ToolReply>)method.Invoke(connection,
            [id, WireJson.Element(new { }), new AgentExecutionContext(Path.GetTempPath(), Guid.NewGuid().ToString("N"), session), CancellationToken.None])!;
    }
    private sealed class HeldTool : IAgentTool
    {
        public int Started;
        public TaskCompletionSource FiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ToolDescriptor Descriptor { get; } = new("test.held", "test_held", "test", "Held read-only fixture", WireJson.Element(new { type = "object" }), true);
        public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Started) == 5) FiveStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new("done");
        }
    }
    private sealed class StatusTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("unified_exec.write_stdin", "write_stdin", "process", "Status fixture", WireJson.Element(new { type = "object" }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(new ToolReply("status-still-responsive"));
    }
    private sealed class Approve : IApprovalService
    { public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(true); }
}
