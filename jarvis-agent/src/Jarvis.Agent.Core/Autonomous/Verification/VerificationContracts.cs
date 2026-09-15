namespace Jarvis.Agent.Core.Autonomous.Verification;

public interface IVerifier
{
    Task<bool> VerifyAsync(CancellationToken cancellationToken = default);
}

public sealed class VerificationPipeline
{
    private readonly IEnumerable<IVerifier> _verifiers;

    public VerificationPipeline(IEnumerable<IVerifier> verifiers)
    {
        _verifiers = verifiers;
    }

    public async Task<bool> VerifyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var verifier in _verifiers)
        {
            if (!await verifier.VerifyAsync(cancellationToken))
                return false;
        }

        return true;
    }
}
