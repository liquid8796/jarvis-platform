using System.IO;
using System.Windows.Media.Imaging;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// A fact that needs the built app; without it there is nothing to render.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AppBuiltTheoryAttribute : NativeUiTheoryAttribute
{
    public AppBuiltTheoryAttribute()
    {
        if (Skip is null && UiGoldenTests.AppExecutable is null)
        {
            Skip = "JarvisCode.App.exe was not found next to the test assembly";
        }
    }
}

/// <summary>
/// Renders surfaces of the real app and compares them to approved baselines.
///
/// What this is: a regression check on *our* pixels. It cannot compare against
/// Claude Code Desktop directly — two different apps showing two different
/// sessions never match pixel for pixel — so reference parity of the layout is
/// asserted numerically by <c>--ui-selftest</c> instead, and this catches the
/// unintended visual change that no assertion was written for.
///
/// Baselines are machine-generated (fonts and DPI decide the pixels), so the
/// a missing baseline fails without writing. Set JARVIS_APPROVE_BASELINES=1
/// explicitly to approve after a deliberate design change.
/// </summary>
[Collection(AppLaunchCollection.Name)]
public sealed class UiGoldenTests
{
    /// <summary>Surfaces stable enough to compare: chrome only, no clocks, no session content.</summary>
    public static TheoryData<string> Surfaces => ["settings", "themes"];

    private const int RenderWidth = 1400;
    private const int RenderHeight = 900;

    /// <summary>Per-channel difference below which two pixels count as equal (anti-aliasing).</summary>
    private const int ChannelTolerance = 8;

    /// <summary>Fraction of differing pixels tolerated before the surface is called changed.</summary>
    private const double MaxDifferingFraction = 0.002;

    internal static readonly string? AppExecutable = FindApp();

    [AppBuiltTheory]
    [MemberData(nameof(Surfaces))]
    public void Surface_matches_its_approved_baseline(string surface)
    {
        var rendered = Render(surface);
        var baseline = Path.Combine(BaselineDirectory, $"{surface}.png");

        if (Environment.GetEnvironmentVariable("JARVIS_APPROVE_BASELINES") == "1")
        {
            Directory.CreateDirectory(BaselineDirectory);
            File.Copy(rendered, baseline, overwrite: true);
            // Writing a baseline is not evidence of anything, so say so rather
            // than reporting a pass the run did not earn.
            Assert.Fail(
                $"baseline for '{surface}' was written to {baseline}. " +
                "Review the image, commit it, and re-run — this run proved nothing.");
        }
        Assert.True(File.Exists(baseline), $"Baseline missing: {baseline}. Verification does not create or approve baselines.");

        var (differing, total, sizeMismatch) = Compare(baseline, rendered);
        if (sizeMismatch is { } mismatch)
        {
            var kept = KeepForInspection(surface, rendered);
            Assert.Fail($"'{surface}' rendered at {mismatch} but the baseline is a different size. " +
                        $"The render is at {kept}; if this is a DPI or window-size change, re-approve " +
                        "with JARVIS_APPROVE_BASELINES=1.");
        }

        var fraction = (double)differing / total;
        if (fraction > MaxDifferingFraction)
        {
            var kept = KeepForInspection(surface, rendered);
            Assert.Fail($"'{surface}' differs from its baseline in {differing} of {total} pixels " +
                        $"({fraction:P2}, tolerance {MaxDifferingFraction:P2}). The render is at {kept}.");
        }
    }

