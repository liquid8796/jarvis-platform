namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed class ProcessVerifierAdapter : ICommandVerifier
{
    private readonly IProcessCommandRunner _runner;

    public ProcessVerifierAdapter(IProcessCommandRunner runner)
    {
        _runner = runner;
    }

    public async Task<VerificationResult> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            request.Command,
            request.WorkingDirectory,
            cancellationToken);

        return new VerificationResult(
            result.Success,
            result.Output,
            result.Error);
    }
}
