namespace Jarvis.Agent.Core.Autonomous.Memory;

public sealed record MemoryEntry(
    string Scope,
    string Key,
    string Value,
    DateTime CreatedAt);
