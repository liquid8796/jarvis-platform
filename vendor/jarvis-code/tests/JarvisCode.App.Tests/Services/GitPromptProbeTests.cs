using System.Diagnostics;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class GitPromptProbeTests
{
    [Fact]
    public void Probe_closes_input_and_drains_large_stderr_without_blocking_stdout()
    {
        var start = Shell("[Console]::In.ReadToEnd() | Out-Null; [Console]::Error.Write(('e' * 131072)); [Console]::Out.Write('fixture@example.test')");
        Assert.Equal("fixture@example.test", SystemReminders.ReadGitOutput(start, 10000));
    }

    [Fact]
    public void A_stalled_probe_times_out_before_stdout_reaches_eof()
    {
        var clock = Stopwatch.StartNew();
        Assert.Null(SystemReminders.ReadGitOutput(Shell("Start-Sleep -Seconds 30"), 500));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    private static ProcessStartInfo Shell(string script)
    {
        var start = new ProcessStartInfo("powershell.exe");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
            start.ArgumentList.Add(argument);
        return start;
    }
}
