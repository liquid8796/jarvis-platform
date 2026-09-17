using System.Text.Json.Serialization;

namespace Jarvis.Protocol;

/// <summary>Bounded coordination metadata; never contains session handles, tool arguments or transcripts.</summary>
public sealed record AgentSessionActivity
{
    public int RunningCalls { get; init; }
    public int QueuedCalls { get; init; }
    public int RunningJobs { get; init; }
    public int RunningTasks { get; init; }
    public int QueuedTasks { get; init; }
    public IReadOnlyList<string> HeldResources { get; init; } = [];
    public IReadOnlyList<string> WaitingResources { get; init; } = [];
    public string State(DateTimeOffset? closedAt) => closedAt is not null
        ? (RunningCalls + RunningJobs + RunningTasks > 0 ? "stopping" : "closed")
        : RunningCalls + RunningJobs + RunningTasks > 0 ? "running"
        : QueuedCalls + QueuedTasks > 0 ? "waiting" : "idle";
}

/// <summary>Local operator projection. Remote tools use an explicitly scoped metadata response instead.</summary>
public sealed record LocalSessionOverview(
    [property: JsonIgnore] AgentSessionIdentity Identity,
    AgentSessionSnapshot Session,
    AgentSessionActivity Activity);
