using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class AcceleratorsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("CmdOrCtrl+N", "Ctrl+N")]
    [InlineData("CmdOrCtrl+Shift+O", "Ctrl+Shift+O")]
    [InlineData("CmdOrCtrl+Plus", "Ctrl++")]
    [InlineData("CmdOrCtrl+-", "Ctrl+-")]
    [InlineData("CmdOrCtrl+,", "Ctrl+,")]
    [InlineData("CmdOrCtrl+\\", "Ctrl+\\")]
    [InlineData("Ctrl+`", "Ctrl+`")]
    [InlineData("Ctrl+Alt+Left", "Ctrl+Alt+Left")]
    [InlineData("Shift+F3", "Shift+F3")]
    [InlineData("F11", "F11")]
    public void ReadsTheReferenceSpellingAsWindowsWritesIt(string? accelerator, string? expected) =>
        Assert.Equal(expected, Accelerators.Display(accelerator));
}

public class AppMenuTests
{
    private static AppMenuState State(
        bool code = true,
        bool zoomed = false,
        UpdateState? update = null) =>
        new(
            OnCodeSurface: code,
            SidebarVisible: true,
            TerminalVisible: false,
            DiffVisible: false,
            BrowserVisible: false,
            SideChatVisible: false,
            ClosePaneEnabled: false,
            FullScreen: false,
            Zoomed: zoomed,
            HardwareAccelerationDisabled: false,
            Update: update ?? UpdateState.Idle);

    private static AppMenuItem Menu(string label, AppMenuState state) =>
        AppMenu.Build(state).Single(m => m.Label == label);

    private static IEnumerable<string> Labels(AppMenuItem menu) =>
        menu.Items!.Where(static i => i.Command != AppMenuCommand.Separator).Select(static i => i.Label);

    [Fact]
    public void HasTheReferencesFourMenusInItsOrder() =>
        Assert.Equal(
            ["File", "Edit", "View", "Help"],
            AppMenu.Build(State()).Select(static m => m.Label));

    [Fact]
    public void FileOffersTheOpenRowsOnlyOnTheCodeSurface()
    {
        Assert.Contains("Open Folder…", Labels(Menu("File", State(code: true))));
        Assert.DoesNotContain("Open Folder…", Labels(Menu("File", State(code: false))));
        Assert.DoesNotContain("Open File…", Labels(Menu("File", State(code: false))));
    }

    [Fact]
    public void FileWithoutTheOpenRowsDrawsNoSeparatorForThem()
    {
        var chat = Menu("File", State(code: false));
        var code = Menu("File", State(code: true));

        Assert.Equal(
            code.Items!.Count(static i => i.Command == AppMenuCommand.Separator) - 1,
            chat.Items!.Count(static i => i.Command == AppMenuCommand.Separator));
    }

    [Fact]
    public void FileCarriesTheReferenceAccelerators()
    {
        var file = Menu("File", State());

        Assert.Equal(
            "CmdOrCtrl+N",
            file.Items!.Single(static i => i.Command == AppMenuCommand.NewConversation).Accelerator);
        Assert.Equal(
            "CmdOrCtrl+Shift+O",
            file.Items!.Single(static i => i.Command == AppMenuCommand.OpenFolder).Accelerator);
        Assert.Equal(
            "CmdOrCtrl+,",
            file.Items!.Single(static i => i.Command == AppMenuCommand.Settings).Accelerator);
        Assert.Equal(
            "CmdOrCtrl+W",
            file.Items!.Single(static i => i.Command == AppMenuCommand.CloseWindow).Accelerator);
        Assert.Null(
            file.Items!.Single(static i => i.Command == AppMenuCommand.Exit).Accelerator);
    }

    [Fact]
    public void EditEndsWithTheThreeFindRows() =>
        Assert.Equal(
            ["Find", "Find Next", "Find Previous"],
            Labels(Menu("Edit", State())).TakeLast(3));

    [Fact]
    public void ViewDrawsThePaneRowsAsCheckboxesNamedByTheirPane()
    {
        var view = Menu("View", State());
        var panes = view.Items!.Where(static i => i.Kind == AppMenuItemKind.Checkbox).ToList();

        Assert.Equal(
            ["Sidebar", "Terminal", "Changes", "Browser", "Side Chat", "Full Screen"],
            panes.Select(static i => i.Label));
    }

    [Fact]
    public void ViewDisablesThePaneRowsOffTheCodeSurface()
    {
        var chat = Menu("View", State(code: false));

        var terminal = chat.Items!.Single(static i => i.Command == AppMenuCommand.ToggleTerminal);
        Assert.False(terminal.Enabled);
        // The sidebar is not a session pane, so it stays available on Chat.
        Assert.True(chat.Items!.Single(static i => i.Command == AppMenuCommand.ToggleSidebar).Enabled);
    }

    [Fact]
    public void ActualSizeIsDisabledAtActualSize()
    {
        Assert.False(Menu("View", State(zoomed: false)).Items!
            .Single(static i => i.Command == AppMenuCommand.ActualSize).Enabled);
        Assert.True(Menu("View", State(zoomed: true)).Items!
            .Single(static i => i.Command == AppMenuCommand.ActualSize).Enabled);
    }

    [Fact]
    public void HelpShowsTheUpdaterStateAsItsOwnRows()
    {
        var idle = Labels(Menu("Help", State()));
        Assert.Contains("Check for Updates…", idle);

        var ready = Menu("Help", State(update: new UpdateState(UpdateStatus.Ready, Version: "2.0.0")));
        Assert.Contains("Restart to update to 2.0.0", Labels(ready));
        Assert.Equal(
            AppMenuCommand.RestartToUpdate,
            ready.Items!.Single(static i => i.Label.StartsWith("Restart to update", StringComparison.Ordinal)).Command);

        var checking = Menu("Help", State(update: new UpdateState(UpdateStatus.Checking)));
        var row = checking.Items!.Single(static i => i.Label == "Checking for Updates…");
        Assert.False(row.Enabled);
        Assert.Equal(AppMenuCommand.UpdateStatusRow, row.Command);
    }

    [Fact]
    public void HelpKeepsTheTroubleshootingRowsInASubmenu()
    {
        var troubleshooting = Menu("Help", State()).Items!
            .Single(static i => i.Label == "Troubleshooting");

        Assert.Equal(AppMenuItemKind.Submenu, troubleshooting.Kind);
        Assert.Equal(
            [
                "Show Logs in Explorer",
                "Copy Installation ID",
                "Generate Diagnostic Report",
                "Disable Hardware Acceleration",
                "Import Claude Code CLI sessions…",
                "Clear Cache and Restart",
                "Reset App Data…",
            ],
            Labels(troubleshooting));
    }

    [Fact]
    public void HelpEndsWithSupportAndAbout() =>
        Assert.Equal(["Get Support", "About Jarvis"], Labels(Menu("Help", State())).TakeLast(2));

    [Fact]
    public void EveryLeafRowCarriesACommandThatIsNotASubmenu()
    {
        foreach (var menu in AppMenu.Build(State()))
        {
            foreach (var item in Flatten(menu))
            {
                if (item.Kind != AppMenuItemKind.Submenu &&
                    item.Command != AppMenuCommand.Separator)
                {
                    Assert.NotEqual(AppMenuCommand.Submenu, item.Command);
                    Assert.NotEqual("", item.Label);
                }
            }
        }

        static IEnumerable<AppMenuItem> Flatten(AppMenuItem item) =>
            item.Items is null ? [item] : item.Items.SelectMany(Flatten);
    }
}
