namespace JarvisCode.App.Services;

/// <summary>What a row of a session menu does when picked.</summary>
public enum SessionMenuAction
{
    None,
    OpenPr,
    GoToRoutine,
    SplitView,
    NewWindow,
    OpenInVsCode,
    OpenInTerminal,
    OpenInExplorer,
    MoveUp,
    MoveDown,
    Pin,
    Unpin,
    MarkRead,
    MarkUnread,
    MarkCompleted,
    Rename,
    SetColor,
    SetTranscriptView,
    SetOutputStyle,
    EditOutputStyles,
    Export,
    CopyLink,
    Fork,
    MoveToGroup,
    NewGroup,
    Archive,
    Unarchive,
    Delete,
    TogglePane,
    MultiMarkUnread,
    MultiMoveToGroup,
    MultiArchive,
    MultiUnarchive,
    MultiDelete,
    MultiSetModel,
}

/// <summary>The shape of one row: a plain row, a rule or a submenu.</summary>
public enum SessionMenuRowKind
{
    Item,
    Separator,
    Submenu,
}

/// <summary>
/// One row of a session menu. <see cref="Shortcut"/> is the reference's single
/// letter or digit, live while the menu is open; <see cref="Argument"/> carries the
/// group id, colour key or pane key an action needs; <see cref="Checked"/> with
/// <see cref="Radio"/> is the reference's `checked`/`checkedRole`.
/// </summary>
public sealed record SessionMenuRow(
    SessionMenuRowKind Kind,
    string Label = "",
    SessionMenuAction Action = SessionMenuAction.None,
    string? Shortcut = null,
    string? Argument = null,
    bool Danger = false,
    bool Disabled = false,
    bool? Checked = null,
    bool Radio = false,
    string? Description = null,
    string? Icon = null,
    IReadOnlyList<SessionMenuRow>? Children = null)
{
    public static SessionMenuRow Separator { get; } = new(SessionMenuRowKind.Separator);
}

/// <summary>A pane row a menu can toggle: the reference's checkbox rows.</summary>
public sealed record SessionMenuPane(string Key, string Label, bool Open, string? Shortcut = null,
    string? Icon = null);

/// <summary>A choice in the Colour or Transcript view submenu.</summary>
public sealed record SessionMenuChoice(string Key, string Label, bool Checked);

/// <summary>
/// Everything the menu builder reads about one session. Each flag is the reference's
/// own gate: <see cref="CanPin"/> is its `canPin`, <see cref="IsAwaiting"/> its
/// `onAckAwaiting`, <see cref="HideMoveToGroup"/> its `hideMoveToGroup`.
/// </summary>
public sealed record SessionMenuContext
{
    public bool IsArchived { get; init; }
    public bool IsPinned { get; init; }

    /// <summary>The row carries a read state at all — the reference's `readState`.</summary>
    public bool HasReadState { get; init; } = true;

    public bool IsUnread { get; init; }

    /// <summary>The session waits on the user (the sidebar's yellow dot); offers "Mark as completed".</summary>
    public bool IsAwaiting { get; init; }

    public bool CanPin { get; init; } = true;
    public bool CanRename { get; init; } = true;
    public bool CanFork { get; init; } = true;
    public bool CanExport { get; init; } = true;
    public bool CanCopyLink { get; init; } = true;
    public bool CanArchive { get; init; } = true;
    public bool CanDelete { get; init; } = true;
    public bool CanSplitView { get; init; }
    public bool CanOpenInNewWindow { get; init; } = true;
    public bool CanOpenInTerminal { get; init; }
    public bool HasLocalPath { get; init; } = true;
    public bool HasPr { get; init; }
    public bool HasRoutine { get; init; }
    public bool HideMoveToGroup { get; init; }
    public bool IsUnarchiving { get; init; }

    /// <summary>Pinned rows can be reordered; the reference shows Move up / Move down for them.</summary>
    public bool CanMoveUp { get; init; }
    public bool CanMoveDown { get; init; }

    public string? CurrentGroupId { get; init; }
    public IReadOnlyList<(string Id, string Name)> Groups { get; init; } = [];

