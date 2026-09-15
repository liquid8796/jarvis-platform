namespace JarvisCode.App.Services;

/// <summary>
/// The reference writes its accelerators in Electron's cross-platform spelling
/// ("CmdOrCtrl+N", "CmdOrCtrl+Plus"). This turns one into what a Windows menu
/// prints beside a row, so the menu model can keep the reference's own strings.
/// </summary>
public static class Accelerators
{
    public static string? Display(string? accelerator)
    {
        if (accelerator is null)
        {
            return null;
        }

        var parts = accelerator.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var keys = new List<string>();
        foreach (var part in parts)
        {
            keys.Add(part switch
            {
                "CmdOrCtrl" or "CommandOrControl" => "Ctrl",
                "Cmd" or "Command" => "Ctrl",
                "Plus" => "+",
                "numadd" => "Num +",
                "numsub" => "Num -",
                "num0" => "Num 0",
                _ => part,
            });
        }

        return string.Join('+', keys);
    }
}

/// <summary>Every action the app menu can raise.</summary>
public enum AppMenuCommand
{
    Separator,
    Submenu,

    NewConversation,
    OpenFile,
    OpenFolder,
    Settings,
    CloseWindow,
    Exit,

    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    SelectAll,
    Find,
    FindNext,
    FindPrevious,

    CommandPalette,
    ToggleSidebar,
    ToggleTerminal,
    ToggleDiff,
    ToggleBrowser,
    ToggleSideChat,
    ClosePane,
    PreviousSidebarTab,
    NextSidebarTab,
    ActualSize,
    ZoomIn,
    ZoomOut,
    FullScreen,
    Reload,
    Back,
    Forward,

    OpenDocumentation,
    CheckForUpdates,
    UpdateStatusRow,
    RestartToUpdate,
    ShowLogsInExplorer,
    CopyInstallationId,
    GenerateDiagnosticReport,
    DisableHardwareAcceleration,
    ImportCliSessions,
    ClearCacheAndRestart,
    ResetAppData,
    GetSupport,
    About,
}

/// <summary>
/// One row of the app menu.
/// </summary>
/// <param name="Command">What the row does; <see cref="AppMenuCommand.Separator"/> draws a rule.</param>
/// <param name="Label">The row's text.</param>
/// <param name="Accelerator">
/// The chord, in the reference's own spelling (CmdOrCtrl+N, Ctrl+`, F11…), or null.
/// </param>
/// <param name="Kind">Whether the row is a plain item, a checkbox or a submenu.</param>
/// <param name="Checked">The checkbox's state; meaningless for the other kinds.</param>
/// <param name="Enabled">False for the rows that only report something.</param>
/// <param name="Items">A submenu's rows.</param>
public sealed record AppMenuItem(
    AppMenuCommand Command,
    string Label = "",
    string? Accelerator = null,
    AppMenuItemKind Kind = AppMenuItemKind.Item,
    bool Checked = false,
    bool Enabled = true,
    IReadOnlyList<AppMenuItem>? Items = null)
{
    public static readonly AppMenuItem Separator = new(AppMenuCommand.Separator);
}

public enum AppMenuItemKind
{
    Item,
    Checkbox,
    Submenu,
}

/// <summary>What the menu needs to know about the window to draw itself.</summary>
/// <param name="OnCodeSurface">The Code surface is showing, which is what the pane rows act on.</param>
/// <param name="SidebarVisible">Ticks the Sidebar checkbox.</param>
/// <param name="TerminalVisible">Ticks the Terminal checkbox.</param>
/// <param name="DiffVisible">Ticks the Diff checkbox.</param>
/// <param name="BrowserVisible">Ticks the Browser checkbox.</param>
/// <param name="SideChatVisible">Ticks the Side Chat checkbox.</param>
/// <param name="ClosePaneEnabled">False when no pane is open to close.</param>
/// <param name="FullScreen">Ticks the Full Screen checkbox.</param>
/// <param name="Zoomed">The reference disables Actual Size at zoom 0.</param>
/// <param name="HardwareAccelerationDisabled">Ticks the troubleshooting checkbox.</param>
/// <param name="Update">The updater's state, which decides the Help menu's update rows.</param>
public readonly record struct AppMenuState(
    bool OnCodeSurface,
    bool SidebarVisible,
    bool TerminalVisible,
    bool DiffVisible,
    bool BrowserVisible,
    bool SideChatVisible,
    bool ClosePaneEnabled,
    bool FullScreen,
    bool Zoomed,
    bool HardwareAccelerationDisabled,
    UpdateState Update);

