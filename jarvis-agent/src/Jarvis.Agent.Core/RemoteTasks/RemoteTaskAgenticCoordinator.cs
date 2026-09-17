using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.Plugins;
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
    private Func<IReadOnlyCollection<string>, IReadOnlyList<PluginSkillDocument>>? SkillLoader { get; init; }
    public IReadOnlyList<CodingPromptLayer> PromptLayers { get; init; } = [];
    public IReadOnlyList<PluginSkillDescriptor> AvailableSkills { get; init; } = [];
    public IReadOnlyList<PluginSkillDocument> LoadSelectedSkills(IReadOnlyCollection<string> ids) =>
        SkillLoader?.Invoke(ids) ?? [];
    internal RemoteTaskPlanningContext WithSkillLoader(Func<IReadOnlyCollection<string>, IReadOnlyList<PluginSkillDocument>>? loader) =>
        this with { SkillLoader = loader };
}

public sealed record RemoteTaskGoalContext(
    RemoteTaskPlan Plan,
    string Project,
    IReadOnlyList<RemoteTaskArtifact> Artifacts)
{
    private Func<IReadOnlyCollection<string>, IReadOnlyList<PluginSkillDocument>>? SkillLoader { get; init; }
    public IReadOnlyList<CodingPromptLayer> PromptLayers { get; init; } = [];
    public IReadOnlyList<PluginSkillDescriptor> AvailableSkills { get; init; } = [];
    public IReadOnlyList<PluginSkillDocument> LoadSelectedSkills(IReadOnlyCollection<string> ids) =>
        SkillLoader?.Invoke(ids) ?? [];
    internal RemoteTaskGoalContext WithSkillLoader(Func<IReadOnlyCollection<string>, IReadOnlyList<PluginSkillDocument>>? loader) =>
        this with { SkillLoader = loader };
    public FrontendVerificationRequirement FrontendRequirement { get; init; } = FrontendVerificationRequirement.None;
    public IReadOnlyList<FrontendEvidence> FrontendEvidence { get; init; } = [];
}

public sealed record RemoteTaskGoalVerification(
    bool Success,
    string? Error = null,
    IReadOnlyList<RemoteTaskStep>? RepairSteps = null,
    IReadOnlyList<FrontendEvidence>? FrontendEvidence = null)
{
    public static RemoteTaskGoalVerification Passed(IReadOnlyList<FrontendEvidence>? frontendEvidence = null) =>
        new(true, FrontendEvidence: frontendEvidence);
    public static RemoteTaskGoalVerification Failed(string error, IReadOnlyList<RemoteTaskStep>? repairSteps = null,
        IReadOnlyList<FrontendEvidence>? frontendEvidence = null) =>
        new(false, error, repairSteps, frontendEvidence);
}
