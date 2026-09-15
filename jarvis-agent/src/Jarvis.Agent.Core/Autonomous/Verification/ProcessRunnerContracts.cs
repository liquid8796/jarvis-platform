namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed record ProcessRunResult(
    bool Success,
    string Output,
    string? Error = null);

public interface IProcessCommandRunner
{
    Task<ProcessRunResult> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
