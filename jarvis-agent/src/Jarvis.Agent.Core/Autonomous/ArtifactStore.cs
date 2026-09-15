namespace Jarvis.Agent.Core.Autonomous;

public sealed class ArtifactStore
{
    private readonly List<AgentArtifact> _artifacts = [];

    public void Add(AgentArtifact artifact)
        => _artifacts.Add(artifact);

    public IReadOnlyList<AgentArtifact> GetAll()
        => _artifacts;
}

public sealed class VerificationEngine : IAgentVerifier
{
    public bool Verify(AgentTask task)
        => task.Artifacts.Count > 0;
}

public sealed class AgentMemoryStore
{
    private readonly Dictionary<string, string> _memory = new();

    public void Save(string key, string value)
        => _memory[key] = value;

    public string? Read(string key)
        => _memory.GetValueOrDefault(key);
}
