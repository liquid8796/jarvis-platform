namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed class ArtifactVerificationPipeline
{
    private readonly IEnumerable<ICommandVerifier> _verifiers;

    public ArtifactVerificationPipeline(IEnumerable<ICommandVerifier> verifiers)
    {
        _verifiers = verifiers;
    }

    public async Task<bool> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        foreach (var verifier in _verifiers)
        {
            var result = await verifier.VerifyAsync(request, cancellationToken);
            if (!result.Success)
                return false;
        }

        return true;
    }
}
