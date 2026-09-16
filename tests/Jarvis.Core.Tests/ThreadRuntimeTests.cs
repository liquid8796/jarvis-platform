using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Threads;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ThreadRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-thread-runtime-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_root, "threads.db");

    [Fact]
    public void Journal_persists_turn_items_artifacts_queue_checkpoints_and_fork_lineage()
    {
        Directory.CreateDirectory(_root);
        string threadId;
        long turnEventId;
        using (var store = new SqliteThreadRuntimeStore(Db))
        {
            var thread = store.Create(_root, "Parity work", "Close orchestration gaps", "runtime");
            threadId = thread.ThreadId;
            var turn = store.AppendTurn(threadId, "assistant", "Implemented runtime",
            [
                new ThreadItemInput("text", "process runtime ready"),
                new ThreadItemInput("artifact", "build-output",
                [new ThreadArtifactInput("log", "artifact://build/1", "{\"passed\":240}")])
            ]);
            turnEventId = turn.EventId;

            var first = store.QueueAdd(threadId, "{\"task\":\"verify\"}");
            var second = store.QueueAdd(threadId, "{\"task\":\"package\"}");
            store.QueueReorder(threadId, [second.QueueId, first.QueueId]);
            store.QueueStart(threadId, second.QueueId);
            store.UpdateMetadata(threadId, goal: "Ship parity", section: "verification");
            store.Compact(threadId, turnEventId, "Runtime work compacted");
            store.Rollback(threadId, turnEventId, "Review from verified turn");

            var fork = store.Fork(threadId, "Alternative follow-up");
            Assert.Equal(threadId, fork.ParentThreadId);
            Assert.True(fork.ForkEventId >= turnEventId);

            var snapshot = store.Get(threadId, includeCompacted: true, maxEvents: 100);
            Assert.Equal("Ship parity", snapshot.Goal);
            Assert.Equal("verification", snapshot.Section);
            Assert.Equal(turnEventId, snapshot.CompactThroughEventId);
            Assert.Equal(turnEventId, snapshot.RollbackTargetEventId);
            Assert.Equal([second.QueueId, first.QueueId], snapshot.Queue.Select(x => x.QueueId));
            Assert.Equal("running", snapshot.Queue[0].Status);
            Assert.Contains(snapshot.Events, e => e.Kind == "turn.appended" && e.PayloadJson.Contains("artifact://build/1"));
            Assert.Contains(snapshot.Events, e => e.Kind == "queue.reordered");
            Assert.Contains(snapshot.Events, e => e.Kind == "checkpoint.compacted");
            Assert.Contains(snapshot.Events, e => e.Kind == "checkpoint.rollback");
            Assert.True(snapshot.Events.Zip(snapshot.Events.Skip(1), (a, b) => a.EventId < b.EventId).All(x => x));
        }

        using var reopened = new SqliteThreadRuntimeStore(Db);
        var persisted = reopened.Get(threadId, includeCompacted: true, maxEvents: 100);
        Assert.Contains(persisted.Events, e => e.EventId == turnEventId);
        Assert.Contains(reopened.Search("process runtime", _root, 10), hit => hit.ThreadId == threadId);
    }

    [Fact]
    public void Default_timeline_uses_compaction_checkpoint_without_deleting_append_only_history()
    {
        Directory.CreateDirectory(_root);
        using var store = new SqliteThreadRuntimeStore(Db);
        var thread = store.Create(_root, "Compaction", null, null);
        var first = store.AppendTurn(thread.ThreadId, "user", null, [new ThreadItemInput("text", "old context")]);
        var second = store.AppendTurn(thread.ThreadId, "assistant", null, [new ThreadItemInput("text", "new context")]);
        store.Compact(thread.ThreadId, first.EventId, "summary of old context");

        var compacted = store.Get(thread.ThreadId, includeCompacted: false, maxEvents: 100);
        Assert.Equal("summary of old context", compacted.CompactSummary);
        Assert.DoesNotContain(compacted.Events, e => e.EventId <= first.EventId);
        Assert.Contains(compacted.Events, e => e.EventId == second.EventId);

        var audit = store.Get(thread.ThreadId, includeCompacted: true, maxEvents: 100);
        Assert.Contains(audit.Events, e => e.EventId == first.EventId);
    }

    [Fact]
    public async Task Thread_tools_publish_bounded_durable_operations()
    {
        Directory.CreateDirectory(_root);
        using var tools = new ThreadRuntimeToolSet(Db);
        var ids = tools.Tools.Select(x => x.Descriptor.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new[] { "thread.append_turn", "thread.checkpoint", "thread.create", "thread.fork", "thread.get", "thread.queue", "thread.search" }, ids.Order().ToArray());

        var context = new AgentExecutionContext(_root, "thread-call", "session");
        var create = tools.Tools.Single(x => x.Descriptor.Id == "thread.create");
        using var created = JsonDocument.Parse((await create.ExecuteAsync(WireJson.Element(new { title = "Tool thread", goal = "test" }), context, CancellationToken.None)).Text);
        var id = created.RootElement.GetProperty("threadId").GetString();

        var append = tools.Tools.Single(x => x.Descriptor.Id == "thread.append_turn");
        var appended = await append.ExecuteAsync(WireJson.Element(new
        {
            threadId = id,
            role = "user",
            items = new[] { new { type = "text", content = "hello durable thread" } }
        }), context, CancellationToken.None);
        Assert.False(appended.IsError);

        var search = tools.Tools.Single(x => x.Descriptor.Id == "thread.search");
        var found = await search.ExecuteAsync(WireJson.Element(new { query = "hello durable", limit = 10 }), context, CancellationToken.None);
        Assert.Contains(id!, found.Text);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
