using System.IO;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Runs the same command line through the reference CLI and through ours and
/// compares what a script would see: stdout, stderr and the exit code.
///
/// Every case here is argument handling only — no case starts a turn, because a
/// turn would spend the user's tokens and need the network. That is also why
/// the cases are safe to run against a machine's real install.
/// </summary>
public sealed class CliBehaviourParityTests
{
    /// <summary>
    /// Command lines whose output and exit code must match the reference byte
    /// for byte after the brand swap.
    /// </summary>
    public static TheoryData<string[]> IdenticalCases =>
    [
        ["--definitely-not-a-flag"],
        ["--not-a-flag", "--also-not-a-flag"],
        ["--model"],
        ["--effort"],
        ["--output-format"],
        ["-p", "hi", "--output-format", "bogus"],
        ["--effort", "ultra"],
        ["--permission-mode", "nonsense"],
        ["--input-format", "bogus"],
        ["--task-budget", "0"],
        ["--task-budget", "abc"],
        ["--output-format", "json"],
        ["mcp", "frobnicate"],
        ["--help"],
        ["doctor", "--help"],
        ["mcp", "--help"],
        ["plugin", "--help"],
        ["project", "purge", "--help"],
    ];

    [ReferenceCliTheory]
    [MemberData(nameof(IdenticalCases))]
    public void Argument_handling_matches_the_reference(string[] arguments)
    {
        var reference = ReferenceInstall.RunReferenceCli(arguments);
        var ours = RunOurs(arguments);

        Assert.False(reference.TimedOut, $"the reference CLI hung on: {string.Join(' ', arguments)}");
        Assert.False(ours.TimedOut, $"our CLI hung on: {string.Join(' ', arguments)}");

        var expected = ReferenceInstall.Rebrand(reference.Combined).TrimEnd('\n');
        var actual = ours.Combined.TrimEnd('\n');
        Assert.True(expected == actual,
            $"`jarvis {string.Join(' ', arguments)}` output differs from the reference " +
            $"(CLI {ReferenceInstall.CliVersion ?? "unknown"}):\n" +
            $"  reference: {Preview(expected)}\n  ours:      {Preview(actual)}");
        Assert.True(reference.ExitCode == ours.ExitCode,
            $"`jarvis {string.Join(' ', arguments)}` exited {ours.ExitCode}, " +
            $"the reference exited {reference.ExitCode}");
    }

    /// <summary>
    /// Where we deliberately differ, and why. Asserting the delta keeps it from
    /// being "fixed" by accident and keeps it from drifting into something else:
    /// these are the only command lines allowed to answer differently.
    /// </summary>
    public static TheoryData<string[], string, int> DeliberateDeltas => new()
    {
        {
            ["--cloud", "anything"],
            "Error: --cloud (cloud sessions) is not available in Jarvis Code.",
            1
        },
        {
            ["--teleport"],
            "Error: --teleport (teleport sessions) is not available in Jarvis Code.",
            1
        },
        {
            ["--remote-control"],
            "Error: --remote-control (Remote Control) is not available in Jarvis Code.",
            1
        },
        {
            ["-p", "hi", "--input-format", "stream-json"],
            "Error: --input-format stream-json requires --output-format stream-json.",
            1
        },
        {
            ["--session-id", "not-a-uuid", "-p", "hi"],
            "Error: --session-id must be a valid UUID",
            1
        },
        {
            ["ultrareview"],
            "Error: cloud-hosted multi-agent review is not available in Jarvis Code — " +
            "use /code-review in a session.",
            1
        },
        {
            ["install"],
            "Error: self-update is not available in Jarvis Code — Jarvis Code ships with the app; " +
            "rebuild or reinstall it instead.",
            1
        },
    };

    [Theory]
    [MemberData(nameof(DeliberateDeltas))]
    public void Unavailable_features_refuse_with_their_documented_reason(
        string[] arguments, string expected, int expectedExitCode)
    {
        var ours = RunOurs(arguments);
        Assert.False(ours.TimedOut, $"our CLI hung on: {string.Join(' ', arguments)}");
        Assert.Equal(expected, ours.Combined.TrimEnd('\n'));
        Assert.Equal(expectedExitCode, ours.ExitCode);
    }

    [ReferenceCliFact]
    public void Version_flag_answers_in_the_reference_shape()
    {
        // The number is ours, the shape is the reference's: "<version> (<product>)".
        var reference = ReferenceInstall.RunReferenceCli(["--version"]);
        var ours = RunOurs(["--version"]);
        Assert.Equal(reference.ExitCode, ours.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+ \(Jarvis Code\)$", ours.Combined.Trim());
        Assert.Matches(@"^\d+\.\d+\.\d+ \(Claude Code\)$", reference.Combined.Trim());
    }

    private static ProcessRun RunOurs(IReadOnlyList<string> arguments)
    {
        var run = ReferenceInstall.RunIsolatedCli(OurCliPath.Value, arguments, TimeSpan.FromSeconds(120));
        return run;
    }

    private static readonly Lazy<string> OurCliPath = new(() =>
    {
        // The project reference copies jarvis.exe next to the test assembly.
        var beside = Path.Combine(AppContext.BaseDirectory, "jarvis.exe");
        if (File.Exists(beside))
        {
            return beside;
        }

        throw new InvalidOperationException(
            $"jarvis.exe was not found next to the test assembly ({AppContext.BaseDirectory}); " +
            "the parity tests need the CLI built.");
    });

    private static string Preview(string text)
    {
        var firstLine = text.Split('\n')[0];
        return firstLine.Length > 160 ? firstLine[..160] + "…" : firstLine;
    }
}
