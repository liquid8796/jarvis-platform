using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

public interface IRemoteTaskAdaptiveCoordinator
{
    Task<RemoteTaskStep?> RepairAsync(RemoteTaskPlan plan, RemoteTaskStep failedStep, RemoteTaskArtifact failure,
        int repairNumber, CancellationToken cancellationToken);
}

public static class RemoteTaskAdaptiveRules
{
    public const int MaxRepairs = 2;
    private static readonly string[] Stages = ["EXECUTE", "BUILD", "TEST", "PACKAGE", "VERIFY"];

    public static bool CanRepair(string executionMode, int repairCount, bool cancelledOrTimedOut, bool coordinatorAvailable) =>
        executionMode == "AUTONOMOUS" && coordinatorAvailable && !cancelledOrTimedOut && repairCount < MaxRepairs;

    public static void ValidateReplacement(RemoteTaskStep failed, RemoteTaskStep replacement)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!StringComparer.Ordinal.Equals(failed.Id, replacement.Id))
            throw new ArgumentException("Adaptive repair must keep the same logical step ID.");
        var failedStage = Array.IndexOf(Stages, failed.Stage);
        var replacementStage = Array.IndexOf(Stages, replacement.Stage);
        if (failedStage < 0 || replacementStage < failedStage)
            throw new ArgumentException("Adaptive repair cannot regress execution stage.");
    }
}
