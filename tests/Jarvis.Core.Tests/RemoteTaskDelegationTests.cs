using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class RemoteTaskDelegationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-delegation-" + Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "jarvis-delegation-project-" + Guid.NewGuid().ToString("N"));
    private readonly string _other = Path.Combine(Path.GetTempPath(), "jarvis-delegation-other-" + Guid.NewGuid().ToString("N"));

    private sealed class NoopTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.noop", "test_noop", "test", "noop",
            WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("ok"));
    }

    public RemoteTaskDelegationTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_other);
    }

    private RemoteTaskHost Host() => new(_root, new WorkspaceDirectories(_project, [_other]), new DynamicToolRegistry([new NoopTool()]),
        () => true, (tool, args, context, ct) => Task.FromResult(new ToolReply("ok")), (_, _) => Task.CompletedTask);

    private static RemoteTaskPlan Plan(string mode = "NORMAL", string? project = null) => new()
    {
        Goal = "delegation test",
        ExecutionMode = mode,
        Project = project,
        Steps = []
    };

    [Fact]
    public async Task Lineage_persists_across_host_reopen()
    {
        var parentId = Guid.NewGuid().ToString();
        var childId = Guid.NewGuid().ToString();
        await using (var host = Host())
        {
            var parent = await host.HandleAsync("create", new("owner", parentId, Plan()), CancellationToken.None);
            Assert.Null(parent.Error);
            var child = await host.HandleAsync("create", new("owner", childId, Plan(), ParentTaskId: parentId), CancellationToken.None);
            Assert.Null(child.Error);
            Assert.Equal(RemoteTaskRules.TaskId(parentId), child.Task!.ParentTaskId);
            Assert.Equal(RemoteTaskRules.TaskId(parentId), child.Task.RootTaskId);
            Assert.Equal(1, child.Task.Depth);
        }

        await using var reopened = Host();
        var loaded = await reopened.HandleAsync("get", new("owner", childId), CancellationToken.None);
        Assert.Equal(RemoteTaskRules.TaskId(parentId), loaded.Task!.ParentTaskId);
        Assert.Equal(RemoteTaskRules.TaskId(parentId), loaded.Task.RootTaskId);
        Assert.Equal(1, loaded.Task.Depth);
    }

    [Fact]
    public async Task Child_mode_and_project_can_only_narrow_or_inherit_parent_scope()
    {
        await using var host = Host();
        var readOnlyParent = Guid.NewGuid().ToString();
        await host.HandleAsync("create", new("owner", readOnlyParent, Plan("READ_ONLY")), CancellationToken.None);

        var broader = await host.HandleAsync("create", new("owner", Guid.NewGuid().ToString(), Plan("NORMAL"), ParentTaskId: readOnlyParent), CancellationToken.None);
        Assert.Equal("invalid", broader.ErrorCode);

        var normalParent = Guid.NewGuid().ToString();
        await host.HandleAsync("create", new("owner", normalParent, Plan("NORMAL")), CancellationToken.None);
        var differentProject = await host.HandleAsync("create", new("owner", Guid.NewGuid().ToString(), Plan("READ_ONLY", _other), ParentTaskId: normalParent), CancellationToken.None);
        Assert.Equal("invalid", differentProject.ErrorCode);

        var narrowed = await host.HandleAsync("create", new("owner", Guid.NewGuid().ToString(), Plan("READ_ONLY"), ParentTaskId: normalParent), CancellationToken.None);
        Assert.Null(narrowed.Error);
    }

    [Fact]
    public async Task Delegation_enforces_depth_and_child_count_limits()
    {
        await using var host = Host();
        var root = Guid.NewGuid().ToString();
        await host.HandleAsync("create", new("owner", root, Plan()), CancellationToken.None);
        var current = root;
        for (var depth = 1; depth <= RemoteTaskDelegation.MaxDepth; depth++)
        {
            var next = Guid.NewGuid().ToString();
            var reply = await host.HandleAsync("create", new("owner", next, Plan(), ParentTaskId: current), CancellationToken.None);
            Assert.Null(reply.Error);
            Assert.Equal(depth, reply.Task!.Depth);
            current = next;
        }
        var tooDeep = await host.HandleAsync("create", new("owner", Guid.NewGuid().ToString(), Plan(), ParentTaskId: current), CancellationToken.None);
        Assert.Equal("invalid", tooDeep.ErrorCode);

        var countParent = Guid.NewGuid().ToString();
        await host.HandleAsync("create", new("owner", countParent, Plan()), CancellationToken.None);
        for (var i = 0; i < RemoteTaskDelegation.MaxChildren; i++)
        {
            var child = await host.HandleAsync("create", new("owner", Guid.NewGuid().ToString(), Plan(), ParentTaskId: countParent), CancellationToken.None);
            Assert.Null(child.Error);
        }
        var tooMany = await host.HandleAsync("create", new("owner", Guid.NewGuid().ToString(), Plan(), ParentTaskId: countParent), CancellationToken.None);
        Assert.Equal("busy", tooMany.ErrorCode);
    }

    [Fact]
    public async Task Join_waits_until_bounded_children_are_terminal()
    {
        var calls = 0;
        var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        var result = await RemoteTaskDelegation.JoinAsync(ids, (id, _) =>
        {
            calls++;
            var terminal = calls > ids.Length;
            return Task.FromResult<RemoteTaskSnapshot?>(new(RemoteTaskRules.TaskId(id), "goal", _project,
                terminal ? "COMPLETED" : "RUNNING", null, terminal ? 1 : 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result.Values, snapshot => Assert.Equal("COMPLETED", snapshot.Status));
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteTaskDelegation.JoinAsync(
            Enumerable.Range(0, RemoteTaskDelegation.MaxChildren + 1).Select(_ => Guid.NewGuid().ToString()),
            (_, _) => Task.FromResult<RemoteTaskSnapshot?>(null), TimeSpan.Zero, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_project)) Directory.Delete(_project, true);
        if (Directory.Exists(_other)) Directory.Delete(_other, true);
    }
}
