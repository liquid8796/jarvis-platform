using System.Text.Json;
using Jarvis.Agent.Core;
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

    [Fact]
    public async Task Loop_parallelizes_only_registry_read_only_ready_actions()
    {
        var registry = new DynamicToolRegistry([
            new DescriptorTool("test.readA", readOnly: true),
            new DescriptorTool("test.readB", readOnly: true),
            new DescriptorTool("test.mutate", readOnly: false)
        ]);
        var executor = new ConcurrencyExecutor();
        var loop = new AdaptiveAgentExecutionLoop(
            new StaticPlanner(new AdaptivePlan([
                new AdaptiveAction("readA", "test.readA", WireJson.Element(new { }), []),
                new AdaptiveAction("readB", "test.readB", WireJson.Element(new { }), []),
                new AdaptiveAction("mutate", "test.mutate", WireJson.Element(new { }), [])
            ])), executor, new PassVerifier(), new NoRepair(),
            new ToolRegistryAdaptiveConcurrencyPolicy(registry), maxParallelReadOnly: 2);

        var result = await loop.ExecuteAsync(new AgentTask("parallel", "bounded reads"));

        Assert.Equal(AgentTaskStatus.Completed, result.Task.Status);
        Assert.Equal(2, executor.MaxActive);
        Assert.False(executor.MutationOverlapped);
        Assert.Contains("mutate", executor.Executed);
    }

    [Fact]
    public async Task Loop_records_every_launched_parallel_first_attempt_before_failing_frontier()
    {
        var registry = new DynamicToolRegistry([
            new DescriptorTool("test.a", readOnly: true),
            new DescriptorTool("test.b", readOnly: true)
        ]);
        var executor = new SelectiveFailureExecutor("a");
        var loop = new AdaptiveAgentExecutionLoop(
            new StaticPlanner(new AdaptivePlan([Action("a"), Action("b")], MaxRepairs: 0)),
            executor, new PassVerifier(), new NoRepair(), new ToolRegistryAdaptiveConcurrencyPolicy(registry), 2);

        var result = await loop.ExecuteAsync(new AgentTask("parallel-audit", "record launched work"));

        Assert.Equal(AgentTaskStatus.Failed, result.Task.Status);
        Assert.Equal(2, executor.Executed.Count);
        Assert.Equal(2, result.Outcomes.Count);
        Assert.Contains(result.Outcomes, outcome => outcome.Action.Id == "b" && outcome.Success);
        Assert.Equal(2, result.Task.Artifacts.Count);
    }

    [Fact]
    public async Task Loop_classifies_each_ready_action_once_per_frontier()
    {
        var policy = new SingleDecisionPolicy();
        var executor = new RecordingExecutor();
        var loop = new AdaptiveAgentExecutionLoop(
            new StaticPlanner(new AdaptivePlan([Action("a"), Action("b")])),
            executor, new PassVerifier(), new NoRepair(), policy, maxParallelReadOnly: 2);

        var result = await loop.ExecuteAsync(new AgentTask("single-decision", "catalog classification"));

        Assert.Equal(AgentTaskStatus.Completed, result.Task.Status);
        Assert.Equal(2, executor.Executed.Count);
        Assert.Equal(1, executor.Executed.Count(id => id == "a"));
        Assert.Equal(1, executor.Executed.Count(id => id == "b"));
        Assert.Equal(1, policy.Calls["a"]);
        Assert.Equal(1, policy.Calls["b"]);
    }

    [Fact]
    public async Task Loop_caps_parallel_read_only_batch_size()
    {
        var tools = Enumerable.Range(0, 6).Select(i => new DescriptorTool("test.r" + i, readOnly: true)).ToArray();
        var registry = new DynamicToolRegistry(tools);
        var executor = new ConcurrencyExecutor();
        var actions = Enumerable.Range(0, 6)
            .Select(i => new AdaptiveAction("r" + i, "test.r" + i, WireJson.Element(new { }), []))
            .ToArray();
        var loop = new AdaptiveAgentExecutionLoop(new StaticPlanner(new AdaptivePlan(actions)), executor,
            new PassVerifier(), new NoRepair(), new ToolRegistryAdaptiveConcurrencyPolicy(registry), maxParallelReadOnly: 2);

        var result = await loop.ExecuteAsync(new AgentTask("parallel-cap", "bounded concurrency"));

        Assert.Equal(AgentTaskStatus.Completed, result.Task.Status);
        Assert.Equal(2, executor.MaxActive);
        Assert.Equal(6, executor.Executed.Count);
    }

    private sealed class SingleDecisionPolicy : IAdaptiveConcurrencyPolicy
    {
        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
        public bool CanRunConcurrently(AdaptiveAction action)
        {
            Calls[action.Id] = Calls.GetValueOrDefault(action.Id) + 1;
            // A second lookup would deliberately flip the answer and expose double-classification.
            return Calls[action.Id] == 1;
        }
    }

    private sealed class DescriptorTool(string id, bool readOnly) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(id, id.Replace('.', '_'), "test", "concurrency test",
            WireJson.Element(new { type = "object", additionalProperties = false }), readOnly, Sensitive: !readOnly);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("ok"));
    }

    private sealed class SelectiveFailureExecutor(string failingId) : IAdaptiveActionExecutor
    {
        public List<string> Executed { get; } = [];
        private readonly object _sync = new();
        public async Task<AgentActionResult> ExecuteAsync(AdaptiveAction action, CancellationToken cancellationToken)
        {
            lock (_sync) Executed.Add(action.Id);
            await Task.Delay(50, cancellationToken);
            return action.Id == failingId
                ? new AgentActionResult(false, "synthetic failure")
                : new AgentActionResult(true, "ok:" + action.Id);
        }
    }

    private sealed class ConcurrencyExecutor : IAdaptiveActionExecutor
    {
        private int _active;
        private int _mutationActive;
        public int MaxActive;
        public bool MutationOverlapped;
        public List<string> Executed { get; } = [];
        private readonly object _sync = new();

        public async Task<AgentActionResult> ExecuteAsync(AdaptiveAction action, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int current;
            do { current = MaxActive; if (active <= current) break; }
            while (Interlocked.CompareExchange(ref MaxActive, active, current) != current);
            var mutation = action.Id == "mutate";
            if (mutation)
            {
                if (active > 1) MutationOverlapped = true;
                Interlocked.Exchange(ref _mutationActive, 1);
            }
            else if (Volatile.Read(ref _mutationActive) != 0) MutationOverlapped = true;
            lock (_sync) Executed.Add(action.Id);
            try { await Task.Delay(100, cancellationToken); return new AgentActionResult(true, "ok:" + action.Id); }
            finally
            {
                if (mutation) Interlocked.Exchange(ref _mutationActive, 0);
                Interlocked.Decrement(ref _active);
            }
        }
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
