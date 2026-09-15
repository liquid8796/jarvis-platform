using System.Diagnostics;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Tests.Tools;

public sealed class ShellToolTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private ToolExecutionContext Context => new() { WorkingDirectory = _temp.Path };

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Shell_CapturesStdout()
    {
        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Write-Output 'hello-from-shell'" }, Context, default);

        Assert.False(result.IsError);
        Assert.Contains("hello-from-shell", result.Content);
    }

    [Fact]
    public async Task Shell_NonZeroExitCode_IsErrorWithExitCode()
    {
        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "exit 3" }, Context, default);

        Assert.True(result.IsError);
        Assert.Contains("exit code 3", result.Content);
    }

    [Fact]
    public async Task Shell_RunsInWorkingDirectory()
    {
        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "(Get-Location).Path" }, Context, default);

        Assert.False(result.IsError);
        Assert.Contains(Path.GetFileName(_temp.Path), result.Content);
    }

    [Fact]
    public async Task Shell_Timeout_KillsProcessAndReportsError()
    {
        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Start-Sleep -Seconds 30", ["timeout_ms"] = 1500 }, Context, default);

        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Content);
    }

    [Fact]
    public async Task PlanMode_AllowsReadOnlyCommands()
    {
        var planContext = new ToolExecutionContext { WorkingDirectory = Context.WorkingDirectory, PlanMode = true };

        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Get-Date" }, planContext, default);

        Assert.False(result.IsError, result.Content);
    }

    [Fact]
    public async Task PlanMode_RejectsMutatingOrUnprovableCommands()
    {
        var planContext = new ToolExecutionContext { WorkingDirectory = Context.WorkingDirectory, PlanMode = true };

        var mutating = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Remove-Item x.txt" }, planContext, default);
        var piped = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Get-Date | Out-File x.txt" }, planContext, default);

        Assert.True(mutating.IsError);
        Assert.Contains("Plan mode", mutating.Content);
        Assert.True(piped.IsError);
    }

    [Fact]
    public async Task Shell_TimedOutTestCommand_IsClassifiedAsHang()
    {
        // The comment keeps the command a recognizable test invocation while the sleep hangs it.
        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Start-Sleep -Seconds 30 # dotnet test", ["timeout_ms"] = 1500 },
            Context, default);

        Assert.True(result.IsError);
        Assert.Contains("Test run hung", result.Content);
        Assert.Contains("hanging test", result.Content);
    }

    [Fact]
    public async Task Shell_MissingCommand_IsError()
    {
        var result = await new ShellTool().ExecuteAsync(new JsonObject(), Context, default);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Shell_ProcessThatLeavesADetachedChild_SettlesOnItsOwnExit()
    {
        // The grandchild inherits this call's stdout pipe, so the pipe stays open
        // long after the command itself is over. The reference settles a command
        // on the child's own exit and never waits for its pipes to close.
        const string command =
            "Start-Process -NoNewWindow -FilePath cmd.exe -WorkingDirectory $env:TEMP " +
            "-ArgumentList '/c','ping -n 20 127.0.0.1 >NUL'; Write-Output 'parent-done'";

        var elapsed = Stopwatch.StartNew();
        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = command }, Context, default);
        elapsed.Stop();

        Assert.False(result.IsError, result.Content);
        Assert.Contains("parent-done", result.Content);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"the call waited {elapsed.Elapsed.TotalSeconds:0.0}s for a detached child to release the pipe");
    }
}
