using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class RemoteTaskContractTests
{
    private static RemoteTaskStep Step(string id = "read", string stage = "EXECUTE") =>
        new() { Id = id, ToolId = "filesystem.Read", Stage = stage };
    private static RemoteTaskPlan Plan(params RemoteTaskStep[] steps) => new() { Goal = "Validate contract", Steps = steps };
    [Theory]
    [InlineData("duplicate")]
    [InlineData("stage-order")]
    [InlineData("unknown-stage")]
    [InlineData("null-steps")]
    [InlineData("null-step")]
    [InlineData("invalid-id")]
    [InlineData("too-many")]
    [InlineData("bad-mode")]
    [InlineData("empty-goal")]
    [InlineData("step-timeout")]
    [InlineData("task-timeout")]
    [InlineData("attempts")]
    [InlineData("arguments")]
    public void Invalid_plans_are_rejected_before_execution(string scenario)
    {
        var plan = scenario switch
        {
            "duplicate" => Plan(Step(), Step()),
            "stage-order" => Plan(Step("test", "TEST"), Step("build", "BUILD")),
            "unknown-stage" => Plan(Step(stage: "DEPLOY")),
            "null-steps" => Plan() with { Steps = null! },
            "null-step" => Plan((RemoteTaskStep)null!),
            "invalid-id" => Plan(Step("../bad")),
            "too-many" => Plan(Enumerable.Range(0, 33).Select(i => Step("s" + i)).ToArray()),
            "bad-mode" => Plan() with { ExecutionMode = "FULL_PERMISSION" },
            "empty-goal" => Plan() with { Goal = " " },
            "step-timeout" => Plan(Step() with { TimeoutSeconds = 1801 }),
            "task-timeout" => Plan() with { TimeoutSeconds = 3601 },
            "attempts" => Plan(Step() with { MaxAttempts = 4 }),
            _ => Plan(Step() with { Arguments = WireJson.Element("not-object") })
        };
        Assert.Throws<ArgumentException>(() => RemoteTaskRules.Validate(plan));
    }
    [Fact]
    public void Task_ids_are_canonical_and_not_paths()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString("N"), RemoteTaskRules.TaskId(id.ToString().ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => RemoteTaskRules.TaskId("../state"));
        Assert.Throws<ArgumentException>(() => RemoteTaskRules.TaskId(Guid.Empty.ToString()));
    }
    [Fact]
    public void Goal_only_and_ordered_engineering_plans_are_valid()
    {
        RemoteTaskRules.Validate(Plan());
        RemoteTaskRules.Validate(Plan(Step("edit"), Step("b", "BUILD"), Step("t", "TEST"), Step("p", "PACKAGE"), Step("v", "VERIFY")));
    }
    [Fact]
    public void Oversized_plan_is_rejected()
    {
        var plan = Plan(Step() with { Arguments = WireJson.Element(new { value = new string('x', RemoteTaskRules.MaxPlanBytes) }) });
        Assert.Throws<ArgumentException>(() => RemoteTaskRules.Validate(plan));
    }
    [Fact]
    public void Remote_full_permission_property_is_not_a_task_field()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RemoteTaskPlan>("{\"goal\":\"x\",\"fullPermission\":true}", WireJson.Options));
    }
}
