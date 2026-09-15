using System.IO;
using System.Text;

namespace JarvisCode.Parity.Tests;

/// <summary>One place the App still spells "Claude", and the file it is in.</summary>
internal readonly record struct BrandOccurrence(string Source, string Text);

/// <summary>
/// This app is Jarvis Code, and the assistant it runs is named Jarvis in front of
/// the user. Renaming the strings was a one-off edit; this is what keeps them
/// renamed.
///
/// Every literal in the App that still spells "Claude" must be declared in
/// <c>Manifest/brand-exceptions.tsv</c> with the reason it is not the assistant's
/// name — Anthropic's models, the separate reference product this app imports
/// from, a wire name, a theme token, or prompt text the model itself reads.
/// Without the check the rebrand lasts only as long as somebody remembers it: a
/// button, notice or settings description ported from the reference would put
/// "Claude" back in front of the user and nothing would say so.
/// </summary>
public sealed class AssistantBrandTests
{
    /// <summary>The brand the reference uses, which no user-visible string here may adopt.</summary>
    private const string ReferenceBrand = "Claude";

    [Fact]
    public void Every_Claude_literal_in_the_app_is_declared()
    {
        var undeclared = Scan()
            .Where(static occurrence => !BrandExceptions.Covers(occurrence))
            .ToList();

        Assert.True(undeclared.Count == 0, Explain(undeclared));
    }

    /// <summary>
    /// The other direction: a row for a string the App no longer has is stale
    /// bookkeeping. Left in, the file stops being a record of decisions and
    /// becomes a list of exemptions nobody can tell are still needed — and a
    /// future string matching a dead row would be waved through by it.
    /// </summary>
    [Fact]
    public void Declared_brand_exceptions_are_still_in_the_app()
    {
        var present = Scan().ToList();
        var stale = BrandExceptions.Reasons.Keys
            .Where(row => row.Text.Length == 0
                ? !present.Any(o => o.Source == row.Source)
                : !present.Contains(row))
            .Select(row => row.Text.Length == 0
                ? $"{row.Source}: the whole file, which no longer spells \"Claude\""
                : $"{row.Source}: {Preview(row.Text)}")
            .ToList();

        Assert.True(stale.Count == 0,
            $"{stale.Count} row(s) in Manifest/brand-exceptions.tsv name text the App no longer " +
            "contains — remove them:\n  " + string.Join("\n  ", stale.Take(20)));
    }

    /// <summary>Every declared exception carries a real reason, not a placeholder.</summary>
    [Fact]
    public void Declared_brand_exceptions_carry_a_reason()
    {
        var empty = BrandExceptions.Reasons
            .Where(static row => row.Value.Trim().Length < 10 ||
                                 row.Value.StartsWith("REVIEW", StringComparison.Ordinal))
            .Select(row => row.Key.Text.Length == 0
                ? $"{row.Key.Source}: the whole file"
                : $"{row.Key.Source}: {Preview(row.Key.Text)}")
            .ToList();

        Assert.True(empty.Count == 0,
            $"{empty.Count} row(s) in Manifest/brand-exceptions.tsv have no reason. Each must say why " +
            "that \"Claude\" is not the assistant's name here:\n  " + string.Join("\n  ", empty.Take(15)));
    }

