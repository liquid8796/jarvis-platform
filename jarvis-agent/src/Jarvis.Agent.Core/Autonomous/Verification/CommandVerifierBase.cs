namespace Jarvis.Agent.Core.Autonomous.Verification;

public abstract class CommandVerifierBase : ICommandVerifier
{
    public abstract Task<VerificationResult> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken = default);
}
