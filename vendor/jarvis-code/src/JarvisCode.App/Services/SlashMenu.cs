using System.Collections.Generic;
using System.Linq;

namespace JarvisCode.App.Services;

/// <summary>
/// The row kinds the reference's slash-command menu models. Four of the
/// reference's nine reach the ccd surface — its builder pushes <c>skill</c>,
/// <c>button</c>, <c>section-header</c> and <c>loading</c> rows and nothing
/// else — plus <c>separator</c>, which shares every rule with the two other
/// non-selectable kinds. The remaining four (<c>submenu</c>,
/// <c>connector-tool</c>, <c>checkbox</c>, <c>toggle</c>, <c>search-input</c>)
/// belong to the chat and Cowork menus built on the same engine.
/// </summary>
public enum SlashMenuItemKind
{
    Skill,
    Button,
    Separator,
    SectionHeader,
    Loading,
}

/// <summary>One row of the slash-command menu.</summary>
public sealed record SlashMenuItem
{
    public SlashMenuItemKind Kind { get; init; } = SlashMenuItemKind.Skill;

    /// <summary>The command's bare name — the menu shows it without the leading slash the user already typed.</summary>
    public string Label { get; init; } = "";

    /// <summary>The id a skill row is selected by; the reference defaults it to the label.</summary>
    public string SkillId { get; init; } = "";

    /// <summary>Shown in the description card beside the highlighted row, never inside the row.</summary>
    public string SkillDescription { get; init; } = "";

    /// <summary>The frontmatter argument-hint, carried into the composer on selection.</summary>
    public string ArgumentHint { get; init; } = "";

    /// <summary>Alternate names; a matching one is shown in parentheses after the label.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Right-aligned caption on a skill row.</summary>
    public string? Subtitle { get; init; }

    /// <summary>The plugin a skill came from, if any: it qualifies the name for matching.</summary>
    public string? SourcePluginName { get; init; }

    /// <summary>The repository a skill came from, if any: it qualifies the name for matching.</summary>
    public string? SourceRepo { get; init; }

    /// <summary>Whether a button row consumes the text after its name, which lets it win a spaced query outright.</summary>
    public bool AcceptsArgs { get; init; }

    /// <summary>What running this row does. Non-selectable kinds carry none.</summary>
    public Func<string, Task>? OnAction { get; init; }
}

/// <summary>
/// The reference slash-command menu's filter, ordering and navigation, ported
/// from the desktop's <c>shared-10-3-tqq7pk.js</c> (Claude 1.40609.0.0): its
/// <c>ix</c>, <c>rx</c>, <c>lx</c>, <c>cx</c>, <c>ux</c>, <c>mx</c>,
/// <c>px</c>, <c>gx</c>, <c>yx</c>, <c>wx</c> and <c>kx</c>.
/// </summary>
public static class SlashMenuFilter
{
    /// <summary>The Fuse options the reference builds its index with.</summary>
    public static readonly FuzzyOptions Options = new() { Threshold = 0.3, Location = 0, Distance = 100 };

    /// <summary>The weighted fields the reference searches, in its order.</summary>
    public static readonly IReadOnlyList<FuzzyKey> Keys =
    [
        new("label", 3),
        new("qualified", 3),
        new("aliases", 3),
        new("parts", 2),
        new("source", 2),
        new("description", 0.5),
    ];

    /// <summary>Past this many characters the reference stops matching and shows nothing.</summary>
    public const int MaxQueryLength = 50;

    /// <summary>How many words of a description the reference indexes.</summary>
    public const int DescriptionWordLimit = 20;

    private const string DescriptionKey = "description";

    /// <summary>The characters a label is split on to give the <c>parts</c> field.</summary>
    private static readonly char[] PartSeparators = [':', '_', '-'];

    /// <summary>Rows that are shown but cannot be selected (the reference's <c>ix</c>).</summary>
    public static bool IsNonSelectable(SlashMenuItem item) =>
        item.Kind is SlashMenuItemKind.Separator or SlashMenuItemKind.SectionHeader or SlashMenuItemKind.Loading;

