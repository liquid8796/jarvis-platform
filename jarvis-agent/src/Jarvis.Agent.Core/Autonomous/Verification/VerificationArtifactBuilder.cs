namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed class VerificationArtifactBuilder
{
    public VerificationArtifact Create(
        VerificationRequest request,
        VerificationResult result)
    {
        return new VerificationArtifact(
            request.Command,
            result.Success,
            result.Output,
            DateTime.UtcNow);
    }
}

public sealed record VerificationArtifact(
    string Command,
    bool Success,
    string Output,
    DateTime CreatedAt);
