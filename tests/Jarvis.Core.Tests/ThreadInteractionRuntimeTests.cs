using System.Diagnostics;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Threads;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ThreadInteractionRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-thread-interactions-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_root, "threads.db");
    private readonly AgentSessionIdentity _identity = new("owner-a", "device-a", AgentSessionRules.NewSessionId());

    [Fact]
    public void Tool_surface_exposes_thread_wait_automation_and_async_input_operations()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        using var runtime = new ThreadInteractionRuntimeToolSet(Db, startPump: false);
        var byId = runtime.Tools.ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);

        Assert.Equal(new[]
        {
            "async_input.cancel", "async_input.list", "async_input.request", "async_input.respond", "async_input.wait",
            "automation.cancel", "automation.create", "automation.get", "automation.list", "automation.run_due", "automation.update",
            "thread.list", "thread.wait"
        }, byId.Keys.OrderBy(value => value, StringComparer.Ordinal));
        Assert.IsAssignableFrom<ICompositeAgentTool>(byId["thread.wait"]);
        Assert.IsAssignableFrom<ICompositeAgentTool>(byId["async_input.wait"]);
        Assert.Equal("thread__list", byId["thread.list"].Descriptor.Name);
        Assert.Equal("automation_create", byId["automation.create"].Descriptor.Name);
    }

    [Fact]
    public async Task Thread_list_and_wait_follow_durable_project_journal()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Waitable work", "Observe events", "runtime");
        var initial = threads.Get(thread.ThreadId, includeCompacted: true, maxEvents: 100);
        var cursor = initial.Events[^1].EventId;
        using var runtime = new ThreadInteractionRuntimeToolSet(Db, startPump: false);
        var tools = ById(runtime);
        var context = Context(_identity);

        var listed = await tools["thread.list"].ExecuteAsync(WireJson.Element(new
        {
            query = "Waitable",
            limit = 10
        }), context, default);
        Assert.False(listed.IsError, listed.Text);
        Assert.Contains(thread.ThreadId, listed.Text, StringComparison.Ordinal);

        var waiting = tools["thread.wait"].ExecuteAsync(WireJson.Element(new
        {
            threadId = thread.ThreadId,
            cursor,
            yieldTimeMs = 5_000,
            limit = 20
        }), context, default);
        await Task.Delay(100);
        threads.AppendTurn(thread.ThreadId, "assistant", "new event",
            [new ThreadItemInput("text", "arrived asynchronously")]);
        var reply = await waiting;

        Assert.False(reply.IsError, reply.Text);
        using var json = JsonDocument.Parse(reply.Text);
        var events = json.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Contains(events, item => item.GetProperty("kind").GetString() == "turn.appended");
        Assert.True(json.RootElement.GetProperty("nextCursor").GetInt64() > cursor);
        Assert.False(json.RootElement.GetProperty("timedOut").GetBoolean());
    }

    [Fact]
    public async Task One_time_automation_enqueues_exactly_one_thread_item_and_completes()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Automation", null, null);
        using var runtime = new ThreadInteractionRuntimeToolSet(Db, startPump: false);
        var tools = ById(runtime);
        var context = Context(_identity);

        var created = await tools["automation.create"].ExecuteAsync(WireJson.Element(new
        {
            threadId = thread.ThreadId,
            name = "verify release",
            payloadJson = "{\"task\":\"verify\",\"release\":\"1.0.95\"}",
            nextDueUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")
        }), context, default);
        Assert.False(created.IsError, created.Text);
        var automationId = Property(created, "automationId").GetString()!;

        var run = await tools["automation.run_due"].ExecuteAsync(WireJson.Element(new
        {
            threadId = thread.ThreadId,
            maxRuns = 10
        }), context, default);
        Assert.False(run.IsError, run.Text);
        using (var runJson = JsonDocument.Parse(run.Text))
            Assert.Single(runJson.RootElement.EnumerateArray());

        var snapshot = threads.Get(thread.ThreadId, includeCompacted: true, maxEvents: 200);
        var queued = Assert.Single(snapshot.Queue);
        Assert.Equal("queued", queued.Status);
        Assert.Contains(automationId, queued.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("verify", queued.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(snapshot.Events, item => item.Kind == "automation.fired");

        var final = await tools["automation.get"].ExecuteAsync(
            WireJson.Element(new { automationId }), context, default);
        Assert.Equal("completed", Property(final, "status").GetString());
        Assert.Equal(2, Property(final, "revision").GetInt64());
        Assert.Equal(1, Property(final, "runCount").GetInt64());

        var secondRun = await tools["automation.run_due"].ExecuteAsync(WireJson.Element(new
        {
            threadId = thread.ThreadId
        }), context, default);
        using var secondJson = JsonDocument.Parse(secondRun.Text);
        Assert.Empty(secondJson.RootElement.EnumerateArray());
        Assert.Single(threads.Get(thread.ThreadId, true, 200).Queue);
    }

    [Fact]
    public void Interval_automation_coalesces_missed_occurrences_and_remains_active()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Recurring", null, null);
        var limits = new ThreadInteractionLimits(MinIntervalSeconds: 1, MaxIntervalSeconds: 3_600);
        using var store = new SqliteThreadInteractionStore(Db, limits);
        var now = DateTimeOffset.UtcNow;
        var automation = store.CreateAutomation(_identity, thread.ThreadId, "heartbeat", "{\"kind\":\"ping\"}",
            now.AddSeconds(-5), intervalSeconds: 1);

        var run = Assert.Single(store.RunDueAutomations(now, 10, _identity, thread.ThreadId));
        Assert.Equal(automation.AutomationId, run.AutomationId);
        Assert.True(run.SkippedOccurrences >= 4);
        Assert.Equal("active", run.Status);
        Assert.True(run.NextDueAt > now);
        Assert.Single(threads.Get(thread.ThreadId, true, 200).Queue);

        var current = store.GetAutomation(_identity, automation.AutomationId);
        Assert.Equal(1, current.RunCount);
        Assert.Equal(2, current.Revision);
        Assert.Equal("active", current.Status);
    }

    [Fact]
    public void Automation_due_comparison_normalizes_non_utc_offsets()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Offset schedule", null, null);
        using var store = new SqliteThreadInteractionStore(Db);
        var now = DateTimeOffset.UtcNow;
        var dueWithOffset = now.AddSeconds(-5).ToOffset(TimeSpan.FromHours(7));
        var automation = store.CreateAutomation(_identity, thread.ThreadId, "offset", "{}", dueWithOffset, null);

        var run = Assert.Single(store.RunDueAutomations(now, 10, _identity, thread.ThreadId));
        Assert.Equal(automation.AutomationId, run.AutomationId);
        Assert.Equal(TimeSpan.Zero, run.ScheduledFor.Offset);
    }

    [Fact]
    public void Automations_are_revisioned_session_owned_and_cancelled_on_session_close()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Scoped automation", null, null);
        using var store = new SqliteThreadInteractionStore(Db);
        var created = store.CreateAutomation(_identity, thread.ThreadId, "scoped", "{}",
            DateTimeOffset.UtcNow.AddHours(1), null);
        var updated = store.UpdateAutomation(_identity, created.AutomationId, 1,
            "updated", null, null, null, intervalSupplied: false, paused: true);
        Assert.Equal(2, updated.Revision);
        Assert.Equal("paused", updated.Status);

        var conflict = Assert.Throws<AgentRequestException>(() => store.UpdateAutomation(_identity,
            created.AutomationId, 1, "stale", null, null, null, false, null));
        Assert.Equal("AUTOMATION_REVISION_CONFLICT", conflict.Code);
        var otherSession = new AgentSessionIdentity(_identity.OwnerId, _identity.DeviceId, AgentSessionRules.NewSessionId());
        Assert.Throws<KeyNotFoundException>(() => store.GetAutomation(otherSession, created.AutomationId));
        var otherDevice = new AgentSessionIdentity(_identity.OwnerId, "device-b", _identity.SessionId);
        Assert.Throws<KeyNotFoundException>(() => store.GetAutomation(otherDevice, created.AutomationId));

        store.CloseSession(_identity);
        var cancelled = store.GetAutomation(_identity, created.AutomationId);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal(3, cancelled.Revision);
        Assert.Null(cancelled.NextDueAt);
    }

    [Fact]
    public async Task Local_pump_materializes_persisted_due_automation_after_runtime_restart()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Pump restart", null, null);
        string automationId;
        using (var seed = new SqliteThreadInteractionStore(Db))
        {
            automationId = seed.CreateAutomation(_identity, thread.ThreadId, "restart catch-up", "{\"job\":1}",
                DateTimeOffset.UtcNow.AddMinutes(-2), null).AutomationId;
        }

        using var runtime = new ThreadInteractionRuntimeToolSet(Db, startPump: true,
            pumpInterval: TimeSpan.FromMilliseconds(50));
        var timeout = Stopwatch.StartNew();
        ThreadSnapshot snapshot;
        do
        {
            snapshot = threads.Get(thread.ThreadId, true, 200);
            if (snapshot.Queue.Count > 0) break;
            await Task.Delay(25);
        } while (timeout.Elapsed < TimeSpan.FromSeconds(5));

        var queued = Assert.Single(snapshot.Queue);
        Assert.Contains(automationId, queued.PayloadJson, StringComparison.Ordinal);
        var get = await ById(runtime)["automation.get"].ExecuteAsync(
            WireJson.Element(new { automationId }), Context(_identity), default);
        Assert.Equal("completed", Property(get, "status").GetString());
    }

    [Fact]
    public async Task Async_input_validates_schema_and_wait_returns_answered_value()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Need approval", null, null);
        using var runtime = new ThreadInteractionRuntimeToolSet(Db, startPump: false);
        var tools = ById(runtime);
        var context = Context(_identity);
        var requested = await tools["async_input.request"].ExecuteAsync(WireJson.Element(new
        {
            threadId = thread.ThreadId,
            prompt = "Approve the release?",
            schemaJson = "{\"type\":\"object\",\"properties\":{\"approved\":{\"type\":\"boolean\"}},\"required\":[\"approved\"],\"additionalProperties\":false}"
        }), context, default);
        Assert.False(requested.IsError, requested.Text);
        var inputId = Property(requested, "inputId").GetString()!;

        var invalid = await tools["async_input.respond"].ExecuteAsync(WireJson.Element(new
        {
            inputId,
            expectedRevision = 1,
            answerJson = "{\"approved\":\"yes\"}"
        }), context, default);
        Assert.True(invalid.IsError);
        Assert.Contains("schema", invalid.Text, StringComparison.OrdinalIgnoreCase);

        var waiting = tools["async_input.wait"].ExecuteAsync(WireJson.Element(new
        {
            inputId,
            yieldTimeMs = 5_000
        }), context, default);
        await Task.Delay(100);
        var answered = await tools["async_input.respond"].ExecuteAsync(WireJson.Element(new
        {
            inputId,
            expectedRevision = 1,
            answerJson = "{\"approved\":true}"
        }), context, default);
        Assert.False(answered.IsError, answered.Text);
        Assert.Equal("answered", Property(answered, "status").GetString());
        Assert.Equal(2, Property(answered, "revision").GetInt64());

        var observed = await waiting;
        Assert.Equal("answered", Property(observed, "status").GetString());
        Assert.Contains("approved", Property(observed, "answerJson").GetString(), StringComparison.Ordinal);
        Assert.Contains(threads.Get(thread.ThreadId, true, 200).Events,
            item => item.Kind == "async_input.answered");
    }

    [Fact]
    public void Async_input_expiration_and_session_close_are_durable_terminal_states()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Input lifecycle", null, null);
        using var store = new SqliteThreadInteractionStore(Db);
        var expiring = store.CreateInput(_identity, thread.ThreadId, "Short lived", "{}", expiresInSeconds: 1);
        store.ExpireDueInputs(DateTimeOffset.UtcNow.AddSeconds(2));
        var expired = store.GetInput(_identity, expiring.InputId);
        Assert.Equal("expired", expired.Status);
        Assert.Equal(2, expired.Revision);

        var pending = store.CreateInput(_identity, thread.ThreadId, "Close me", "{}", null);
        store.CloseSession(_identity);
        var cancelled = store.GetInput(_identity, pending.InputId);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal(2, cancelled.Revision);
        var terminal = Assert.Throws<AgentRequestException>(() =>
            store.RespondInput(_identity, pending.InputId, 2, "{}"));
        Assert.Equal("ASYNC_INPUT_TERMINAL", terminal.Code);
    }

    [Fact]
    public void Async_input_is_revisioned_and_owned_by_exact_session_and_device()
    {
        Directory.CreateDirectory(_root);
        using var threads = new SqliteThreadRuntimeStore(Db);
        var thread = threads.Create(_root, "Scoped input", null, null);
        using var store = new SqliteThreadInteractionStore(Db);
        var input = store.CreateInput(_identity, thread.ThreadId, "Choose", "{\"type\":\"string\"}", null);

        var conflict = Assert.Throws<AgentRequestException>(() =>
            store.RespondInput(_identity, input.InputId, expectedRevision: 2, answerJson: "\"a\""));
        Assert.Equal("ASYNC_INPUT_REVISION_CONFLICT", conflict.Code);
        var otherSession = new AgentSessionIdentity(_identity.OwnerId, _identity.DeviceId, AgentSessionRules.NewSessionId());
        Assert.Throws<KeyNotFoundException>(() => store.GetInput(otherSession, input.InputId));
        var otherDevice = new AgentSessionIdentity(_identity.OwnerId, "device-b", _identity.SessionId);
        Assert.Throws<KeyNotFoundException>(() => store.GetInput(otherDevice, input.InputId));

        var answered = store.RespondInput(_identity, input.InputId, 1, "\"a\"");
        Assert.Equal("answered", answered.Status);
        Assert.Equal(2, answered.Revision);
    }

    [Fact]
    public async Task Tools_require_explicit_session_and_enforce_workspace_scope()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(Path.GetTempPath(), "jarvis-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            using var threads = new SqliteThreadRuntimeStore(Db);
            var insideThread = threads.Create(_root, "Inside", null, null);
            var outsideThread = threads.Create(outside, "Outside", null, null);
            using var runtime = new ThreadInteractionRuntimeToolSet(Db, startPump: false);
            var tools = ById(runtime);
            var ephemeral = new AgentExecutionContext(_root, "call", AgentSessionRules.NewEphemeralExecutionId());
            var noSession = await tools["thread.list"].ExecuteAsync(WireJson.Element(new { }), ephemeral, default);
            Assert.True(noSession.IsError);

            var context = Context(_identity);
            var inside = await tools["async_input.request"].ExecuteAsync(WireJson.Element(new
            {
                threadId = insideThread.ThreadId,
                prompt = "Inside?"
            }), context, default);
            Assert.False(inside.IsError, inside.Text);

            var denied = await tools["async_input.request"].ExecuteAsync(WireJson.Element(new
            {
                threadId = outsideThread.ThreadId,
                prompt = "Outside?"
            }), context, default);
            Assert.True(denied.IsError);
            Assert.Contains("outside selected workspaces", denied.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    private static Dictionary<string, IAgentTool> ById(ThreadInteractionRuntimeToolSet runtime) =>
        runtime.Tools.ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);

    private AgentExecutionContext Context(AgentSessionIdentity identity) =>
        new(_root, "thread-interaction-test", identity.SessionId)
        {
            OwnerId = identity.OwnerId,
            AgentDeviceId = identity.DeviceId,
            SessionCancellation = CancellationToken.None
        };

    private static JsonElement Property(ToolReply reply, string name)
    {
        using var document = JsonDocument.Parse(reply.Text);
        return document.RootElement.GetProperty(name).Clone();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
