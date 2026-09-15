using System.IO;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// A fact that needs a real API key and spends tokens, so it runs only when
/// asked for by name.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LiveTurnFactAttribute : NativeUiFactAttribute
{
    public LiveTurnFactAttribute()
    {
        if (Skip is not null) return;
        if (Environment.GetEnvironmentVariable("JARVIS_E2E") != "1")
        {
            Skip = "set JARVIS_E2E=1 to run the end-to-end turns (they need a configured provider " +
                   "and spend tokens)";
        }
        else if (UiGoldenTests.AppExecutable is null)
        {
            Skip = "JarvisCode.App.exe was not found next to the test assembly";
        }
        else if (Environment.GetEnvironmentVariable(ParityNativeFixture.LiveSettingsVariable) is not { Length: > 0 } settings || !File.Exists(settings))
        {
            Skip = "JARVIS_PARITY_LIVE_SETTINGS must name an explicit encrypted settings fixture for live verification";
        }
    }
}

/// <summary>
/// Manual diagnostics for the app's real controls and windows. These require
/// explicit native-UI opt-in and are not routine checks for unrelated patches.
/// </summary>
[Collection(AppLaunchCollection.Name)]
public sealed class SmokeSelfTestTests
{
    /// <summary>
    /// The pane self-test starts WebView2, loads a page and drives CDP, so it is
    /// given room; the geometry self-test next door runs in well under a minute.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    [AppBuiltFact]
    public void Browser_pane_toolset_drives_a_real_page() =>
        RunSelfTest("--pane-selftest", "the Browser pane's own tools no longer drive a page end to end");

    [AppBuiltFact]
    public void Tasks_pane_opens_an_agents_transcript() =>
        RunSelfTest("--subagent-selftest", "the tasks pane's subagent view no longer opens, or does not " +
                                           "return to its list");

    [AppBuiltFact]
    public void Command_menu_matches_the_reference_shape() =>
        RunSelfTest("--slash-selftest", "the composer's command menu lost the reference's bounds, its "
                                      + "one-line row, its description card, its alias, its highlight, "
                                      + "its caret hint or the completion Tab performs");

    [LiveTurnFact]
    public void One_real_turn_completes() =>
        RunSelfTest("--e2e=Reply with the single word: ok", "a real turn did not complete");

    /// <summary>
    /// The session-switch round trip. Exit 3 is the app reporting that the model
    /// answered before the switch could happen — nothing was proven, so verification fails as inconclusive.
    /// </summary>
    [LiveTurnFact]
    public void A_turn_survives_a_session_switch()
    {
        var (exitCode, output) = Run("--e2e-switch=Count slowly from 1 to 20, one number per line.");
        Assert.True(exitCode == 0,
            $"session-switch verification did not prove completion (exit {exitCode}; exit 3 is inconclusive):\n{output}");
    }

    private static void RunSelfTest(string flag, string whatBroke)
    {
        var (exitCode, output) = Run(flag);
        Assert.True(exitCode == 0, $"{flag} exited {exitCode}: {whatBroke}.\n{output}");
    }

    /// <summary>
    /// Runs the app with one self-test flag in a throwaway profile. The
    /// screenshot argument is what arms these flags — without it the app opens
    /// the surface and waits for a person — so it is always passed, and the
    /// image is discarded.
    /// </summary>
    private static (int ExitCode, string Output) Run(string flag)
    {
        // Named after the flag, so a profile left behind by a killed run says
        // which test left it; by its letters rather than its hash, because
        // string hashing is randomized per process.
        var suffix = new string([.. flag.Split('=')[0].Where(char.IsLetter)]);
        var profile = $"parity-smoke-{Environment.ProcessId}-{suffix}-{Guid.NewGuid():N}";
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), $"JarvisCode-{profile}");
        var screenshot = Path.Combine(Path.GetTempPath(), $"jarvis-{profile}.png");
        var workspace = ParityNativeFixture.CreateWorkspace();
        ProcessRun? run = null;

        try
        {
            if (flag.StartsWith("--e2e", StringComparison.Ordinal)) ParityNativeFixture.CopyLiveSettings(root);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "ui-settings.json"), """{"extensionsAutoUpdate":false}""");
            run = ReferenceInstall.Run(
                UiGoldenTests.AppExecutable!,
                [$"--profile={profile}", flag, $"--screenshot={screenshot}", "--window-size=1400x900", $"--code-dir={workspace}"],
                Budget, info => ParityNativeFixture.Configure(info, workspace));

            Assert.False(run.TimedOut, $"the app did not finish {flag} within {Budget.TotalMinutes:0} minutes");
            var output = (run.StandardOutput + run.StandardError).Trim();
            return (run.ExitCode, output.Length > 4000 ? output[^4000..] : output);
        }
        finally
        {
            try { ParityNativeFixture.Retain(suffix, root, screenshot, run); }
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
