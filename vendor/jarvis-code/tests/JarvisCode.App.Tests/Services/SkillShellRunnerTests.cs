using System.Diagnostics;
using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Permissions;

namespace JarvisCode.App.Tests.Services;

public sealed class SkillShellRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("skillshell-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_preprocessing_command_that_leaves_a_detached_child_settles_on_its_own_exit()
    {
        // The grandchild inherits the command's pipes and outlives it, so reading
        // those pipes to their end would wait for the grandchild, not the command.
        var gate = new UiPermissionGate { Mode = PermissionMode.Bypass, WorkingDirectory = _dir };
        const string command =
            "Start-Process -NoNewWindow -FilePath cmd.exe -WorkingDirectory $env:TEMP " +
            "-ArgumentList '/c','ping -n 20 127.0.0.1 >NUL'; Write-Output 'parent-done'";

        var elapsed = Stopwatch.StartNew();
        var outcome = SkillShellRunner.Run(command, "powershell", _dir, gate, TimeSpan.FromSeconds(60));
        elapsed.Stop();

        Assert.Null(outcome.PermissionError);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("parent-done", outcome.Stdout);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"the run waited {elapsed.Elapsed.TotalSeconds:0.0}s for a detached child to release the pipe");
    }

    [Fact]
    public void A_preprocessing_command_reports_its_output_and_exit_code()
    {
        var gate = new UiPermissionGate { Mode = PermissionMode.Bypass, WorkingDirectory = _dir };

        var outcome = SkillShellRunner.Run(
            "Write-Output 'from-skill'; exit 4", "powershell", _dir, gate, TimeSpan.FromSeconds(60));

        Assert.Equal(4, outcome.ExitCode);
        Assert.Contains("from-skill", outcome.Stdout);
    }
}
