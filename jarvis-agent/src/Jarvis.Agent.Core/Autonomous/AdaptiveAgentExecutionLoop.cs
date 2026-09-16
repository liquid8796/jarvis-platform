using Jarvis.Agent.Core.Autonomous.Planning;

namespace Jarvis.Agent.Core.Autonomous;

public interface IAdaptiveAgentPlanner
{
    Task<AdaptivePlan> PlanAsync(AgentTask task, CancellationToken cancellationToken);
}

public interface IAdaptiveActionExecutor
{
    Task<AgentActionResult> ExecuteAsync(AdaptiveAction action, CancellationToken cancellationToken);
}

public interface IAdaptiveActionVerifier
{
    Task<AdaptiveVerificationResult> VerifyAsync(AdaptiveAction action, AgentActionResult result, CancellationToken cancellationToken);
}

public interface IAdaptiveAgentReplanner
{
    Task<AdaptiveAction?> RepairAsync(AgentTask task, AdaptivePlan plan, AdaptiveActionOutcome failed,
        int repairNumber, CancellationToken cancellationToken);
}

public interface IAdaptiveConcurrencyPolicy
{
    bool CanRunConcurrently(AdaptiveAction action);
}

/// <summary>
/// Grants parallel execution only to currently installed read-only, non-sensitive tools.
/// Dynamic catalog replacement therefore changes the decision at the next ready frontier.
/// </summary>
public sealed class ToolRegistryAdaptiveConcurrencyPolicy(DynamicToolRegistry registry) : IAdaptiveConcurrencyPolicy
{
    public bool CanRunConcurrently(AdaptiveAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var snapshot = registry?.Snapshot ?? throw new ArgumentNullException(nameof(registry));
        return snapshot.Tools.TryGetValue(action.ToolId, out var tool) &&
            tool.Descriptor.ReadOnly && !tool.Descriptor.Sensitive;
    }
}

public sealed record AdaptiveVerificationResult(bool Success, string? Error = null)
{
    public static AdaptiveVerificationResult Passed() => new(true);
    public static AdaptiveVerificationResult Failed(string error) => new(false, error);
}

public sealed record AdaptiveActionOutcome(
    AdaptiveAction Action,
    AgentActionResult Execution,
    AdaptiveVerificationResult Verification,
    int RepairNumber)
{
    public bool Success => Execution.Success && Verification.Success;
}

public sealed record AdaptiveExecutionResult(AgentTask Task, IReadOnlyList<AdaptiveActionOutcome> Outcomes);

