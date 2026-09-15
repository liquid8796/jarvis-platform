using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The two session menus, checked against the reference's own row order (the ccd
/// chunk `ce4f4374c`'s `Z` for the sidebar row and `ca80fca8d`'s `ly` for the header).
/// </summary>
public sealed class SessionMenuModelTests
{
    private static IReadOnlyList<string> Labels(IReadOnlyList<SessionMenuRow> rows) =>
        [.. rows.Where(r => r.Kind != SessionMenuRowKind.Separator).Select(r => r.Label)];

    [Fact]
    public void Sidebar_row_lists_the_reference_order()
    {
        var rows = SessionMenuModel.ForSidebarRow(new SessionMenuContext
        {
            HasPr = true,
            HasRoutine = true,
            CanSplitView = true,
            Colors = [new SessionMenuChoice("", "Default", true)],
            TranscriptViews = [new SessionMenuChoice("Normal", "Normal", true)],
        });

        Assert.Equal(
            [
                "Open PR", "Go to routine", "Open in",
                "Pin", "Mark as unread", "Rename", "Color", "Transcript view",
                "Export", "Copy link", "Fork",
                "Move to group", "Archive", "Delete",
            ],
            Labels(rows));
    }

    [Fact]
    public void Mark_as_completed_replaces_the_read_row_only_while_the_session_waits()
    {
        var awaiting = SessionMenuModel.ForSidebarRow(new SessionMenuContext
        {
            HasReadState = false,
            IsAwaiting = true,
        });
        Assert.Contains("Mark as completed", Labels(awaiting));
        Assert.DoesNotContain("Mark as unread", Labels(awaiting));

        var read = SessionMenuModel.ForSidebarRow(new SessionMenuContext { IsUnread = true });
        Assert.Contains("Mark as read", Labels(read));
        Assert.DoesNotContain("Mark as completed", Labels(read));
    }

    [Fact]
    public void Header_menu_drops_pin_and_move_to_group_and_leads_with_the_panes()
    {
        var rows = SessionMenuModel.ForHeader(new SessionMenuContext
        {
            IsUnread = true,
            Panes = [new SessionMenuPane("tasks", "Background tasks", true)],
            Colors = [new SessionMenuChoice("", "Default", true)],
        });

        var labels = Labels(rows);
        Assert.Equal("Background tasks", labels[0]);
        Assert.DoesNotContain("Pin", labels);
        Assert.DoesNotContain("Move to group", labels);
        Assert.DoesNotContain("Mark as unread", labels);
        Assert.Equal(["Background tasks", "Open in", "Rename", "Color", "Export", "Copy link", "Fork",
            "Archive", "Delete"], labels);
    }

    [Fact]
    public void Archived_row_offers_unarchive_and_says_so_while_it_runs()
    {
        var rows = SessionMenuModel.ForSidebarRow(new SessionMenuContext { IsArchived = true });
        Assert.Contains("Unarchive", Labels(rows));

        var running = SessionMenuModel.ForSidebarRow(new SessionMenuContext
        {
            IsArchived = true,
            IsUnarchiving = true,
        });
        var row = running.Single(r => r.Action == SessionMenuAction.Unarchive);
        Assert.Equal("Unarchiving…", row.Label);
        Assert.True(row.Disabled);
    }

    [Fact]
    public void Letters_are_the_reference_map()
    {
        var rows = SessionMenuModel.ForSidebarRow(new SessionMenuContext { HasPr = true });
        Assert.Equal(SessionMenuAction.OpenPr, SessionMenuModel.ForShortcut(rows, 'g')!.Action);
        Assert.Equal(SessionMenuAction.Pin, SessionMenuModel.ForShortcut(rows, 'p')!.Action);
        Assert.Equal(SessionMenuAction.MarkUnread, SessionMenuModel.ForShortcut(rows, 'u')!.Action);
        Assert.Equal(SessionMenuAction.Rename, SessionMenuModel.ForShortcut(rows, 'r')!.Action);
        Assert.Equal(SessionMenuAction.Export, SessionMenuModel.ForShortcut(rows, 'e')!.Action);
        Assert.Equal(SessionMenuAction.CopyLink, SessionMenuModel.ForShortcut(rows, 'c')!.Action);
        Assert.Equal(SessionMenuAction.Fork, SessionMenuModel.ForShortcut(rows, 'f')!.Action);
        Assert.Equal(SessionMenuAction.Archive, SessionMenuModel.ForShortcut(rows, 'a')!.Action);
        Assert.Equal(SessionMenuAction.Delete, SessionMenuModel.ForShortcut(rows, 'd')!.Action);
    }

    [Fact]
    public void Move_to_group_numbers_its_rows_and_offers_ungrouped_only_when_there_is_one_to_clear()
    {
        var grouped = SessionMenuModel.ForSidebarRow(new SessionMenuContext
        {
            Groups = [("a", "Alpha"), ("b", "Beta")],
            CurrentGroupId = "a",
        }).Single(r => r.Kind == SessionMenuRowKind.Submenu && r.Label == "Move to group").Children!;

        Assert.Equal(["Alpha", "Beta", "Ungrouped", "New group…"],
            grouped.Where(r => r.Kind == SessionMenuRowKind.Item).Select(r => r.Label));
        Assert.Equal(["1", "2", "3", "4"],
            grouped.Where(r => r.Kind == SessionMenuRowKind.Item).Select(r => r.Shortcut));
        Assert.True(grouped.Single(r => r.Label == "Alpha").Checked);

        var ungrouped = SessionMenuModel.ForSidebarRow(new SessionMenuContext
        {
            Groups = [("a", "Alpha")],
        }).Single(r => r.Kind == SessionMenuRowKind.Submenu && r.Label == "Move to group").Children!;
        Assert.DoesNotContain("Ungrouped", ungrouped.Select(r => r.Label));
        Assert.Equal("2", ungrouped.Single(r => r.Label == "New group…").Shortcut);
    }

    [Fact]
    public void Multi_select_menu_counts_its_rows()
    {
        var rows = SessionMenuModel.ForSidebarRow(new SessionMenuContext { MultiCount = 3 });
        Assert.Equal(["Mark 3 as unread", "Move 3 to group", "Archive 3", "Delete 3"], Labels(rows));
        Assert.True(rows.Single(r => r.Action == SessionMenuAction.MultiDelete).Danger);
    }

    [Fact]
    public void Open_in_rows_are_numbered_in_the_reference_order()
    {
        var rows = SessionMenuModel.OpenInRows(new SessionMenuContext
        {
            CanSplitView = true,
            CanOpenInTerminal = true,
        });
        Assert.Equal(["Split view", "New window", "VS Code", "Terminal", "Explorer"], rows.Select(r => r.Label));
        Assert.Equal(["1", "2", "3", "4", "5"], rows.Select(r => r.Shortcut));
    }

    [Fact]
    public void A_menu_never_opens_or_closes_on_a_rule()
    {
        var rows = SessionMenuModel.ForSidebarRow(new SessionMenuContext
        {
            CanPin = false,
            CanRename = false,
            CanExport = false,
            CanCopyLink = false,
            CanFork = false,
            HasReadState = false,
            HideMoveToGroup = true,
            HasLocalPath = false,
            CanOpenInNewWindow = false,
        });
        Assert.NotEmpty(rows);
        Assert.NotEqual(SessionMenuRowKind.Separator, rows[0].Kind);
        Assert.NotEqual(SessionMenuRowKind.Separator, rows[^1].Kind);
    }
}
