using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using JarvisCode.Cli;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Captures the reference CLI's own help output once per test run. Each command
/// costs a process launch of roughly a second, so the twenty of them are taken
/// in parallel and shared by every test in the class.
/// </summary>
public sealed class ReferenceHelpFixture
{
    private readonly ConcurrentDictionary<string, ProcessRun> _runs = new(StringComparer.Ordinal);

    public ReferenceHelpFixture()
    {
        if (ReferenceInstall.CliPath is null)
        {
            return;
        }

        Parallel.ForEach(
            CliHelpParityTests.CommandNames,
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            name => _runs[name] = ReferenceInstall.RunReferenceCli(
                name == CliHelpParityTests.RootCommand
                    ? ["--help"]
                    : [.. name.Split(' '), "--help"]));
    }

    public ProcessRun Help(string command) => _runs[command];
}

/// <summary>
/// The reference CLI's help text, compared against ours by running the real
/// binary — the check that `gen-help-texts.py` was actually re-run after the
/// reference updated. A failure here is not necessarily a bug in our code: it
/// usually means the installed CLI moved and HelpTexts.cs needs regenerating,
/// which is why the message says so and names both versions.
/// </summary>
public sealed partial class CliHelpParityTests(ReferenceHelpFixture fixture) : IClassFixture<ReferenceHelpFixture>
{
    internal const string RootCommand = "(root)";

    /// <summary>
    /// Every command path HelpTexts.cs carries a constant for — driven off the
    /// generated table, so a newly captured command is covered automatically.
    /// </summary>
    internal static readonly string[] CommandNames =
        [RootCommand, .. HelpTexts.ByPath.Keys];

    public static TheoryData<string> Commands => [.. CommandNames];

    [ReferenceCliTheory]
    [MemberData(nameof(Commands))]
    public void Help_text_matches_the_installed_reference(string command)
    {
        var reference = fixture.Help(command);
        Assert.False(reference.TimedOut, $"the reference CLI did not answer `{command} --help` in time");

        var expected = ReferenceInstall.Rebrand(reference.Combined).TrimEnd('\n');
        var ours = OurHelp(command).ReplaceLineEndings("\n").TrimEnd('\n');

        Assert.True(expected == ours, Explain(command, expected, ours));
    }

    [ReferenceCliFact]
    public void Every_subcommand_the_reference_declares_has_a_help_constant()
    {
        // Guards the other direction: a new subcommand in a newer reference must
        // not go unnoticed just because we never asked for its help text.
        var root = ReferenceInstall.Rebrand(fixture.Help(RootCommand).Combined);
        var section = root[(root.IndexOf("Commands:", StringComparison.Ordinal) + "Commands:".Length)..];
        // Only entry lines name a command: they sit at exactly two spaces of
        // indent. Description text wraps far deeper, so anything else is prose.
        var declared = section
            .Split('\n')
            .Select(static line => CommandEntry().Match(line))
            .Where(static match => match.Success)
            // "plugin|plugins [options]" → "plugin"
            .Select(static match => match.Groups[1].Value.Split('|')[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var missing = declared.Where(static name => Subcommands.HelpFor(name) is null).ToList();
        Assert.True(missing.Count == 0,
            $"the reference declares subcommand(s) we carry no help text for: {string.Join(", ", missing)}. " +
            $"Capture them and re-run src/JarvisCode.Cli/gen-help-texts.py.");
    }

    /// <summary>A command entry in a help listing: two spaces, the name, then its description.</summary>
    [GeneratedRegex(@"^  ([a-z][\w-]*(?:\|[\w-]+)*)(?:\s|$)")]
    private static partial Regex CommandEntry();

    private static string OurHelp(string command) =>
        command == RootCommand ? HelpTexts.Root : Subcommands.HelpFor(command)
            ?? throw new InvalidOperationException($"no help constant for '{command}'");

    /// <summary>
    /// Points at the first differing line rather than dumping two screenfuls —
    /// help texts are long and a raw inequality message is unreadable.
    /// </summary>
    private static string Explain(string command, string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        int index = 0;
        while (index < expectedLines.Length && index < actualLines.Length &&
               expectedLines[index] == actualLines[index])
        {
            index++;
        }

        var reference = index < expectedLines.Length ? expectedLines[index] : "(no more lines)";
        var ours = index < actualLines.Length ? actualLines[index] : "(no more lines)";
        return $"`{command} --help` differs from the installed reference " +
               $"(CLI {ReferenceInstall.CliVersion ?? "unknown"}) at line {index + 1}:\n" +
               $"  reference: {reference}\n" +
               $"  ours:      {ours}\n" +
               "If the reference was updated, re-run src/JarvisCode.Cli/gen-help-texts.py " +
               "(its docstring holds the capture recipe) rather than hand-editing HelpTexts.cs.";
    }
}
