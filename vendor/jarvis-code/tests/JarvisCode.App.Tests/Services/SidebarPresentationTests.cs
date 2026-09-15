using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The sidebar's grouping, sorting and filtering, plus the landing's branch order and
/// the composer's "+" menu — the four plain models the Code chrome renders from.
/// </summary>
public sealed class SidebarPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static SidebarSessionInput Row(string id, string folder = @"C:\work\alpha", int hoursAgo = 1) =>
        new(id, id, folder, Now.AddDays(-3), Now.AddHours(-hoursAgo));

    private static IReadOnlyList<SidebarSection> Build(
        IReadOnlyList<SidebarSessionInput> rows,
        SidebarFilterState state,
        IReadOnlyList<(string, string, bool)>? groups = null) =>
        SidebarPresentation.Build(rows, state, groups ?? [], "", static _ => false, Now);

    [Fact]
    public void Pinned_rows_lead_under_their_own_header()
    {
        var sections = Build(
            [Row("a"), Row("b") with { IsPinned = true }],
            new SidebarFilterState { GroupBy = "none" });
        Assert.Equal("Pinned", sections[0].Title);
        Assert.Equal(["b"], sections[0].Rows.Select(r => r.Id));
        Assert.Equal(["a"], sections[1].Rows.Select(r => r.Id));
    }

    [Fact]
    public void Grouping_by_folder_makes_one_section_per_directory()
    {
        var sections = Build(
            [Row("a", @"C:\work\alpha"), Row("b", @"C:\work\beta"), Row("c", @"C:\work\alpha")],
            new SidebarFilterState { GroupBy = "project" });
        Assert.Equal(["Pinned", "alpha", "beta"], sections.Select(s => s.Title));
        Assert.Equal(["a", "c"], sections[1].Rows.Select(r => r.Id).Order());
    }

    [Fact]
    public void Grouping_by_date_uses_the_reference_three_headings_in_order()
    {
        var sections = Build(
            [Row("old", hoursAgo: 96), Row("today", hoursAgo: 1), Row("yesterday", hoursAgo: 26)],
            new SidebarFilterState { GroupBy = "date" });
        Assert.Equal(["Pinned", "Today", "Yesterday", "Older"], sections.Select(s => s.Title));
    }

    [Fact]
    public void Grouping_by_state_uses_the_reference_buckets_in_its_own_order()
    {
        var sections = Build(
            [
                Row("done"),
                Row("review") with { PrState = PrDisplayState.Open },
                Row("working") with { IsRunning = true },
                Row("blocked") with { NeedsInput = true },
            ],
            new SidebarFilterState { GroupBy = "state" });
        Assert.Equal(["Pinned", "Needs input", "Ready for review", "Working", "Completed"], sections.Select(s => s.Title));
        Assert.Equal([SidebarPresentation.PinnedKey, "state-blocked", "state-review", "state-working", "state-done"], sections.Select(s => s.Key));
    }

    [Fact]
    public void A_merged_pull_request_is_not_a_review_bucket()
    {
        var sections = Build(
            [Row("a") with { PrState = PrDisplayState.Merged }],
            new SidebarFilterState { GroupBy = "state" });
        Assert.Equal(["Pinned", "Completed"], sections.Select(s => s.Title));
    }

    [Fact]
    public void The_state_grouping_drops_rows_past_its_last_activity_window()
    {
        var rows = new[] { Row("fresh", hoursAgo: 2), Row("stale", hoursAgo: 24 * 20) };
        var week = Build(rows, new SidebarFilterState { GroupBy = "state" });
        Assert.Equal(["fresh"], week.SelectMany(s => s.Rows).Select(r => r.Id));

        var all = Build(rows, new SidebarFilterState { GroupBy = "state", StateActivityDays = 0 });
        Assert.Equal(["fresh", "stale"], all.SelectMany(s => s.Rows).Select(r => r.Id).Order());

        // The window is the State grouping's alone, which is the only place the
        // reference offers it.
        var flat = Build(rows, new SidebarFilterState { GroupBy = "none" });
        Assert.Equal(2, flat.SelectMany(s => s.Rows).Count());
    }

    [Fact]
    public void The_first_section_carries_the_header_filter_and_menu()
    {
        var sections = Build(
            [Row("a") with { GroupId = "g1" }, Row("b")],
            new SidebarFilterState { GroupBy = "custom" },
            [("g1", "Work", false)]);
        Assert.True(sections[0].CarriesHeaderTrailing);
        Assert.All(sections.Skip(1), s => Assert.False(s.CarriesHeaderTrailing));
    }

    [Fact]
    public void Recents_is_a_bucket_label_only_while_the_list_is_ungrouped()
    {
        var flat = Build([Row("a")], new SidebarFilterState { GroupBy = "none" });
        Assert.Equal(["Pinned", "Recents"], flat.Select(s => s.Title));
        Assert.Equal("recents", flat[1].Key);

        var byFolder = Build([Row("a")], new SidebarFilterState { GroupBy = "project" });
        Assert.DoesNotContain("Recents", byFolder.Select(s => s.Title));
    }

    [Fact]
    public void A_side_session_is_filed_under_its_parent()
    {
        var (ordered, parentOf) = SidebarPresentation.Nest(
        [
            Row("parent"),
            Row("other"),
            Row("child") with { SideParentId = "parent" },
        ]);
        Assert.Equal(["parent", "child", "other"], ordered.Select(r => r.Id));
        Assert.Equal("parent", parentOf["child"]);

        var runs = SidebarPresentation.Runs(ordered, parentOf);
        Assert.Equal([null, "parent", null], runs.Select(r => r.ParentId));
        Assert.Equal(["child"], runs[1].Rows.Select(r => r.Id));
    }

    [Fact]
    public void A_grandchild_is_filed_under_the_root_of_its_chain()
    {
        var (ordered, parentOf) = SidebarPresentation.Nest(
        [
            Row("root"),
            Row("child") with { SideParentId = "root" },
            Row("grandchild") with { SideParentId = "child" },
        ]);
        Assert.Equal(["root", "child", "grandchild"], ordered.Select(r => r.Id));
        Assert.Equal("root", parentOf["grandchild"]);
    }

    [Fact]
    public void A_parent_the_bucket_does_not_hold_leaves_the_row_flat()
    {
        var (ordered, parentOf) = SidebarPresentation.Nest([Row("child") with { SideParentId = "elsewhere" }]);
        Assert.Empty(parentOf);
        Assert.Equal(["child"], ordered.Select(r => r.Id));
    }

    [Fact]
    public void A_bucket_folds_past_twenty_rows_and_show_more_reveals_another_twenty()
    {
        var rows = Enumerable.Range(0, 45).Select(i => Row($"s{i:00}")).ToList();
        var (first, hidden) = SidebarPresentation.Truncate(rows, extraRevealed: 0);
        Assert.Equal(20, first.Count);
        Assert.Equal(25, hidden);

        var (second, stillHidden) = SidebarPresentation.Truncate(rows, extraRevealed: 20);
        Assert.Equal(40, second.Count);
        Assert.Equal(5, stillHidden);

        var (all, none) = SidebarPresentation.Truncate(rows, extraRevealed: 40);
        Assert.Equal(45, all.Count);
        Assert.Equal(0, none);
    }

    [Fact]
    public void Custom_groups_keep_their_order_and_end_with_ungrouped()
    {
        var sections = Build(
            [Row("a") with { GroupId = "g1" }, Row("b")],
            new SidebarFilterState { GroupBy = "custom" },
            [("g1", "Work", false)]);
        Assert.Equal(["Pinned", "Work", "Ungrouped"], sections.Select(s => s.Title));
        Assert.True(sections.Where(s => !s.Pinned).All(s => s.AcceptsDrop));
    }

    [Fact]
    public void Archived_rows_close_the_list_unless_the_filter_asked_for_them()
    {
        var rows = new[] { Row("a"), Row("z") with { IsArchived = true } };
        // "Active" is the reference's default and hides archived sessions outright.
        var active = Build(rows, new SidebarFilterState { GroupBy = "none" });
        Assert.DoesNotContain("Archived", active.Select(s => s.Title));

        var all = Build(rows, new SidebarFilterState { GroupBy = "none", Status = "all" });
        Assert.Equal("Archived", all[^1].Title);
        // Archived is a bucket of its own, not a custom group: it collapses under its
        // own key and nothing can be dropped into it.
        Assert.Equal("::archived", all[^1].Key);
        Assert.Null(all[^1].GroupId);
        Assert.False(all[^1].AcceptsDrop);

        var archived = Build(rows, new SidebarFilterState { GroupBy = "none", Status = "archived" });
        Assert.Single(archived);
        Assert.Equal(["z"], archived[0].Rows.Select(r => r.Id));
    }

    [Theory]
    [InlineData("recency", new[] { "new", "old" })]
    [InlineData("alpha", new[] { "new", "old" })]
    public void The_sort_orders_a_bucket(string sortBy, string[] expected)
    {
        var rows = new[] { Row("old", hoursAgo: 10), Row("new", hoursAgo: 1) };
        Assert.Equal(expected, SidebarPresentation.Order(rows, sortBy).Select(r => r.Id));
    }

    [Fact]
    public void Recent_folders_are_the_newest_use_of_each_directory()
    {
        var folders = SidebarPresentation.RecentFolders(
        [
            Row("a", @"C:\work\alpha", hoursAgo: 5),
            Row("b", @"C:\work\beta", hoursAgo: 1),
            Row("c", @"C:\work\alpha", hoursAgo: 9),
        ]);
        Assert.Equal([@"C:\work\beta", @"C:\work\alpha"], folders);
    }

    [Fact]
    public void The_filter_menu_carries_the_reference_sections_and_defaults()
    {
        var state = new SidebarFilterState();
        Assert.Equal("project", state.GroupBy);
        Assert.Equal("recency", state.SortBy);
        Assert.Equal("active", state.Status);

        var menu = SidebarFilterModel.Build(state);
        Assert.Equal(["Status"], menu.StatusSections.Select(s => s.Label));
        Assert.Equal(["Group by", "Sort by"], menu.GroupSortSections.Select(s => s.Label));
        Assert.Equal(["Show empty groups", "Show PR status"], menu.Toggles.Select(t => t.Label));
        Assert.False(menu.ShowClearFilters);

        // Last activity is offered on exactly one grouping, which is where the
        // reference puts it, and it carries its own option list.
        var byState = SidebarFilterModel.Build(state with { GroupBy = "state" });
        Assert.Equal(["Status", "Last activity"], byState.StatusSections.Select(s => s.Label));
        Assert.Equal(["1d", "3d", "7d", "30d", "All"],
            SidebarFilterModel.StateActivityOptions.Select(o => o.Label));
        Assert.Equal(7, SidebarFilterModel.DefaultStateActivityDays);
        Assert.DoesNotContain("Show empty groups", byState.Toggles.Select(t => t.Label));

        Assert.Equal(["Date", "Folder", "State", "Custom groups", "None"],
            SidebarFilterModel.GroupByOptions.Select(o => o.Label));
        Assert.Equal(["Name", "Date created", "Last activity"],
            SidebarFilterModel.SortByOptions.Select(o => o.Label));
    }

    [Fact]
    public void A_non_default_status_makes_the_filter_read_active()
    {
        var state = new SidebarFilterState { Status = "all" };
        Assert.True(SidebarFilterModel.IsFilterActive(state));
        Assert.Equal("Filter (active)", SidebarFilterModel.TriggerLabel(state));
        Assert.Equal("Filter", SidebarFilterModel.TriggerLabel(new SidebarFilterState()));
        Assert.Equal("active", SidebarFilterModel.ClearFilters(state).Status);

        // An activity window off its default counts as a filter, but only while the
        // grouping that offers it is the one in force.
        var narrowed = new SidebarFilterState { GroupBy = "state", StateActivityDays = 1 };
        Assert.True(SidebarFilterModel.IsFilterActive(narrowed));
        Assert.False(SidebarFilterModel.IsFilterActive(narrowed with { GroupBy = "date" }));
        Assert.Equal(7, SidebarFilterModel.ClearFilters(narrowed).StateActivityDays);
    }

    [Fact]
    public void The_row_status_ladder_is_the_reference_precedence()
    {
        var idle = new SessionRowStatus { HasCompleted = true };
        Assert.Equal(SessionRowState.Idle, SessionRowStates.Resolve(idle));
        Assert.Equal(SessionRowState.Running, SessionRowStates.Resolve(idle with { IsRunning = true }));
        Assert.Equal(SessionRowState.Awaiting,
            SessionRowStates.Resolve(idle with { IsRunning = true, IsAwaiting = true }));
        Assert.Equal(SessionRowState.Error,
            SessionRowStates.Resolve(idle with { IsAwaiting = true, HasError = true }));

        // An unread answer reads as ready; a pull request outranks it unless the user
        // marked the row unread by hand.
        Assert.Equal(SessionRowState.Ready, SessionRowStates.Resolve(idle with { IsUnread = true }));
        Assert.Equal(SessionRowState.Pr,
            SessionRowStates.Resolve(idle with { IsUnread = true, PrState = PrDisplayState.Merged }));
        Assert.Equal(SessionRowState.Ready,
            SessionRowStates.Resolve(idle with
            {
                IsUnread = true,
                ExplicitUnread = true,
                PrState = PrDisplayState.Merged,
            }));

        // An archived row answers only ready-or-idle, whatever else is true of it.
        Assert.Equal(SessionRowState.Idle,
            SessionRowStates.Resolve(idle with { IsArchived = true, IsRunning = true }));
        Assert.Equal(SessionRowState.Ready,
            SessionRowStates.Resolve(idle with { IsArchived = true, IsUnread = true }));

        Assert.Equal([0, 1, 2, 3, 4, 5],
        [
            SessionRowStates.Rank(SessionRowState.Error), SessionRowStates.Rank(SessionRowState.Awaiting),
            SessionRowStates.Rank(SessionRowState.Running), SessionRowStates.Rank(SessionRowState.Ready),
            SessionRowStates.Rank(SessionRowState.Pr), SessionRowStates.Rank(SessionRowState.Idle),
        ]);
    }

    [Fact]
    public void Only_a_live_state_draws_a_mark()
    {
        Assert.False(SessionRowStates.ShowsMark(SessionRowState.Idle, false));
        Assert.False(SessionRowStates.ShowsMark(SessionRowState.Pr, false));
        Assert.False(SessionRowStates.ShowsMark(SessionRowState.Running, isArchived: true));
        Assert.True(SessionRowStates.ShowsMark(SessionRowState.Running, false));
        Assert.Equal("Awaiting answer",
            SessionRowStates.Label(SessionRowState.Awaiting, AwaitingKind.Question));
        Assert.Equal("Awaiting input", SessionRowStates.Label(SessionRowState.Awaiting));
        Assert.Equal("Unread response", SessionRowStates.Label(SessionRowState.Ready));
    }

    [Fact]
    public void The_row_geometry_is_the_reference_desktop_density()
    {
        var m = SidebarMetrics.Current;
        Assert.Equal(30, m.RowHeight);
        Assert.Equal(14, m.RowFontSize);
        Assert.Equal(13, m.GroupFontSize);
        Assert.Equal(8, m.RadiusPill);
        Assert.Equal(24, m.RowControl);
        Assert.Equal(28, m.LeadingSlot);
        Assert.Equal(14, m.GroupPaddingTop);
        // Its derived values: an iconless label lines up with an icon's centre, a
        // nested row is pushed in by one icon plus a gap, and the trailing controls
        // sit half the leftover height from the edge.
        Assert.Equal(6, m.LabelInset);
        Assert.Equal(28, m.RowIndent);
        Assert.Equal(3, m.RowControlInset);
        Assert.Equal(15.5, m.AccordionGuideOffset);

        // The base declaration is the compact set, which the desktop overrides.
        Assert.Equal(26, SidebarMetrics.Compact.RowHeight);
        Assert.Equal(32, SidebarMetrics.Comfortable.RowHeight);
    }

    [Fact]
    public void A_label_overflows_only_once_it_runs_past_the_room_the_controls_leave()
    {
        // Undecorated: the full width is the label's.
        Assert.False(SidebarMetrics.LabelOverflows(100, 120, decorated: false, suffixed: false));
        Assert.True(SidebarMetrics.LabelOverflows(130, 120, decorated: false, suffixed: false));

        // Decorated: 44px of the trailing edge belongs to the controls.
        Assert.True(SidebarMetrics.LabelOverflows(100, 120, decorated: true, suffixed: false));
        Assert.False(SidebarMetrics.LabelOverflows(70, 120, decorated: true, suffixed: false));
    }

    [Fact]
    public void A_group_moves_along_the_list_and_stops_at_its_ends()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new SessionGroupsStore(file);
            var a = store.CreateGroup("A");
            var b = store.CreateGroup("B");
            var c = store.CreateGroup("C");

            Assert.False(store.MoveGroup(a.Id, -1));
            Assert.False(store.MoveGroup(c.Id, 1));
            Assert.Equal(["A", "B", "C"], store.Data.Groups.Select(g => g.Name));

            Assert.True(store.MoveGroup(a.Id, 1));
            Assert.Equal(["B", "A", "C"], store.Data.Groups.Select(g => g.Name));

            // A drag lands a group on another's index, which is a move of that distance.
            Assert.True(store.MoveGroup(b.Id, 2));
            Assert.Equal(["A", "C", "B"], store.Data.Groups.Select(g => g.Name));

            // The order survives a reload, since a drag is worth nothing that does not.
            Assert.Equal(["A", "C", "B"], new SessionGroupsStore(file).Data.Groups.Select(g => g.Name));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_bulk_older_actions_carry_the_reference_copy()
    {
        Assert.Equal("Bulk actions for older sessions", SessionBulkActions.SubmenuLabel);
        Assert.Equal("Archive all (3)", SessionBulkActions.ArchiveAll(3));
        Assert.Equal("Delete all (0)", SessionBulkActions.DeleteAll(0));
        Assert.Equal("Archive older sessions?", SessionBulkActions.ArchiveTitle);
        Assert.Equal(
            "This will archive 1 session older than a week. Pinned sessions are not affected.",
            SessionBulkActions.ArchiveBody(1));
        Assert.Equal(
            "This will permanently delete 2 sessions older than a week. Pinned sessions are not " +
            "affected. This cannot be undone.",
            SessionBulkActions.DeleteBody(2));
        Assert.Equal("Archived 1 older session", SessionBulkActions.Archived(1));
        Assert.Equal("Deleted 4 older sessions", SessionBulkActions.Deleted(4));
    }

    [Fact]
    public void The_branch_picker_orders_the_default_then_the_well_known_names()
    {
        var ordered = LandingPresentation.Order(
            ["zeta", "develop", "main", "feature/x", "master"], defaultBranch: "feature/x");
        Assert.Equal(["feature/x", "main", "master", "develop", "zeta"], ordered);
    }

    [Fact]
    public void A_query_filters_and_promotes_its_exact_match()
    {
        var ordered = LandingPresentation.Order(["main", "maintenance", "mainline"], null, "main");
        Assert.Equal(["main", "mainline", "maintenance"], ordered);
    }

    [Fact]
    public void A_default_branch_missing_from_the_list_is_added()
    {
        var ordered = LandingPresentation.Order(["feature"], defaultBranch: "master");
        Assert.Equal(["master", "feature"], ordered);
    }

    [Fact]
    public void The_plus_menu_lists_the_reference_rows_in_its_order()
    {
        var rows = PlusMenuModel.Build(new PlusMenuContext
        {
            IsDraft = true,
            GithubIssueAvailable = true,
            LinearIssueAvailable = true,
        });
        Assert.Equal(
            ["Add files or photos", "Add folder", "Import GitHub issue", "Import Linear issue",
                "Slash commands", "Add connectors", "Add plugins…"],
            rows.Select(r => r.Label));
    }

    [Fact]
    public void The_issue_rows_only_appear_on_a_draft_and_say_why_when_they_cannot_run()
    {
        var started = PlusMenuModel.Build(new PlusMenuContext { GithubIssueAvailable = true });
        Assert.DoesNotContain("Import GitHub issue", started.Select(r => r.Label));

        var noRemote = PlusMenuModel.Build(new PlusMenuContext
        {
            IsDraft = true,
            GithubIssueUnavailableReason = "no-github-remote",
        });
        var row = noRemote.Single(r => r.Label == "Import GitHub issue");
        Assert.True(row.Disabled);
        Assert.Equal("This folder doesn’t have a GitHub remote", row.Tooltip);
    }

    [Fact]
    public void Connectors_and_plugins_become_submenus_once_there_are_any()
    {
        var rows = PlusMenuModel.Build(new PlusMenuContext
        {
            Connectors = [new PlusMenuConnector("linear", true, true, false, 4)],
            Plugins = [new PlusMenuPlugin("acme", [new PlusMenuSkill("/acme:build", "Builds it")])],
        });

        var connectors = rows.Single(r => r.Label == "Connectors");
        Assert.Equal(["linear", "Manage connectors", "Browse connectors"],
            connectors.Children!.Select(r => r.Label));
        // Without a per-session switch the row opens the page instead of pretending
        // to toggle; the reference draws a switch because it edits the session's config.
        Assert.Null(connectors.Children!.Single(r => r.Label == "linear").Checked);
        Assert.Equal(PlusMenuAction.ManageConnectors,
            connectors.Children!.Single(r => r.Label == "linear").Action);

        var plugins = rows.Single(r => r.Label == "Plugins");
        Assert.Equal(["acme", "Manage plugins", "Browse plugins"], plugins.Children!.Select(r => r.Label));
        Assert.Equal(["/acme:build"], plugins.Children![0].Children!.Select(r => r.Label));
    }

    [Fact]
    public void The_working_directory_menu_is_the_reference_row_order()
    {
        var rows = WorkingDirectoryMenu.Build(new WorkingDirectoryContext
        {
            RepoName = "alpha",
            Cwd = @"C:\work\alpha",
            Branch = "feature",
            RepoUrl = "https://github.com/owner/alpha",
            CanChangeDirectory = true,
            CanOpenInTerminal = true,
        });
        Assert.Equal(
            ["VS Code", "Explorer", "Copy path", "Change directory", "Open repo on GitHub",
                "Open in terminal", "Copy branch name"],
            rows.Select(r => r.Label));
        Assert.True(rows.Single(r => r.Label == "Open in terminal").SeparatorBefore);
    }

    [Fact]
    public void The_group_label_names_the_folder_only_once_a_session_has_several()
    {
        Assert.Equal("Working directory",
            WorkingDirectoryMenu.Label(new WorkingDirectoryContext { RepoName = "alpha" }));
        Assert.Equal("alpha",
            WorkingDirectoryMenu.Label(new WorkingDirectoryContext { RepoName = "alpha", Multi = true }));
    }

    [Fact]
    public void The_chip_tooltip_joins_the_path_and_the_repositories()
    {
        Assert.Equal(@"C:\work\alpha", WorkingDirectoryMenu.ChipTooltip(@"C:\work\alpha", ["alpha"], "alpha"));
        Assert.Equal(@"C:\work\alpha · owner/alpha",
            WorkingDirectoryMenu.ChipTooltip(@"C:\work\alpha", ["owner/alpha"], "alpha"));
        Assert.Null(WorkingDirectoryMenu.ChipTooltip(null, ["alpha"], "alpha"));
    }

    [Fact]
    public void The_session_colours_are_the_reference_table()
    {
        Assert.Equal(["red", "blue", "green", "yellow", "purple", "orange", "pink", "cyan"],
            SessionColors.All.Select(c => c.Key));
        Assert.Equal("#dc2626", SessionColors.All[0].Hex);
        Assert.True(SessionColors.IsKnown("cyan"));
        Assert.False(SessionColors.IsKnown("teal"));
        var (ring, glow) = SessionColors.Glow(SessionColors.All[0]);
        Assert.Equal("#33dc2626", ring);
        Assert.Equal("#66dc2626", glow);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(9500, "9.5k")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_500_000, "1.5M")]
    public void The_context_ring_formats_tokens_the_reference_way(long count, string expected) =>
        Assert.Equal(expected, ContextRing.Format(count));

    [Fact]
    public void The_context_summary_carries_the_percentage_and_the_tooltip()
    {
        var (summary, pct) = ContextRing.Summarize(9500, 200000);
        Assert.Equal("9.5k / 200k (5%)", summary);
        Assert.Equal(5, pct);
        Assert.Equal("Context 9.5k / 200k (5%)", ContextRing.Tooltip(summary));
        Assert.Equal("Usage · Context 9.5k / 200k (5%)", ContextRing.AccessibleName(summary));

        var (over, none) = ContextRing.Summarize(300000, 200000);
        Assert.Equal("300k", over);
        Assert.Null(none);
    }

    [Theory]
    [InlineData(10, ContextRingTier.Normal)]
    [InlineData(75, ContextRingTier.Warning)]
    [InlineData(90, ContextRingTier.Critical)]
    public void The_ring_tier_is_the_reference_ladder(double pct, ContextRingTier tier) =>
        Assert.Equal(tier, ContextRing.Tier(pct));
}
