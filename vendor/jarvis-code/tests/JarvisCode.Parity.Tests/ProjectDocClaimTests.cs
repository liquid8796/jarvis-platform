using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// CLAUDE.md describes what this port carries, and nothing checked that the
/// things it names exist.
/// </summary>
/// <remarks>
/// This is not a parity check against the reference — it is a check on our own
/// documentation, and it needs nothing installed. It exists because on
/// 2026-09-01 CLAUDE.md claimed the system prompt carried the reference's
/// <c>&lt;browser_surfaces&gt;</c> note while the string was in no source file:
/// the sort of error no other test can catch, because there is nothing to write
/// a test against for something that does not exist. Two claims are cheap to
/// verify and go stale silently, so both are verified.
/// </remarks>
public class ProjectDocClaimTests
{
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string Doc { get; } = File.ReadAllText(Path.Combine(RepoRoot, "CLAUDE.md"));

    /// <summary>
    /// Backticked paths into this repo's own source, e.g.
    /// <c>Services/HostPromptSections.cs</c>. Reference paths (app.asar, the
    /// bundle chunks) do not match, because they carry no such directory.
    /// </summary>
    private static readonly Regex SourcePaths = new(
        @"`([A-Za-z0-9_./-]*(?:Services|Views|Controls|ViewModels|Agent|Tools|Mcp|Hooks|Theming|Infrastructure|Markdown|Memory|Documents|BackgroundTasks|Customization|Models)/[A-Za-z0-9_.-]+\.(?:cs|xaml))`",
        RegexOptions.Compiled);

    /// <summary>
    /// Backticked prompt/protocol tags with a separator in the name, e.g.
    /// <c>&lt;system-reminder&gt;</c>. The separator is what makes a name
    /// distinctive enough to search for; a bare <c>&lt;env&gt;</c> would match
    /// anywhere and prove nothing. The search is for the name without its
    /// brackets, because a tag is usually built from a constant.
    /// </summary>
    private static readonly Regex TagNames = new(
        @"`<([a-z][a-z0-9]*(?:[_-][a-z0-9]+)+)>`", RegexOptions.Compiled);

    [Fact]
    public void Every_source_file_CLAUDE_md_names_exists()
    {
        var files = SourceFiles();
        var missing = SourcePaths.Matches(Doc)
            .Select(static m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(claim => !Resolves(claim, files))
            .OrderBy(static c => c, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, new StringBuilder()
            .AppendLine($"CLAUDE.md names {missing.Count} source file(s) that do not exist:")
            .AppendLine(string.Join("\n", missing.Select(static m => "  " + m)))
            .AppendLine("Rename the mention, or restore the file.")
            .ToString());
    }

    [Fact]
    public void Every_tag_CLAUDE_md_says_this_port_carries_is_in_the_source()
    {
        var source = ReadAllSource();
        var missing = TagNames.Matches(Doc)
            .Select(static m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(tag => !source.Contains(tag, StringComparison.Ordinal))
            .OrderBy(static t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, new StringBuilder()
            .AppendLine($"CLAUDE.md says this port carries {missing.Count} tag(s) no source file mentions:")
            .AppendLine(string.Join("\n", missing.Select(static m => "  <" + m + ">")))
            .AppendLine("Either build it, or stop claiming it.")
            .ToString());
    }

    /// <summary>
    /// CLAUDE.md writes a path as <c>Core/Agent/Foo.cs</c> where the project
    /// directory is <c>JarvisCode.Core</c>, so the project shorthand is
    /// expanded before matching.
    /// </summary>
    private static bool Resolves(string claim, IReadOnlyList<string> files)
    {
        string[] candidates =
        [
            claim,
            System.Text.RegularExpressions.Regex.Replace(
                claim, @"^(Core|App|Cli|Host|Providers)/", "JarvisCode.$1/"),
        ];
        return candidates.Any(candidate =>
            files.Any(f => f.EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<string> SourceFiles()
    {
        var files = new List<string>();
        foreach (var root in new[] { "src", "tests" })
        {
            var directory = Path.Combine(RepoRoot, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            files.AddRange(Directory
                .EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(static p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Where(static p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                       StringComparison.Ordinal)
                                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                       StringComparison.Ordinal))
                .Select(static p => p.Replace(Path.DirectorySeparatorChar, '/')));
        }

        return files;
    }

    private static string ReadAllSource()
    {
        var builder = new StringBuilder();
        foreach (var path in SourceFiles().Where(static p => p.Contains("/src/", StringComparison.Ordinal)))
        {
            builder.Append(File.ReadAllText(path)).Append('\n');
        }

        return builder.ToString();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("could not find the repository root from " + AppContext.BaseDirectory);
    }
}