    public IReadOnlyList<SessionMenuChoice>? Colors { get; init; }
    public IReadOnlyList<SessionMenuChoice>? TranscriptViews { get; init; }

    /// <summary>
    /// The output styles this session may run at — the reference's Output style
    /// submenu, which sits between Transcript view and Export.
    /// </summary>
    public IReadOnlyList<SessionMenuChoice>? OutputStyles { get; init; }

    public IReadOnlyList<SessionMenuPane> Panes { get; init; } = [];

    /// <summary>Set for the Recents header's multi-select: how many rows the actions apply to.</summary>
    public int MultiCount { get; init; }
    public bool MultiUnarchive { get; init; }

    /// <summary>The models the multi-select's "Set model for {n}" submenu offers.</summary>
    public IReadOnlyList<SessionMenuChoice>? MultiModels { get; init; }
}

/// <summary>
/// The reference's session menu, row for row and in its order. The sidebar row's menu
/// is `Z` in the ccd chunk `ce4f4374c` (desktop 1.40609.1.0, offsets 2188-13500) and
/// the header's is `ly` in `ca80fca8d` (~301k); both surfaces render from the same rows
/// here so they cannot drift apart. Pure so the order is unit-testable.
/// </summary>
public static class SessionMenuModel
{
    /// <summary>The reference's own group labels for the Open in submenu.</summary>
    public const string OpenInLabel = "Open in";

    /// <summary>
    /// What a menu trigger is called to an assistive reader — the reference's
    /// unlabelled arm of its <c>Wz</c> pair ("More options" / "More options for
    /// {label}").
    /// </summary>
    public const string MoreOptionsLabel = "More options";

    public const string ContinueInLabel = "Continue in";

