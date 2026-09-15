using Jarvis.Agent.Core.Autonomous;
using Jarvis.Agent.Core.Autonomous.Recovery;

namespace Jarvis.Core.Tests;

public sealed class AutonomousAgentHarnessTests
{
    [Fact]
    public async Task Execution_loop_completes_when_plan_and_verification_succeed()
    {
        var loop = new AgentExecutionLoop(
            new TestPlanner(),
            new TestExecutor(),
            new TestVerifier());

        var result = await loop.ExecuteAsync(new AgentTask("task-1", "test"));

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Artifacts);
    }

    [Fact]
    public void Recovery_policy_limits_retry()
    {
        var policy = new RecoveryPolicy { MaxRetries = 2 };

        Assert.True(policy.CanRetry(0));
        Assert.False(policy.CanRetry(2));
    }

    private sealed class TestPlanner : IAgentPlanner
    {
        public IReadOnlyList<AgentAction> Plan(AgentTask task) =>
            [new AgentAction("test.tool", "payload")];
    }

    private sealed class TestExecutor : IAgentExecutor
    {
        public Task<AgentActionResult> ExecuteAsync(AgentAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentActionResult(true, "ok"));
    }

    private sealed class TestVerifier : IAgentVerifier
    {
        public bool Verify(AgentTask task) => true;
    }
}
