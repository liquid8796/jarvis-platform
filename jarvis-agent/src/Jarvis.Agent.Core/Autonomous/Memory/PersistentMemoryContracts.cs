namespace Jarvis.Agent.Core.Autonomous.Memory;

public interface IAgentMemory
{
    void Save(string key, string value);

    string? Get(string key);
}
