namespace Jarvis.Agent.Core.Autonomous.Memory;

public sealed class SqliteMemoryStore : IAgentMemory
{
    private readonly Dictionary<string, MemoryEntry> _entries = new();

    public void Save(string key, string value)
    {
        _entries[key] = new MemoryEntry(
            "default",
            key,
            value,
            DateTime.UtcNow);
    }

    public string? Get(string key)
    {
        return _entries.TryGetValue(key, out var entry)
            ? entry.Value
            : null;
    }
}
