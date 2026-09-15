using System.IO;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The line endings inside this port's multi-line literals.
///
/// C# raw string literals keep the line endings of the file they are written
/// in, and the prompts, tool docs and system-reminders this app sends are raw
/// string literals. A source file saved with CRLF therefore puts CRLF on the
/// wire where the reference sends LF: the same text, no longer the same bytes,
/// and nothing else in this suite would notice — every assertion here compares
/// text, and text comparison is exactly what a stray carriage return survives.
///
/// The repository pins LF in <c>.gitattributes</c> so a checkout cannot
/// introduce them. This is the other half: an editor that saves CRLF anyway.
/// </summary>
public sealed class LineEndingParityTests
{
    [Fact]
    public void No_multi_line_literal_carries_a_carriage_return()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains('\r'))
            {
                continue;
            }

            foreach (var ported in PortedText.CSharpStrings(text, preserveLineEndings: true))
            {
                // Only literals that span lines in the source: a `\r` written as
                // an escape is code doing its job, not a checkout artefact.
                if (ported.Multiline && ported.Segments.Any(static segment => segment.Text.Contains('\r')))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}:{ported.Line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} multi-line literal(s) carry CRLF, so the text they send differs from the " +
            "reference byte for byte. Convert the file to LF (the repository's .gitattributes pins it; an " +
            "editor overrode it):\n  " + string.Join("\n  ", offenders.Distinct().Take(25)));
    }

    /// <summary>
    /// The rule itself, since a working tree can be normalized while the next
    /// clone is not: without this file, git's own default on Windows
    /// (core.autocrlf=true) rewrites every source file on checkout.
    /// </summary>
    [Fact]
    public void The_repository_pins_lf_for_source_files()
    {
        var path = Path.Combine(RepoPaths.Root, ".gitattributes");
        Assert.True(File.Exists(path), $"{path} is missing: a fresh clone on Windows would check every " +
                                       "source file out as CRLF and change the bytes this app sends.");
        Assert.Contains("eol=lf", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>The separator the failure messages indent their lists with.</summary>
    private const string NewlineIndent = "\n  ";

    /// <summary>
    /// A source file holding a NUL byte is not text: git stores it as binary, so
    /// every diff of it reads "Bin N -> M bytes" and a review sees nothing. One
    /// reached this repository inside a string literal, where the compiler was
    /// happy and only the diff looked wrong.
    /// </summary>
    [Fact]
    public void No_source_file_carries_a_nul_byte()
    {
        var offenders = SourceFiles()
            .Where(static file => Array.IndexOf(File.ReadAllBytes(file), (byte)0) >= 0)
            .Select(file => Path.GetRelativePath(RepoPaths.Root, file))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} source file(s) hold a NUL byte, so git treats them as binary and their " +
            "diffs show nothing:" + NewlineIndent + string.Join(NewlineIndent, offenders.Take(25)));
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(RepoPaths.Src, "*.cs", SearchOption.AllDirectories)
            .Where(static file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
