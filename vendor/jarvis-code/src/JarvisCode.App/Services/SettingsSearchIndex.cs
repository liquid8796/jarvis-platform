namespace JarvisCode.App.Services;

/// <summary>
/// One searchable row of the settings dialog, in the shape of the reference's own
/// search index (ion-dist <c>ca25db325-CwyIN-8d.js</c>: <c>{section, label, id}</c>
/// per row, with a few rows carrying a <c>keyword</c> alias).
/// </summary>
public sealed record SettingsSearchRow(string Section, string Label, string? Keyword = null);

/// <summary>A section the dialog can open: its id, the group it sits in and its nav label.</summary>
public sealed record SettingsSearchSection(string Id, string Group, string Name, IReadOnlyList<string> Keywords);

/// <summary>One result the search popup lists.</summary>
public sealed record SettingsSearchHit(
    string Kind,
    SettingsSearchSection Section,
    string? Via,
    int MoreCount = 0);

/// <summary>
/// The reference settings dialog's search, ported from the modal's own matcher
/// (ion-dist <c>cc011544b-BHyJJRs7.js</c>, its <c>xe</c>): a query ranks a
/// section by its name first and by its rows second, an exact match beats a
/// prefix beats a word-subsequence beats a scattered one, and the results are
/// section hits with up to three matching rows shown beneath and a
/// "+N more" line for the rest. Pure, so the ranking is testable.
/// </summary>
public static class SettingsSearchIndex
{
    /// <summary>The reference's <c>Se</c>: extra words a section answers to.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SectionKeywords =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["connectors"] = ["mcp", "integrations"],
            ["desktop/extensions"] = ["mcp"],
            ["usage"] = ["overage", "extra usage", "spend limit", "usage credits"],
            ["customize-connectors"] = ["mcp", "integrations", "tools"],
            ["customize-skills"] = ["custom instructions", "agent skills"],
            ["customize-plugins"] = ["marketplace", "extensions"],
        };

    /// <summary>
    /// Every searchable row of this build's settings, in the reference's own index
    /// shape — one <c>{section, label}</c> per row, in page order. The reference's
    /// list (ion-dist <c>ca25db325-CwyIN-8d.js</c>, its <c>b</c>) is the model; the
    /// rows here are the ones this build actually renders, so a search can only
    /// find something a page shows.
    /// </summary>
    public static readonly IReadOnlyList<SettingsSearchRow> Rows =
    [
        new("general", "Avatar"),
        new("general", "Full name"),
        new("general", "What should Jarvis call you?"),
        new("general", "What best describes your work?"),
        new("general", "Instructions for Jarvis"),
        new("general", "Appearance"),
        new("general", "Chat font"),
        new("general", "Motion"),
        new("general", "New chat view"),
        new("general", "Notifications"),
        new("general", "Response completions"),
        new("general", "Code notifications"),
        new("general", "Code permission requests"),
        new("general", "Default model"),
        new("general", "Effort"),
        new("general", "Reminder text overrides", "client_data"),
        new("general", "Override source"),
        new("general", "Override file"),
        new("general", "Web search"),
        new("general", "Search endpoint"),
        new("general", "Shell timeout (seconds)"),
        new("providers", "Providers", "api key"),
        new("permissions", "Default mode"),
        new("permissions", "Global rules"),
        new("usage", "Time range"),
        new("usage", "Chart metric"),
        new("claude-code", "Light code theme"),
        new("claude-code", "Dark code theme"),
        new("claude-code", "Code font"),
        new("claude-code", "Interface font"),
        new("claude-code", "Transcript text size"),
        new("claude-code", "Transcript width"),
        new("claude-code", "Allow bypass permissions mode"),
        new("claude-code", "Dynamic workflows"),
        new("claude-code", "Permission requests"),
        new("claude-code", "Questions"),
        new("claude-code", "Task complete"),
        new("claude-code", "Notification sound"),
        new("claude-code", "Draw attention on notifications"),
        new("claude-code", "Archive inactive sessions"),
        new("claude-code", "Worktree location"),
        new("claude-code", "Browser"),
        new("claude-code", "Browser tools"),
        new("claude-code", "Open links in built-in browser"),
        new("claude-code", "Persist sessions"),
        new("claude-code", "Allowed sites"),
        new("claude-code", "Android Emulator"),
        new("claude-code", "Pull requests"),
        new("claude-code", "Branch prefix"),
        new("claude-code", "Create pull requests automatically"),
        new("claude-code", "Create as draft"),
        new("claude-code", "Auto-archive after PR merge or close"),
        new("claude-code", "Plugins"),
        new("import", "Import"),
        new("import", "Export"),
        new("import", "Import history"),
        new("themes", "Mode"),
        new("themes", "Theme"),
        new("features", "Computer use"),
        new("features", "Teammate mode"),
        new("fingerprints", "Attribution block"),
        new("desktop", "Run on startup"),
        new("desktop", "Quick Entry keyboard shortcut"),
        new("desktop", "System tray"),
        new("desktop", "Keep computer awake"),
        new("desktop", "Browser use"),
        new("desktop", "Allow all browser actions"),
        new("desktop", "Computer use"),
        new("desktop", "Enable computer use"),
        new("desktop", "Unhide apps when Jarvis finishes"),
        new("desktop", "Denied apps"),
        new("desktop", "Close to tray"),
        new("desktop", "Start in tray"),
        new("desktop", "Explorer context menu"),
        new("desktop/extensions", "Extensions", "mcp"),
        new("desktop/extensions", "Enable auto-updates for extensions"),
        new("desktop/developer", "Local MCP servers"),
        new("desktop/debug", "Debug"),
    ];

    /// <summary>How many rows a section hit shows before folding the rest into "+N more".</summary>
    public const int RowsShownPerSection = 3;

    private static string Normalize(string text) =>
        text.Trim().ToLowerInvariant();

    private static string Squash(string text) => text.Replace(" ", "");

    /// <summary>
    /// The reference's rank: 0 exact, 1 prefix, 2 every query word starts a word
    /// (or a run of words) of the candidate, 3 every query word appears somewhere,
    /// -1 no match. Spaces are ignored in the first two tiers.
    /// </summary>
    public static int Rank(string query, string candidate)
    {
        var q = Normalize(query);
        var squashed = Squash(q);
        var words = q.Length == 0 ? [] : q.Split(' ');
        var c = Normalize(candidate);
        var cs = Squash(c);
        if (cs == squashed)
        {
            return 0;
        }

        if (cs.StartsWith(squashed, StringComparison.Ordinal))
        {
            return 1;
        }

        if (!words.All(word => cs.Contains(word, StringComparison.Ordinal)))
        {
            return -1;
        }

        var parts = c.Split(' ');
        var runs = parts.Select((_, i) => string.Concat(parts.Skip(i))).ToList();
        return words.All(word => runs.Any(run => run.StartsWith(word, StringComparison.Ordinal))) ? 2 : 3;
    }

    private static bool KeywordMatch(string query, string keyword) =>
        Squash(Normalize(keyword)).Contains(Squash(Normalize(query)), StringComparison.Ordinal);

    /// <summary>
    /// Runs the query over the sections and their rows. An empty query lists every
    /// section, as the reference's popup does before anything is typed.
    /// </summary>
    public static IReadOnlyList<SettingsSearchHit> Search(
        IReadOnlyList<SettingsSearchSection> sections,
        IReadOnlyList<SettingsSearchRow> rows,
        string query)
    {
        var normalized = Normalize(query);
        if (Squash(normalized).Length == 0)
        {
            return [.. sections.Select(s => new SettingsSearchHit("hit", s, null))];
        }

        var showChildren = Squash(normalized).Length >= 2;
        var scored = new List<(SettingsSearchSection Section, int Score, bool NameMatched, List<string> Rows, string? Keyword)>();
        foreach (var section in sections)
        {
            var nameRank = Rank(query, section.Name);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var matches = rows
                .Where(r => r.Section == section.Id)
                .SelectMany(r => r.Keyword is null ? [(Text: r.Label, r.Label)] : new[] { (Text: r.Keyword, r.Label), (Text: r.Label, r.Label) })
                .Select(r => (r.Label, Rank: Rank(query, r.Text)))
                .Where(r => r.Rank >= 0)
                .OrderBy(r => r.Rank)
                .Where(r => seen.Add(r.Label))
                .Select(r => r.Label)
                .ToList();

            if (nameRank >= 0)
            {
                scored.Add((section, nameRank, true, matches, null));
                continue;
            }

            if (matches.Count > 0)
            {
                var first = rows.Where(r => r.Section == section.Id)
                    .Select(r => Rank(query, r.Keyword ?? r.Label))
                    .Concat(rows.Where(r => r.Section == section.Id).Select(r => Rank(query, r.Label)))
                    .Where(r => r >= 0)
                    .Min();
                scored.Add((section, 4 + first, false, matches, null));
                continue;
            }

            var keyword = section.Keywords.FirstOrDefault(k => KeywordMatch(query, k));
            if (keyword is not null)
            {
                scored.Add((section, 8, false, [], keyword));
            }
        }

        scored.Sort((a, b) => a.Score.CompareTo(b.Score));

        var used = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<SettingsSearchHit>();

        void AddChildren(SettingsSearchSection section, List<string> matched)
        {
            foreach (var label in matched.Take(RowsShownPerSection))
            {
                used.Add(label);
                hits.Add(new SettingsSearchHit("child", section, label));
            }

            var more = matched.Count - RowsShownPerSection;
            if (more > 0)
            {
                hits.Add(new SettingsSearchHit("more", section, null, more));
            }
        }

        foreach (var (section, _, nameMatched, matched, keyword) in scored)
        {
            if (keyword is not null)
            {
                hits.Add(new SettingsSearchHit("hit", section, keyword));
                continue;
            }

            if (nameMatched)
            {
                hits.Add(new SettingsSearchHit("hit", section, null));
                if (showChildren)
                {
                    AddChildren(section, matched.Where(m => !used.Contains(m)).ToList());
                }

                continue;
            }

            if (!showChildren)
            {
                var first = matched[0];
                if (!used.Add(first))
                {
                    continue;
                }

                hits.Add(new SettingsSearchHit("hit", section, first));
                continue;
            }

            var remaining = matched.Where(m => !used.Contains(m)).ToList();
            if (remaining.Count == 0)
            {
                continue;
            }

            if (remaining.Count == 1)
            {
                used.Add(remaining[0]);
                hits.Add(new SettingsSearchHit("hit", section, remaining[0]));
                continue;
            }

            hits.Add(new SettingsSearchHit("hit", section, null));
            AddChildren(section, remaining);
        }

        return hits;
    }
}
