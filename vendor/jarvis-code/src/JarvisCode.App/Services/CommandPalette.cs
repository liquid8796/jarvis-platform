namespace JarvisCode.App.Services;

/// <summary>
/// The groups a palette row can sit in, in the order the reference renders them
/// (its group table in <c>c2771e1f6-Czf-iSjS.js</c>): the untitled "other" group
/// first, then Navigation, Chat, Project, Task, Session, Settings, General.
/// </summary>
public enum PaletteGroup
{
    Other,
    Navigation,
    Chat,
    Project,
    Task,
    Session,
    Settings,
    General,
}

/// <summary>One command the palette can run.</summary>
public sealed record PaletteAction(
    string Key,
    PaletteGroup Group,
    string Description,
    Action Execute)
{
    /// <summary>The chord shown on the right of the row, already in this platform's spelling.</summary>
    public string? Shortcut { get; init; }

    /// <summary>Extra words the row matches on but does not show — the reference's searchHints.</summary>
    public IReadOnlyList<string> SearchHints { get; init; } = [];

    /// <summary>A dimmed second line, which the per-setting rows carry.</summary>
    public string? SecondaryText { get; init; }

    /// <summary>
    /// The reference's <c>queryOnly</c>: the row exists only once something has
    /// been typed, so an empty actions list stays short.
    /// </summary>
    public bool QueryOnly { get; init; }
}

/// <summary>A row in the rendered palette: either a command or a session to open.</summary>
public abstract record PaletteRow(string Id);

public sealed record PaletteActionRow(PaletteAction Action) : PaletteRow(Action.Key);

/// <summary>A recent session, with the badges the reference draws beside its title.</summary>
public sealed record PaletteSessionRow(
    string SessionId,
    string Title,
    string? Snippet,
    bool IsScheduled,
    int RunCount,
    int? PullRequestNumber,
    Action Open) : PaletteRow(SessionId);

/// <summary>A titled run of rows. A null title renders without a header, as the "other" group does.</summary>
public sealed record PaletteGroupView(string Id, string? Title, IReadOnlyList<PaletteRow> Items);

/// <summary>Which half of the palette the user is in.</summary>
public enum PaletteMode
{
    /// <summary>Quick actions and recents — what Ctrl+K opens on.</summary>
    Default,

    /// <summary>Every command, grouped. Tab enters it; Backspace on an empty query leaves it.</summary>
    Actions,
}

/// <summary>
/// The command palette's model, ported from the reference desktop's
/// <c>CommandPaletteBody</c> (<c>c2771e1f6-Czf-iSjS.js</c>) and the command
/// registry it reads (<c>gP</c> in <c>shared-6-D8hZtQZb.js</c>).
///
/// Free of WPF: the grouping, the ordering and the filter are what a wrong port
/// gets wrong, and they are unit-tested here rather than eyeballed on screen.
/// </summary>
public static class CommandPalette
{
    // ---- chrome ----

    public const string DefaultPlaceholder = "Search or start a session";
    public const string ActionsPlaceholder = "Search actions";
    public const string DefaultListboxLabel = "Command palette options";
    public const string ActionsListboxLabel = "Actions";
    public const string QuickActionsTitle = "Quick actions";

    /// <summary>The header over the matched commands in the default mode; clicking it enters actions mode.</summary>
    public const string ActionsGroupTitle = "Actions";
    public const string RecentsTitle = "Recents";
    public const string EmptyLabel = "No results found";
    public const string SearchingDeeperLabel = "Searching deeper...";
    public const string CloseLabel = "Close";
    public const string UntitledSession = "Untitled session";
    public const string ScheduledBadge = "Scheduled";
    public const string FooterSelect = "Select";
    public const string FooterActions = "Actions";
    public const string FooterOpenMenu = "Open menu";

    /// <summary>The reference's own cap: the results region never grows past this.</summary>
    public const double MaxResultsHeight = 440;

    /// <summary>
    /// How many rows the reference lets the recents half contribute in the
    /// default mode. (Its search mode allows 25; this port has no search mode.)
    /// </summary>
    public const int MaxOrganicItems = 7;

    /// <summary>A query longer than this matches nothing, as the reference's <c>fa</c> has it.</summary>
    public const int MaxQueryLength = 50;

    public static string GroupTitle(PaletteGroup group) => group switch
    {
        PaletteGroup.Navigation => "Navigation",
        PaletteGroup.Chat => "Chat",
        PaletteGroup.Project => "Project",
        PaletteGroup.Task => "Task",
        PaletteGroup.Session => "Session",
        PaletteGroup.Settings => "Settings",
        PaletteGroup.General => "General",
        // The reference's first group carries no header at all.
        _ => "",
    };

