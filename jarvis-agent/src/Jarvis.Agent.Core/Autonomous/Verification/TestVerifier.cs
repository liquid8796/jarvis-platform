namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed class TestVerifier : ICommandVerifier
{
    private readonly ICommandVerifier _commandVerifier;

    public TestVerifier(ICommandVerifier commandVerifier)
    {
        _commandVerifier = commandVerifier;
    }

    public Task<VerificationResult> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _commandVerifier.VerifyAsync(request, cancellationToken);
    }
}