/// <summary>
/// Bounded plan/execute/verify/repair loop. Only the failed logical action can be
/// replaced; actions already verified as complete are never replayed by this loop.
/// Independent ready actions may run in parallel only when the injected policy marks
/// their current tool read-only/non-sensitive. Mutating/sensitive actions and all repairs
/// are serialized. Tool authorization remains the responsibility of the injected executor.
/// </summary>
public sealed class AdaptiveAgentExecutionLoop(
    IAdaptiveAgentPlanner planner,
    IAdaptiveActionExecutor executor,
    IAdaptiveActionVerifier verifier,
    IAdaptiveAgentReplanner replanner,
    IAdaptiveConcurrencyPolicy? concurrencyPolicy = null,
    int maxParallelReadOnly = 1)
{
    private readonly IAdaptiveConcurrencyPolicy _concurrencyPolicy = concurrencyPolicy ?? DenyParallelPolicy.Instance;
    private readonly int _maxParallelReadOnly = maxParallelReadOnly is >= 1 and <= 8
        ? maxParallelReadOnly
        : throw new ArgumentOutOfRangeException(nameof(maxParallelReadOnly), "Parallel read-only concurrency must be 1..8.");

    public async Task<AdaptiveExecutionResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Status = AgentTaskStatus.Running;
        var plan = (await planner.PlanAsync(task, cancellationToken).ConfigureAwait(false)).Validate();
        var remaining = plan.Actions.Select(action => action.Id).ToHashSet(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var outcomes = new List<AdaptiveActionOutcome>();

        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = plan.Actions.Where(action => remaining.Contains(action.Id) &&
                (action.DependsOn ?? []).All(completed.Contains)).ToArray();
            if (ready.Length == 0)
            {
                task.Status = AgentTaskStatus.Failed;
                return new(task, outcomes);
            }

            // Decide once per frontier/action. A hot catalog replacement may affect the next
            // frontier, but cannot move one ready action between parallel/serial buckets mid-pass.
            var decisions = ready.Select(action => (Action: action, Parallel: _concurrencyPolicy.CanRunConcurrently(action))).ToArray();
            var concurrent = decisions.Where(decision => decision.Parallel).Select(decision => decision.Action).ToArray();
            var serial = decisions.Where(decision => !decision.Parallel).Select(decision => decision.Action).ToArray();

            if (concurrent.Length > 0)
            {
                var firstAttempts = await ExecuteConcurrentFirstAttemptsAsync(concurrent, cancellationToken).ConfigureAwait(false);

                // Task.WhenAll means every first attempt in this read-only frontier has already run.
                // Journal all of them before any repair/failure can return from the loop, otherwise a
                // sibling success could be executed but absent from audit/no-replay state.
                foreach (var initial in firstAttempts) Record(task, outcomes, initial);
                for (var index = 0; index < concurrent.Length; index++)
                {
                    if (!firstAttempts[index].Success) continue;
                    completed.Add(concurrent[index].Id);
                    remaining.Remove(concurrent[index].Id);
                }
                for (var index = 0; index < concurrent.Length; index++)
                {
                    var initial = firstAttempts[index];
                    if (initial.Success) continue;
                    if (!await FinishLogicalActionAsync(concurrent[index], initial, task, plan, completed, remaining, outcomes, cancellationToken).ConfigureAwait(false))
                    {
                        task.Status = AgentTaskStatus.Failed;
                        return new(task, outcomes);
                    }
                }
            }

            foreach (var original in serial)
            {
                if (!await FinishLogicalActionAsync(original, null, task, plan, completed, remaining, outcomes, cancellationToken).ConfigureAwait(false))
                {
                    task.Status = AgentTaskStatus.Failed;
                    return new(task, outcomes);
                }
            }
        }

        task.Status = AgentTaskStatus.Completed;
        return new(task, outcomes);
    }

    private async Task<AdaptiveActionOutcome[]> ExecuteConcurrentFirstAttemptsAsync(
        IReadOnlyList<AdaptiveAction> actions, CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(_maxParallelReadOnly, _maxParallelReadOnly);
        var tasks = actions.Select(async action =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await ExecuteAttemptAsync(action, 0, cancellationToken).ConfigureAwait(false); }
            finally { slots.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<bool> FinishLogicalActionAsync(
        AdaptiveAction original,
        AdaptiveActionOutcome? initial,
        AgentTask task,
        AdaptivePlan plan,
        HashSet<string> completed,
        HashSet<string> remaining,
        List<AdaptiveActionOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var candidate = initial?.Action ?? original;
        var repairNumber = initial?.RepairNumber ?? 0;
        var outcome = initial;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (outcome is null)
            {
                outcome = await ExecuteAttemptAsync(candidate, repairNumber, cancellationToken).ConfigureAwait(false);
                Record(task, outcomes, outcome);
            }

            if (outcome.Success)
            {
                completed.Add(original.Id);
                remaining.Remove(original.Id);
                return true;
            }

            if (repairNumber >= plan.MaxRepairs) return false;

            var replacement = await replanner.RepairAsync(task, plan, outcome, repairNumber + 1, cancellationToken).ConfigureAwait(false);
            if (replacement is null) return false;
            ValidateReplacement(original, replacement, completed);
            candidate = replacement;
            repairNumber++;
            outcome = await ExecuteAttemptAsync(candidate, repairNumber, cancellationToken).ConfigureAwait(false);
            Record(task, outcomes, outcome);
        }
    }

    private async Task<AdaptiveActionOutcome> ExecuteAttemptAsync(
        AdaptiveAction candidate, int repairNumber, CancellationToken cancellationToken)
    {
        AgentActionResult execution;
        try
        {
            execution = await executor.ExecuteAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            execution = new AgentActionResult(false, "Execution failed: " + ex.GetType().Name);
        }

        AdaptiveVerificationResult verification;
        if (!execution.Success)
            verification = AdaptiveVerificationResult.Failed(execution.Output);
        else
        {
            try { verification = await verifier.VerifyAsync(candidate, execution, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { verification = AdaptiveVerificationResult.Failed("Verification failed: " + ex.GetType().Name); }
        }
        return new AdaptiveActionOutcome(candidate, execution, verification, repairNumber);
    }

    private static void Record(AgentTask task, List<AdaptiveActionOutcome> outcomes, AdaptiveActionOutcome outcome)
    {
        outcomes.Add(outcome);
        task.Artifacts.Add(new AgentArtifact("action", outcome.Action.Id, outcome.Execution.Output));
    }

    private static void ValidateReplacement(AdaptiveAction original, AdaptiveAction replacement, HashSet<string> completed)
    {
        if (!StringComparer.Ordinal.Equals(original.Id, replacement.Id))
            throw new InvalidOperationException("A repair must replace the same logical action ID.");
        if (string.IsNullOrWhiteSpace(replacement.ToolId) || replacement.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidOperationException("Repair action tool or arguments are invalid.");
        if ((replacement.DependsOn ?? []).Any(dependency => !completed.Contains(dependency)))
            throw new InvalidOperationException("A repair cannot introduce unmet dependencies or replay completed work.");
    }

    private sealed class DenyParallelPolicy : IAdaptiveConcurrencyPolicy
    {
        public static readonly DenyParallelPolicy Instance = new();
        public bool CanRunConcurrently(AdaptiveAction action) => false;
    }
}