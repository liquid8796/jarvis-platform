using System.Text;
using System.Text.RegularExpressions;
using JarvisCode.App.Services;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Enumerates the prompt appendices the desktop keeps in <c>app.asar</c> and
/// requires each to be carried or declared.
/// </summary>
/// <remarks>
/// The byte-corpus checks can only compare text this port already emits; they
/// cannot notice a section the reference appends that was never ported, because
/// there is no sentence to fail. That is how four of them went missing until
/// 2026-09-01. This reads the reference's own declarations instead: the
/// appendices sit as adjacent string literals that all open with a blank line,
/// so one window around a known member yields the set.
///
/// Scope is that block. <c>&lt;browser_surfaces&gt;</c> is built in the browser
/// chunk from an interpolated server-name prefix and is pinned by
/// <see cref="HostPromptSections"/>'s own text check instead.
/// </remarks>
public class HostPromptAppendixParityTests
{
    /// <summary>A member of the block, used to find the rest.</summary>
    private const string Anchor = "format them as markdown links so the user can click";

    /// <summary>
    /// `name="\n\n…"`, ``name=`\n\n…` `` or `name='\n\n…'` — an appendix is a
    /// literal that opens with an escaped blank line, which is how it joins onto
    /// the prompt. All three quote forms are matched because the minifier picks
    /// whichever the body needs fewest escapes for: 1.44121.2.0 moved the
    /// auto-fix-PR block to single quotes, and matching two forms silently
    /// dropped it out of the set instead of reporting a change.
    /// </summary>
    private static readonly Regex Appendix = new(
        @"=\s*(?:""((?:[^""\\]|\\.)*)""|`((?:[^`\\]|\\.)*)`|'((?:[^'\\]|\\.)*)')",
        RegexOptions.Compiled);

    private const int MinimumLength = 120;

    [ReferenceAppFact]
    public void Every_desktop_prompt_appendix_is_carried_or_declared()
    {
        var found = Appendices();
        Assert.True(found.Count >= 4,
            $"expected the desktop's appendix block to hold at least four sections, found {found.Count}. " +
            "The block has moved: re-find it from a live session's system prompt and update the anchor.");

        // The auto-fix-PR block is the one this port does not carry, so it is
        // accounted for by its manifest row alone. Asserting that keeps the
        // declaration path live: without the row it would be unaccounted, which
        // is what makes the check above discriminate rather than always pass.
        Assert.Contains(found, section => !IsCarried(section) && IsDeclared(section));

        var unaccounted = found
            .Where(static section => !IsCarried(section) && !IsDeclared(section))
            .ToList();

        Assert.True(unaccounted.Count == 0, new StringBuilder()
            .AppendLine($"{unaccounted.Count} section(s) the desktop appends to the system prompt are")
            .AppendLine("neither carried by HostPromptSections nor declared in")
            .AppendLine("Deltas/reference-surface-deltas.tsv as a prompt row:")
            .AppendLine(string.Join("\n", unaccounted.Select(static s => "  " + Preview(s))))
            .ToString());
    }

    [ReferenceAppFact]
    public void The_sections_this_port_carries_are_all_in_that_block()
    {
        // The other direction: a section we ship that the reference no longer
        // appends would be this app talking to itself.
        var found = Appendices();
        string[] carried =
        [
            HostPromptSections.FileLinks,
            HostPromptSections.ShellFences,
        ];

        foreach (var section in carried)
        {
            Assert.True(
                found.Any(f => f.Contains(FirstSentence(section), StringComparison.Ordinal)),
                $"this port appends a section the reference's block no longer has: {Preview(section)}");
        }
    }

    /// <summary>The appendix literals, unescaped, in bundle order.</summary>
    private static IReadOnlyList<string> Appendices()
    {
        var corpus = ReferenceCorpora.Desktop;
        var offsets = corpus.Occurrences(Anchor, limit: 4);
        Assert.True(offsets.Count > 0,
            "the desktop bundle no longer contains the markdown-link appendix; the block has moved");

        var window = corpus.Window(offsets[0], before: 2_000, after: 9_000);
        var sections = new List<string>();
        foreach (Match match in Appendix.Matches(window))
        {
            var raw = match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value;
            if (!raw.StartsWith(@"\n\n", StringComparison.Ordinal) || raw.Length < MinimumLength)
            {
                continue;
            }

            sections.Add(Unescape(raw));
        }

        return sections;
    }

    private static bool IsCarried(string section)
    {
        var sentence = FirstSentence(section);
        return HostPromptSections.FileLinks.Contains(sentence, StringComparison.Ordinal)
            || HostPromptSections.ShellFences.Contains(sentence, StringComparison.Ordinal)
            // The terminal-dialog note is adapted here: two of the four commands
            // it names are real in this app, so its opening words are matched
            // rather than the whole sentence.
            || section.Contains("Terminal-dialog slash commands", StringComparison.Ordinal);
    }

    private static bool IsDeclared(string section) =>
        SurfaceDeltas.Rows.Any(row =>
            row.Kind == "prompt" && section.Contains(row.Name, StringComparison.Ordinal));

    private static string FirstSentence(string section)
    {
        var trimmed = section.Trim();
        var stop = trimmed.IndexOf(". ", StringComparison.Ordinal);
        return stop > 20 ? trimmed[..stop] : trimmed[..Math.Min(80, trimmed.Length)];
    }

    private static string Preview(string section)
    {
        var flat = section.Trim().Replace('\n', ' ');
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    private static string Unescape(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\' || i + 1 >= raw.Length)
            {
                builder.Append(raw[i]);
                continue;
            }

            var next = raw[++i];
            switch (next)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'u' when i + 4 < raw.Length
                              && int.TryParse(raw.AsSpan(i + 1, 4),
                                  System.Globalization.NumberStyles.HexNumber, null, out var code):
                    builder.Append((char)code);
                    i += 4;
                    break;
                default: builder.Append(next); break;
            }
        }

        return builder.ToString();
    }
}
