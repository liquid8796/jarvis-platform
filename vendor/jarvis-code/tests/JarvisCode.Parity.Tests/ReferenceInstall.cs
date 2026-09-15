using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Locates the Claude Code install these tests compare against: the standalone
/// CLI binary and the packaged desktop app's string catalogue. Everything is
/// discovered once per test run and cached, because both lookups are slow (a
/// process launch and a package query) and neither changes mid-run.
///
/// Nothing here fails when the reference is absent — <see cref="ReferenceFactAttribute"/>
/// turns that into a skipped test, so the suite stays green on a machine that
/// does not have Claude Code installed.
/// </summary>
internal static partial class ReferenceInstall
{
    private static readonly Lazy<string?> LazyCliPath = new(FindCli);
    private static readonly Lazy<string?> LazyCliVersion = new(ReadCliVersion);
    private static readonly Lazy<string?> LazyAppDirectory = new(FindApp);
    private static readonly Lazy<IReadOnlyDictionary<string, string>?> LazyCatalogue = new(LoadCatalogue);

    /// <summary>The standalone `claude.exe`, or null when it is not installed.</summary>
    public static string? CliPath => LazyCliPath.Value;

    /// <summary>The version the CLI reports, for failure messages that must name what drifted.</summary>
    public static string? CliVersion => LazyCliVersion.Value;

    /// <summary>The packaged desktop app's `app` directory, or null.</summary>
    public static string? AppDirectory => LazyAppDirectory.Value;

    /// <summary>
    /// The packaged app's version, read from its install path
    /// (`Claude_1.40609.0.0_x64__…`) — the same string the package manifest
    /// carries, without a second PowerShell launch to ask for it.
    /// </summary>
    public static string? AppVersion
    {
        get
        {
            var package = Path.GetFileName(Path.GetDirectoryName(AppDirectory) ?? "");
            var fields = package.Split('_');
            return fields.Length >= 2 && fields[1].Contains('.') ? fields[1] : null;
        }
    }

    /// <summary>The desktop app's en-US string catalogue: message id → the text the UI shows.</summary>
    public static IReadOnlyDictionary<string, string>? Catalogue => LazyCatalogue.Value;

    public static string CliMissingReason =>
        "the reference Claude Code CLI is not installed on this machine " +
        "(checked JARVIS_REFERENCE_CLI, ~/.local/bin, desktop bundles and MSIX LocalCache bundles)";

    public static string CatalogueMissingReason =>
        "the packaged Claude desktop app is not installed on this machine " +
        "(its en-US.json string catalogue is what these assertions read)";

    private static string? FindCli()
    {
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code"),
        };
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
            roots.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                .Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code")));