    /// <summary>
    /// The identity a selection survives a list change by (the reference's
    /// <c>gx</c>): a skill is its id plus its routing, and nothing else has one.
    /// This build never routes a row, so the routing half is always empty —
    /// which is what the reference also produces for an unshadowed command.
    /// </summary>
    public static string? Identity(SlashMenuItem? item) =>
        item is { Kind: SlashMenuItemKind.Skill } ? item.SkillId + ":" : null;

    /// <summary>The plugin or repository that qualifies a row's name (the reference's <c>rx</c>).</summary>
    internal static string SourceOf(SlashMenuItem item) =>
        item.Kind == SlashMenuItemKind.Skill ? item.SourcePluginName ?? item.SourceRepo ?? "" : "";

    /// <summary>The first alias that starts with the query (the reference's <c>mx</c>), shown in parentheses.</summary>
    public static string? MatchedAlias(IReadOnlyList<string> aliases, string query) =>
        string.IsNullOrEmpty(query)
            ? null
            : aliases.FirstOrDefault(alias => alias.ToLowerInvariant().StartsWith(query, StringComparison.Ordinal));

    /// <summary>The first case-insensitive occurrence of the query, which is the only run the reference bolds.</summary>
    public static (int Start, int End)? HighlightRange(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return null;
        }

