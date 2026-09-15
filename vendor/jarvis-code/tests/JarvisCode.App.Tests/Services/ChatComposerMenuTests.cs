using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ChatComposerMenuTests
{
    private static IReadOnlyList<ChatMenuRow> Menu(
        IReadOnlyList<ChatConnectorRow>? connectors = null,
        ChatToolAccess access = ChatToolAccess.LoadWhenNeeded,
        bool webSearch = false,
        bool screenshot = true,
        bool projects = true,
        bool gitHub = false) =>
        ChatComposerMenu.Plus(screenshot, projects, gitHub, connectors ?? [], access, webSearch);

    [Fact]
    public void The_menu_is_the_reference_groups_in_its_order()
    {
        var labels = Menu().Select(static r => r.Kind == ChatMenuKind.Separator ? "—" : r.Label).ToList();
        Assert.Equal(
            [
                ChatComposerMenu.AddFilesOrPhotos,
                ChatComposerMenu.TakeAScreenshot,
                ChatComposerMenu.AddToProject,
                "—",
                ChatComposerMenu.Skills,
                ChatComposerMenu.AddConnector,
                ChatComposerMenu.Plugins,
                "—",
                ChatComposerMenu.WebSearch,
            ],
            labels);
    }

    [Fact]
    public void A_row_this_build_cannot_honour_is_left_out()
    {
        var labels = Menu(screenshot: false, projects: false).Select(static r => r.Label).ToList();
        Assert.DoesNotContain(ChatComposerMenu.TakeAScreenshot, labels);
        Assert.DoesNotContain(ChatComposerMenu.AddToProject, labels);
        Assert.Equal(ChatComposerMenu.AddFilesOrPhotos, labels[0]);
    }

    [Fact]
    public void Web_search_is_a_checkbox_carrying_the_conversations_state()
    {
        var row = Menu(webSearch: true).Single(static r => r.Id == "web-search");
        Assert.Equal(ChatMenuKind.Checkbox, row.Kind);
        Assert.True(row.Checked);
        Assert.False(Menu().Single(static r => r.Id == "web-search").Checked);
    }

    [Fact]
    public void With_connectors_the_row_is_Connectors_and_lists_them()
    {
        var row = Menu([new ChatConnectorRow("linear", true, true), new ChatConnectorRow("sentry", true, false)])
            .Single(static r => r.Id == "connectors");
        Assert.Equal(ChatComposerMenu.Connectors, row.Label);
        var connectors = row.Items!.Where(static r => r.Id?.StartsWith("connector:", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(["linear", "sentry"], connectors.Select(static r => r.Label));
        Assert.True(connectors[0].Checked);
        Assert.False(connectors[1].Checked);
    }

    [Fact]
    public void A_disconnected_connector_is_counted_on_the_row()
    {
        var row = Menu([new ChatConnectorRow("linear", false, true)]).Single(static r => r.Id == "connectors");
        Assert.Equal("1 needs reconnection", row.Suffix);

        var two = ChatComposerMenu.ConnectorsRow(
            [new ChatConnectorRow("a", false, false), new ChatConnectorRow("b", false, false)],
            ChatToolAccess.LoadWhenNeeded);
        Assert.Equal("2 need reconnection", two.Suffix);
    }

    [Fact]
    public void Manage_connectors_leads_and_tool_access_closes_the_submenu()
    {
        var row = Menu([new ChatConnectorRow("linear", true, false)]).Single(static r => r.Id == "connectors");
        Assert.Equal(ChatComposerMenu.ManageConnectors, row.Items![0].Label);
        Assert.Equal(ChatComposerMenu.ToolAccess, row.Items[^1].Label);
    }

    [Fact]
    public void Tool_access_carries_both_rows_with_their_hints_and_the_current_one_checked()
    {
        var rows = ChatComposerMenu.ToolAccessRow(ChatToolAccess.AlreadyLoaded).Items!;
        Assert.Equal(ChatComposerMenu.LoadToolsWhenNeeded, rows[0].Label);
        Assert.Equal(ChatComposerMenu.LoadWhenNeededHint, rows[0].Subtitle);
        Assert.False(rows[0].Checked);
        Assert.Equal(ChatComposerMenu.ToolsAlreadyLoaded, rows[1].Label);
        Assert.Equal(ChatComposerMenu.AlreadyLoadedHint, rows[1].Subtitle);
        Assert.True(rows[1].Checked);
    }

    [Fact]
    public void The_project_picker_checks_the_chats_own_project()
    {
        var rows = ChatComposerMenu.ProjectPicker(["Acme", "Beta"], "Beta", "", anyExist: true);
        Assert.False(rows[0].Checked);
        Assert.True(rows[1].Checked);
        Assert.Equal(ChatComposerMenu.StartANewProject, rows[^1].Label);
    }

    [Fact]
    public void The_project_picker_says_which_kind_of_empty_it_is()
    {
        Assert.Equal(
            ChatComposerMenu.NoProjectsYet,
            ChatComposerMenu.ProjectPicker([], null, "", anyExist: false)[0].Label);
        Assert.Equal(
            ChatComposerMenu.NoMatches,
            ChatComposerMenu.ProjectPicker([], null, "zzz", anyExist: true)[0].Label);
    }
}
