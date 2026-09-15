namespace Jarvis.Agent.Core.Autonomous.Recovery;

public sealed record RecoveryPolicy(int MaxRetries = 3)
{
    public bool CanRetry(int attempt) => attempt < MaxRetries;
}

public sealed class FailureAnalyzer
{
    public string Analyze(Exception exception) => exception.Message;
}
