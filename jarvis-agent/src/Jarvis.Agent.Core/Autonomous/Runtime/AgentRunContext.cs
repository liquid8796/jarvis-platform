namespace Jarvis.Agent.Core.Autonomous.Runtime;

public sealed class AgentRunContext
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public Dictionary<string, string> Properties { get; } = new();
}
