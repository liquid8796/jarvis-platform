namespace Jarvis.Agent.Core.Autonomous.Verification;

public sealed class ProcessToolsCommandRunner : IProcessCommandRunner
{
    private readonly Func<string, string, CancellationToken, Task<ProcessRunResult>> _runner;

    public ProcessToolsCommandRunner(
        Func<string, string, CancellationToken, Task<ProcessRunResult>> runner)
    {
        _runner = runner;
    }

    public Task<ProcessRunResult> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default)
        => _runner(command, workingDirectory, cancellationToken);
}
