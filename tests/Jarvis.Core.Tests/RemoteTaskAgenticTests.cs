using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class RemoteTaskAgenticTests
{
    [Fact]
    public void Core_exposes_a_vendor_neutral_agentic_task_coordinator_contract()
    {
        var type = typeof(RemoteTaskHost).Assembly.GetType("Jarvis.Agent.Core.RemoteTasks.IRemoteTaskAgenticCoordinator");
        Assert.NotNull(type);
    }

    [Fact]
    public async Task Goal_only_autonomous_task_is_planned_executed_and_goal_verified_when_agentic_coordinator_is_configured()
    {
        var coordinator = new AgenticCoordinator();
        var (terminal, _) = await RunAutonomousAsync(coordinator);

        Assert.Equal("COMPLETED", terminal.Status);
        Assert.Equal(1, coordinator.PlanCalls);
        Assert.Equal(1, coordinator.VerifyCalls);
        Assert.Equal(1, terminal.CompletedSteps);
        Assert.Contains(coordinator.LastPlanningContext!.PromptLayers, layer => layer.Name == "coding-base");
        Assert.Contains(coordinator.LastGoalContext!.PromptLayers, layer => layer.Name == "outcomes");
    }

    [Fact]
    public async Task Successful_steps_do_not_complete_task_when_goal_verification_fails()
    {
        var coordinator = new AgenticCoordinator(RemoteTaskGoalVerification.Failed("goal incomplete"));
        var (terminal, _) = await RunAutonomousAsync(coordinator);

        Assert.Equal("FAILED", terminal.Status);
        Assert.Contains("goal incomplete", terminal.Error, StringComparison.Ordinal);
        Assert.Equal(1, terminal.CompletedSteps);
        Assert.Equal(1, coordinator.VerifyCalls);
    }

    [Fact]
    public async Task Goal_verification_can_request_bounded_repair_steps_before_completion()
    {
        var coordinator = new AgenticCoordinator(
            RemoteTaskGoalVerification.Failed("render proof missing",
                [new RemoteTaskStep { Id = "prove", ToolId = "test.inspect", Stage = "VERIFY", TimeoutSeconds = 10 }]),
            RemoteTaskGoalVerification.Passed());
        var (terminal, artifacts) = await RunAutonomousAsync(coordinator);

        Assert.Equal("COMPLETED", terminal.Status);
        Assert.Equal(2, terminal.CompletedSteps);
        Assert.Equal(2, coordinator.VerifyCalls);
        Assert.Contains(artifacts, artifact => artifact.StepId == "prove" && artifact.Success);
    }

    private static async Task<(RemoteTaskSnapshot Snapshot, IReadOnlyList<RemoteTaskArtifact> Artifacts)> RunAutonomousAsync(
        AgenticCoordinator coordinator)
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-agentic-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            await using var host = new RemoteTaskHost(Path.Combine(root, "tasks"), new WorkspaceDirectories(workspace),
                new DynamicToolRegistry([new InspectTool()]), () => true,
                (_, _, _, _) => Task.FromResult(new ToolReply("INSPECT_OK")), (_, _) => Task.CompletedTask, coordinator);
            var id = Guid.NewGuid().ToString();
            var created = await host.HandleAsync("create", new("owner", id, new RemoteTaskPlan
            {
                Goal = "Inspect the project and prove completion",
                ExecutionMode = "AUTONOMOUS",
                TimeoutSeconds = 20
            }), CancellationToken.None);

            Assert.Null(created.Error);
            var terminal = await TerminalAsync(host, id);
            var artifactReply = await host.HandleAsync("artifacts", new("owner", id, Offset: 0, Limit: 20), CancellationToken.None);
            return (terminal, artifactReply.Artifacts ?? []);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<RemoteTaskSnapshot> TerminalAsync(RemoteTaskHost host, string id)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var reply = await host.HandleAsync("get", new("owner", id), CancellationToken.None);
            if (reply.Task is { } task && RemoteTaskRules.IsTerminal(task.Status)) return task;
            await Task.Delay(25);
        }
        throw new TimeoutException("Task did not reach a terminal state.");
    }

    private sealed class AgenticCoordinator(params RemoteTaskGoalVerification[] verifications) : IRemoteTaskAgenticCoordinator
    {
        private readonly Queue<RemoteTaskGoalVerification> _verifications = new(verifications);
        public int PlanCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public RemoteTaskPlanningContext? LastPlanningContext { get; private set; }
        public RemoteTaskGoalContext? LastGoalContext { get; private set; }

        public Task<RemoteTaskPlan> PlanAsync(RemoteTaskPlanningContext context, CancellationToken cancellationToken)
        {
            PlanCalls++;
            LastPlanningContext = context;
            return Task.FromResult(context.Draft with
            {
                Project = context.Project,
                Steps = [new RemoteTaskStep { Id = "inspect", ToolId = "test.inspect", Stage = "EXECUTE", TimeoutSeconds = 10 }]
            });
        }

        public Task<RemoteTaskStep?> RepairAsync(RemoteTaskPlan plan, RemoteTaskStep failedStep, RemoteTaskArtifact failure,
            int repairNumber, CancellationToken cancellationToken) => Task.FromResult<RemoteTaskStep?>(null);

        public Task<RemoteTaskGoalVerification> VerifyGoalAsync(RemoteTaskGoalContext context, CancellationToken cancellationToken)
        {
            VerifyCalls++;
            LastGoalContext = context;
            return Task.FromResult(_verifications.Count > 0 ? _verifications.Dequeue() : RemoteTaskGoalVerification.Passed());
        }
    }

    private sealed class InspectTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.inspect", "test_inspect", "test", "Inspect project state",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("INSPECT_OK"));
    }
}
