namespace Jarvis.Agent.Core.Autonomous.Recovery;

public sealed class RetryExecutionLoop
{
    private readonly FailureRecoveryEngine _engine;

    public RetryExecutionLoop(FailureRecoveryEngine engine)
    {
        _engine = engine;
    }

    public bool ShouldRetry(int attempt)
    {
        return _engine.CanRetry(attempt);
    }
}
