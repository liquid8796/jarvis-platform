namespace Jarvis.Agent.Core.Autonomous.Memory;

public enum MemoryScope
{
    Session,
    Project,
    Architecture,
    Execution
}

public sealed record MemoryRecord(
    MemoryScope Scope,
    string Key,
    string Value,
    DateTime CreatedAt);
