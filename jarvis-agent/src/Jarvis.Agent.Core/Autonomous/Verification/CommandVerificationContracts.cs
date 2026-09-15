namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed record VerificationRequest(
    string Command,
    string WorkingDirectory);

public sealed record VerificationResult(
    bool Success,
    string Output,
    string? Error = null);

public interface ICommandVerifier
{
    Task<VerificationResult> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken = default);
}