/// <summary>
/// The application menu, ported from the reference's own template (app.asar
/// <c>index.chunk-DnlgCaT3.js</c>: <c>SWn</c> composes <c>KLn</c> File,
/// <c>CIn</c> Edit, <c>oWn</c> View and <c>CHn</c> Help; <c>lWn</c>/<c>uWn</c>/
/// <c>dWn</c> build the View rows, <c>SHn</c> the Troubleshooting submenu and
/// <c>bSn</c> the update rows). On Windows the reference pops it from the title
/// bar rather than hanging a menu bar over the window, so this app does too.
///
/// Rows the reference has that this build does not, declared in the parity suite's
/// surface manifest: its Developer menu and Enable Developer Mode row (both gated
/// on an internal build), the five Cowork troubleshooting rows, Record Net Log
/// (30s), and the hidden accelerator-only
/// duplicates it registers for Paste and Match Style, the numpad zoom keys and the
/// second Find chords, which WPF binds directly instead of hiding menu rows.
/// </summary>
public static class AppMenu
{
    public const string FileLabel = "File";
    public const string EditLabel = "Edit";
    public const string ViewLabel = "View";
    public const string HelpLabel = "Help";
    public const string TroubleshootingLabel = "Troubleshooting";

    /// <summary>Where Open Documentation goes. The reference points at its own support site.</summary>
    public const string DocumentationUrl = "https://github.com/liquid8796/jarvis-code#readme";

    /// <summary>Where Get Support goes.</summary>
    public const string SupportUrl = "https://github.com/liquid8796/jarvis-code/issues";

    public static IReadOnlyList<AppMenuItem> Build(AppMenuState state) =>
    [
        File(state),
        Edit(),
        View(state),
        Help(state),
    ];

    private static AppMenuItem File(AppMenuState state)
    {
        var items = new List<AppMenuItem>
        {
            new(AppMenuCommand.NewConversation, "New Conversation", "CmdOrCtrl+N"),
        };

        // The reference offers the two open rows only on the Code surface, and
        // draws the separator above them only when at least one is there.
        if (state.OnCodeSurface)
        {
            items.Add(AppMenuItem.Separator);
            items.Add(new AppMenuItem(AppMenuCommand.OpenFile, "Open File…"));
            items.Add(new AppMenuItem(AppMenuCommand.OpenFolder, "Open Folder…", "CmdOrCtrl+Shift+O"));
        }

        items.Add(AppMenuItem.Separator);
        items.Add(new AppMenuItem(AppMenuCommand.Settings, "Settings", "CmdOrCtrl+,"));
        items.Add(AppMenuItem.Separator);
        items.Add(new AppMenuItem(AppMenuCommand.CloseWindow, "Close Window", "CmdOrCtrl+W"));
        items.Add(AppMenuItem.Separator);
        items.Add(new AppMenuItem(AppMenuCommand.Exit, "Exit"));
        return new AppMenuItem(
            AppMenuCommand.Submenu, FileLabel, Kind: AppMenuItemKind.Submenu, Items: items);
    }

    private static AppMenuItem Edit() =>
        new(AppMenuCommand.Submenu, EditLabel, Kind: AppMenuItemKind.Submenu, Items:
        [
            new(AppMenuCommand.Undo, "Undo", "CmdOrCtrl+Z"),
            new(AppMenuCommand.Redo, "Redo", "Ctrl+Y"),
            AppMenuItem.Separator,
            new(AppMenuCommand.Cut, "Cut", "CmdOrCtrl+X"),
            new(AppMenuCommand.Copy, "Copy", "CmdOrCtrl+C"),
            new(AppMenuCommand.Paste, "Paste", "CmdOrCtrl+V"),
            new(AppMenuCommand.SelectAll, "Select All", "CmdOrCtrl+A"),
            AppMenuItem.Separator,
            new(AppMenuCommand.Find, "Find", "CmdOrCtrl+F"),
            new(AppMenuCommand.FindNext, "Find Next", "F3"),
            new(AppMenuCommand.FindPrevious, "Find Previous", "Shift+F3"),
        ]);