        return FindCliFrom(Environment.GetEnvironmentVariable("JARVIS_REFERENCE_CLI"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"), roots);
    }

    /// <summary>An explicit target never silently falls back to a different installation.</summary>
    internal static string? FindCliFrom(string? explicitPath, string standalone, IEnumerable<string> bundleRoots)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        if (File.Exists(standalone))
            return standalone;
        return bundleRoots.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories)
            .Select(directory => (Path: Path.Combine(directory, "claude.exe"),
                Version: Version.TryParse(Path.GetFileName(directory), out var version) ? version : new Version(0, 0)))
            .Where(candidate => File.Exists(candidate.Path))
            .OrderByDescending(candidate => candidate.Version)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .Select(candidate => candidate.Path).FirstOrDefault();
    }

    private static string? ReadCliVersion()
    {
        if (CliPath is not { } path)
        {
            return null;
        }

        var run = RunIsolatedCli(path, ["--version"], TimeSpan.FromSeconds(60));
        // "2.1.251 (Claude Code)" — the number is all a failure message needs.
        return run.ExitCode == 0 ? run.StandardOutput.Trim().Split(' ')[0] : null;
    }

    /// <summary>
    /// The packaged app lives under %ProgramFiles%\WindowsApps, which cannot be
    /// enumerated even as administrator while files inside it read normally — so
    /// the install location comes from the package manifest rather than a glob.
    /// </summary>
    private static string? FindApp()
    {
        if (Environment.GetEnvironmentVariable("JARVIS_REFERENCE_APP") is { Length: > 0 } selected)
        {
            var app = Directory.Exists(Path.Combine(selected, "resources")) ? selected : Path.Combine(selected, "app");
            return Directory.Exists(app) ? app : null;
        }
        var run = Run(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "(Get-AppxPackage -Name Claude).InstallLocation"],
            TimeSpan.FromSeconds(90));
        if (run.ExitCode != 0)
        {
            return null;
        }

        var location = run.StandardOutput.Trim();
        if (location.Length == 0)
        {
            return null;
        }

        var appDirectory = Path.Combine(location, "app");
        return Directory.Exists(appDirectory) ? appDirectory : null;
    }

    private static IReadOnlyDictionary<string, string>? LoadCatalogue()
    {
        if (AppDirectory is not { } app)
        {
            return null;
        }

        var path = Path.Combine(app, "resources", "ion-dist", "i18n", "en-US.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Entries are either a bare string or an object carrying the text
            // under "defaultMessage"; both shapes appear in the shipped file.
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                entries[property.Name] = property.Value.GetString() ?? "";
            }
            else if (property.Value.ValueKind == JsonValueKind.Object &&
                     property.Value.TryGetProperty("defaultMessage", out var message) &&
                     message.ValueKind == JsonValueKind.String)
            {
                entries[property.Name] = message.GetString() ?? "";
            }
        }

        return entries;
    }

    /// <summary>
    /// The brand swap `gen-help-texts.py` applies when it generates HelpTexts.cs.
    /// Kept identical to the generator on purpose: the parity test re-derives the
    /// expected text from the reference's live output, so any divergence between
    /// these two rules would show up as a phantom failure.
    /// </summary>
    public static string Rebrand(string text) =>
        BareCommand().Replace(text.Replace("Claude Code", "Jarvis Code"), "jarvis");

    [GeneratedRegex(@"(?<![\w.'\-])claude(?=\s)")]
    private static partial Regex BareCommand();

    public static ProcessRun RunReferenceCli(IReadOnlyList<string> arguments) =>
        CliPath is { } path
            ? RunIsolatedCli(path, arguments, TimeSpan.FromSeconds(120))
            : throw new InvalidOperationException(CliMissingReason);

    /// <summary>
    /// Pure help/argument checks must not load the caller's project, hooks,
    /// accounts or CLI profile. Captures which need a configured fixture use
    /// the general Run overload explicitly instead.
    /// </summary>
    internal static ProcessRun RunIsolatedCli(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        using var isolation = new CliProcessIsolation();
        return Run(fileName, arguments, timeout, isolation.Configure);
    }

    /// <summary>
    /// Runs a process with stdin closed and both streams captured. Stdin matters:
    /// the CLI starts an interactive session when it has a terminal, so a test
    /// that left stdin open would hang instead of printing and exiting.
    /// </summary>
    public static ProcessRun Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        Action<ProcessStartInfo>? configure = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Both CLIs emit UTF-8 (arrows, the pause glyph, en/em dashes). Without
            // this the parent decodes them in the console's OEM codepage and every
            // comparison fails on mojibake instead of on real drift.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        configure?.Invoke(info);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"could not start {fileName}");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone between the timeout and the kill.
            }

            return new ProcessRun(-1, stdout.Result, stderr.Result, TimedOut: true);
        }

        return new ProcessRun(process.ExitCode, stdout.Result, stderr.Result, TimedOut: false);
    }
}

public sealed record ProcessRun(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    /// <summary>Both streams, normalized to \n so a comparison is not a line-ending test.</summary>
    public string Combined => (StandardOutput + StandardError).ReplaceLineEndings("\n");
}
