namespace Jarvis.Agent.Core.Autonomous.Memory;

public sealed class AgentMemoryStore
{
    private readonly Dictionary<string, string> _memory = new();

    public void Save(string key, string value) => _memory[key] = value;

    public string? Get(string key) => _memory.GetValueOrDefault(key);
}
