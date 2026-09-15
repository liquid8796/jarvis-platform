using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.Cli.Repl.Input;

/// <summary>Which of the three completions the caret is inside.</summary>
internal enum AutocompleteKind
{
    None,

    /// <summary>A leading <c>/</c>: the command menu.</summary>
    Command,

    /// <summary>An <c>@</c> token: a file mention.</summary>
    FileMention,

    /// <summary>A path token inside <c>!</c> shell mode.</summary>
    Path,
}

/// <summary>One row of the completion popup.</summary>
internal sealed record AutocompleteItem(string Value, string Label, string? Description = null, string? Alias = null);

/// <summary>
/// The completion the caret sits in: the token being completed, where it starts
/// in the text, the rows and the highlighted one. Accepting a row replaces the
/// token, which is the reference's behaviour for all three menus — the command
/// menu completes the composer with <c>/name </c> rather than running it.
/// </summary>
internal sealed record Autocomplete(
    AutocompleteKind Kind,
    string Token,
    int TokenStart,
    IReadOnlyList<AutocompleteItem> Items,
    int Index = 0)
{
    public AutocompleteItem? Selected => Items.Count == 0 ? null : Items[Math.Clamp(Index, 0, Items.Count - 1)];

    public Autocomplete WithIndex(int index) =>
        this with { Index = Items.Count == 0 ? 0 : ((index % Items.Count) + Items.Count) % Items.Count };

    public Autocomplete Next() => WithIndex(Index + 1);

    public Autocomplete Previous() => WithIndex(Index - 1);

    /// <summary>The composer text and caret after accepting the highlighted row.</summary>
    public (string Text, int Offset) Accept(string text, int caret)
    {
        if (Selected is not { } item)
        {
            return (text, caret);
        }

        var replacement = Kind switch
        {
            AutocompleteKind.Command => "/" + item.Value + " ",
            AutocompleteKind.FileMention => "@" + item.Value + (item.Value.EndsWith('/') ? "" : " "),
            _ => item.Value + (item.Value.EndsWith('/') ? "" : " "),
        };
        var head = text[..TokenStart];
        var tail = caret <= text.Length ? text[caret..] : "";
        return (head + replacement + tail, head.Length + replacement.Length);
    }
}

/// <summary>
/// Builds the completion for wherever the caret is. Pure: the file lookup is a
/// delegate, so the tests drive it without touching a disk.
/// </summary>
internal static class AutocompleteEngine
{
    /// <summary>The reference's popup height.</summary>
    public const int MaxRows = 10;

    public static Autocomplete? For(
        string text,
        int caret,
        IReadOnlyList<SlashMenuItem> commands,
        Func<string, IReadOnlyList<string>> listPaths)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (text.StartsWith('/') && !text[..caret].Contains(' ', StringComparison.Ordinal))
        {
            var query = text[1..caret];
            var rows = SlashMenuFilter.Filter(commands, query)
                .Take(MaxRows)
                .Select(item => new AutocompleteItem(
                    item.Label,
                    item.Label,
                    item.SkillDescription,
                    SlashMenuFilter.MatchedAlias(item.Aliases, query)))
                .ToList();
            return rows.Count == 0 ? null : new Autocomplete(AutocompleteKind.Command, query, 0, rows);
        }

        int start = TokenStart(text, caret);
        var token = text[start..caret];
        if (token.StartsWith('@'))
        {
            var rows = Paths(listPaths, token[1..]);
            return rows.Count == 0
                ? null
                : new Autocomplete(AutocompleteKind.FileMention, token[1..], start, rows);
        }

        if (text.TrimStart().StartsWith('!') && token.Length > 0 && start > 0)
        {
            var rows = Paths(listPaths, token);
            return rows.Count == 0 ? null : new Autocomplete(AutocompleteKind.Path, token, start, rows);
        }

        return null;
    }

    private static IReadOnlyList<AutocompleteItem> Paths(
        Func<string, IReadOnlyList<string>> listPaths, string prefix) =>
        [.. listPaths(prefix).Take(MaxRows).Select(path => new AutocompleteItem(path, path))];

    private static int TokenStart(string text, int caret)
    {
        int i = caret;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1]))
        {
            i--;
        }

        return i;
    }

    /// <summary>
    /// The file lookup the REPL uses: entries under the working directory whose
    /// path starts with the typed prefix, directories marked with a trailing
    /// slash so the next keystroke keeps completing inside them.
    /// </summary>
    public static IReadOnlyList<string> ListPaths(string workingDirectory, string prefix)
    {
        try
        {
            var normalized = prefix.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            var directoryPart = slash < 0 ? "" : normalized[..(slash + 1)];
            var namePart = slash < 0 ? normalized : normalized[(slash + 1)..];
            var directory = Path.GetFullPath(
                directoryPart.Length == 0 ? workingDirectory : Path.Combine(workingDirectory, directoryPart));
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var results = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') && !namePart.StartsWith('.'))
                {
                    continue;
                }

                if (!name.StartsWith(namePart, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isDirectory = Directory.Exists(entry);
                results.Add(directoryPart + name + (isDirectory ? "/" : ""));
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