    /// <summary>
    /// Every place in the App that spells the reference's brand, one line at a
    /// time.
    ///
    /// C# is read as literals: the parser skips comments, so a note explaining
    /// what the reference does needs no row, and fixed runs rather than whole
    /// literals key an interpolated string by text that does not move when the
    /// surrounding expression does. XAML is read line by line instead — its
    /// literal extractor knows six attribute names, which is right for pairing
    /// strings with a catalogue but would let element content and any seventh
    /// attribute through unseen, and "unseen" is the one answer this check may
    /// not give.
    /// </summary>
    internal static IEnumerable<BrandOccurrence> Scan()
    {
        var found = new SortedSet<BrandOccurrence>(Comparer);
        // Both front-ends: the CLI's REPL renders as much user-facing text as
        // the app's surfaces do, so the same rule has to reach it.
        foreach (var file in new[] { "JarvisCode.App", "JarvisCode.Cli" }
                     .SelectMany(project => Directory.EnumerateFiles(
                         Path.Combine(RepoPaths.Src, project), "*.*", SearchOption.AllDirectories)))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".xaml") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            if (!source.Contains(ReferenceBrand, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(RepoPaths.Src, file).Replace('\\', '/');
            IEnumerable<string> runs = extension == ".cs"
                ? PortedText.CSharpStrings(source).SelectMany(static s => s.FixedRuns())
                : [source];

            foreach (var line in runs.SelectMany(static run => run.Split('\n')))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains(ReferenceBrand, StringComparison.Ordinal))
                {
                    found.Add(new BrandOccurrence(relative, trimmed));
                }
            }
        }

        return found;
    }

    private static string Explain(IReadOnlyList<BrandOccurrence> undeclared)
    {
        var builder = new StringBuilder()
            .AppendLine($"{undeclared.Count} literal(s) in the App still spell \"Claude\". This app is " +
                        "Jarvis Code: if the user reads it, rename it to Jarvis.");
        // Listed generously rather than at the suite's usual fifteen: this
        // failure is a work list, and a row cannot be written for a line the
        // message elided.
        foreach (var occurrence in undeclared.Take(40))
        {
            builder.AppendLine($"  {occurrence.Source}: {Preview(occurrence.Text)}");
        }

        if (undeclared.Count > 40)
        {
            builder.AppendLine($"  … and {undeclared.Count - 40} more");
        }

        return builder
            .AppendLine("If the name is right where it stands — Anthropic's models, the reference product")
            .AppendLine("this app imports from, a wire name or theme token, or prompt text the model reads —")
            .AppendLine("add it to Manifest/brand-exceptions.tsv with that reason.")
            .ToString();
    }

    private static string Preview(string text) =>
        text.Length > 110 ? $"\"{text[..110]}…\"" : $"\"{text}\"";

    private static readonly IComparer<BrandOccurrence> Comparer =
        Comparer<BrandOccurrence>.Create(static (a, b) =>
        {
            int source = string.CompareOrdinal(a.Source, b.Source);
            return source != 0 ? source : string.CompareOrdinal(a.Text, b.Text);
        });
}

/// <summary>
/// The places the App says "Claude" on purpose, and why — the same shape as
/// <c>Deltas/ported-text-deltas.tsv</c> (file, reason, text) so the two read the
/// same way.
///
/// A row with an empty text column exempts a whole file. That is for the files
/// that hold nothing but prompt text the model reads, where every line is
/// already pinned to the reference verbatim by
/// <see cref="PortedTextParityTests"/>: listing three hundred characters of the
/// /init prompt per line would bury the rows that are actually decisions.
/// </summary>
internal static class BrandExceptions
{
    private static readonly Lazy<IReadOnlyDictionary<BrandOccurrence, string>> Entries = new(Load);

    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Manifest", "brand-exceptions.tsv");

    public static IReadOnlyDictionary<BrandOccurrence, string> Reasons => Entries.Value;

    /// <summary>Whether a row covers this occurrence, by file or by exact text.</summary>
    public static bool Covers(BrandOccurrence occurrence) =>
        Entries.Value.ContainsKey(occurrence) ||
        Entries.Value.ContainsKey(new BrandOccurrence(occurrence.Source, ""));

    private static IReadOnlyDictionary<BrandOccurrence, string> Load()
    {
        var rows = new Dictionary<BrandOccurrence, string>();
        if (!File.Exists(Path))
        {
            return rows;
        }

        foreach (var raw in File.ReadAllLines(Path))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            // Two fields is the file-level form. Read that way rather than as a
            // trailing empty column, because an editor that strips trailing
            // whitespace would otherwise turn a row into a silently ignored one.
            var fields = raw.Split('\t');
            if (fields.Length >= 2)
            {
                var text = fields.Length >= 3 ? PortedTextDeltas.Decode(fields[2]) : "";
                rows[new BrandOccurrence(fields[0], text)] = fields[1];
            }
        }

        return rows;
    }
}