        var at = text.ToLowerInvariant().IndexOf(query, StringComparison.Ordinal);
        return at < 0 ? null : (at, at + query.Length);
    }

    /// <summary>The next selectable row at or after <paramref name="from"/> (the reference's <c>yx</c>).</summary>
    public static int NextSelectable(IReadOnlyList<SlashMenuItem> items, int from)
    {
        var next = from + 1;
        while (next < items.Count && IsNonSelectable(items[next]))
        {
            next++;
        }

        return next < items.Count ? next : from;
    }

    /// <summary>The previous selectable row (the reference's <c>wx</c>).</summary>
    public static int PreviousSelectable(IReadOnlyList<SlashMenuItem> items, int from)
    {
        var previous = from - 1;
        while (previous >= 0 && IsNonSelectable(items[previous]))
        {
            previous--;
        }

        return previous >= 0 ? previous : from;
    }

    /// <summary>The row a freshly opened menu starts on (the reference's <c>kx</c>).</summary>
    public static int FirstSelectable(IReadOnlyList<SlashMenuItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (!IsNonSelectable(items[i]))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether a row still answers what was typed, by the looser rule the
    /// reference applies when Enter arrives on a row the user never moved to:
    /// the name, an alias, the source, or the qualified name either contains
    /// the query or is the word the query's arguments follow.
    /// </summary>
    public static bool LooselyMatches(SlashMenuItem? item, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        if (item is null || IsNonSelectable(item))
        {
            return false;
        }

        var label = (item.Label ?? "").ToLowerInvariant();
        var candidates = new List<string> { label };
        if (item.Kind == SlashMenuItemKind.Skill)
        {
            candidates.AddRange(item.Aliases.Select(static alias => alias.ToLowerInvariant()));
        }

        var source = SourceOf(item).ToLowerInvariant();
        if (source.Length > 0)
        {
            candidates.Add(source);
            candidates.Add(source + ":" + label);

            var colon = query.IndexOf(':');
            if (colon > 0
                && source.Contains(query[..colon], StringComparison.Ordinal)
                && label.Contains(query[(colon + 1)..], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return candidates.Any(candidate =>
            candidate.Length > 0
            && (candidate.Contains(query, StringComparison.Ordinal)
                || query.StartsWith(candidate + " ", StringComparison.Ordinal)));
    }

    /// <summary>A row prepared for matching (the reference's <c>cx</c>).</summary>
    internal sealed record Indexed(
        SlashMenuItem Item,
        string Label,
        string Qualified,
        IReadOnlyList<string> Parts,
        string Source,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> Description);

    internal static Indexed Describe(SlashMenuItem item)
    {
        var label = (item.Label ?? "").ToLowerInvariant();
        var source = SourceOf(item).ToLowerInvariant();
        var parts = label.Split(PartSeparators, StringSplitOptions.RemoveEmptyEntries);
        var aliases = item.Kind == SlashMenuItemKind.Skill
            ? item.Aliases.Select(static alias => alias.ToLowerInvariant()).ToList()
            : [];

        return new Indexed(
            item,
            label,
            source.Length > 0 ? source + ":" + label : label,
            parts.Length > 1 ? parts : [],
            source,
            aliases,
            DescriptionWords(item));
    }

    /// <summary>The first twenty word-ish runs of a skill's description (the reference's <c>lx</c>).</summary>
    private static IReadOnlyList<string> DescriptionWords(SlashMenuItem item)
    {
        if (item.Kind != SlashMenuItemKind.Skill || string.IsNullOrEmpty(item.SkillDescription))
        {
            return [];
        }

        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in item.SkillDescription.ToLowerInvariant())
        {
            // JavaScript's \W is ASCII: anything outside [A-Za-z0-9_] splits.
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
                if (words.Count == DescriptionWordLimit)
                {
                    return words;
                }
            }
        }

        if (current.Length > 0 && words.Count < DescriptionWordLimit)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    internal static IReadOnlyList<string> ValuesFor(Indexed record, string key) => key switch
    {
        "label" => [record.Label],
        "qualified" => [record.Qualified],
        "aliases" => record.Aliases,
        "parts" => record.Parts,
        "source" => [record.Source],
        DescriptionKey => record.Description,
        _ => [],
    };

    /// <summary>
    /// The visible rows for a query (the reference's <c>px</c>). The optional
    /// <paramref name="usageRank"/> is the reference's third parameter; the ccd
    /// menu calls this with two arguments, so its rank is a constant zero and
    /// the last sort tier never fires there.
    /// </summary>
    public static IReadOnlyList<SlashMenuItem> Filter(
        IReadOnlyList<SlashMenuItem> items,
        string query,
        Func<string, double>? usageRank = null)
    {
        if (string.IsNullOrEmpty(query))
        {
            return items;
        }

        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0 || q.Length > MaxQueryLength)
        {
            return [];
        }

        // A spaced query whose first word names a button that takes arguments
        // resolves to that button alone, so its arguments are not searched for.
        var space = q.IndexOf(' ');
        if (space > 0)
        {
            var first = q[..space];
            var button = items.FirstOrDefault(item =>
                item.Kind == SlashMenuItemKind.Button
                && item.AcceptsArgs
                && string.Equals(item.Label.ToLowerInvariant(), first, StringComparison.Ordinal));
            if (button is not null)
            {
                return [button];
            }
        }

        var records = items.Where(item => !IsNonSelectable(item)).Select(Describe).ToList();
        var index = new FuzzyIndex<Indexed>(records, Keys, ValuesFor, Options);

        var results = index.Search(q).Where(result => !MatchedDescriptionOnly(result, q)).ToList();
        var strong = results.Where(result => ContainsQuery(result.Item, q)).ToList();
        return [.. Order(strong.Count > 0 ? strong : results, q, usageRank).Select(result => result.Item.Item)];
    }

    /// <summary>
    /// A result whose only matching field was the description, and whose
    /// description does not actually contain the query, is a fuzzy accident.
    /// </summary>
    private static bool MatchedDescriptionOnly(FuzzyResult<Indexed> result, string query) =>
        result.Matches.Count != 0
        && !result.Matches.Any(match => !string.Equals(match.Key, DescriptionKey, StringComparison.Ordinal))
        && !result.Item.Description.Any(word => word.Contains(query, StringComparison.Ordinal));

    /// <summary>Whether a row contains the query outright, rather than only fuzzily.</summary>
    private static bool ContainsQuery(Indexed record, string query)
    {
        var colon = query.IndexOf(':');
        if (colon != -1
            && record.Source.Length > 0
            && record.Source.Contains(query[..colon], StringComparison.Ordinal)
            && record.Label.Contains(query[(colon + 1)..], StringComparison.Ordinal))
        {
            return true;
        }

        return record.Qualified.Contains(query, StringComparison.Ordinal)
            || record.Aliases.Any(alias => alias.Contains(query, StringComparison.Ordinal))
            || record.Description.Any(word => word.Contains(query, StringComparison.Ordinal));
    }

    private static List<FuzzyResult<Indexed>> Order(
        List<FuzzyResult<Indexed>> results,
        string query,
        Func<string, double>? usageRank)
    {
        var ranks = new double[results.Count];
        if (usageRank is not null)
        {
            for (var i = 0; i < results.Count; i++)
            {
                ranks[i] = usageRank(results[i].Item.Qualified);
            }
        }

        // JavaScript's sort is stable, so a full tie keeps Fuse's own order.
        var order = Enumerable.Range(0, results.Count).ToList();
        order.Sort((a, b) =>
        {
            var compared = Compare(results[a], results[b], query, ranks[a], ranks[b]);
            return compared != 0 ? compared : a.CompareTo(b);
        });

        return [.. order.Select(i => results[i])];
    }

    private static int Compare(
        FuzzyResult<Indexed> left,
        FuzzyResult<Indexed> right,
        string query,
        double leftRank,
        double rightRank)
    {
        var leftLabel = left.Item.Label;
        var rightLabel = right.Item.Label;

        var exact = string.Equals(leftLabel, query, StringComparison.Ordinal);
        if (exact != string.Equals(rightLabel, query, StringComparison.Ordinal))
        {
            return exact ? -1 : 1;
        }

        var aliasExact = left.Item.Aliases.Contains(query, StringComparer.Ordinal);
        if (aliasExact != right.Item.Aliases.Contains(query, StringComparer.Ordinal))
        {
            return aliasExact ? -1 : 1;
        }

        var leftPrefix = leftLabel.StartsWith(query, StringComparison.Ordinal);
        var rightPrefix = rightLabel.StartsWith(query, StringComparison.Ordinal);
        if (leftPrefix != rightPrefix)
        {
            return leftPrefix ? -1 : 1;
        }

        if (leftPrefix && rightPrefix && leftLabel.Length != rightLabel.Length)
        {
            return leftLabel.Length - rightLabel.Length;
        }

        var leftAlias = left.Item.Aliases.FirstOrDefault(alias => alias.StartsWith(query, StringComparison.Ordinal));
        var rightAlias = right.Item.Aliases.FirstOrDefault(alias => alias.StartsWith(query, StringComparison.Ordinal));
        if ((leftAlias is not null) != (rightAlias is not null))
        {
            return leftAlias is not null ? -1 : 1;
        }

        if (leftAlias is not null && rightAlias is not null && leftAlias.Length != rightAlias.Length)
        {
            return leftAlias.Length - rightAlias.Length;
        }

        var leftQualified = left.Item.Qualified.StartsWith(query, StringComparison.Ordinal);
        var rightQualified = right.Item.Qualified.StartsWith(query, StringComparison.Ordinal);
        if (leftQualified != rightQualified)
        {
            return leftQualified ? -1 : 1;
        }

        if (leftQualified && rightQualified && left.Item.Qualified.Length != right.Item.Qualified.Length)
        {
            return left.Item.Qualified.Length - right.Item.Qualified.Length;
        }

        var leftBucket = (int)Math.Floor(10 * left.Score);
        var rightBucket = (int)Math.Floor(10 * right.Score);
        return leftBucket != rightBucket ? leftBucket - rightBucket : rightRank.CompareTo(leftRank);
    }
}
