using JarvisCode.Core.Agent;

namespace JarvisCode.Core.Tests.Agent;

public sealed class SessionIdleSubscriptionTests
{
    [Fact]
    public void Idle_exit_expiry_are_mutually_exclusive_one_shot_deliveries()
    {
        using var broker = new SessionIdleSubscriptions(automaticExpiry: false);
        var now = DateTimeOffset.UtcNow;
        List<SessionIdleNotice> notices = [];
        broker.Subscribe("target", "self", "Example", notices.Add, now);
        broker.Idle("target", now, "Finished <tag> review\nUnrelated followup");
        broker.Exit("target");
        broker.Sweep(now.AddDays(1));
        var idle = Assert.Single(notices);
        Assert.Equal("idle", idle.Kind);
        Assert.Equal("Finished tag review", idle.Detail);
        Assert.Contains("not an instruction", idle.Message);
        notices.Clear();
        broker.Subscribe("target", "self", "Example", notices.Add, now);
        broker.Sweep(now.AddHours(11));
        Assert.Empty(notices);
        broker.Sweep(now.AddHours(12));
        Assert.Equal("expired", Assert.Single(notices).Kind);
        notices.Clear();
        broker.Subscribe("target", "self", "Example", notices.Add, now);
        broker.Exit("target", now);
        Assert.Equal("exited", Assert.Single(notices).Kind);
    }

    [Fact]
    public void Subscription_cap_is_32_and_replacing_one_target_does_not_spend_a_second_slot()
    {
        using var broker = new SessionIdleSubscriptions(false);
        for (var i = 0; i < 32; i++) Assert.Null(broker.Subscribe("target-" + i, "self", "Example", _ => { }));
        Assert.Null(broker.Subscribe("target-0", "self", "Example", _ => { }));
        Assert.Contains("32", broker.Subscribe("extra", "self", "Example", _ => { }));
        broker.Exit("self");
        Assert.Null(broker.Subscribe("extra", "self", "Example", _ => { }));
    }
}
