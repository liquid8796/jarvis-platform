namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed record BuildVerificationResult(
    bool Success,
    string Command,
    string Output);
