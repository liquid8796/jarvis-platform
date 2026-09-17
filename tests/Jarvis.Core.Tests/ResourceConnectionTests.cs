using System.Reflection;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ResourceConnectionTests
{
    [Fact]
    public async Task Writes_to_five_independent_files_are_not_serialized_by_a_global_write_lock()
    {
        var held = new HeldWrite();
        var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection([held], new Approve(), gate);
        var calls = Enumerable.Range(0, 5).Select(i => Invoke(connection, Path.Combine(Path.GetTempPath(), "jarvis-file-" + Guid.NewGuid().ToString("N")), "session-" + i)).ToArray();
        try { await held.FiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { held.Release.TrySetResult(); await Task.WhenAll(calls); }
    }

    [Fact]
    public async Task Resource_wait_uses_the_bounded_queue_deadline_and_never_runs_after_timeout()
    {
        var held = new HeldWrite(); var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection([held], new Approve(), gate);
        connection.ApplyExecutionSettings(new() { Revision = 2, QueueTimeoutSeconds = 1 });
        var path = Path.Combine(Path.GetTempPath(), "jarvis-file-" + Guid.NewGuid().ToString("N"));
        var first = Invoke(connection, path, "session-a");
        try
        {
            await held.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var error = await Assert.ThrowsAsync<AgentRequestException>(() => Invoke(connection, path, "session-b").WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("QUEUE_TIMEOUT", error.Code);
            Assert.Equal(1, Volatile.Read(ref held.Started));
        }
        finally { held.Release.TrySetResult(); await first; }
    }

    private static Task<ToolReply> Invoke(AgentConnection connection, string path, string session)
    {
        var method = typeof(AgentConnection).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == "InvokeInstalledToolAsync" && m.GetParameters().Length == 4);
        return (Task<ToolReply>)method.Invoke(connection, ["filesystem.Write", WireJson.Element(new { file_path = path }),
            new AgentExecutionContext(Path.GetTempPath(), Guid.NewGuid().ToString("N"), session), CancellationToken.None])!;
    }
    private sealed class HeldWrite : IAgentTool
    {
        public int Started;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ToolDescriptor Descriptor { get; } = new("filesystem.Write", "filesystem__Write", "filesystem", "Synthetic held write",
            WireJson.Element(new { type = "object", properties = new { file_path = new { type = "string" } }, required = new[] { "file_path" }, additionalProperties = false }), false);
        public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref Started); FirstStarted.TrySetResult(); if (count == 5) FiveStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken); return new("done");
        }
    }
    private sealed class Approve : IApprovalService
    { public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(true); }
}
