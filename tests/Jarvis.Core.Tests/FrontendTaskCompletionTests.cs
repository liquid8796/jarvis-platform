using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class FrontendTaskCompletionTests
{
    [Fact]
    public async Task Autonomous_frontend_task_fails_closed_when_goal_verifier_omits_rendered_evidence()
    {
        var terminal = await RunAsync([]);

        Assert.Equal("NEEDS_VERIFICATION", terminal.Status);
        Assert.NotNull(terminal.Verification);
        Assert.True(terminal.Verification.Required);
        Assert.False(terminal.Verification.Passed);
        Assert.Contains(nameof(FrontendEvidenceKind.Screenshot), terminal.Verification.Missing);
    }

    [Fact]
    public async Task Autonomous_frontend_task_without_agentic_coordinator_fails_closed_when_browser_is_unavailable()
    {
        var terminal = await RunWithoutAgenticAsync();

        Assert.Equal("NEEDS_VERIFICATION", terminal.Status);
        Assert.NotNull(terminal.Verification);
        Assert.True(terminal.Verification.Required);
        Assert.False(terminal.Verification.Passed);
        Assert.Equal("not_run", terminal.Verification.State);
        Assert.Contains(nameof(FrontendEvidenceKind.RenderedDom), terminal.Verification.Missing);
    }

    [Fact]
    public async Task Autonomous_visual_frontend_task_rejects_coordinator_booleans_and_empty_ledger()
    {
        var evidence = FrontendVerificationTests.BaseEvidence().Concat(new[]
        {
            new FrontendEvidence(FrontendEvidenceKind.ResponsiveDesktop, true, "desktop 1440x900"),
            new FrontendEvidence(FrontendEvidenceKind.ResponsiveMobile, true, "mobile 390x844"),
            new FrontendEvidence(FrontendEvidenceKind.Overflow, true, "no clipping")
        }).ToArray();

        var fidelity = VisualFidelityLedger.Create([]);
        var terminal = await RunAsync(evidence, fidelity);

        Assert.Equal("NEEDS_VERIFICATION", terminal.Status);
        Assert.NotNull(terminal.Verification);
        Assert.True(terminal.Verification.Required);
        Assert.False(terminal.Verification.Passed);
        Assert.Contains(nameof(FrontendEvidenceKind.Screenshot), terminal.Verification.Missing);
    }

    private static async Task<RemoteTaskSnapshot> RunWithoutAgenticAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-frontend-no-agentic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            await using var host = new RemoteTaskHost(Path.Combine(root, "tasks"), new WorkspaceDirectories(workspace),
                new DynamicToolRegistry([new InspectTool()]), () => true,
                (_, _, _, _) => Task.FromResult(new ToolReply("UI_READY at http://localhost:4173/")),
                (_, _) => Task.CompletedTask);
            var id = Guid.NewGuid().ToString();
            var created = await host.HandleAsync("create", new("owner", id, new RemoteTaskPlan
            {
                Goal = "Polish the frontend UI at http://localhost:4173/",
                ExecutionMode = "AUTONOMOUS",
                TimeoutSeconds = 20,
                Steps = [new RemoteTaskStep { Id = "edit-ui", ToolId = "test.inspect", Stage = "EXECUTE", TimeoutSeconds = 10 }]
            }), CancellationToken.None);
            Assert.Null(created.Error);
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var reply = await host.HandleAsync("get", new("owner", id), CancellationToken.None);
                if (reply.Task is { } task && (RemoteTaskRules.IsTerminal(task.Status) || task.Status.StartsWith("NEEDS_", StringComparison.Ordinal))) return task;
                await Task.Delay(25);
            }
            throw new TimeoutException("Frontend task did not reach a terminal state.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<RemoteTaskSnapshot> RunAsync(IReadOnlyList<FrontendEvidence> evidence, VisualFidelityLedger? visualFidelity = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-frontend-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            await using var host = new RemoteTaskHost(Path.Combine(root, "tasks"), new WorkspaceDirectories(workspace),
                new DynamicToolRegistry([new InspectTool()]), () => true,
                (_, _, _, _) => Task.FromResult(new ToolReply("UI_OK")), (_, _) => Task.CompletedTask,
                new FrontendCoordinator(evidence, visualFidelity));
            var id = Guid.NewGuid().ToString();
            var created = await host.HandleAsync("create", new("owner", id, new RemoteTaskPlan
            {
                Goal = "Polish responsive dashboard UI layout on desktop and mobile",
                ExecutionMode = "AUTONOMOUS",
                TimeoutSeconds = 20
            }), CancellationToken.None);
            Assert.Null(created.Error);
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var reply = await host.HandleAsync("get", new("owner", id), CancellationToken.None);
                if (reply.Task is { } task && (RemoteTaskRules.IsTerminal(task.Status) || task.Status.StartsWith("NEEDS_", StringComparison.Ordinal))) return task;
                await Task.Delay(25);
            }
            throw new TimeoutException("Frontend task did not reach a terminal state.");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FrontendCoordinator(IReadOnlyList<FrontendEvidence> evidence, VisualFidelityLedger? visualFidelity) : IRemoteTaskAgenticCoordinator
    {
        public Task<RemoteTaskPlan> PlanAsync(RemoteTaskPlanningContext context, CancellationToken cancellationToken) =>
            Task.FromResult(context.Draft with
            {
                Project = context.Project,
                Steps = [new RemoteTaskStep { Id = "edit-ui", ToolId = "test.inspect", Stage = "EXECUTE", TimeoutSeconds = 10 }]
            });

        public Task<RemoteTaskStep?> RepairAsync(RemoteTaskPlan plan, RemoteTaskStep failedStep, RemoteTaskArtifact failure,
            int repairNumber, CancellationToken cancellationToken) => Task.FromResult<RemoteTaskStep?>(null);

        public Task<RemoteTaskGoalVerification> VerifyGoalAsync(RemoteTaskGoalContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteTaskGoalVerification(true, FrontendEvidence: evidence, VisualFidelity: visualFidelity));
    }

    private sealed class InspectTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.inspect", "test_inspect", "test", "Inspect project state",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("UI_OK"));
    }
}
