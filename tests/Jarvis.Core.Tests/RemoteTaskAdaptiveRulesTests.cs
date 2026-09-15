using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class RemoteTaskAdaptiveRulesTests
{
    private static RemoteTaskStep Step(string id = "build", string stage = "BUILD", string tool = "test.tool") => new()
    {
        Id = id, Stage = stage, ToolId = tool, Arguments = WireJson.Element(new { }), MaxAttempts = 1
    };

    [Fact]
    public void Repair_must_keep_logical_id_and_not_regress_stage()
    {
        var failed = Step();
        Assert.Throws<ArgumentException>(() => RemoteTaskAdaptiveRules.ValidateReplacement(failed, Step("other")));
        Assert.Throws<ArgumentException>(() => RemoteTaskAdaptiveRules.ValidateReplacement(failed, Step(stage: "EXECUTE")));
        RemoteTaskAdaptiveRules.ValidateReplacement(failed, Step(stage: "TEST", tool: "test.repair"));
    }

    [Fact]
    public void Adaptive_repair_is_bounded_and_only_for_autonomous_mode()
    {
        Assert.False(RemoteTaskAdaptiveRules.CanRepair("NORMAL", repairCount: 0, cancelledOrTimedOut: false, coordinatorAvailable: true));
        Assert.False(RemoteTaskAdaptiveRules.CanRepair("AUTONOMOUS", repairCount: 0, cancelledOrTimedOut: true, coordinatorAvailable: true));
        Assert.True(RemoteTaskAdaptiveRules.CanRepair("AUTONOMOUS", repairCount: 0, cancelledOrTimedOut: false, coordinatorAvailable: true));
        Assert.False(RemoteTaskAdaptiveRules.CanRepair("AUTONOMOUS", RemoteTaskAdaptiveRules.MaxRepairs, false, true));
    }
}