    /// <summary>"{n} results available" / "No results found", which the palette announces.</summary>
    public static string ResultsAnnouncement(int count) => count switch
    {
        0 => EmptyLabel,
        1 => "1 result available",
        _ => $"{count} results available",
    };

    /// <summary>The reference's plural row under a scheduled recent.</summary>
    public static string RunCountLabel(int runs) => runs == 1 ? "1 run" : $"{runs} runs";

    public static string PullRequestLabel(int number) => $"PR #{number}";

    // ---- the registry ----

    /// <summary>
    /// A registry row: what the reference's <c>gP</c> table says about a command,
    /// with the delegate left to whoever can run it.
    /// </summary>
    public sealed record PaletteEntry(
        string Key,
        PaletteGroup Group,
        string Description,
        string? Shortcut = null,
        IReadOnlyList<string>? SearchHints = null);

    /// <summary>
    /// The reference's command registry (<c>gP</c> in <c>shared-6-D8hZtQZb.js</c>),
    /// filtered the way its own <c>isAvailable</c> filter does: a row it marks
    /// <c>hiddenFromCommandPalette</c> keeps its chord but never shows as a row,
    /// and a row nothing here can execute is dropped rather than listed dead.
    /// The rows this build cannot carry are declared in the parity suite's
    /// surface manifest.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> Registry { get; } =
    [
        new("command_palette", PaletteGroup.Other, "Command menu", "ctrl+k"),
        new("chats", PaletteGroup.Navigation, "All chats"),
        new("claude_code", PaletteGroup.Navigation, "Code"),
        new("new_conversation", PaletteGroup.Navigation, "New chat", "cmd+shift+o"),
        new("scheduled_tasks", PaletteGroup.Navigation, "Scheduled tasks", SearchHints: ["routines", "cron"]),
        new("file_upload", PaletteGroup.Chat, "Upload file", "ctrl+u"),
        new("model_selector", PaletteGroup.Chat, "Open model menu", "cmd+shift+i"),
        // The reference's other chat rows. Its extended_thinking, toggle_dictation
        // and btw_side_chat entries set hiddenFromCommandPalette, so they keep
        // their chords (Ctrl+Shift+E, Ctrl+D, Ctrl+;) and take no row here.
        new("incognito", PaletteGroup.Chat, "Incognito chat", "cmd+shift+i"),
        new("delete_chat", PaletteGroup.Chat, "Delete chat", SearchHints: ["remove", "trash"]),
        new("archive_code_session", PaletteGroup.Session, "Archive session", "cmd+alt+a", ["hide"]),
        new("unarchive_code_session", PaletteGroup.Session, "Unarchive session", SearchHints: ["restore"]),
        new("delete_code_session", PaletteGroup.Session, "Delete session", SearchHints: ["remove", "trash"]),
        new("rename_code_session", PaletteGroup.Session, "Rename session", "cmd+alt+r"),
        new("pin_code_session", PaletteGroup.Session, "Pin session", "cmd+alt+p", ["star", "favorite"]),
        new("unpin_code_session", PaletteGroup.Session, "Unpin session", "cmd+alt+p", ["star", "favorite"]),
        new("fork_code_session", PaletteGroup.Session, "Fork session", "cmd+alt+o", ["duplicate", "clone"]),
        new("new_code_session_from_current", PaletteGroup.Session, "New session with current settings",
            "cmd+shift+n", ["duplicate"]),
        new("jump_prev_prompt", PaletteGroup.Session, "Jump to previous prompt", "alt+up"),
        new("jump_next_prompt", PaletteGroup.Session, "Jump to next prompt", "alt+down"),
        new("copy_code_session_link", PaletteGroup.Session, "Copy session link", "cmd+alt+l", ["url"]),
        new("toggle_read_code_session", PaletteGroup.Session, "Mark session as read/unread", "cmd+alt+u"),
        new("open_code_session_pr", PaletteGroup.Session, "Open session PR", "cmd+alt+g"),
        new("toggle_sidebar", PaletteGroup.General, "Toggle sidebar", "ctrl+."),
        new("shortcuts_modal", PaletteGroup.General, "Keyboard shortcuts", "ctrl+/"),
    ];

    /// <summary>"Settings → {group} → {section}", the row one settings section gets.</summary>
    public static string SettingsRowLabel(string group, string section) => $"Settings → {group} → {section}";

    /// <summary>"Customize → {section}".</summary>
    public static string CustomizeRowLabel(string section) => $"Customize → {section}";

    /// <summary>The hint every settings row matches on, beside its own words.</summary>
    public const string SettingsSearchHint = "settings";

    // ---- filtering ----

