namespace Jarvis.Agent.Core.Autonomous.Recovery;

public sealed class FailureRecoveryEngine
{
    private readonly RecoveryPolicy _policy;

    public FailureRecoveryEngine(RecoveryPolicy policy)
    {
        _policy = policy;
    }

    public bool CanRetry(int attempt)
    {
        return _policy.CanRetry(attempt);
    }
}
