namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed class ProcessCommandVerifierAdapter : ICommandVerifier
{
    private readonly Func<string, string, CancellationToken, Task<VerificationResult>> _runner;

    public ProcessCommandVerifierAdapter(
        Func<string, string, CancellationToken, Task<VerificationResult>> runner)
    {
        _runner = runner;
    }

    public Task<VerificationResult> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _runner(
            request.Command,
            request.WorkingDirectory,
            cancellationToken);
    }
}
