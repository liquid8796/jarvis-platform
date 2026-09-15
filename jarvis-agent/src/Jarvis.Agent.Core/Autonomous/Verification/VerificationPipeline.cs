namespace Jarvis.Agent.Core.Autonomous;

public sealed class VerificationPipeline
{
    private readonly IEnumerable<IAgentVerifier> _verifiers;

    public VerificationPipeline(IEnumerable<IAgentVerifier> verifiers)
    {
        _verifiers = verifiers;
    }

    public bool Verify(AgentTask task) => _verifiers.All(x => x.Verify(task));
}
