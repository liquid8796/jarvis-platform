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
/// Tool authorization remains the responsibility of the injected executor.
/// </summary>
public sealed class AdaptiveAgentExecutionLoop(
    IAdaptiveAgentPlanner planner,
    IAdaptiveActionExecutor executor,
    IAdaptiveActionVerifier verifier,
    IAdaptiveAgentReplanner replanner)
{
    public async Task<AdaptiveExecutionResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Status = AgentTaskStatus.Running;
        var plan = (await planner.PlanAsync(task, cancellationToken).ConfigureAwait(false)).Validate();
        var map = plan.Actions.ToDictionary(action => action.Id, StringComparer.Ordinal);
        var remaining = map.Keys.ToHashSet(StringComparer.Ordinal);
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

            foreach (var original in ready)
            {
                var candidate = original;
                var repairNumber = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

                    var outcome = new AdaptiveActionOutcome(candidate, execution, verification, repairNumber);
                    outcomes.Add(outcome);
                    task.Artifacts.Add(new AgentArtifact("action", candidate.Id, execution.Output));
                    if (outcome.Success)
                    {
                        completed.Add(original.Id);
                        remaining.Remove(original.Id);
                        break;
                    }

                    if (repairNumber >= plan.MaxRepairs)
                    {
                        task.Status = AgentTaskStatus.Failed;
                        return new(task, outcomes);
                    }

                    var replacement = await replanner.RepairAsync(task, plan, outcome, repairNumber + 1, cancellationToken).ConfigureAwait(false);
                    if (replacement is null)
                    {
                        task.Status = AgentTaskStatus.Failed;
                        return new(task, outcomes);
                    }
                    ValidateReplacement(original, replacement, completed);
                    candidate = replacement;
                    repairNumber++;
                }
            }
        }

        task.Status = AgentTaskStatus.Completed;
        return new(task, outcomes);
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
}
