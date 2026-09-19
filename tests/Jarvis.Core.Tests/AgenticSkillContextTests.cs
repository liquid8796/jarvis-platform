using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Plugins;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class AgenticSkillContextTests
{
    [Fact]
    public async Task Planning_context_projects_skill_metadata_and_loads_full_instructions_only_on_selection()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-agentic-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var descriptor = new PluginSkillDescriptor("demo", "frontend-testing", "Rendered frontend QA", "C:/virtual/SKILL.md", 80);
        var loaded = new PluginSkillDocument(descriptor, "SECRET FULL SKILL: inspect rendered UI.");
        var coordinator = new SkillCoordinator();
        try
        {
            await using var host = new RemoteTaskHost(Path.Combine(root, "tasks"), new WorkspaceDirectories(workspace),
                new DynamicToolRegistry([new InspectTool()]), () => true,
                (_, _, _, _) => Task.FromResult(new ToolReply("OK")), (_, _) => Task.CompletedTask, coordinator);
            host.ConfigureSkills([descriptor], ids => ids.Contains(descriptor.Id, StringComparer.OrdinalIgnoreCase) ? [loaded] : []);

            var id = Guid.NewGuid().ToString();
            var create = await host.HandleAsync("create", new("owner", id, new RemoteTaskPlan
            {
                Goal = "Inspect backend state",
                ExecutionMode = "AUTONOMOUS",
                CodingVerification = new() { Mode = "not_required", Reason = "No source mutation; this fixture exercises only the coordinator skill-context contract." },
                TimeoutSeconds = 20
            }), CancellationToken.None);
            Assert.Null(create.Error);

            for (var attempt = 0; attempt < 80; attempt++)
            {
                var reply = await host.HandleAsync("get", new("owner", id), CancellationToken.None);
                if (reply.Task is { } task && RemoteTaskRules.IsTerminal(task.Status)) break;
                await Task.Delay(25);
            }

            Assert.NotNull(coordinator.Context);
            Assert.Contains(coordinator.Context.AvailableSkills, skill => skill.Id == descriptor.Id);
            var skillLayer = Assert.Single(coordinator.Context.PromptLayers.Where(layer => layer.Name == "skills"));
            Assert.Contains("demo/frontend-testing", skillLayer.Content, StringComparison.Ordinal);
            Assert.Contains("Rendered frontend QA", skillLayer.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET FULL SKILL", skillLayer.Content, StringComparison.Ordinal);
            var document = Assert.Single(coordinator.Context.LoadSelectedSkills([descriptor.Id]));
            Assert.Contains("SECRET FULL SKILL", document.Instructions, StringComparison.Ordinal);
            Assert.NotNull(coordinator.GoalContext);
            Assert.Contains(coordinator.GoalContext.AvailableSkills, skill => skill.Id == descriptor.Id);
            Assert.Single(coordinator.GoalContext.LoadSelectedSkills([descriptor.Id]));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class SkillCoordinator : IRemoteTaskAgenticCoordinator
    {
        public RemoteTaskPlanningContext? Context { get; private set; }
        public RemoteTaskGoalContext? GoalContext { get; private set; }

        public Task<RemoteTaskPlan> PlanAsync(RemoteTaskPlanningContext context, CancellationToken cancellationToken)
        {
            Context = context;
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
            GoalContext = context;
            return Task.FromResult(RemoteTaskGoalVerification.Passed());
        }
    }

    private sealed class InspectTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.inspect", "test_inspect", "test", "Inspect",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("OK"));
    }
}