    private static AppMenuItem View(AppMenuState state)
    {
        // The reference's pane rows are enabled only on the Code surface, and their
        // labels are the checkbox names rather than the Show/Hide pair, because the
        // Windows menu passes checkbox: true.
        var paneRows = state.OnCodeSurface;
        return new AppMenuItem(AppMenuCommand.Submenu, ViewLabel, Kind: AppMenuItemKind.Submenu, Items:
        [
            new(AppMenuCommand.CommandPalette, "Command Palette…", "CmdOrCtrl+K"),
            new(AppMenuCommand.ToggleSidebar, "Sidebar", "CmdOrCtrl+B",
                AppMenuItemKind.Checkbox, Checked: state.SidebarVisible),
            AppMenuItem.Separator,
            new(AppMenuCommand.ToggleTerminal, "Terminal", "Ctrl+`",
                AppMenuItemKind.Checkbox, Checked: paneRows && state.TerminalVisible, Enabled: paneRows),
            new(AppMenuCommand.ToggleDiff, "Changes", "CmdOrCtrl+Shift+D",
                AppMenuItemKind.Checkbox, Checked: paneRows && state.DiffVisible, Enabled: paneRows),
            new(AppMenuCommand.ToggleBrowser, "Browser", "CmdOrCtrl+Shift+B",
                AppMenuItemKind.Checkbox, Checked: paneRows && state.BrowserVisible, Enabled: paneRows),
            new(AppMenuCommand.ToggleSideChat, "Side Chat", "CmdOrCtrl+;",
                AppMenuItemKind.Checkbox, Checked: paneRows && state.SideChatVisible, Enabled: paneRows),
            new(AppMenuCommand.ClosePane, "Close Pane", "CmdOrCtrl+\\",
                Enabled: paneRows && state.ClosePaneEnabled),
            AppMenuItem.Separator,
            new(AppMenuCommand.PreviousSidebarTab, "Previous Sidebar Tab", "Ctrl+Alt+Left"),
            new(AppMenuCommand.NextSidebarTab, "Next Sidebar Tab", "Ctrl+Alt+Right"),
            AppMenuItem.Separator,
            new(AppMenuCommand.ActualSize, "Actual Size", "CmdOrCtrl+0", Enabled: state.Zoomed),
            new(AppMenuCommand.ZoomIn, "Zoom In", "CmdOrCtrl+Plus"),
            new(AppMenuCommand.ZoomOut, "Zoom Out", "CmdOrCtrl+-"),
            AppMenuItem.Separator,
            new(AppMenuCommand.FullScreen, "Full Screen", "F11",
                AppMenuItemKind.Checkbox, Checked: state.FullScreen),
            AppMenuItem.Separator,
            new(AppMenuCommand.Reload, "Reload", "F5"),
        ]);
    }

    private static AppMenuItem Help(AppMenuState state)
    {
        var items = new List<AppMenuItem>
        {
            new(AppMenuCommand.OpenDocumentation, "Open Documentation"),
        };

        foreach (var row in UpdateMenuLabels.For(state.Update))
        {
            var command = state.Update.Status switch
            {
                UpdateStatus.Ready => AppMenuCommand.RestartToUpdate,
                _ when row.Enabled => AppMenuCommand.CheckForUpdates,
                _ => AppMenuCommand.UpdateStatusRow,
            };
            items.Add(new AppMenuItem(command, row.Label, Enabled: row.Enabled));
        }

        items.Add(AppMenuItem.Separator);
        items.Add(new AppMenuItem(
            AppMenuCommand.Submenu, TroubleshootingLabel, Kind: AppMenuItemKind.Submenu, Items:
            [
                new(AppMenuCommand.ShowLogsInExplorer, "Show Logs in Explorer"),
                new(AppMenuCommand.CopyInstallationId, "Copy Installation ID"),
                new(AppMenuCommand.GenerateDiagnosticReport, DiagnosticReportText.MenuLabel),
                AppMenuItem.Separator,
                new(AppMenuCommand.DisableHardwareAcceleration, "Disable Hardware Acceleration",
                    Kind: AppMenuItemKind.Checkbox, Checked: state.HardwareAccelerationDisabled),
                AppMenuItem.Separator,
                new(AppMenuCommand.ImportCliSessions, "Import Claude Code CLI sessions…"),
                new(AppMenuCommand.ClearCacheAndRestart, "Clear Cache and Restart"),
                new(AppMenuCommand.ResetAppData, "Reset App Data…"),
            ]));
        items.Add(AppMenuItem.Separator);
        items.Add(new AppMenuItem(AppMenuCommand.GetSupport, "Get Support"));
        items.Add(new AppMenuItem(AppMenuCommand.About, "About Jarvis"));
        return new AppMenuItem(
            AppMenuCommand.Submenu, HelpLabel, Kind: AppMenuItemKind.Submenu, Items: items);
    }
}
