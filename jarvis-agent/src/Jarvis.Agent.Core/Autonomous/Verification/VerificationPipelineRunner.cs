namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed class VerificationPipelineRunner
{
    private readonly IEnumerable<ICommandVerifier> _verifiers;

    public VerificationPipelineRunner(IEnumerable<ICommandVerifier> verifiers)
    {
        _verifiers = verifiers;
    }

    public async Task<bool> RunAsync(VerificationRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var verifier in _verifiers)
        {
            if (!(await verifier.VerifyAsync(request, cancellationToken)).Success)
            {
                return false;
            }
        }

        return true;
    }
}