    /// <summary>
    /// The sidebar row's overflow / right-click menu (`Z`). Its order, gate for gate:
    /// Open PR - Go to routine - Open in - Move up - Move down - Pin/Unpin -
    /// Mark as read/unread or Mark as completed - Rename - Color - Transcript view -
    /// Export - Copy link - Fork - Move to group - pane rows - Archive/Unarchive - Delete.
    /// </summary>
    public static IReadOnlyList<SessionMenuRow> ForSidebarRow(SessionMenuContext c)
    {
        if (c.MultiCount > 0)
        {
            return ForMultiSelect(c);
        }

        var rows = new List<SessionMenuRow>();
        if (c.HasPr)
        {
            rows.Add(Item("Open PR", SessionMenuAction.OpenPr, "g"));
        }

        if (c.HasRoutine)
        {
            rows.Add(Item("Go to routine", SessionMenuAction.GoToRoutine));
        }

        var openIn = OpenInRows(c);
        if (openIn.Count > 0)
        {
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Submenu, OpenInLabel, Children: openIn));
            rows.Add(SessionMenuRow.Separator);
        }
        else if (c.HasPr || c.HasRoutine)
        {
            rows.Add(SessionMenuRow.Separator);
        }

        rows.AddRange(MiddleRows(c));

        if (!c.HideMoveToGroup)
        {
            rows.Add(SessionMenuRow.Separator);
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Submenu, "Move to group", Children: MoveToGroupRows(c)));
        }

        if (c.Panes.Count > 0)
        {
            rows.Add(SessionMenuRow.Separator);
            foreach (var pane in c.Panes)
            {
                rows.Add(Item(pane.Label, SessionMenuAction.TogglePane, argument: pane.Key));
            }
        }

        var archive = ArchiveRows(c);
        if (archive.Count > 0)
        {
            rows.Add(SessionMenuRow.Separator);
            rows.AddRange(archive);
        }

        return Tidy(rows);
    }

    /// <summary>
    /// The session header's overflow menu (`ly`): pane checkbox rows first, then Go to
    /// routine, Open in, and the same named block the sidebar carries - the header
    /// passes `canPin:false` and `hideMoveToGroup:true`, which is why neither appears,
    /// and it carries no read-state row at all.
    /// </summary>
    public static IReadOnlyList<SessionMenuRow> ForHeader(SessionMenuContext c)
    {
        var header = c with
        {
            CanPin = false,
            HideMoveToGroup = true,
            HasReadState = false,
            IsAwaiting = false,
            CanSplitView = false,
        };

        var rows = new List<SessionMenuRow>();
        foreach (var pane in header.Panes)
        {
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Item, pane.Label, SessionMenuAction.TogglePane,
                Shortcut: pane.Shortcut, Argument: pane.Key, Checked: pane.Open, Icon: pane.Icon));
        }

        if (header.Panes.Count > 0)
        {
            rows.Add(SessionMenuRow.Separator);
        }

        if (header.HasRoutine)
        {
            rows.Add(Item("Go to routine", SessionMenuAction.GoToRoutine));
        }

        var openIn = OpenInRows(header);
        if (openIn.Count > 0)
        {
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Submenu, OpenInLabel, Children: openIn));
            rows.Add(SessionMenuRow.Separator);
        }
        else if (header.HasRoutine)
        {
            rows.Add(SessionMenuRow.Separator);
        }

        rows.AddRange(NamedRows(header));

        var archive = ArchiveRows(header);
        if (archive.Count > 0)
        {
            rows.Add(SessionMenuRow.Separator);
            rows.AddRange(archive);
        }

        return Tidy(rows);
    }

    /// <summary>
    /// The Recents header's multi-select menu: Mark N as unread, Set model for N,
    /// Move N to group, Archive/Unarchive N, Delete N.
    /// </summary>
    private static IReadOnlyList<SessionMenuRow> ForMultiSelect(SessionMenuContext c)
    {
        var n = c.MultiCount;
        var rows = new List<SessionMenuRow>
        {
            Item($"Mark {n} as unread", SessionMenuAction.MultiMarkUnread, "u"),
        };
        if (c.MultiModels is { Count: > 0 })
        {
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Submenu, $"Set model for {n}",
                Children:
                [
                    .. c.MultiModels.Select(choice => new SessionMenuRow(SessionMenuRowKind.Item, choice.Label,
                        SessionMenuAction.MultiSetModel, Argument: choice.Key, Checked: choice.Checked,
                        Radio: true)),
                ]));
        }

        if (!c.HideMoveToGroup)
        {
            var groups = new List<SessionMenuRow>();
            var digit = 0;
            foreach (var (id, name) in c.Groups)
            {
                groups.Add(Item(name, SessionMenuAction.MultiMoveToGroup, Digit(digit++), argument: id));
            }

            if (c.Groups.Count > 0)
            {
                groups.Add(SessionMenuRow.Separator);
            }

            groups.Add(Item("Ungrouped", SessionMenuAction.MultiMoveToGroup, Digit(digit++)));
            groups.Add(SessionMenuRow.Separator);
            groups.Add(Item("New group…", SessionMenuAction.NewGroup, Digit(digit)));
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Submenu, $"Move {n} to group", Children: groups));
        }

        rows.Add(SessionMenuRow.Separator);
        rows.Add(c.MultiUnarchive
            ? Item($"Unarchive {n}", SessionMenuAction.MultiUnarchive, "a")
            : Item($"Archive {n}", SessionMenuAction.MultiArchive, "a"));
        rows.Add(Item($"Delete {n}", SessionMenuAction.MultiDelete, "d", danger: true));
        return rows;
    }

    /// <summary>
    /// The Open in submenu's rows in the reference's order (`g` in `cfba1e557`):
    /// Split view, New window, editors, Terminal, Cloud, Desktop app, Explorer.
    /// Rows carrying a <see cref="SessionMenuRow.Description"/> are its "continue-on"
    /// section, which is what splits the submenu into two labelled groups; this build
    /// has no such destination, so the section is always empty and the labels stay off.
    /// </summary>
    public static List<SessionMenuRow> OpenInRows(SessionMenuContext c)
    {
        var rows = new List<SessionMenuRow>();
        if (c.CanSplitView)
        {
            rows.Add(Item("Split view", SessionMenuAction.SplitView));
        }

        if (c.CanOpenInNewWindow)
        {
            rows.Add(Item("New window", SessionMenuAction.NewWindow));
        }

        if (c.HasLocalPath)
        {
            rows.Add(Item("VS Code", SessionMenuAction.OpenInVsCode));
        }

        if (c.CanOpenInTerminal)
        {
            rows.Add(Item("Terminal", SessionMenuAction.OpenInTerminal));
        }

        if (c.HasLocalPath)
        {
            rows.Add(Item("Explorer", SessionMenuAction.OpenInExplorer));
        }

        return AssignDigits(rows);
    }

    /// <summary>Splits the Open in rows the reference's way: plain rows, then the "continue-on" ones.</summary>
    public static (IReadOnlyList<SessionMenuRow> OpenIn, IReadOnlyList<SessionMenuRow> ContinueIn) SplitOpenIn(
        IReadOnlyList<SessionMenuRow> rows) =>
        ([.. rows.Where(r => r.Description is null)], [.. rows.Where(r => r.Description is not null)]);

    private static List<SessionMenuRow> MiddleRows(SessionMenuContext c)
    {
        var rows = new List<SessionMenuRow>();
        if (c.CanPin && c.CanMoveUp)
        {
            rows.Add(Item("Move up", SessionMenuAction.MoveUp));
        }

        if (c.CanPin && c.CanMoveDown)
        {
            rows.Add(Item("Move down", SessionMenuAction.MoveDown));
        }

        if (c.CanPin)
        {
            rows.Add(c.IsPinned ? Item("Unpin", SessionMenuAction.Unpin, "p") : Item("Pin", SessionMenuAction.Pin, "p"));
        }

        if (c.HasReadState)
        {
            rows.Add(c.IsUnread
                ? Item("Mark as read", SessionMenuAction.MarkRead, "u")
                : Item("Mark as unread", SessionMenuAction.MarkUnread, "u"));
        }
        else if (c.IsAwaiting)
        {
            rows.Add(Item("Mark as completed", SessionMenuAction.MarkCompleted, "u"));
        }

        rows.AddRange(NamedRows(c));
        return rows;
    }

    /// <summary>Rename, Color, Transcript view, Export, Copy link, Fork - shared by both menus.</summary>
    private static List<SessionMenuRow> NamedRows(SessionMenuContext c)
    {
        var rows = new List<SessionMenuRow>();
        if (c.CanRename)
        {
            rows.Add(Item("Rename", SessionMenuAction.Rename, "r"));
        }

        if (c.Colors is { Count: > 0 })
        {
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Submenu, "Color",
                Children:
                [
                    .. c.Colors.Select(choice => new SessionMenuRow(SessionMenuRowKind.Item, choice.Label,
                        SessionMenuAction.SetColor, Argument: choice.Key, Checked: choice.Checked, Radio: true)),
                ]));
        }

        if (c.TranscriptViews is { Count: > 0 })
        {
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Submenu, "Transcript view",
                Children:
                [
                    .. c.TranscriptViews.Select(choice => new SessionMenuRow(SessionMenuRowKind.Item, choice.Label,
                        SessionMenuAction.SetTranscriptView, Argument: choice.Key, Checked: choice.Checked, Radio: true)),
                ]));
        }

        if (c.OutputStyles is { Count: > 0 })
        {
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Submenu, "Output style",
                Children:
                [
                    .. c.OutputStyles.Select(choice => new SessionMenuRow(SessionMenuRowKind.Item, choice.Label,
                        SessionMenuAction.SetOutputStyle, Argument: choice.Key, Checked: choice.Checked,
                        Radio: true)),
                    Item("New style…", SessionMenuAction.EditOutputStyles),
                ]));
        }

        if (c.CanExport)
        {
            rows.Add(Item("Export", SessionMenuAction.Export, "e"));
        }

        if (c.CanCopyLink)
        {
            rows.Add(Item("Copy link", SessionMenuAction.CopyLink, "c"));
        }

        if (c.CanFork)
        {
            rows.Add(Item("Fork", SessionMenuAction.Fork, "f"));
        }

        return rows;
    }

    private static List<SessionMenuRow> MoveToGroupRows(SessionMenuContext c)
    {
        var rows = new List<SessionMenuRow>();
        var digit = 0;
        foreach (var (id, name) in c.Groups)
        {
            rows.Add(new SessionMenuRow(SessionMenuRowKind.Item, name, SessionMenuAction.MoveToGroup,
                Shortcut: Digit(digit++), Argument: id, Checked: c.CurrentGroupId == id, Radio: true));
        }

        if (c.CurrentGroupId is not null)
        {
            if (c.Groups.Count > 0)
            {
                rows.Add(SessionMenuRow.Separator);
            }

            rows.Add(new SessionMenuRow(SessionMenuRowKind.Item, "Ungrouped", SessionMenuAction.MoveToGroup,
                Shortcut: Digit(c.Groups.Count), Checked: false, Radio: true));
        }

        if (c.Groups.Count > 0 || c.CurrentGroupId is not null)
        {
            rows.Add(SessionMenuRow.Separator);
        }

        // The reference numbers the create row after every assignment row, which is
        // one per group plus the Ungrouped row when there is a group to clear.
        var assignments = c.Groups.Count + (c.CurrentGroupId is not null ? 1 : 0);
        rows.Add(Item("New group…", SessionMenuAction.NewGroup, Digit(assignments)));
        return rows;
    }

    private static List<SessionMenuRow> ArchiveRows(SessionMenuContext c)
    {
        var rows = new List<SessionMenuRow>();
        if (!c.IsArchived && c.CanArchive)
        {
            rows.Add(Item("Archive", SessionMenuAction.Archive, "a"));
        }
        else if (c.IsArchived && c.CanArchive)
        {
            rows.Add(c.IsUnarchiving
                ? Item("Unarchiving…", SessionMenuAction.Unarchive, "a", disabled: true)
                : Item("Unarchive", SessionMenuAction.Unarchive, "a"));
        }

        if (c.CanDelete)
        {
            rows.Add(Item("Delete", SessionMenuAction.Delete, "d", danger: true));
        }

        return rows;
    }

    /// <summary>
    /// The letter a row answers to while the menu is open - the reference's `wn`
    /// hotkey map, which skips a disabled row and takes the first match.
    /// </summary>
    public static SessionMenuRow? ForShortcut(IReadOnlyList<SessionMenuRow> rows, char key)
    {
        var letter = char.ToLowerInvariant(key).ToString();
        return rows.FirstOrDefault(r => r.Kind == SessionMenuRowKind.Item && !r.Disabled && r.Shortcut == letter);
    }

    private static SessionMenuRow Item(string label, SessionMenuAction action, string? shortcut = null,
        bool danger = false, bool disabled = false, string? argument = null, string? description = null) =>
        new(SessionMenuRowKind.Item, label, action, shortcut, argument, danger, disabled, Description: description);

    /// <summary>Digits 1-9 on the first nine rows of a submenu; nothing past that, like the reference's `On`.</summary>
    private static string? Digit(int index) => index < 9 ? (index + 1).ToString() : null;

    private static List<SessionMenuRow> AssignDigits(List<SessionMenuRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i] = rows[i] with { Shortcut = Digit(i) };
        }

        return rows;
    }

    /// <summary>No leading, trailing or doubled separators, whatever the gates left behind.</summary>
    private static IReadOnlyList<SessionMenuRow> Tidy(List<SessionMenuRow> rows)
    {
        var tidy = new List<SessionMenuRow>();
        foreach (var row in rows)
        {
            if (row.Kind == SessionMenuRowKind.Separator &&
                (tidy.Count == 0 || tidy[^1].Kind == SessionMenuRowKind.Separator))
            {
                continue;
            }

            tidy.Add(row);
        }

        while (tidy.Count > 0 && tidy[^1].Kind == SessionMenuRowKind.Separator)
        {
            tidy.RemoveAt(tidy.Count - 1);
        }

        return tidy;
    }
}
