using System.IO;
using System.Text;

namespace JarvisCode.Parity.Tests;

/// <summary>A string this UI renders, and the reference catalogue entry it came from.</summary>
internal readonly record struct UiStringEntry(string MessageId, string Source, string Text);

/// <summary>
/// Every string this UI renders that the reference desktop app also ships,
/// paired with the message id that carries it there.
///
/// <see cref="UiStringParityTests.PortedStrings"/> pins the handful whose
/// *behaviour* matters — the label a method returns for a given elapsed time,
/// the name an action reports. This manifest is the flat remainder: three
/// hundred window titles, buttons, empty states and notices that are only ever
/// literals, and which no accessor can be written for. Keeping them as data is
/// what makes the check exhaustive instead of anecdotal.
/// </summary>
internal static class UiStringManifest
{
    /// <summary>
    /// Below this a match is coincidence, not evidence: a two-letter word
    /// occurs somewhere in a 23,000-entry catalogue whatever we do.
    /// </summary>
    private const int MinimumLength = 4;

    /// <summary>The App's own sources — this is a check on the UI, not on the engine.</summary>
    private static readonly string[] ScannedRoots = ["JarvisCode.App"];

    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Manifest", "ui-strings.tsv");

    public static string SourcePath { get; } =
        Path.Combine(RepoPaths.ParityTests, "Manifest", "ui-strings.tsv");

    /// <summary>
    /// Matches that are not UI text: language names, JSON keys, folder names,
    /// registry values and permission-rule tool names that happen to spell a
    /// catalogue entry. Pinning one would make an unrelated reference rewording
    /// fail this suite, so they are excluded by name and by reason.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyIgnored = new(LoadIgnored);

    public static IReadOnlyDictionary<string, string> Ignored => LazyIgnored.Value;

    private static IReadOnlyDictionary<string, string> LoadIgnored()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Manifest", "ui-strings-ignored.tsv");
        var ignored = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return ignored;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length >= 2)
            {
                ignored[PortedTextDeltas.Decode(fields[0])] = fields[1];
            }
        }

        return ignored;
    }

    public static IReadOnlyList<UiStringEntry> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        var entries = new List<UiStringEntry>();
        foreach (var line in File.ReadAllLines(FilePath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length >= 3)
            {
                entries.Add(new UiStringEntry(fields[0], fields[1], PortedTextDeltas.Decode(fields[2])));
            }
        }

        return entries;
    }

    /// <summary>
    /// Scans the App's sources for text the reference catalogue also carries.
    ///
    /// The pairing is by exact text, which is what makes it mechanical: a string
    /// we wrote ourselves that happens to read like the reference's *is* the
    /// reference's, for the purpose of noticing that the reference changed it.
    /// </summary>
    public static IReadOnlyList<UiStringEntry> Scan(IReadOnlyDictionary<string, string> catalogue)
    {
        var ordered = catalogue.OrderBy(static p => p.Key, StringComparer.Ordinal).ToList();
        var idByText = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, text) in ordered)
        {
            // One text can carry several ids; the first in catalogue order is
            // taken so the manifest is stable between regenerations.
            idByText.TryAdd(text, id);
        }

        // A string this app rebranded reads "Jarvis" where the reference reads
        // "Claude", and would otherwise pair with nothing — which would quietly
        // drop it from the manifest and leave the reference free to reword it
        // unnoticed. Every verbatim spelling is indexed first, in its own pass,
        // so a rebranding can never take the slot a real catalogue entry owns.
        foreach (var (id, text) in ordered)
        {
            idByText.TryAdd(UiBrand.Apply(text), id);
        }

        var found = new SortedDictionary<(string Text, string Source), string>(Comparer);
        foreach (var root in ScannedRoots)
        {
            var directory = Path.Combine(RepoPaths.Src, root);
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is not (".cs" or ".xaml") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(RepoPaths.Src, file).Replace('\\', '/');
                var text = File.ReadAllText(file);
                var candidates = extension == ".cs"
                    ? PortedText.CSharpStrings(text).Select(static s => s.Constant).OfType<string>()
                    : PortedText.XamlStrings(text);

                foreach (var candidate in candidates)
                {
                    if (candidate.Length >= MinimumLength &&
                        candidate.Any(char.IsLetter) &&
                        !Ignored.ContainsKey(candidate) &&
                        idByText.TryGetValue(candidate, out var id))
                    {
                        found.TryAdd((candidate, relative), id);
                    }
                }
            }
        }

        // One row per string: the first source that renders it is enough to
        // point a failure at, and pinning every call site would churn the file
        // on every refactor.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<UiStringEntry>();
        foreach (var ((text, source), id) in found)
        {
            if (seen.Add(text))
            {
                entries.Add(new UiStringEntry(id, source, text));
            }
        }

        return [.. entries.OrderBy(static e => e.MessageId, StringComparer.Ordinal)];
    }

    public static void Write(string path, IReadOnlyList<UiStringEntry> entries, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new StringBuilder()
            .AppendLine("# Strings this UI renders that the reference desktop app also ships.")
            .AppendLine("# Columns: reference message id, the source that renders it, the text.")
            .AppendLine("# The text is what THIS app renders: where it names the assistant it says")
            .AppendLine("# Jarvis, and UiBrand compares it to the reference's \"Claude\" wording.")
            .AppendLine($"# Regenerated from the packaged Claude desktop app {version}.")
            .AppendLine("# JARVIS_APPROVE_UI_STRINGS=1 rewrites it.");
        foreach (var entry in entries)
        {
            builder.Append(entry.MessageId).Append('\t').Append(entry.Source).Append('\t')
                .AppendLine(PortedTextDeltas.Encode(entry.Text));
        }

        File.WriteAllText(path, builder.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }

    private static readonly IComparer<(string Text, string Source)> Comparer =
        Comparer<(string Text, string Source)>.Create(static (a, b) =>
        {
            int text = string.CompareOrdinal(a.Text, b.Text);
            return text != 0 ? text : string.CompareOrdinal(a.Source, b.Source);
        });
}
