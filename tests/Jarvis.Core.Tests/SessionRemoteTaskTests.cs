using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class SessionRemoteTaskTests
{
    [Fact]
    public async Task Task_reads_plans_artifacts_and_cancellation_require_the_creating_session()
    {
        await using var fixture = new Fixture();
        var a = AgentSessionRules.NewSessionId(); var b = AgentSessionRules.NewSessionId();
        var id = Guid.NewGuid().ToString();
        var created = await fixture.Host.HandleAsync("create", new("owner", id, Plan(), SessionId: a), CancellationToken.None);
        Assert.Null(created.Error);
        foreach (var operation in new[] { "get", "artifacts", "cancel", "plan" })
        {
            var reply = await fixture.Host.HandleAsync(operation, new("owner", id, operation == "plan" ? Plan(true) : null, SessionId: b), CancellationToken.None);
            Assert.Equal("not_found", reply.ErrorCode);
        }
        var own = await fixture.Host.HandleAsync("get", new("owner", id, SessionId: a), CancellationToken.None);
        Assert.Equal("NEEDS_PLAN", own.Task!.Status);
        var child = await fixture.Host.HandleAsync("create", new("owner", Guid.NewGuid().ToString(), Plan(), ParentTaskId: id, SessionId: b), CancellationToken.None);
        Assert.NotNull(child.Error);
    }

    [Fact]
    public async Task Task_step_uses_original_workspace_and_application_identity_after_session_changes()
    {
        await using var fixture = new Fixture();
        var session = AgentSessionRules.NewSessionId();
        var selected = fixture.Workspace;
        var resolver = new Func<RemoteTaskRequest, AgentExecutionContext?>(request => request.SessionId is null ? null :
            new(selected, "task-request", request.SessionId) { OwnerId = request.OwnerId, AgentDeviceId = "agent", WorkspaceRevision = 7 });
        var field = typeof(RemoteTaskHost).GetField("_resolveSession", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(fixture.Host, resolver);
        var id = Guid.NewGuid().ToString();
        var created = await fixture.Host.HandleAsync("create", new("owner", id, Plan(), SessionId: session), CancellationToken.None);
        Assert.Null(created.Error);
        selected = fixture.OtherWorkspace;
        var planned = await fixture.Host.HandleAsync("plan", new("owner", id, Plan(true), SessionId: session), CancellationToken.None);
        Assert.Null(planned.Error);
        var executed = await fixture.Seen.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(fixture.Workspace, executed.Workspace);
        Assert.Equal(session, executed.SessionId);
        Assert.Equal("owner", executed.OwnerId);
        Assert.Equal("agent", executed.AgentDeviceId);
        Assert.Equal(7, executed.WorkspaceRevision);
    }

    [Fact]
    public async Task Five_tasks_run_and_sixth_queues_without_the_old_two_task_limit()
    {
        await using var fixture = new Fixture(hold: true);
        var requests = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var id = Guid.NewGuid().ToString(); requests.Add(id);
            var reply = await fixture.Host.HandleAsync("create", new("owner", id, Plan(true)), CancellationToken.None);
            Assert.Null(reply.Error);
        }
        for (var i = 0; i < 5; i++) await fixture.Seen.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Seen.Reader.TryRead(out _));
        fixture.Release.TrySetResult();
        await fixture.Seen.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static RemoteTaskPlan Plan(bool steps = false) => new()
    {
        Goal = "Session task fixture", TimeoutSeconds = 20,
        Steps = steps ? [new() { Id = "inspect", ToolId = "test.inspect", TimeoutSeconds = 15 }] : []
    };
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-session-task-" + Guid.NewGuid().ToString("N"));
        public string Workspace { get; }
        public string OtherWorkspace { get; }
        public RemoteTaskHost Host { get; }
        public Channel<AgentExecutionContext> Seen { get; } = Channel.CreateUnbounded<AgentExecutionContext>();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture(bool hold = false)
        {
            Workspace = Path.Combine(_root, "workspace-a"); OtherWorkspace = Path.Combine(_root, "workspace-b");
            Directory.CreateDirectory(Workspace); Directory.CreateDirectory(OtherWorkspace);
            Host = new(Path.Combine(_root, "tasks"), new WorkspaceDirectories(Workspace), new DynamicToolRegistry([new InspectTool()]),
                () => true, async (_, _, context, ct) => { Seen.Writer.TryWrite(context); if (hold) await Release.Task.WaitAsync(ct); return new("ok"); },
                (_, _) => Task.CompletedTask);
        }
        public async ValueTask DisposeAsync() { Release.TrySetResult(); await Host.DisposeAsync(); Directory.Delete(_root, true); }
    }
    private sealed class InspectTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.inspect", "test_inspect", "test", "Inspect", WireJson.Element(new { type = "object" }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(new ToolReply("ok"));
    }
}