    private static readonly FuzzyKey[] ActionKeys =
    [
        // The reference's own weights: what the row says, then the hidden hints.
        new("descriptionLower", 0.7),
        new("hintsLower", 0.3),
    ];

    private static readonly FuzzyOptions ActionOptions = new()
    {
        // Its shared search helper passes no threshold for the action list, so
        // Fuse runs at the helper's own default of 0.2 with location ignored.
        Threshold = 0.2,
        IgnoreLocation = true,
        Location = 0,
        Distance = 100,
    };

    /// <summary>
    /// The reference's action filter: an empty query keeps registry order and
    /// drops the query-only rows, a query over 50 characters matches nothing,
    /// and anything else is Fuse with the two weighted keys — sorted with the
    /// rows whose description begins inside the query first, then by score.
    /// </summary>
    public static IReadOnlyList<PaletteAction> Filter(IReadOnlyList<PaletteAction> actions, string query)
    {
        var trimmed = (query ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return [.. actions.Where(static a => !a.QueryOnly)];
        }

        if (trimmed.Length > MaxQueryLength)
        {
            return [];
        }

        var index = new FuzzyIndex<PaletteAction>(actions, ActionKeys, Values, ActionOptions);
        var results = index.Search(trimmed);

        return
        [
            .. results
                .OrderByDescending(r => StartsWithQuery(r, trimmed))
                .ThenBy(static r => r.Score)
                .ThenBy(static r => r.Index)
                .Select(static r => r.Item)
        ];

        static IReadOnlyList<string> Values(PaletteAction action, string key) => key switch
        {
            "descriptionLower" => [action.Description.ToLowerInvariant()],
            "hintsLower" => [.. action.SearchHints.Select(static h => h.ToLowerInvariant())],
            _ => [],
        };
    }

    /// <summary>
    /// The reference's <c>startsWithQuery</c> tiebreak: a match on the *first*
    /// key whose matched range begins at index 0. Fuse marks a text position as
    /// matched whenever that character occurs anywhere in the pattern, so a
    /// range starting at zero is exactly "the value's first character is one of
    /// the query's".
    /// </summary>
    private static bool StartsWithQuery(FuzzyResult<PaletteAction> result, string query)
    {
        var first = ActionKeys[0].Name;
        foreach (var match in result.Matches)
        {
            if (match.Key == first && match.Value.Length > 0 && query.Contains(match.Value[0]))
            {
                return true;
            }
        }

        return false;
    }

    // ---- grouping ----

    /// <summary>
    /// Actions mode: one group per <see cref="PaletteGroup"/> that has rows, in
    /// the reference's order, with the empty ones dropped.
    /// </summary>
    public static IReadOnlyList<PaletteGroupView> GroupActions(IReadOnlyList<PaletteAction> actions)
    {
        var groups = new List<PaletteGroupView>();
        foreach (var group in Enum.GetValues<PaletteGroup>())
        {
            var rows = actions.Where(a => a.Group == group).Select(static a => (PaletteRow)new PaletteActionRow(a)).ToList();
            if (rows.Count > 0)
            {
                var title = GroupTitle(group);
                groups.Add(new PaletteGroupView(
                    group.ToString().ToLowerInvariant(), title.Length == 0 ? null : title, rows));
            }
        }

        return groups;
    }

    /// <summary>
    /// Default mode. With nothing typed the quick-action group leads and the
    /// recents follow; once something is typed the matched actions come first,
    /// then the quick action, then the rest — the reference's own reordering.
    /// </summary>
    public static IReadOnlyList<PaletteGroupView> GroupDefault(
        IReadOnlyList<PaletteAction> matchedActions,
        IReadOnlyList<PaletteRow> quickActions,
        IReadOnlyList<PaletteRow> recents,
        string query)
    {
        var quick = new PaletteGroupView("send_message_group", QuickActionsTitle, quickActions);
        var rest = new List<PaletteGroupView>();
        if (recents.Count > 0)
        {
            rest.Add(new PaletteGroupView("recents", RecentsTitle, recents));
        }

        if ((query ?? "").Trim().Length == 0)
        {
            return [quick, .. rest];
        }

        var actionRows = matchedActions.Select(static a => (PaletteRow)new PaletteActionRow(a)).ToList();
        if (actionRows.Count == 0)
        {
            return [quick, .. rest];
        }

        // The reference titles the matched-actions block "Actions" and makes
        // that header a button into the actions mode.
        return [new PaletteGroupView("actions", ActionsGroupTitle, actionRows), quick, .. rest];
    }

    /// <summary>Every row across the groups, in the order the arrow keys walk them.</summary>
    public static IReadOnlyList<PaletteRow> Flatten(IReadOnlyList<PaletteGroupView> groups) =>
        [.. groups.SelectMany(static g => g.Items)];
}
