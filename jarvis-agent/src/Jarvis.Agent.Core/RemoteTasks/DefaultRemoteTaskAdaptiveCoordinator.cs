using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

/// <summary>
/// Conservative production repair policy. It may retry the same logical step only when the
/// currently installed descriptor is read-only and non-sensitive; it never invents a tool,
/// changes arguments, broadens permissions, or repeats an ambiguous mutating action.
/// </summary>
public sealed class DefaultRemoteTaskAdaptiveCoordinator(DynamicToolRegistry registry) : IRemoteTaskAdaptiveCoordinator
{
    public Task<RemoteTaskStep?> RepairAsync(RemoteTaskPlan plan, RemoteTaskStep failedStep, RemoteTaskArtifact failure,
        int repairNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (repairNumber is < 1 or > RemoteTaskAdaptiveRules.MaxRepairs)
            return Task.FromResult<RemoteTaskStep?>(null);
        var snapshot = registry.Snapshot;
        if (!snapshot.Tools.TryGetValue(failedStep.ToolId, out var tool) ||
            !tool.Descriptor.ReadOnly || tool.Descriptor.Sensitive || !failure.Success &&
            failure.Error?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true)
            return Task.FromResult<RemoteTaskStep?>(null);

        return Task.FromResult<RemoteTaskStep?>(failedStep with { MaxAttempts = 1 });
    }
}
