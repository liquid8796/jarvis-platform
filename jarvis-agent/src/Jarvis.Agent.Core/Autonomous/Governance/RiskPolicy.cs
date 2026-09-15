namespace Jarvis.Agent.Core.Autonomous.Governance;

public enum RiskLevel
{
    Low,
    Medium,
    High
}

public sealed class RiskPolicy
{
    public bool RequiresApproval(RiskLevel level) => level != RiskLevel.Low;
}
