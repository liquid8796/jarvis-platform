using System.Text.Json;
using Jarvis.Agent.Core.Autonomous;
using Jarvis.Agent.Core.Autonomous.Planning;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class AdaptiveAgentHarnessTests
{
    private static AdaptiveAction Action(string id, params string[] dependsOn) =>
        new(id, "test." + id, WireJson.Element(new { }), dependsOn);

    [Fact]
    public void Plan_rejects_duplicate_missing_and_cyclic_dependencies()
    {
        Assert.Throws<ArgumentException>(() => new AdaptivePlan([Action("a"), Action("a")]).Validate());
        Assert.Throws<ArgumentException>(() => new AdaptivePlan([Action("a", "missing")]).Validate());
        Assert.Throws<ArgumentException>(() => new AdaptivePlan([Action("a", "b"), Action("b", "a")]).Validate());
    }

    [Fact]
    public async Task Loop_executes_dependencies_before_dependents()
    {
        var executor = new RecordingExecutor();
        var loop = new AdaptiveAgentExecutionLoop(
            new StaticPlanner(new AdaptivePlan([Action("a"), Action("b", "a"), Action("c", "b")])),
            executor, new PassVerifier(), new NoRepair());

        var result = await loop.ExecuteAsync(new AgentTask("task", "goal"));

        Assert.Equal(AgentTaskStatus.Completed, result.Task.Status);
        Assert.Equal(["a", "b", "c"], executor.Executed);
        Assert.Equal(3, result.Outcomes.Count);
    }

    [Fact]
    public async Task Verification_failure_uses_bounded_replacement_without_replaying_completed_actions()
    {
        var executor = new RecordingExecutor();
        var verifier = new FailFirstVerifier("b");
        var replanner = new ReplaceFailedAction();
        var loop = new AdaptiveAgentExecutionLoop(
            new StaticPlanner(new AdaptivePlan([Action("a"), Action("b", "a"), Action("c", "b")], MaxRepairs: 2)),
            executor, verifier, replanner);

        var result = await loop.ExecuteAsync(new AgentTask("task", "goal"));

        Assert.Equal(AgentTaskStatus.Completed, result.Task.Status);
        Assert.Equal(["a", "b", "b", "c"], executor.Executed);
        Assert.Equal(1, replanner.Calls);
        Assert.Equal(1, executor.Executed.Count(x => x == "a"));
    }

    [Fact]
    public async Task Repair_budget_stops_repeated_failure()
    {
        var executor = new RecordingExecutor();
        var replanner = new ReplaceFailedAction();
        var loop = new AdaptiveAgentExecutionLoop(
            new StaticPlanner(new AdaptivePlan([Action("a")], MaxRepairs: 1)),
            executor, new AlwaysFailVerifier(), replanner);

        var result = await loop.ExecuteAsync(new AgentTask("task", "goal"));

        Assert.Equal(AgentTaskStatus.Failed, result.Task.Status);
        Assert.Equal(2, executor.Executed.Count);
        Assert.Equal(1, replanner.Calls);
    }

    private sealed class StaticPlanner(AdaptivePlan plan) : IAdaptiveAgentPlanner
    {
        public Task<AdaptivePlan> PlanAsync(AgentTask task, CancellationToken cancellationToken) => Task.FromResult(plan);
    }

    private sealed class RecordingExecutor : IAdaptiveActionExecutor
    {
        public List<string> Executed { get; } = [];
        public Task<AgentActionResult> ExecuteAsync(AdaptiveAction action, CancellationToken cancellationToken)
        {
            Executed.Add(action.Id);
            return Task.FromResult(new AgentActionResult(true, "ok:" + action.Id));
        }
    }

    private sealed class PassVerifier : IAdaptiveActionVerifier
    {
        public Task<AdaptiveVerificationResult> VerifyAsync(AdaptiveAction action, AgentActionResult result, CancellationToken cancellationToken) =>
            Task.FromResult(AdaptiveVerificationResult.Passed());
    }

    private sealed class FailFirstVerifier(string id) : IAdaptiveActionVerifier
    {
        private bool _failed;
        public Task<AdaptiveVerificationResult> VerifyAsync(AdaptiveAction action, AgentActionResult result, CancellationToken cancellationToken)
        {
            if (action.Id == id && !_failed) { _failed = true; return Task.FromResult(AdaptiveVerificationResult.Failed("synthetic verification")); }
            return Task.FromResult(AdaptiveVerificationResult.Passed());
        }
    }

    private sealed class AlwaysFailVerifier : IAdaptiveActionVerifier
    {
        public Task<AdaptiveVerificationResult> VerifyAsync(AdaptiveAction action, AgentActionResult result, CancellationToken cancellationToken) =>
            Task.FromResult(AdaptiveVerificationResult.Failed("still failing"));
    }

    private sealed class NoRepair : IAdaptiveAgentReplanner
    {
        public Task<AdaptiveAction?> RepairAsync(AgentTask task, AdaptivePlan plan, AdaptiveActionOutcome failed, int repairNumber, CancellationToken cancellationToken) =>
            Task.FromResult<AdaptiveAction?>(null);
    }

    private sealed class ReplaceFailedAction : IAdaptiveAgentReplanner
    {
        public int Calls;
        public Task<AdaptiveAction?> RepairAsync(AgentTask task, AdaptivePlan plan, AdaptiveActionOutcome failed, int repairNumber, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<AdaptiveAction?>(failed.Action with { ToolId = failed.Action.ToolId + ".repair" });
        }
    }
}
