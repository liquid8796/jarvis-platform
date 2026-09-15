namespace Jarvis.Agent.Core.Autonomous.Runtime;

public sealed record AutonomousTaskResult(
    bool Success,
    string TaskId,
    IReadOnlyCollection<string> Artifacts,
    string? Error = null);