    /// <summary>
    /// Launches the app in a throwaway profile pinned to one theme, so the
    /// comparison is not a test of whatever theme the developer last picked.
    /// </summary>
    private static string Render(string surface)
    {
        // Fixed, not per-run: the profile name is printed in the window title,
        // so a unique one would change the pixels it is compared against.
        const string profile = "parity-golden";

        // That fixed name is also the app's single-instance key, and this repo is
        // worked in several checkouts at once — a second suite rendering at the
        // same moment deletes this profile out from under the first, whose app
        // then forwards to the other instance and exits 0 without a screenshot.
        // The profile admits one renderer at a time, so take it one at a time.
        using var one = new Mutex(initiallyOwned: false, @"Local\JarvisCode.Parity.UiGolden");
        var held = false;
        var profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JarvisCode-" + profile);
        var backupWorkspace = ParityNativeFixture.CreateWorkspace();
        var backup = Path.Combine(backupWorkspace, "profile-backup");
        var hadProfile = false;
        var prepared = false;
        try
        {
            held = one.WaitOne(TimeSpan.FromMinutes(5));
        }
        catch (AbandonedMutexException)
        {
            // A renderer that died holding it leaves the profile to this one.
            held = true;
        }

        try
        {
            Assert.True(held, "Another golden renderer still owns the fixed parity-golden profile; no render was attempted.");
            hadProfile = Directory.Exists(profileRoot);
            if (hadProfile) Directory.Move(profileRoot, backup);
            prepared = true;
            return RenderExclusively(surface, profile);
        }
        finally
        {
            if (held)
            {
                try
                {
                    if (prepared)
                    {
                        if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, recursive: true);
                        if (hadProfile && Directory.Exists(backup)) Directory.Move(backup, profileRoot);
                    }
                }
                finally { one.ReleaseMutex(); }
            }
            ParityNativeFixture.DeleteWorkspace(backupWorkspace);
        }
    }

    /// <summary>
    /// How many times a render is attempted. A checkout that does not yet carry
    /// the lock above can still be holding the profile's single-instance key when
    /// this one launches, and the app then forwards to it and exits 0 with nothing
    /// written. That is a race for the profile, not a fact about the pixels, so it
    /// is waited out — a render that does happen is compared once, however it
    /// turns out.
    /// </summary>
    private const int RenderAttempts = 3;

    private static string RenderExclusively(string surface, string profile)
    {
        var output = Path.Combine(Path.GetTempPath(), $"jarvis-golden-{surface}-{Environment.ProcessId}.png");
        var lastExit = 0;
        var lastError = "";
        for (var attempt = 1; attempt <= RenderAttempts; attempt++)
        {
            var workspace = ParityNativeFixture.CreateWorkspace();
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), $"JarvisCode-{profile}");
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "ui-settings.json"),
                """{"themeMode":"Dark","zoomFactor":1,"extensionsAutoUpdate":false}""");

            File.Delete(output);
            ProcessRun run;
            try
            {
                run = ReferenceInstall.Run(
                AppExecutable!,
                [
                    $"--profile={profile}",
                    $"--open={surface}",
                    $"--screenshot={output}",
                    $"--window-size={RenderWidth}x{RenderHeight}",
                ],
                TimeSpan.FromSeconds(180), info => ParityNativeFixture.Configure(info, workspace));

                ParityNativeFixture.Retain("golden-" + surface + "-" + attempt, root, output, run);
            }
            finally { ParityNativeFixture.DeleteWorkspace(workspace); }

            Assert.False(run.TimedOut, $"the app did not render '{surface}' within 180s");
            if (File.Exists(output))
            {
                return output;
            }

            lastExit = run.ExitCode;
            lastError = run.StandardError.Trim();
            if (attempt < RenderAttempts)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }

        Assert.Fail(
            $"the app exited {lastExit} without writing a screenshot for '{surface}' " +
            $"in {RenderAttempts} attempts. stderr: {lastError}");
        return output;
    }

    private static (int Differing, int Total, string? SizeMismatch) Compare(string baselinePath, string renderPath)
    {
        var (baseline, baseWidth, baseHeight) = ReadPixels(baselinePath);
        var (render, renderW, renderH) = ReadPixels(renderPath);
        if (baseWidth != renderW || baseHeight != renderH)
        {
            return (0, 0, $"{renderW}x{renderH}");
        }

        int differing = 0;
        for (int i = 0; i < baseline.Length; i += 4)
        {
            // Bgra32: comparing the three colour channels is enough — the
            // captures are opaque.
            if (Math.Abs(baseline[i] - render[i]) > ChannelTolerance ||
                Math.Abs(baseline[i + 1] - render[i + 1]) > ChannelTolerance ||
                Math.Abs(baseline[i + 2] - render[i + 2]) > ChannelTolerance)
            {
                differing++;
            }
        }

        return (differing, baseline.Length / 4, null);
    }

    private static (byte[] Pixels, int Width, int Height) ReadPixels(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight);
    }

    /// <summary>Keeps a failing render beside the baselines so it can be looked at.</summary>
    private static string KeepForInspection(string surface, string rendered)
    {
        var directory = Environment.GetEnvironmentVariable(ParityNativeFixture.EvidenceVariable) is { Length: > 0 } evidence
            ? Path.GetFullPath(evidence) : BaselineDirectory;
        var destination = Path.Combine(directory, $"{surface}.actual.png");
        Directory.CreateDirectory(directory);
        File.Copy(rendered, destination, overwrite: true);
        return destination;
    }

    /// <summary>
    /// The baselines live in the source tree, not the build output: an approved
    /// image is a reviewed artefact that belongs in the commit.
    /// </summary>
    private static string BaselineDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JarvisCode.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(
                directory?.FullName ?? AppContext.BaseDirectory,
                "tests", "JarvisCode.Parity.Tests", "Baselines");
        }
    }

    private static string? FindApp()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "JarvisCode.App.exe");
        return File.Exists(beside) ? beside : null;
    }
}
