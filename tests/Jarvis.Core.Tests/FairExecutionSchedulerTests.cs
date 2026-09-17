using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class FairExecutionSchedulerTests
{
    [Fact]
    public async Task Five_calls_run_and_sixth_waits_until_a_slot_is_released()
    {
        var scheduler = Create(5, 20);
        using IDisposable lifetime = scheduler;
        var leases = new List<IDisposable>();
        try
        {
            for (var i = 0; i < 5; i++) leases.Add(await Acquire(scheduler, "session-" + i));
            var sixth = Acquire(scheduler, "session-six");
            Assert.False(sixth.IsCompleted);
            Assert.Equal(5, (int)scheduler.RunningCount);
            leases[0].Dispose(); leases.RemoveAt(0);
            leases.Add(await sixth.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(5, (int)scheduler.RunningCount);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Assert.Equal(0, (int)scheduler.RunningCount);
    }

    [Fact]
    public async Task Lowering_limit_drains_without_cancelling_and_increasing_opens_slots()
    {
        var scheduler = Create(3, 20);
        using IDisposable lifetime = scheduler;
        var first = await Acquire(scheduler, "a");
        var second = await Acquire(scheduler, "b");
        var third = await Acquire(scheduler, "c");
        scheduler.Configure(1, 20, TimeSpan.FromSeconds(10));
        var pending = Acquire(scheduler, "d");
        first.Dispose(); second.Dispose();
        Assert.False(pending.IsCompleted);
        scheduler.Configure(2, 20, TimeSpan.FromSeconds(10));
        using var admitted = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, (int)scheduler.RunningCount);
        third.Dispose();
    }

    [Fact]
    public async Task Queues_rotate_between_sessions_instead_of_draining_one_chat()
    {
        var scheduler = Create(1, 20);
        using IDisposable lifetime = scheduler;
        var running = await Acquire(scheduler, "a");
        var a1 = Acquire(scheduler, "a");
        var a2 = Acquire(scheduler, "a");
        var a3 = Acquire(scheduler, "a");
        var b1 = Acquire(scheduler, "b");
        running.Dispose();
        (await a1).Dispose();
        using (await b1.WaitAsync(TimeSpan.FromSeconds(2))) Assert.False(a2.IsCompleted);
        (await a2).Dispose();
        (await a3).Dispose();
    }

    [Fact]
    public async Task Cancelled_queue_entries_release_capacity_without_using_a_slot()
    {
        var scheduler = Create(1, 1);
        using IDisposable lifetime = scheduler;
        using var running = await Acquire(scheduler, "a");
        using var cancel = new CancellationTokenSource();
        var queued = Acquire(scheduler, "b", cancel.Token);
        var full = await Assert.ThrowsAsync<AgentRequestException>(async () => await Acquire(scheduler, "c"));
        Assert.Equal("QUEUE_FULL", full.Code);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        Assert.Equal(0, (int)scheduler.QueuedCount);
        var replacement = Acquire(scheduler, "c");
        running.Dispose();
        (await replacement.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    [Fact]
    public async Task Queue_deadlines_are_reported_separately_from_execution_timeouts()
    {
        var scheduler = Create(1, 2);
        using IDisposable lifetime = scheduler;
        scheduler.Configure(1, 2, TimeSpan.FromMilliseconds(80));
        using var running = await Acquire(scheduler, "a");
        var error = await Assert.ThrowsAsync<AgentRequestException>(async () => await Acquire(scheduler, "b"));
        Assert.Equal("QUEUE_TIMEOUT", error.Code);
        Assert.Equal(0, (int)scheduler.QueuedCount);
        Assert.Equal(1, (int)scheduler.RunningCount);
    }

    private static FairExecutionScheduler Create(int concurrent, int queued)
    {
        var type = typeof(AgentConnection).Assembly.GetType("Jarvis.Agent.Core.Execution.FairExecutionScheduler");
        Assert.NotNull(type);
        return (FairExecutionScheduler)Activator.CreateInstance(type, concurrent, queued, TimeSpan.FromSeconds(10))!;
    }
    private static async Task<IDisposable> Acquire(FairExecutionScheduler scheduler, string session, CancellationToken ct = default) =>
        (IDisposable)await scheduler.AcquireAsync(session, ct);
}
