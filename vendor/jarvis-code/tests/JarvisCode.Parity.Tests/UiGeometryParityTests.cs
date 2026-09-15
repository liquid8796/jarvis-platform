using System.IO;

namespace JarvisCode.Parity.Tests;

/// <summary>A fact that needs the built app; without it there is nothing to lay out.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AppBuiltFactAttribute : NativeUiFactAttribute
{
    public AppBuiltFactAttribute()
    {
        if (Skip is null && UiGoldenTests.AppExecutable is null)
        {
            Skip = "JarvisCode.App.exe was not found next to the test assembly";
        }
    }
}

/// <summary>
/// Drives the app's own <c>--ui-selftest</c>, which measures the metrics this UI
/// was ported against Claude Code Desktop for — sidebar, title bar, session
/// header, settings nav, theme picker and quick entry — on the laid-out visual
/// tree. It runs only for explicitly selected UI verification.
/// </summary>
[Collection(AppLaunchCollection.Name)]
public sealed class UiGeometryParityTests
{
    [AppBuiltFact]
    public void Reference_measured_geometry_still_lays_out_that_way()
    {
        var profile = $"parity-geometry-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), $"JarvisCode-{profile}");
        var screenshot = Path.Combine(Path.GetTempPath(), $"jarvis-uiselftest-{Environment.ProcessId}.png");
        var workspace = ParityNativeFixture.CreateWorkspace();
        ProcessRun? run = null;

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "ui-settings.json"), """{"extensionsAutoUpdate":false}""");
            run = ReferenceInstall.Run(
                UiGoldenTests.AppExecutable!,
                [
                    $"--profile={profile}",
                    "--ui-selftest",
                    $"--screenshot={screenshot}",
                    "--window-size=1400x900",
                    $"--code-dir={workspace}",
                ],
                TimeSpan.FromSeconds(180), info => ParityNativeFixture.Configure(info, workspace));

            Assert.False(run.TimedOut, "the app did not finish --ui-selftest within 180s");
            Assert.True(run.ExitCode == 0,
                $"--ui-selftest exited {run.ExitCode}: at least one reference-measured metric no longer " +
                $"lays out at its expected size. The failing metric and both numbers are in " +
                $"{Path.Combine(root, "logs")}.");
        }
        finally
        {
            try { ParityNativeFixture.Retain("geometry", root, screenshot, run); }
            finally
            {
                TryDelete(screenshot);
                TryDeleteDirectory(root);
                ParityNativeFixture.DeleteWorkspace(workspace);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a passing test over.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
