namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed class ProcessCommandVerifier : ICommandVerifier
{
    private readonly Func<string, Task<VerificationResult>> _executor;

    public ProcessCommandVerifier(Func<string, Task<VerificationResult>> executor)
    {
        _executor = executor;
    }

    public Task<VerificationResult> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _executor(request.Command);
    }
}
