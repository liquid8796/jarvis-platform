namespace Jarvis.Agent.Core.Autonomous.Memory;

public sealed record ProjectMemoryEntry(
    string Project,
    string Decision,
    string Reason,
    DateTime CreatedAt);
