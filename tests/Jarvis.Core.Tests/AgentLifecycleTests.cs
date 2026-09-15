using Jarvis.Agent.Core;

namespace Jarvis.Core.Tests;

public sealed class AgentLifecycleTests
{
    [Fact]
    public async Task Hub_notifies_each_subscriber_once_and_isolates_failures()
    {
        var hub = new AgentLifecycleHub();
        var seen = new List<AgentLifecycleEvent>();
        using var good = hub.Subscribe((evt, _) => { seen.Add(evt); return Task.CompletedTask; });
        using var bad = hub.Subscribe((_, _) => throw new InvalidOperationException("synthetic"));

        await hub.NotifyAsync(new AgentLifecycleEvent(AgentLifecycleKind.Interrupt, "thread-1", "turn-1", "call-1"));

        var item = Assert.Single(seen);
        Assert.Equal(AgentLifecycleKind.Interrupt, item.Kind);
        Assert.Equal("thread-1", item.ThreadId);
        Assert.Equal("turn-1", item.TurnId);
        Assert.Equal("call-1", item.CallId);
    }

    [Fact]
    public async Task Disposed_subscription_is_not_called()
    {
        var hub = new AgentLifecycleHub();
        var calls = 0;
        var subscription = hub.Subscribe((_, _) => { calls++; return Task.CompletedTask; });
        subscription.Dispose();

        await hub.NotifyAsync(new AgentLifecycleEvent(AgentLifecycleKind.Stop));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Execution_context_carries_thread_turn_and_existing_call_identity()
    {
        var context = new AgentExecutionContext("c:/work", "call-7", "session-2")
        {
            ThreadId = "thread-3",
            TurnId = "turn-4"
        };

        Assert.Equal("thread-3", context.ThreadId);
        Assert.Equal("turn-4", context.TurnId);
        Assert.Equal("call-7", context.CallId);
        Assert.Equal("session-2", context.SessionId);
    }
}
