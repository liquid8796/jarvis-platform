using Jarvis.Agent.Core.Prompting;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

/// <summary>
/// Optional vendor-neutral coordinator for goal-owned autonomous tasks. Implementations may
/// call a local or remote model, but Core never selects a provider or grants permissions.
/// Every returned plan/repair is revalidated by <see cref="RemoteTaskHost"/> before execution.
/// </summary>
public interface IRemoteTaskAgenticCoordinator : IRemoteTaskAdaptiveCoordinator
{
    Task<RemoteTaskPlan> PlanAsync(RemoteTaskPlanningContext context, CancellationToken cancellationToken);
    Task<RemoteTaskGoalVerification> VerifyGoalAsync(RemoteTaskGoalContext context, CancellationToken cancellationToken);
}

public sealed record RemoteTaskPlanningContext(
    RemoteTaskPlan Draft,
    string Project,
    IReadOnlyList<ToolDescriptor> Tools)
{
    public IReadOnlyList<CodingPromptLayer> PromptLayers { get; init; } = [];
}

public sealed record RemoteTaskGoalContext(
    RemoteTaskPlan Plan,
    string Project,
    IReadOnlyList<RemoteTaskArtifact> Artifacts)
{
    public IReadOnlyList<CodingPromptLayer> PromptLayers { get; init; } = [];
}

public sealed record RemoteTaskGoalVerification(
    bool Success,
    string? Error = null,
    IReadOnlyList<RemoteTaskStep>? RepairSteps = null)
{
    public static RemoteTaskGoalVerification Passed() => new(true);
    public static RemoteTaskGoalVerification Failed(string error, IReadOnlyList<RemoteTaskStep>? repairSteps = null) =>
        new(false, error, repairSteps);
}
