using System.Windows;
using System.Windows.Interop;
using JarvisCode.App.Composition;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Utilities;
using JarvisCode.Core.Sessions;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

public partial class MainWindow : Window
{
    private const int WmSettingChange = 0x001A;

    /// <summary>What the sidebar shows for a session that never got a title.</summary>
    private const string UntitledSession = "New session";

    private readonly AppServices _services;
    private readonly ChatSurface _chatSurface = new();
    private readonly CodeWorkspace _codeWorkspace = new();
    private bool _initialized;
    private Services.GlobalHotkey? _quickEntryHotkey;
    private Services.TrayService? _tray;

    /// <summary>Set by /exit so "close to tray" does not swallow the quit.</summary>
    private bool _exitRequested;

    /// <summary>keybindings.json chords, in addition to the built-in ones.</summary>
    private IReadOnlyList<Services.UserKeybinding> _userKeybindings = [];
    private QuickEntryWindow? _quickEntry;
    private Customize.CustomizeSurface? _customizeSurface;
    private bool _customizeActive;

    /// <summary>The one engine view every mermaid fence in the app renders in.</summary>
    private readonly Services.MermaidRenderer _mermaid;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        Title = "Jarvis Code" + services.Paths.WindowTitleSuffix;

        Toasts.Attach(services.Toasts);

        // The chat renderer's mermaid fences all render in one hidden engine view;
        // it lives with the window, so a screenshot run that never opens one has
        // no renderer and every fence stays a code block.
        _mermaid = new MermaidRenderer();
        _mermaid.Attach(MermaidHost);
        MermaidRenderer.Current = _mermaid;

        RestoreWindowBounds();
        StateChanged += OnWindowStateChanged;
        SourceInitialized += (_, _) => HookWndProc();
        Closing += OnWindowClosing;

        // "Stop Jarvis" pressed on a page the agent is driving stops the turn that
        // is driving it — the surface in front, which is the one that opened it.
        services.Browser.StopRequested += () => Dispatcher.BeginInvoke(() =>
        {
            ActiveSurface.ViewModel.CancelTurn();
            services.Browser.EndSession();
        });

        // The tray icon belongs to the process, not to the window: it needs no window
        // handle of its own, and a start-in-tray launch never shows the window at all —
        // hanging it off SourceInitialized left that launch with no icon to click.
        SetupTray();
        services.Theme.ThemeChanged += (_, _) => UpdateTrayColor();

        _chatSurface.Initialize(services, isCodeSurface: false);
        RebuildChatTools();
        services.UiSettings.Saved += (_, _) => RebuildChatTools();
        _chatSurface.ChatToolsChanged += (_, _) => RebuildChatTools();
        _chatSurface.CustomizeRequested += (_, page) => EnterCustomize(page);
        _chatSurface.ProjectChanged += (_, _) => RefreshSessionList();
        services.UiSettings.Saved += (_, _) => UpdateKeepAwake();
        _codeWorkspace.Initialize(services);
        foreach (var surface in new[] { _chatSurface, _codeWorkspace.ChatSurface, _codeWorkspace.SplitChatSurface })
        {
            WireSurface(surface);
        }

        // A pane created later for the split grid needs the same wiring.
        _codeWorkspace.PaneAdded += WireSurface;
        // The Runs pane's own two exits: a row opens that run's session, Details the
        // Scheduled page the routine lives on.
        _codeWorkspace.SessionOpenRequested += (_, id) => _ = OpenSessionAsync(id);
        // A session row dropped on the pane mosaic: "Add split" opens it beside the
        // others, "Open here" takes the tile it landed on.
        _codeWorkspace.SessionDropped += (_, drop) =>
            _ = drop.Split ? OpenInSplitViewAsync(drop.SessionId) : OpenSessionAsync(drop.SessionId);
        _codeWorkspace.ScheduledPageRequested += (_, _) => OpenRoutines();

        Sidebar.NewSessionRequested += (_, _) => StartNewSession();
        Sidebar.CustomizeRequested += (_, _) => EnterCustomize();
        Sidebar.ScheduledRequested += (_, _) => OpenRoutines();

        CustomizeNav.BackRequested += (_, _) => ExitCustomize();
        CustomizeNav.SkillsRequested += (_, _) => _customizeSurface?.ShowSkills();
        CustomizeNav.ConnectorsRequested += (_, _) => _customizeSurface?.ShowConnectors();
        CustomizeNav.PluginsRequested += (_, _) => _customizeSurface?.ShowPlugins();
        CustomizeNav.MemoryRequested += (_, _) => _customizeSurface?.ShowMemory();
        CustomizeNav.OrgPluginsRequested += (_, _) => _customizeSurface?.ShowOrgPlugins();
        CustomizeNav.AddPluginRequested += (_, _) =>
        {
            EnterCustomize();
            _customizeSurface?.InstallPluginFromFolder();
        };
        CustomizeNav.PluginSelected += (_, name) => _customizeSurface?.ShowPlugins(name);
        Sidebar.FilterChanged += (_, _) => RefreshSessionList();
        Sidebar.SetServices(services);
        Sidebar.SetToasts(services.Toasts);
        Sidebar.SessionSelected += (_, id) => _ = OpenSessionAsync(id);
        Sidebar.SessionDeleteRequested += (_, id) => _ = DeleteSessionAsync(id);
        Sidebar.SessionExportRequested += (_, id) => _ = ExportSessionAsync(id);
        Sidebar.SessionRenameRequested += (_, args) => _ = RenameSessionAsync(args.SessionId, args.NewTitle);

        // (The initial view models' IsRunning hook rides WireSurface.)

        Sidebar.SessionSplitRequested += (_, id) => _ = OpenInSplitViewAsync(id);
        _codeWorkspace.ChatSurface.HomeSessionRequested += (_, id) => _ = OpenSessionAsync(id);
        Sidebar.SessionWindowRequested += (_, id) => _ = OpenInOwnWindowAsync(id);
        Sidebar.RoutineRequested += (_, id) => OpenRoutines(routineId: id);
        Sidebar.SetUser(
            services.UiSettings.Current.UserDisplayName,
            services.Paths.ProfileName is { } profile ? $"Profile: {profile}" : "");

        var ui = services.UiSettings.Current;
        _sidebarWidth = Math.Clamp(ui.SidebarWidth, 200, 400);
        SidebarColumn.Width = new GridLength(_sidebarWidth);
        if (ui.SidebarCollapsed)
        {
            SetSidebarCollapsed(true);
        }

        SizeChanged += OnWindowSizeChanged;

        EnsureRoutineRunner();
        StartAutoArchive();
        WirePluginMonitors();
        SweepOrphanTeams();
        InitializeShell();
        Sidebar.UpdateCardClicked += (_, _) => _ = RestartToUpdateAsync();
        Activated += (_, _) => OnWindowActivatedForAttention();
        services.SessionGroups.Changed += (_, _) => Dispatcher.InvokeAsync(RefreshSessionList);

        _initialized = true;
        if (ui.LastSurface == "code")
        {
            CodeTab.IsChecked = true;
        }
        else
        {
            ApplySurface();
        }

        Loaded += (_, _) =>
        {
            RefreshSessionList();
            if (ui.LastSessionId is { Length: > 0 } lastSession)
            {
                _ = OpenSessionAsync(lastSession);
            }

            ActiveSurface.FocusInput();
        };

        ApplyZoom(ui.ZoomFactor);
        ReloadUserKeybindings();
        if (ui.FullscreenMode)
        {
            Loaded += (_, _) => SetFullscreen(true);
        }

        PreviewKeyDown += OnGlobalKeyDown;

        // Holding Ctrl+Shift raises the sidebar's jump-hint keycaps, and letting go of
        // either takes them down — the reference's own `xB` chord over its `EB` listener.
        PreviewKeyDown += (_, _) => Sidebar.UpdateJumpHints(JumpHintChordHeld);
        PreviewKeyUp += (_, _) => Sidebar.UpdateJumpHints(JumpHintChordHeld);
        Deactivated += (_, _) => Sidebar.UpdateJumpHints(false);
        PreviewMouseWheel += (_, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            {
                e.Handled = true;
                StepZoom(e.Delta > 0 ? 1 : -1);
            }
        };
    }

    // ---- zoom (Ctrl+= / Ctrl+- / Ctrl+0) ----

    private static readonly double[] ZoomSteps = [0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    private void StepZoom(int direction)
    {
        var current = _services.UiSettings.Current.ZoomFactor;
        var index = Array.FindIndex(ZoomSteps, z => Math.Abs(z - current) < 0.01);
        if (index < 0)
        {
            index = Array.FindIndex(ZoomSteps, z => z >= current);
            index = index < 0 ? ZoomSteps.Length - 1 : index;
        }

        ApplyZoom(ZoomSteps[Math.Clamp(index + direction, 0, ZoomSteps.Length - 1)]);
    }

    private void ApplyZoom(double factor)
    {
        factor = Math.Clamp(factor, ZoomSteps[0], ZoomSteps[^1]);
        BodyGrid.LayoutTransform = Math.Abs(factor - 1.0) < 0.01
            ? null
            : new System.Windows.Media.ScaleTransform(factor, factor);
        if (Math.Abs(_services.UiSettings.Current.ZoomFactor - factor) > 0.001)
        {
            _services.UiSettings.Current.ZoomFactor = factor;
            _services.UiSettings.Save();
        }
    }

    // ---- command palette & keyboard shortcuts ----

    private CommandPaletteView? _palette;
    private ShortcutsView? _shortcuts;

    public async void OpenCommandPalette()
    {
        if (OverlayHost.Content is CommandPaletteView)
        {
            CloseOverlay();
            return;
        }

        if (_palette is null)
        {
            _palette = new CommandPaletteView();
            _palette.CloseRequested += (_, _) => CloseOverlay();
        }

        var actions = BuildPaletteActions();
        var quickActions = new List<Services.PaletteRow>
        {
            // The reference's quick action on a Code route is the one row
            // "New session"; everything else lives in the actions list.
            new Services.PaletteActionRow(new Services.PaletteAction(
                "new_code_session", Services.PaletteGroup.Other, "New session", () => StartNewSession())),
        };

       var recents = new List<Services.PaletteRow>();
        try
        {
            var sessions = await ActiveList.ListAsync();
            foreach (var session in sessions.OrderByDescending(static s => s.UpdatedAt)
                         .Take(Services.CommandPalette.MaxOrganicItems))
            {
                var captured = session.Id;
                recents.Add(new Services.PaletteSessionRow(
                    session.Id,
                    session.Title,
                    Snippet: null,
                    IsScheduled: false,
                    RunCount: 0,
                    PullRequestNumber: null,
                    () => _ = OpenSessionAsync(captured)));
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            // The palette works without the session rows.
        }

        _palette.Show(actions, quickActions, recents);
        OverlayHost.Content = _palette;
    }

    /// <summary>
    /// The registry rows this session can actually run, plus the settings and
    /// customize rows the reference generates from its own navigation.
    /// </summary>
    private IReadOnlyList<Services.PaletteAction> BuildPaletteActions()
    {
        var handlers = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            ["command_palette"] = () => Dispatcher.BeginInvoke(OpenCommandPalette),
            ["chats"] = () => ChatTab.IsChecked = true,
            ["claude_code"] = () => CodeTab.IsChecked = true,
            ["new_conversation"] = () => StartNewSession(),
            ["scheduled_tasks"] = () => OpenRoutines(),
            ["file_upload"] = () => ActiveSurface.OpenAddFilesDialog(),
            ["model_selector"] = () => ActiveSurface.OpenModelMenu(),
            ["jump_prev_prompt"] = () => ActiveSurface.JumpToPrompt(-1),
            ["jump_next_prompt"] = () => ActiveSurface.JumpToPrompt(1),
            ["toggle_sidebar"] = () => SetSidebarCollapsed(SidebarColumn.Width.Value > 0),
            ["shortcuts_modal"] = OpenShortcuts,
        };

        if (!IsCodeActive)
        {
            // The reference's chat-group rows, on the route that has them.
            handlers["incognito"] = () => _chatSurface.StartIncognito();
            handlers["delete_chat"] = () => _ = DeleteSessionAsync(_chatSurface.ViewModel.Session.Id);
        }

        if (IsCodeActive)
        {
            var id = ActiveSurface.ViewModel.Session.Id;
            var archived = _services.SessionGroups.IsArchived(id);
            var pinned = _services.SessionGroups.IsPinned(id);
            handlers["archive_code_session"] = () => _services.SessionGroups.SetArchived(id, true);
            handlers["unarchive_code_session"] = () => _services.SessionGroups.SetArchived(id, false);
            handlers["delete_code_session"] = () => _ = DeleteSessionAsync(id);
            handlers["rename_code_session"] = () => RenameActiveSession();
            handlers["pin_code_session"] = () => _services.SessionGroups.SetPinned(id, true);
            handlers["unpin_code_session"] = () => _services.SessionGroups.SetPinned(id, false);
            handlers["fork_code_session"] = () => _ = ForkActiveSessionAsync();
            handlers["new_code_session_from_current"] = StartSessionWithCurrentSettings;
            handlers["copy_code_session_link"] = () => CopySessionLink(id);
            handlers["toggle_read_code_session"] =
                () => _services.SessionGroups.SetUnread(id, !_services.SessionGroups.IsUnread(id));
            handlers["open_code_session_pr"] = () => _ = OpenActiveSessionPrAsync();

            // The reference offers exactly one of each pair, by the session's state.
            handlers.Remove(archived ? "archive_code_session" : "unarchive_code_session");
            handlers.Remove(pinned ? "pin_code_session" : "unpin_code_session");
        }

        var actions = new List<Services.PaletteAction>();
        foreach (var entry in Services.CommandPalette.Registry)
        {
            if (!handlers.TryGetValue(entry.Key, out var execute))
            {
                continue;
            }

            actions.Add(new Services.PaletteAction(entry.Key, entry.Group, entry.Description, execute)
            {
                Shortcut = entry.Shortcut,
                SearchHints = entry.SearchHints ?? [],
            });
        }

        foreach (var (group, label) in Settings.SettingsDialog.NavSections)
        {
            var capturedGroup = group;
            var capturedLabel = label;
            actions.Add(new Services.PaletteAction(
                $"settings:{group}:{label}",
                Services.PaletteGroup.Settings,
                Services.CommandPalette.SettingsRowLabel(group, label),
                () => OpenSettings(capturedGroup, capturedLabel))
            {
                SearchHints = [Services.CommandPalette.SettingsSearchHint],
            });
        }

        foreach (var (page, label) in CustomizeSections)
        {
            var captured = page;
            actions.Add(new Services.PaletteAction(
                $"customize:{page}",
                Services.PaletteGroup.Settings,
                Services.CommandPalette.CustomizeRowLabel(label),
                () => EnterCustomize(captured)));
        }

        return actions;
    }

    /// <summary>The Customize surface's own sections, as its second-level nav lists them.</summary>
    private static readonly (string Page, string Label)[] CustomizeSections =
        [("skills", "Skills"), ("connectors", "Connectors"), ("plugins", "Personal plugins")];

    private void RenameActiveSession()
    {
        var session = ActiveSurface.ViewModel.Session;
        if (InputDialog.Prompt(this, "Rename session", session.Title, "Rename") is { } newTitle)
        {
            _ = RenameSessionAsync(session.Id, newTitle);
        }
    }

    /// <summary>
    /// Ctrl+Shift+N: a new session in the same folder, on the same model, with
    /// the same added directories — the settings the current one is running on.
    /// </summary>
    private void StartSessionWithCurrentSettings()
    {
        var source = ActiveSurface.ViewModel.Session;
        var fresh = JarvisCode.Core.Sessions.Session.CreateNew(source.WorkingDirectory);
        fresh.ModelId = source.ModelId;
        fresh.AdditionalDirectories = [.. source.AdditionalDirectories];
        ActiveSurface.LoadSession(fresh);
        RefreshSessionList();
    }

    /// <summary>Ctrl+Alt+L: the link that reopens this session, on the clipboard.</summary>
    private void CopySessionLink(string sessionId)
    {
        try
        {
            System.Windows.Clipboard.SetText(Services.DeepLinks.ForSession(sessionId));
            _services.Toasts.AddSuccess("Link copied to clipboard.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing was copied.
        }
    }

    public void OpenShortcuts()
    {
        if (_shortcuts is null)
        {
            _shortcuts = new ShortcutsView();
            _shortcuts.CloseRequested += (_, _) => CloseOverlay();
        }

        OverlayHost.Content = _shortcuts;
        _shortcuts.Focus();
    }

    private void CloseOverlay()
    {
        if (OverlayHost.Content is CommandPaletteView or ShortcutsView)
        {
            OverlayHost.Content = null;
            ActiveSurface.FocusInput();
        }
    }

    /// <summary>Explorer's "New Jarvis Code Session Here" and the tray land here.</summary>
    public void StartCodeSessionIn(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        CodeTab.IsChecked = true;
        _codeWorkspace.ChatSurface.StartNew(directory);
        RefreshSessionList();
    }

    /// <summary>"Import Claude Code CLI sessions…" — terminal and Code-tab transcripts alike.</summary>
    public async Task ImportCliSessionsAsync()
    {
        Services.CliImportResult result;
        try
        {
            result = await Task.Run(() => Services.CliSessionImporter.ImportAsync(_services.Sessions));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Error importing sessions: {ex.Message}", "Import Claude Code CLI sessions");
            return;
        }

        RefreshSessionList();
        var lines = new List<string>();
        if (result.Imported > 0)
        {
            lines.Add($"Imported {result.Imported} session{(result.Imported == 1 ? "" : "s")}.");
        }

        if (result.SkippedExisting > 0)
        {
            lines.Add($"Skipped {result.SkippedExisting} session{(result.SkippedExisting == 1 ? "" : "s")} " +
                      "already in your session list.");
        }

        if (result.Failed > 0)
        {
            lines.Add($"{result.Failed} session{(result.Failed == 1 ? "" : "s")} couldn't be imported.");
        }

        if (lines.Count == 0)
        {
            lines.Add("No CLI sessions to import.");
        }

        MessageBox.Show(this, string.Join("\n", lines), "Import Claude Code CLI sessions");
    }

    private async Task ContinueLastSessionAsync()
    {
        try
        {
            var sessions = await ActiveList.ListAsync();
            if (sessions.OrderByDescending(static s => s.UpdatedAt).FirstOrDefault() is { } last)
            {
                await OpenSessionAsync(last.Id);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Runs one row of the reference's pane keymap. Returns false for a command
    /// this build has nothing behind, so the chord falls through rather than
    /// being swallowed by a row that does nothing.
    /// </summary>
    private bool RunPaneCommand(Services.PaneCommand command)
    {
        switch (command)
        {
            case Services.PaneCommand.TogglePreview:
                _codeWorkspace.TogglePanel("browser");
                return true;
            case Services.PaneCommand.ToggleDiff:
                _codeWorkspace.TogglePanel("changes");
                return true;
            case Services.PaneCommand.ToggleTerminal:
                _codeWorkspace.TogglePanel("terminal");
                return true;
            case Services.PaneCommand.ToggleFileBrowser:
                _codeWorkspace.TogglePanel("files");
                return true;
            case Services.PaneCommand.ClosePane:
                // The reference's Ctrl+\ closes one pane — the last one opened — and
                // the focused session tile only once no pane is left.
                if (_codeWorkspace.OpenPanes is [.., var lastPane])
                {
                    _codeWorkspace.ClosePane(lastPane);
                }
                else
                {
                    _codeWorkspace.ClosePane(_codeWorkspace.ActiveChatSurface);
                }

                return true;
            case Services.PaneCommand.ToggleSideChat:
                _codeWorkspace.ToggleSideChat();
                return true;
            case Services.PaneCommand.CycleTranscriptMode:
                ActiveSurface.CycleTranscriptView();
                return true;
            case Services.PaneCommand.BackgroundTasks:
                _codeWorkspace.TogglePanel("runs");
                return true;
            case Services.PaneCommand.OpenModeMenu:
                ActiveSurface.OpenModeMenu();
                return true;
            case Services.PaneCommand.OpenModelMenu:
                ActiveSurface.OpenModelMenu();
                return true;
            case Services.PaneCommand.OpenEffortMenu:
                ActiveSurface.ToggleEffortMenu();
                return true;
            case Services.PaneCommand.CycleChipLevel:
                return ActiveSurface.CycleChipLevel();
            case Services.PaneCommand.ToggleSelectionMode:
                _codeWorkspace.ToggleBrowserPicker();
                return true;
            case Services.PaneCommand.NewPreviewTab:
                _codeWorkspace.OpenBrowserTab();
                return true;
            case Services.PaneCommand.JumpPrevPrompt:
                ActiveSurface.JumpToPrompt(-1);
                return true;
            case Services.PaneCommand.JumpNextPrompt:
                ActiveSurface.JumpToPrompt(1);
                return true;
            case Services.PaneCommand.ToggleDiffFileList:
                // The reference folds the changed-file list away without touching
                // the pane itself, so this does not open one that is closed.
                _codeWorkspace.ChangesPanel.ShowFiles = !_codeWorkspace.ChangesPanel.ShowFiles;
                return true;
            case Services.PaneCommand.AttachTerminalOutput:
                if (_codeWorkspace.TerminalSelection is { } selection)
                {
                    _codeWorkspace.ActiveChatSurface.AttachContextText(selection);
                }
                else
                {
                    ActiveSurface.OpenAddFilesDialog();
                }

                return true;
            default:
                return false;
        }
    }

    private void OnGlobalKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // An open effort popover eats its keys first, wherever focus sits —
        // the reference intercepts composer-menu keys at the window level.
        if (ActiveSurface.EffortMenuHandleKey(e))
        {
            return;
        }

        var ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        var shift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
        var alt = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt);

        // The comparison view votes on a bare ArrowLeft/ArrowRight while its
        // question is up, which is the reference's own window-level listener.
        if (!ctrl && !shift && !alt && _chatSurface.HandleComparisonKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        // Alt chords surface as Key.System with the real key in SystemKey.
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        // User chords from keybindings.json run before the built-ins, but only
        // for gestures the built-ins don't already own (they return early).
        if (_userKeybindings.Count > 0 &&
            Services.UserKeybindings.Match(_userKeybindings, System.Windows.Input.Keyboard.Modifiers, key) is { } action)
        {
            e.Handled = true;
            RunKeybindingAction(action);
            return;
        }

        // The reference's pane keymap, which owns the chords its shortcut sheet
        // advertises; the window's own chords follow for everything it has no
        // row for.
        if (IsCodeActive && Services.PaneKeymap.Match(System.Windows.Input.Keyboard.Modifiers, key) is { } paneCommand &&
            RunPaneCommand(paneCommand))
        {
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == System.Windows.Input.Key.T)
        {
            if (_services.UiSettings.Current.ThemePickerHotkeyEnabled)
            {
                e.Handled = true;
                OpenThemePicker();
            }
        }
        else if (ctrl && !shift && e.Key == System.Windows.Input.Key.N)
        {
            e.Handled = true;
            StartNewSession();
        }
        else if (ctrl && e.Key == System.Windows.Input.Key.OemComma)
        {
            // The reference binds settings to cmd+, on its desktop build and
            // registers ctrl+shift+, in the command registry besides.
            e.Handled = true;
            OpenSettings();
        }
        else if (ctrl && !shift && !alt && e.Key == System.Windows.Input.Key.OemPeriod)
        {
            // The registry's toggle_sidebar.
            e.Handled = true;
            SetSidebarCollapsed(SidebarColumn.Width.Value > 0);
        }
        else if (ctrl && !shift && !alt && e.Key == System.Windows.Input.Key.D)
        {
            // The registry's toggle_dictation.
            e.Handled = true;
            ActiveSurface.ToggleDictation();
        }
        else if (ctrl && shift && !alt && e.Key == System.Windows.Input.Key.N)
        {
            // The registry's new_code_session_from_current.
            e.Handled = true;
            StartSessionWithCurrentSettings();
        }
        else if (IsCodeActive && ctrl && !shift && !alt && key == System.Windows.Input.Key.U)
        {
            // The registry's file_upload, which the Composer section advertises.
            e.Handled = true;
            ActiveSurface.OpenAddFilesDialog();
        }
        else if (IsCodeActive && ctrl && alt && !shift && key == System.Windows.Input.Key.L)
        {
            // The sheet's "Copy session link".
            e.Handled = true;
            CopySessionLink(ActiveSurface.ViewModel.Session.Id);
        }
        else if (ctrl && alt && !shift &&
                 key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)
        {
            // The reference's previous/next surface, which its sheet lists as
            // "Next sidebar tab" / "Previous sidebar tab": here the two surfaces are Chat and Code.
            e.Handled = true;
            var toCode = key == System.Windows.Input.Key.Right ? !IsCodeActive : IsCodeActive;
            if (toCode)
            {
                CodeTab.IsChecked = true;
            }
            else
            {
                ChatTab.IsChecked = true;
            }
        }
        else if (ctrl && !shift && e.Key == System.Windows.Input.Key.F)
        {
            e.Handled = true;
            OpenFind();
        }
        else if (!ctrl && !alt && e.Key == System.Windows.Input.Key.F3)
        {
            // The reference registers F3 and Shift+F3 for the two find steps, and
            // hides a second pair on Ctrl+G / Ctrl+Shift+G behind them.
            e.Handled = true;
            StepFind(shift ? -1 : 1);
        }
        else if (ctrl && !alt && e.Key == System.Windows.Input.Key.G)
        {
            e.Handled = true;
            StepFind(shift ? -1 : 1);
        }
        else if (ctrl && shift && e.Key == System.Windows.Input.Key.Y && IsCodeActive)
        {
            e.Handled = true;
            _codeWorkspace.ToggleChangesFiles();
        }
        else if (ctrl && !shift && e.Key == System.Windows.Input.Key.K)
        {
            e.Handled = true;
            OpenCommandPalette();
        }
        else if (ctrl && shift && e.Key == System.Windows.Input.Key.E)
        {
            e.Handled = true;
            ActiveSurface.ToggleEffortMenu();
        }
        else if (ctrl && shift && !alt && e.Key == System.Windows.Input.Key.M)
        {
            e.Handled = true;
            ActiveSurface.OpenModeMenu();
        }
        else if (IsCodeActive && ctrl && shift && !alt && e.Key == System.Windows.Input.Key.L)
        {
            // The reference's Ctrl+Shift+L: the terminal selection becomes a
            // context chip; with none, the file picker opens instead.
            e.Handled = true;
            if (_codeWorkspace.TerminalSelection is { } selection)
            {
                _codeWorkspace.ActiveChatSurface.AttachContextText(selection);
            }
            else
            {
                ActiveSurface.OpenAddFilesDialog();
            }
        }
        else if (ctrl && shift && !alt && e.Key == System.Windows.Input.Key.I)
        {
            // The reference gives Ctrl+Shift+I to an incognito chat; on Code, which
            // has no such thing, this port's own model chord stands.
            e.Handled = true;
            if (IsCodeActive)
            {
                ActiveSurface.OpenModelMenu();
            }
            else
            {
                _chatSurface.StartIncognito();
            }
        }
        else if (!IsCodeActive && ctrl && shift && !alt
                 && e.Key is System.Windows.Input.Key.OemPeriod or System.Windows.Input.Key.Decimal)
        {
            // Ctrl+Shift+. is the reference's model selector chord on Chat.
            e.Handled = true;
            ActiveSurface.OpenModelMenu();
        }
        else if (!IsCodeActive && ctrl && !shift && !alt && e.Key == System.Windows.Input.Key.U)
        {
            e.Handled = true;
            ActiveSurface.OpenAddFilesDialog();
        }
        else if (!IsCodeActive && ctrl && !alt && e.Key == System.Windows.Input.Key.OemSemicolon)
        {
            // Ctrl+; is the reference's own side-chat chord.
            e.Handled = true;
            _chatSurface.ToggleSideChat();
        }
        else if (IsCodeActive && ctrl && !alt && !shift && key == System.Windows.Input.Key.W)
        {
            e.Handled = true;
            ActiveSurface.StartNew();
            RefreshSessionList();
        }
        else if (IsCodeActive && ctrl && !alt && key == System.Windows.Input.Key.Tab)
        {
            e.Handled = true;
            _ = StepSessionAsync(shift ? -1 : 1);
        }
        else if (IsCodeActive && ctrl && shift && !alt && key == System.Windows.Input.Key.OemCloseBrackets)
        {
            e.Handled = true;
            _ = StepSessionAsync(1);
        }
        else if (IsCodeActive && ctrl && shift && !alt && key == System.Windows.Input.Key.OemOpenBrackets)
        {
            e.Handled = true;
            _ = StepSessionAsync(-1);
        }
        else if (IsCodeActive && ctrl && !shift && !alt && key == System.Windows.Input.Key.OemCloseBrackets)
        {
            e.Handled = true;
            _codeWorkspace.CycleFocus(1);
        }
        else if (IsCodeActive && ctrl && !shift && !alt && key == System.Windows.Input.Key.OemOpenBrackets)
        {
            e.Handled = true;
            _codeWorkspace.CycleFocus(-1);
        }
        else if (IsCodeActive && ctrl && !shift && !alt && key == System.Windows.Input.Key.Oem5)
        {
            // Ctrl+\ — close the open side panel first, else the focused tile.
            e.Handled = true;
            if (_codeWorkspace.HasOpenPanel)
            {
                _codeWorkspace.ShowPanel(null);
            }
            else
            {
                _codeWorkspace.ClosePane(_codeWorkspace.ActiveChatSurface);
            }
        }
        else if (IsCodeActive && ctrl && !alt && !shift &&
                 key is >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D9)
        {
            e.Handled = true;
            _ = JumpToSessionAsync(key - System.Windows.Input.Key.D0);
        }
        else if (IsCodeActive && ctrl && !shift && !alt && key == System.Windows.Input.Key.O)
        {
            // The reference's Ctrl+O: cycle the transcript view.
            e.Handled = true;
            ActiveSurface.CycleTranscriptView();
        }
        else if (IsCodeActive && ctrl && alt && !shift && key == System.Windows.Input.Key.P)
        {
            e.Handled = true;
            var id = ActiveSurface.ViewModel.Session.Id;
            _services.SessionGroups.SetPinned(id, !_services.SessionGroups.IsPinned(id));
        }
        else if (IsCodeActive && ctrl && alt && !shift && key == System.Windows.Input.Key.R)
        {
            // Ctrl+Alt+R and F2 both open the header's in-place editor, which is where
            // the reference renames a session from.
            e.Handled = true;
            ActiveSurface.BeginHeaderRename();
        }
        else if (IsCodeActive && !ctrl && !alt && !shift && key == System.Windows.Input.Key.F2)
        {
            e.Handled = true;
            ActiveSurface.BeginHeaderRename();
        }
        else if (IsCodeActive && ctrl && alt && !shift && key == System.Windows.Input.Key.U)
        {
            e.Handled = true;
            var id = ActiveSurface.ViewModel.Session.Id;
            _services.SessionGroups.SetUnread(id, !_services.SessionGroups.IsUnread(id));
        }
        else if (IsCodeActive && ctrl && alt && !shift && key == System.Windows.Input.Key.G)
        {
            e.Handled = true;
            _ = OpenActiveSessionPrAsync();
        }
        else if (IsCodeActive && ctrl && alt && !shift && key == System.Windows.Input.Key.O)
        {
            e.Handled = true;
            _ = ForkActiveSessionAsync();
        }
        else if (IsCodeActive && ((ctrl && alt && !shift && key == System.Windows.Input.Key.A) ||
                                 (ctrl && shift && !alt && key == System.Windows.Input.Key.Back)))
        {
            // The sheet lists both of the reference's archive chords.
            e.Handled = true;
            var id = ActiveSurface.ViewModel.Session.Id;
            _services.SessionGroups.SetArchived(id, !_services.SessionGroups.IsArchived(id));
        }
        else if (ctrl && !shift && e.Key == System.Windows.Input.Key.Oem2)
        {
            e.Handled = true;
            OpenShortcuts();
        }
        else if (ctrl && e.Key is System.Windows.Input.Key.OemPlus or System.Windows.Input.Key.Add)
        {
            e.Handled = true;
            StepZoom(1);
        }
        else if (ctrl && e.Key is System.Windows.Input.Key.OemMinus or System.Windows.Input.Key.Subtract)
        {
            e.Handled = true;
            StepZoom(-1);
        }
        else if (ctrl && !shift && e.Key is System.Windows.Input.Key.D0 or System.Windows.Input.Key.NumPad0)
        {
            e.Handled = true;
            ApplyZoom(1.0);
        }
        else if (e.Key == System.Windows.Input.Key.Escape &&
                 OverlayHost.Content is CommandPaletteView or ShortcutsView)
        {
            e.Handled = true;
            CloseOverlay();
        }
    }

    private bool IsCodeActive => CodeTab.IsChecked == true;

    private ChatSurface ActiveSurface => IsCodeActive ? _codeWorkspace.ActiveChatSurface : _chatSurface;

    private JsonSessionStore ActiveStore => IsCodeActive ? _services.Sessions : _services.ChatSessions;

    private Services.SessionListCache? _codeList;
    private Services.SessionListCache? _chatList;

    /// <summary>Cached sidebar listing; only changed session files are re-read.</summary>
    private Services.SessionListCache ActiveList => IsCodeActive
        ? _codeList ??= new Services.SessionListCache(
            _services.Sessions, System.IO.Path.Combine(_services.Paths.SessionsDirectory, "code"))
        : _chatList ??= new Services.SessionListCache(
            _services.ChatSessions, System.IO.Path.Combine(_services.Paths.SessionsDirectory, "chat"));

    /// <summary>
    /// Chat carries no agentic tools of its own. What it may carry are the two
    /// opt-ins: desktop control, from Settings, and the connectors this
    /// conversation switched on in the composer's tools menu — which is where the
    /// reference keeps that choice too, per conversation rather than per account.
    /// </summary>
    private void RebuildChatTools()
    {
        var tools = new List<JarvisCode.Core.Tools.ITool>();
        if (_services.UiSettings.Current.ComputerUseInChat)
        {
            tools.AddRange(Services.ComputerUseTools.CreateIfEnabled(_services.UiSettings));
        }

        var enabled = _services.ChatConversations
            .Get(_chatSurface.ViewModel.Session.Id, _services.Settings.Current.EnableWebSearch)
            .Connectors;
        if (enabled.Count > 0)
        {
            tools.AddRange(_services.Mcp.Tools.Where(tool =>
                tool is JarvisCode.Core.Mcp.McpToolAdapter adapter
                && enabled.Contains(adapter.ServerName, StringComparer.OrdinalIgnoreCase)));
        }

        _chatSurface.ViewModel.ExtraTools = tools;
    }

    private void ApplySurface()
    {
        if (_customizeActive)
        {
            ExitCustomize(applySurface: false);
        }

        SurfaceHost.Content = IsCodeActive ? _codeWorkspace : _chatSurface;
        Sidebar.SetSurface(IsCodeActive);
        _services.UiSettings.Current.LastSurface = IsCodeActive ? "code" : "chat";
        RefreshSessionList();
        ActiveSurface.FocusInput();
    }

    // ---- notifications (finished / needs input while away) ----

    private readonly Dictionary<string, DateTime> _lastAttentionPing = new(StringComparer.Ordinal);

    /// <summary>
    /// Tray balloon when Jarvis finishes or stalls while the window is in the
    /// background. Which of the three notification types this is decides the level
    /// the user picked for it — Off, Badge only or Banners — and "Draw attention on
    /// notifications" flashes the taskbar button on top of it, both from
    /// Settings › Jarvis Code › Local sessions (see <see cref="Services.NotificationPolicy"/>).
    /// </summary>
    private void NotifyIfAway(string title, ViewModels.ChatViewModel viewModel, string notificationType)
    {
        // notification hooks hear about the ping even when the balloon is off.
        viewModel.RunNotificationHooks(title);

        var ui = _services.UiSettings.Current;
        if (!ui.NotifyWhenDone)
        {
            return;
        }

        // Only ping an away user: hidden to tray, minimized, or another app in front.
        var away = !(IsVisible && IsActive && WindowState != WindowState.Minimized);
        if (!away)
        {
            return;
        }

        // A turn can raise several permission prompts; one ping per session per while.
        var key = $"{title}|{viewModel.Session.Id}";
        var now = DateTime.UtcNow;
        if (_lastAttentionPing.TryGetValue(key, out var last) && (now - last).TotalSeconds < 45)
        {
            return;
        }

        _lastAttentionPing[key] = now;

        if (Services.NotificationPolicy.ShouldFlash(ui.DrawAttentionOnNotifications, appFocusedAndVisible: !away))
        {
            FlashTaskbarButton();
        }

        // The taskbar badge counts what is waiting, which is the other half of the
        // level policy: "Badge only" shows no banner but still has to be counted.
        NoteBlockedOnUser(notificationType, viewModel.Session.Id);

        if (!Services.NotificationPolicy.ShowsBanner(ui.NotificationLevels, notificationType))
        {
            return;
        }

        var session = viewModel.Session.Title;
        _tray?.ShowNotification(
            string.IsNullOrWhiteSpace(session) ? LocalSessionTitle : session,
            title,
            Services.NotificationPolicy.IsSilent(ui.NotificationSound));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    /// <summary>
    /// The reference's <c>requestUserAttention</c> on Windows: flash the taskbar
    /// button until the window comes forward (FLASHW_TRAY | FLASHW_TIMERNOFG).
    /// </summary>
    private void FlashTaskbarButton()
    {
        if (PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource source)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FlashInfo>(),
            Window = source.Handle,
            Flags = 0x00000002 | 0x0000000C, // FLASHW_TRAY | FLASHW_TIMERNOFG
            Count = uint.MaxValue,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    // ---- auto-archive (idle sessions) ----

    private System.Windows.Threading.DispatcherTimer? _autoArchiveTimer;

    private void StartAutoArchive()
    {
        _ = RunAutoArchiveAsync();
        _autoArchiveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(6),
        };
        _autoArchiveTimer.Tick += (_, _) => _ = RunAutoArchiveAsync();
        _autoArchiveTimer.Start();
        StartWellbeing();
    }

    // ---- /wellbeing (break reminders) ----

    private System.Windows.Threading.DispatcherTimer? _wellbeingTimer;
    private DateTimeOffset _wellbeingAnchor = DateTimeOffset.Now;

    /// <summary>Minute tick; the anchor keeps sliding while reminders are off, so
    /// enabling /wellbeing starts counting from that moment.</summary>
    private void StartWellbeing()
    {
        _wellbeingTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _wellbeingTimer.Tick += (_, _) =>
        {
            var minutes = _services.UiSettings.Current.WellbeingMinutes;
            if (minutes <= 0)
            {
                _wellbeingAnchor = DateTimeOffset.Now;
            }
            else if (DateTimeOffset.Now - _wellbeingAnchor >= TimeSpan.FromMinutes(minutes))
            {
                _wellbeingAnchor = DateTimeOffset.Now;
                _tray?.ShowNotification(
                    "Time for a break",
                    $"You've been at it for {minutes} minutes — stretch, water, eyes off the screen.");
            }
        };
        _wellbeingTimer.Start();
    }

    /// <summary>
    /// The reference's auto-archive: local sessions idle past the configured period are
    /// archived; running sessions never are. Archiving is reversible from the sidebar.
    /// </summary>
    /// <summary>
    /// Team directories outlive a process that died mid-run, so a session that
    /// no longer exists has its team swept here — the reference cleans up its
    /// session teams the same way.
    /// </summary>
    private void SweepOrphanTeams()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var store = new JarvisCode.Core.Agent.TeamStore(_services.Paths.TeamsDirectory);
                var sessions = await _services.Sessions.ListAsync();
                var chat = await _services.ChatSessions.ListAsync();
                store.RemoveOrphans(sessions.Concat(chat).Select(s => s.Id));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                           or System.Text.Json.JsonException or NotSupportedException)
            {
                // A sweep that cannot read the session list simply waits for the
                // next launch; nothing depends on it having run.
            }
        });
    }

    private async Task RunAutoArchiveAsync()
    {
        var days = _services.UiSettings.Current.AutoArchiveDays;
        if (days <= 0)
        {
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.Now.AddDays(-days);
            _codeList ??= new Services.SessionListCache(
                _services.Sessions, System.IO.Path.Combine(_services.Paths.SessionsDirectory, "code"));
            var sessions = await _codeList.ListAsync();
            var running = _codeWorkspace.ChatSurface.RunningSessionIds;
            var archivedAny = false;
            foreach (var session in sessions)
            {
                if (session.UpdatedAt < cutoff &&
                    !running.Contains(session.Id) &&
                    session.Id != _codeWorkspace.ChatSurface.ViewModel.Session.Id &&
                    !_services.SessionGroups.IsArchived(session.Id))
                {
                    _services.SessionGroups.SetArchived(session.Id, true);
                    archivedAny = true;
                }
            }

            if (archivedAny)
            {
                RefreshSessionList();
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            // Auto-archive is housekeeping; never let it surface as a crash.
        }
    }

    // ---- routines (Scheduled) ----

    private Services.RoutineRunner? _routineRunner;
    private RoutinesView? _routinesView;

    private Services.RoutineRunner EnsureRoutineRunner()
    {
        if (_routineRunner is null)
        {
            _routineRunner = new Services.RoutineRunner(_services);
            _routineRunner.RoutineCompleted += (routine, session) => Dispatcher.InvokeAsync(() =>
            {
                if (routine.NotifyOnCompletion)
                {
                    _tray?.ShowNotification(
                        "Routine finished", $"{routine.Name} — open \"{session.Title}\" in the sidebar.");
                }

                RefreshSessionList();
                RefreshScheduledCount();
                _routinesView?.Render();
            });

            // "Get a desktop notification when a routine run fails" — the one
            // notification that matters for work nobody watched.
            _routineRunner.RoutineFailed += (routine, _) => Dispatcher.InvokeAsync(() =>
            {
                if (_services.UiSettings.Current.NotifyOnRoutineFailure)
                {
                    _tray?.ShowNotification(
                        $"Routine “{RoutineLabel(routine)}” failed", "Open the run to see what happened.");
                }
            });

            _routineRunner.RoutineStarted += routine => Dispatcher.InvokeAsync(
                () => ShowRoutineNotice($"Routine “{RoutineLabel(routine)}” started."));

            _routineRunner.RoutineMissed += (routine, slot) => Dispatcher.InvokeAsync(() => ShowRoutineNotice(
                $"Routine “{RoutineLabel(routine)}” missed at " +
                $"{slot.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}. Running now."));

            _routineRunner.RoutineSkipped += (routine, reason) => Dispatcher.InvokeAsync(() =>
            {
                ShowRoutineNotice(reason == Services.RoutineSkipReasons.PerTaskLimit
                    ? "The previous run was still in progress."
                    : "Other routines were already running.");
                _routinesView?.Render();
            });
        }

        return _routineRunner;
    }

    public void OpenRoutines(string? pose = null, string? routineId = null)
    {
        if (_customizeActive)
        {
            ExitCustomize(applySurface: false);
        }

        if (_routinesView is null)
        {
            _routinesView = new RoutinesView();
            _routinesView.Initialize(
                EnsureRoutineRunner(),
                () => [.. _services.Settings.Models.Select(static m => m.ModelId)],
                _services.UiSettings.Current,
                _services.UiSettings.Save);
            _routinesView.BackRequested += (_, _) => ApplySurface();

            // "Create with Jarvis" and the template cards hand the page's prompt
            // to a fresh Code session, which is where the routine gets set up.
            _routinesView.SetupRequested += (_, prompt) =>
            {
                ApplySurface();
                StartNewSession();
                ActiveSurface.PrefillInput(prompt);
            };

            _routinesView.SessionRequested += (_, sessionId) =>
            {
                ApplySurface();
                _ = OpenSessionAsync(sessionId);
            };
        }

        switch (pose)
        {
            case null when routineId is { } id:
                // "Go to routine" lands on the routine's own detail page.
                _routinesView.ShowDetail(id);
                break;
            case "editor":
                _routinesView.ShowEditor();
                break;
            case "detail" when _routinesView.Items().FirstOrDefault() is { } first:
                _routinesView.ShowDetail(first.Id);
                break;
            case "approvals":
                // Poses the reference's Always-allowed row with grants on it: a
                // browser approval and two tool rules a run collected.
                _routinesView.ShowDetail(PoseRoutineWithApprovals());
                break;
            default:
                _routinesView.ShowList();
                break;
        }

        RefreshScheduledCount();
        SurfaceHost.Content = _routinesView;
    }

    /// <summary>
    /// Dev/verification hook: a routine carrying the grants the detail page's
    /// Always-allowed section draws, written into the profile it is posed in.
    /// </summary>
    private string PoseRoutineWithApprovals()
    {
        var runner = EnsureRoutineRunner();
        var routines = runner.Store.Load();
        var routine = routines.FirstOrDefault(r => r.Name == "Nightly review")
            ?? new JarvisCode.Core.Routines.Routine { Name = "Nightly review" };
        routine.Description = "Reads yesterday's commits and reports anything risky.";
        routine.Instruction = "Review the commits from the last 24 hours.";
        routine.CronExpression = "0 9 * * *";
        routine.PermissionModeName = null;
        routine.ChromePermissionMode = Services.ChromePermissionModes.FollowAPlan;
        routine.ChromeAllowedDomains = ["https://github.com", "https://example.test"];
        routine.ApprovedPermissions =
        [
            Services.RoutineApprovals.Key("Bash", "git log"),
            Services.RoutineApprovals.Key("mcp__linear__list_issues", null),
        ];
        if (!routines.Contains(routine))
        {
            routines.Add(routine);
        }

        runner.Store.Save(routines);
        _routinesView!.Render();
        return routine.Id;
    }

    /// <summary>The name a notification calls a routine, falling back to its id.</summary>
    private static string RoutineLabel(JarvisCode.Core.Routines.Routine routine) =>
        routine.Name is { Length: > 0 } name ? name : Services.ScheduledTaskPresentation.NameFromId(routine.Id);

    /// <summary>
    /// A routine notice — a toast, which is where the reference puts these. The
    /// tray still carries the two that must reach someone who is not looking at
    /// the app at all: a finished run and a failed one.
    /// </summary>
    private void ShowRoutineNotice(string message) => _services.Toasts.AddSuccess(message);

    /// <summary>Keeps the sidebar's Scheduled row showing how many runs there have been.</summary>
    private void RefreshScheduledCount()
    {
        if (_routineRunner is null)
        {
            return;
        }

        var runs = _routineRunner.Store.Load().Sum(r => _routineRunner.Runs.RunCount(r.Id));
        Sidebar.SetScheduledRunCount(runs);
    }

    // ---- customize mode ----

    public void EnterCustomize(string? page = null)
    {
        if (_customizeSurface is null)
        {
            _customizeSurface = new Customize.CustomizeSurface();
            _customizeSurface.Initialize(_services, () => ActiveSurface.ViewModel.Session.WorkingDirectory);
            _customizeSurface.PluginsChanged += (_, _) => RefreshCustomizeNav();
            _customizeSurface.SkillsChanged += (_, _) => Services.SkillCatalog.InvalidateCache();
            _customizeSurface.ToolPoliciesChanged += (_, _) => ApplyToolPolicies();
            // "Create with Claude" hands the prompt to a Code session in this project
            // rather than running it here: skill authoring is a turn, not a form.
            _customizeSurface.CodeSessionRequested += (_, prompt) =>
            {
                ExitCustomize();
                CodeTab.IsChecked = true;
                _codeWorkspace.ChatSurface.PrefillInput(prompt);
            };
        }

        if (!_customizeActive)
        {
            _customizeActive = true;
            Sidebar.Visibility = Visibility.Collapsed;
            CustomizeNav.Visibility = Visibility.Visible;
            _autoCollapsedSidebar = false;
            SidebarColumn.Width = new GridLength(_sidebarWidth);
            SidebarSplitter.Visibility = Visibility.Visible;
            SurfaceHost.Content = _customizeSurface;
            _customizeSurface.ShowHub();
        }

        RefreshCustomizeNav();
        switch (page)
        {
            case "skills":
                _customizeSurface.ShowSkills();
                break;
            case "connectors":
                _customizeSurface.ShowConnectors();
                break;
            case "plugins":
                _customizeSurface.ShowPlugins();
                break;
            case "memory":
                _customizeSurface.ShowMemory();
                break;
            case "directory":
                _customizeSurface.ShowSkillDirectory();
                break;
            case "orgplugins":
                _customizeSurface.ShowOrgPlugins();
                break;
        }
    }

    private void ExitCustomize(bool applySurface = true)
    {
        _customizeActive = false;
        CustomizeNav.Visibility = Visibility.Collapsed;
        ApplySidebarVisual(_services.UiSettings.Current.SidebarCollapsed);
        if (applySurface)
        {
            SurfaceHost.Content = IsCodeActive ? (object)_codeWorkspace : _chatSurface;
            ActiveSurface.FocusInput();
        }
    }

    private void RefreshCustomizeNav()
    {
        try
        {
            var plugins = Services.PluginLibrary.LoadAllScopes(
                _services.Paths, ActiveSurface.ViewModel.Session.WorkingDirectory);
            CustomizeNav.ShowPlugins(plugins.Installed);
        }
        catch (System.IO.IOException)
        {
            CustomizeNav.ShowPlugins([]);
        }

        CustomizeNav.ShowOrganizationFolder(
            _services.UiSettings.Current.OrganizationPluginsFolder.Length > 0);
    }

    /// <summary>
    /// Hands the organization's tool policy to every open session's gate: an
    /// allow or a block becomes a rule line, and "Ask each time" a tool the gate
    /// never settles on its own.
    /// </summary>
    private void ApplyToolPolicies()
    {
        var policies = _services.UiSettings.Current.McpToolPolicies;
        var lines = Services.ToolPolicies.ToRuleLines(policies);
        var alwaysAsk = Services.ToolPolicies.AlwaysAskTools(policies);
        foreach (var surface in _codeWorkspace.AllCodeSurfaces)
        {
            surface.ViewModel.Gate.AddSessionRuleLines(lines);
            surface.ViewModel.Gate.AlwaysAskTools = alwaysAsk;
        }
    }

    private void RefreshSessionList()
    {
        _ = RefreshSessionListAsync();
    }

    private async Task RefreshSessionListAsync()
    {
        if (_poseSessions is { } posed)
        {
            Sidebar.ShowSessions(posed, groupByProject: true, "pose-unread", null,
                runningSessionIds: ["pose-running"], awaitingSessionIds: ["pose-awaiting"]);
            return;
        }

        try
        {
            var sessions = await ActiveList.ListAsync();
            if (!IsCodeActive)
            {
                // The reference's first-chat onboarding is exactly that: the first
                // chat. A session with nothing in it does not count as one yet.
                _chatSurface.SetChatCount(
                    sessions.Count(s => s.Id != _chatSurface.ViewModel.Session.Id));
            }

            var running = IsCodeActive
                ? _codeWorkspace.AllCodeSurfaces.SelectMany(static s => s.RunningSessionIds).Distinct().ToList()
                : ActiveSurface.RunningSessionIds;
            Sidebar.ShowSessions(
                sessions,
                groupByProject: IsCodeActive,
                activeSessionId: ActiveSurface.ViewModel.Session.Id,
                filter: Sidebar.FilterText,
                runningSessionIds: running);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            // The sidebar list is cosmetic; a bad session file must not crash the shell.
        }
    }

    /// <summary>A jarvis-code://session/{id} link, opened in the Code surface.</summary>
    public void OpenSessionById(string sessionId)
    {
        if (!IsCodeActive)
        {
            CodeTab.IsChecked = true;
        }

        _ = OpenSessionAsync(sessionId);
    }

    private async Task OpenSessionAsync(string sessionId)
    {
        // With the split open, a session already showing on another tile gets
        // focused there instead of being opened twice with two writers.
        if (IsCodeActive)
        {
            var other = _codeWorkspace.Panes.FirstOrDefault(p =>
                !ReferenceEquals(p, ActiveSurface) && p.ViewModel.Session.Id == sessionId);
            if (other is not null)
            {
                other.FocusInput();
                return;
            }
        }

        // The reference's `isTitleLoading`: the bar shows its skeleton rather than the
        // previous session's name while this one is still being read off disk.
        ActiveSurface.TitleLoading = true;
        Core.Sessions.Session? session;
        try
        {
            session = await ActiveStore.LoadAsync(sessionId);
        }
        finally
        {
            ActiveSurface.TitleLoading = false;
        }

        if (session is null)
        {
            // The row is listed and the store cannot load it: the reference's
            // "Session not found on disk" card, over a session that starts fresh
            // in the folder the row named.
            await ShowSessionNotFoundAsync(sessionId);
            return;
        }

        ActiveSurface.LoadSession(session);
        if (IsCodeActive)
        {
            _services.SessionGroups.SetUnread(sessionId, false);
            ArmPluginMonitors(session.WorkingDirectory, session.Id);
        }

        RefreshSessionList();
    }

    /// <summary>
    /// Binds a placeholder session on the folder the missing row named and shows the
    /// reference's card over it, so "Send a message to start fresh in this
    /// directory" is literally what happens next.
    /// </summary>
    private async Task ShowSessionNotFoundAsync(string sessionId)
    {
        Core.Sessions.SessionSummary? summary = null;
        try
        {
            summary = (await ActiveList.ListAsync()).FirstOrDefault(s => s.Id == sessionId);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            // The listing is only there for the folder to start fresh in.
        }

        if (!Services.SessionNotFound.ShouldShow(summary is not null, loaded: false))
        {
            return;
        }

        var placeholder = Core.Sessions.Session.CreateNew(
            summary?.WorkingDirectory is { Length: > 0 } cwd ? cwd : Environment.CurrentDirectory);
        if (summary is not null)
        {
            placeholder.Title = summary.Title;
        }

        ActiveSurface.LoadSession(placeholder);
        ActiveSurface.ShowSessionNotFound(sessionId);
    }

    // ---- plugin monitors ----

    /// <summary>
    /// Arms the plugin monitors a session's plugins declare and delivers each stdout
    /// line to it as a task notification, which is what the reference's host does:
    /// "Each stdout line is delivered to the model as a &lt;task_notification&gt;
    /// event; the process runs for the session lifetime." The <c>always</c> monitors
    /// arm when a Code session opens; an <c>on-skill-invoke:</c> one arms the first
    /// time that skill is dispatched.
    /// </summary>
    private void ArmPluginMonitors(string cwd, string sessionId, string? invokedSkill = null)
    {
        if (cwd.Length == 0)
        {
            return;
        }

        var plugins = Services.PluginLibrary
            .LoadActive(_services.Paths, cwd, _services.UiSettings.Current).Installed;
        var discovered = Services.PluginMonitors.Discover(plugins.Select(static p => p.Directory));
        foreach (var (monitor, root) in Services.PluginMonitors.ArmedBy(discovered, invokedSkill))
        {
            _services.PluginMonitors.Arm(
                monitor, root, System.IO.Path.Combine(root, "data"), cwd, sessionId);
        }
    }

    private void WirePluginMonitors()
    {
        _services.PluginMonitors.LineReceived += (monitor, sessionId, line) =>
            Dispatcher.BeginInvoke(() =>
            {
                var target = _codeWorkspace.AllCodeSurfaces
                    .Select(static s => s.ViewModel)
                    .FirstOrDefault(vm => vm.Session.Id == sessionId);
                target?.DeliverTaskNotification(
                    monitor.Key,
                    "running",
                    monitor.Description is { Length: > 0 } text ? text : monitor.Name,
                    line);
            });
        Services.SkillCatalog.SkillInvoked += (skillName, cwd) =>
            Dispatcher.BeginInvoke(() =>
                ArmPluginMonitors(cwd, ActiveSurface.ViewModel.Session.Id, skillName));
    }

    /// <summary>Everything a chat surface needs from the window, fixed panes and grid panes alike.</summary>
    private void WireSurface(ChatSurface surface)
    {
        // The session-not-found card's three actions.
        surface.ImportCliSessionsRequested += (_, _) => _ = ImportCliSessionsAsync();
        surface.SessionArchiveRequested += (_, id) =>
        {
            _services.SessionGroups.SetArchived(id, true);
            RefreshSessionList();
            StartNewSession();
        };
        surface.SessionDeleteRequested += (_, id) => _ = DeleteSessionAsync(id);

        // The branch-switch dialog's "{n} other sessions are using this folder" line.
        surface.OtherSessionWorkingDirectories = () =>
            [.. _codeWorkspace.Panes
                .Where(p => !ReferenceEquals(p, surface))
                .Select(p => p.ViewModel.Session.WorkingDirectory)
                .Where(static d => d.Length > 0)];
        surface.SessionPersisted += (_, _) =>
        {
            _services.UiSettings.Current.LastSessionId = ActiveSurface.ViewModel.Session.Id;
            RefreshSessionList();
        };
        // A session can now be running while another is on screen, so the sidebar's
        // spinners follow every turn on the surface, not just the visible one.
        surface.RunningStateChanged += (_, _) =>
        {
            RefreshSessionList();
            UpdateKeepAwake();
        };
        var isCodeSurface = !ReferenceEquals(surface, _chatSurface);
        surface.TurnFinished += vm =>
        {
            // "Teach mode ends automatically when your turn ends."
            _services?.Teach.End();
            // The pages the agent was driving stop showing it as active.
            _services?.Browser.EndSession();
            NotifyIfAway(TurnCompleteBody, vm, Services.NotificationPolicy.Idle);
            InstallStagedUpdateIfIdle();
            if (isCodeSurface)
            {
                MarkUnreadIfBackground(vm);
            }
        };
        surface.AttentionRequested += vm =>
            NotifyIfAway(AwaitingInputBody, vm, Services.NotificationPolicy.Permission);
        surface.SettingsRequested += (_, _) => OpenSettings();
        surface.ThemePickerRequested += (_, _) => OpenThemePicker();
        surface.CustomizeNavigateRequested += (_, page) => EnterCustomize(page);
        surface.ResumePickerRequested += (_, _) =>
        {
            SetSidebarCollapsed(false);
            Sidebar.FocusSearch();
        };
        surface.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.ChatViewModel.IsRunning))
            {
                RefreshSessionList();
            }
        };
    }

    /// <summary>Holds the machine awake while anything runs, when the setting asks for it.</summary>
    private void UpdateKeepAwake()
    {
        var active = _services.UiSettings.Current.KeepComputerAwake &&
            (_chatSurface.RunningSessionIds.Count > 0 ||
             _codeWorkspace.AllCodeSurfaces.Any(static s => s.RunningSessionIds.Count > 0));
        Services.KeepAwake.SetActive(active);
    }

    /// <summary>A turn ended in a session the user wasn't looking at — flag it unread.</summary>
    private void MarkUnreadIfBackground(ViewModels.ChatViewModel vm)
    {
        var id = vm.Session.Id;
        var visible = IsActive && IsCodeActive &&
            _codeWorkspace.Panes.Any(p => p.ViewModel.Session.Id == vm.Session.Id);
        if (!visible)
        {
            _services.SessionGroups.SetUnread(id, true);
        }
    }

    /// <summary>The sidebar's display order: pinned, then groups, then ungrouped by recency.</summary>
    private async Task<List<string>> VisibleSessionOrderAsync()
    {
        var sessions = await ActiveList.ListAsync();
        var groups = _services.SessionGroups;
        var byId = new HashSet<string>(sessions.Select(static s => s.Id), StringComparer.Ordinal);
        var order = new List<string>();
        void Add(string id)
        {
            if (byId.Contains(id) && !groups.IsArchived(id) && !order.Contains(id))
            {
                order.Add(id);
            }
        }

        foreach (var id in groups.Data.PinnedSessionIds)
        {
            Add(id);
        }

        foreach (var group in groups.Data.Groups)
        {
            foreach (var id in group.SessionIds)
            {
                Add(id);
            }
        }

        foreach (var summary in sessions.OrderByDescending(static s => s.UpdatedAt))
        {
            Add(summary.Id);
        }

        return order;
    }

    /// <summary>Ctrl+Shift+]/[ and Ctrl+Tab — cycle through the sidebar's sessions.</summary>
    private async Task StepSessionAsync(int delta)
    {
        var order = await VisibleSessionOrderAsync();
        if (order.Count == 0)
        {
            return;
        }

        var index = order.IndexOf(ActiveSurface.ViewModel.Session.Id);
        var next = index < 0
            ? (delta > 0 ? 0 : order.Count - 1)
            : (index + delta + order.Count) % order.Count;
        await OpenSessionAsync(order[next]);
    }

    /// <summary>
    /// The reference's jump-hint chord: Ctrl+Shift with neither Alt nor the Windows
    /// key (its `yB`).
    /// </summary>
    private static bool JumpHintChordHeld =>
        System.Windows.Input.Keyboard.Modifiers ==
        (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);

    /// <summary>
    /// Ctrl+1…9 — jump to the nth session in the sidebar. It follows the order the
    /// sidebar actually drew, so the row a hint keycap numbers is the row the digit
    /// opens; a surface with no rendered list falls back to the stored order.
    /// </summary>
    private async Task JumpToSessionAsync(int position)
    {
        var order = Sidebar.RenderedOrder.Count > 0
            ? [.. Sidebar.RenderedOrder]
            : await VisibleSessionOrderAsync();
        if (position >= 1 && position <= order.Count)
        {
            await OpenSessionAsync(order[position - 1]);
        }
    }

    private async Task ForkActiveSessionAsync()
    {
        _services.Toasts.AddSuccess(Services.ToastText.Forking);
        var (fork, _) = await Services.SessionActions.ForkAsync(_services, ActiveSurface.ViewModel.Session);
        if (fork is null)
        {
            _services.Toasts.AddError(Services.ToastText.ForkUnavailable);
            return;
        }

        ActiveSurface.LoadSession(fork);
        RefreshSessionList();
    }

    private async Task OpenActiveSessionPrAsync()
    {
        if (await Services.SessionActions.OpenPullRequestAsync(
                ActiveSurface.ViewModel.Session.WorkingDirectory) is not null)
        {
            _services.Toasts.AddError(Services.ToastText.PullRequestFailed);
        }
    }

    /// <summary>Sidebar row → "Open in split view".</summary>
    private async Task OpenInSplitViewAsync(string sessionId)
    {
        if (!IsCodeActive)
        {
            CodeTab.IsChecked = true;
        }

        var session = await _services.Sessions.LoadAsync(sessionId);
        if (session is null)
        {
            return;
        }

        _codeWorkspace.OpenSplitView(session);
        _services.SessionGroups.SetUnread(sessionId, false);
        RefreshSessionList();
    }

    private async Task DeleteSessionAsync(string sessionId)
    {
        if (!await ConfirmRemovalAsync(sessionId, Services.SessionDialogAction.Delete))
        {
            return;
        }

        try
        {
            await ActiveStore.DeleteAsync(sessionId);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _services.Toasts.AddError(Services.ToastText.DeleteFailed);
            return;
        }

        var surfaces = IsCodeActive
            ? _codeWorkspace.AllCodeSurfaces.ToArray()
            : [_chatSurface];
        foreach (var surface in surfaces)
        {
            if (surface.ViewModel.Session.Id == sessionId)
            {
                surface.StartNew();
            }

            // The session may have been running out of sight; its turn would otherwise
            // keep going and save the file straight back.
            surface.ForgetSession(sessionId);
        }

        RefreshSessionList();
    }

    /// <summary>
    /// The reference's confirm for removing a session: while it asks git what
    /// the worktree still holds the dialog says so and cannot be committed, and
    /// once it knows it either lists what would be discarded or asks the plain
    /// question.
    /// </summary>
    private async Task<bool> ConfirmRemovalAsync(string sessionId, Services.SessionDialogAction action)
    {
        var session = await LoadSessionQuietlyAsync(sessionId);
        var title = session?.Title is { Length: > 0 } named ? named : UntitledSession;
        var directory = session?.WorkingDirectory ?? "";

        if (Services.WorktreeChanges.IsLinkedWorktree(directory))
        {
            var changes = await Services.WorktreeChanges.UncommittedAsync(directory);
            if (changes.Count > 0)
            {
                return ConfirmDialog.Ask(
                    this, Services.SessionDialogs.Uncommitted(action, changes, moreSessionsFollow: false));
            }
        }

        return ConfirmDialog.Ask(
            this,
            action == Services.SessionDialogAction.Archive
                ? Services.SessionDialogs.ArchiveTitle
                : Services.SessionDialogs.DeleteTitle(1),
            Services.SessionDialogs.DeleteBody(title),
            action == Services.SessionDialogAction.Archive
                ? Services.SessionDialogs.Archive
                : Services.SessionDialogs.Delete,
            focusCancel: true);
    }

    private async Task<Session?> LoadSessionQuietlyAsync(string sessionId)
    {
        try
        {
            return await ActiveStore.LoadAsync(sessionId);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The name the delete prompt quotes. It is only decoration on the prompt, so an
    /// unreadable session still gets offered for deletion under the sidebar's own
    /// fallback label.
    /// </summary>
    private async Task<string> SessionTitleAsync(string sessionId)
    {
        Session? session = null;
        try
        {
            session = await ActiveStore.LoadAsync(sessionId);
        }
        catch (UnauthorizedAccessException)
        {
            // A locked file must not block the prompt; the fallback name covers it.
        }

        var title = session?.Title;
        return string.IsNullOrWhiteSpace(title) ? UntitledSession : title;
    }

    private async Task RenameSessionAsync(string sessionId, string newTitle)
    {
        if (ActiveSurface.ViewModel.Session.Id == sessionId)
        {
            ActiveSurface.ViewModel.Session.Title = newTitle;
            await ActiveStore.SaveAsync(ActiveSurface.ViewModel.Session);
        }
        else
        {
            var session = await ActiveStore.LoadAsync(sessionId);
            if (session is null)
            {
                return;
            }

            session.Title = newTitle;
            await ActiveStore.SaveAsync(session);
        }

        RefreshSessionList();
    }

    private async Task ExportSessionAsync(string sessionId)
    {
        var session = await ActiveStore.LoadAsync(sessionId);
        if (session is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export transcript",
            FileName = $"{session.Title}.md",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, JarvisCode.Core.Utilities.TranscriptExporter.Render(session));
            var exported = dialog.FileName;
            ShowToast("Session exported", "Show in folder", () => RevealInExplorer(exported));
        }
    }

    // ---- window chrome ----

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // A borderless maximized window bleeds past the monitor edge by the
        // resize border; pad the content back into view.
        WindowRoot.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void HookWndProc()
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
            _quickEntryHotkey = new Services.GlobalHotkey(source, id: 0xA11C);
            SyncQuickEntryHotkey();
            _services.UiSettings.Saved += (_, _) => Dispatcher.Invoke(SyncQuickEntryHotkey);
        }

    }

    private void SyncQuickEntryHotkey()
    {
        if (_quickEntryHotkey is null)
        {
            return;
        }

        if (_services.UiSettings.Current.QuickEntryEnabled)
        {
            _quickEntryHotkey.Register(_services.UiSettings.Current.QuickEntryShortcut);
        }
        else
        {
            _quickEntryHotkey.Unregister();
        }
    }

    /// <summary>
    /// Re-registers the Quick Entry chord after the settings page changed it, and
    /// reports what the OS said so the recorder can show the right sentence.
    /// </summary>
    public Services.HotkeyOutcome ReregisterQuickEntry()
    {
        if (_quickEntryHotkey is null || !_services.UiSettings.Current.QuickEntryEnabled)
        {
            return Services.HotkeyOutcome.None;
        }

        return _quickEntryHotkey.Register(_services.UiSettings.Current.QuickEntryShortcut);
    }

    /// <summary>Shows or hides the tray icon for the "System tray" switch.</summary>
    public void ApplyTrayVisibility()
    {
        if (_tray is not null)
        {
            _tray.IsVisible = _services.UiSettings.Current.ShowInSystemTray;
        }
    }

    private void SetupTray()
    {
        _tray = new Services.TrayService("Jarvis Code" + _services.Paths.WindowTitleSuffix);
        _tray.OpenRequested += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _tray.IsVisible = _services.UiSettings.Current.ShowInSystemTray;
        _tray.QuitRequested += (_, _) => Dispatcher.Invoke(() =>
        {
            SaveWindowBounds();
            Application.Current.Shutdown();
        });
        UpdateTrayColor();
    }

    private void UpdateTrayColor()
    {
        if (_tray is not null &&
            Application.Current.Resources["AccentBrandColor"] is System.Windows.Media.Color accent)
        {
            _tray.UpdateIcon(accent);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSettingChange && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            _services.Theme.RefreshSystemMode();
        }
        else if (msg == Services.GlobalHotkey.WmHotkey && _quickEntryHotkey?.Matches(wParam) == true)
        {
            handled = true;
            ToggleQuickEntry();
        }

        return IntPtr.Zero;
    }

    // ---- persistence ----

    private void RestoreWindowBounds()
    {
        var ui = _services.UiSettings.Current;
        if (ui.WindowWidth is > 200 && ui.WindowHeight is > 200)
        {
            Width = ui.WindowWidth.Value;
            Height = ui.WindowHeight.Value;
        }

        if (ui.WindowLeft is { } left && ui.WindowTop is { } top)
        {
            // Only restore a position that is still on a screen.
            var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (left >= SystemParameters.VirtualScreenLeft - 32 && left < virtualRight - 64 &&
                top >= SystemParameters.VirtualScreenTop - 32 && top < virtualBottom - 64)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (ui.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var ui = _services.UiSettings.Current;
        if (ui.CloseToTray && !_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        SaveWindowBounds();

        // session_end hooks get a bounded window to run before the process goes.
        try
        {
            // Task.Run keeps the hook awaits off this (blocked) dispatcher.
            var farewells = _codeWorkspace.AllCodeSurfaces
                .Select(static s => Task.Run(s.ViewModel.RunSessionEndHooksAsync))
                .Append(Task.Run(_chatSurface.ViewModel.RunSessionEndHooksAsync))
                .ToArray();
            Task.WaitAll(farewells, TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // A failing farewell hook must not block shutdown.
        }

        Application.Current.Shutdown();
    }

    private void SaveWindowBounds()
    {
        var ui = _services.UiSettings.Current;
        ui.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            ui.WindowLeft = Left;
            ui.WindowTop = Top;
            ui.WindowWidth = Width;
            ui.WindowHeight = Height;
        }

        _services.UiSettings.Save();
    }

    // ---- shell actions ----

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
        => SetSidebarCollapsed(SidebarColumn.Width.Value > 0);

    private void SetSidebarCollapsed(bool collapsed)
    {
        // A manual choice overrides the narrow-window auto-collapse.
        _autoCollapsedSidebar = false;
        ApplySidebarVisual(collapsed);
        _services.UiSettings.Current.SidebarCollapsed = collapsed;
    }

    private void ApplySidebarVisual(bool collapsed)
    {
        SidebarColumn.Width = new GridLength(collapsed ? 0 : _sidebarWidth);
        SidebarSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_customizeActive)
        {
            CustomizeNav.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            Sidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The sidebar folds away below this window width and comes back above it.</summary>
    private const double SidebarAutoCollapseWidth = 900;

    private bool _autoCollapsedSidebar;

    private double _sidebarWidth = 288;

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        var narrow = e.NewSize.Width < SidebarAutoCollapseWidth;
        if (narrow && !_autoCollapsedSidebar && SidebarColumn.Width.Value > 0)
        {
            _autoCollapsedSidebar = true;
            ApplySidebarVisual(collapsed: true);
        }
        else if (!narrow && _autoCollapsedSidebar)
        {
            _autoCollapsedSidebar = false;
            if (!_services.UiSettings.Current.SidebarCollapsed)
            {
                ApplySidebarVisual(collapsed: false);
            }
        }
    }

    private void OnSidebarSplitterDragCompleted(
        object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SidebarColumn.Width.Value <= 0)
        {
            return;
        }

        _sidebarWidth = Math.Clamp(SidebarColumn.Width.Value, 200, 400);
        SidebarColumn.Width = new GridLength(_sidebarWidth);
        _services.UiSettings.Current.SidebarWidth = _sidebarWidth;
        _services.UiSettings.Save();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
    }

    private void OnSurfaceChecked(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            ApplySurface();
        }
    }

    private void OnThemePickerClick(object sender, RoutedEventArgs e) => OpenThemePicker();

    private ThemePickerWindow? _themePicker;

    public void OpenThemePicker()
    {
        if (_themePicker is not null)
        {
            _themePicker.Close();
            return;
        }

        _themePicker = new ThemePickerWindow(_services) { Owner = this };
        _themePicker.Closed += (_, _) => _themePicker = null;
        _themePicker.Show();
        _themePicker.Activate();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    public void OpenSettings(string? group = null, string? label = null)
    {
        if (OverlayHost.Content is Settings.SettingsDialog existing)
        {
            if (group is not null && label is not null)
            {
                existing.SelectPanel(group, label);
            }

            return;
        }

        var dialog = new Settings.SettingsDialog(_services);
        dialog.CloseRequested += (_, _) =>
        {
            OverlayHost.Content = null;
            ActiveSurface.FocusInput();
        };
        dialog.ThemePickerRequested += (_, _) => OpenThemePicker();
        dialog.PluginsRequested += (_, _) =>
        {
            OverlayHost.Content = null;
            EnterCustomize("plugins");
        };
        OverlayHost.Content = dialog;
        if (group is not null && label is not null)
        {
            dialog.SelectPanel(group, label);
        }

        dialog.Focus();
    }

    // ---- app-level entry points ----

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>The notification's title when the session has no name of its own.</summary>
    private const string LocalSessionTitle = "Local Session";

    /// <summary>The reference's two notification bodies, in its own words.</summary>
    private const string TurnCompleteBody = "Jarvis finished a task";

    private const string AwaitingInputBody = "Jarvis is waiting for your input";

    /// <summary>The balloon the reference raises when the window goes to the tray.</summary>
    private const string BackgroundBalloonTitle = "Jarvis runs in the Notification Area";

    private const string BackgroundBalloonBody =
        "Jarvis runs in the background even when you close the window. Click the Jarvis icon in the tray to " +
        "reopen the app, or right-click to quit.";

    /// <summary>/bg — hide to the tray; running turns keep streaming into their sessions.</summary>
    public void SendToBackground()
    {
        SaveWindowBounds();
        Hide();
        _tray?.ShowNotification(BackgroundBalloonTitle, BackgroundBalloonBody);
    }

    /// <summary>/exit — quit for real, even with "close to tray" on, running the farewell hooks.</summary>
    public void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>/tui — chromeless maximized presentation, or back to the normal frame.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            SaveWindowBounds();
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // a style change while maximized keeps the old frame
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _services.UiSettings.Current.WindowMaximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        _services.UiSettings.Current.FullscreenMode = fullscreen;
        _services.UiSettings.Save();
    }

    /// <summary>Reloads keybindings.json; user chords add to the built-in ones.</summary>
    public void ReloadUserKeybindings() =>
        _userKeybindings = Services.UserKeybindings.Load(_services.Paths.KeybindingsFile);

    /// <summary>Executes one keybindings.json action id.</summary>
    private void RunKeybindingAction(string action)
    {
        switch (action)
        {
            case "command-palette":
                OpenCommandPalette();
                break;
            case "theme-picker":
                OpenThemePicker();
                break;
            case "settings":
                OpenSettings();
                break;
            case "new-session":
                StartNewSession();
                break;
            case "find":
                ActiveSurface.ToggleFindBar();
                break;
            case "model-menu":
                ActiveSurface.OpenModelMenu();
                break;
            case "mode-menu":
                ActiveSurface.OpenModeMenu();
                break;
            case "effort-menu":
                ActiveSurface.ToggleEffortMenu();
                break;
            case "shortcuts":
                OpenShortcuts();
                break;
            case "focus-composer":
                ActiveSurface.FocusInput();
                break;
            case "transcript-view":
                ActiveSurface.CycleTranscriptView();
                break;
            default:
                // The pane actions are the keymap's own command names, so a
                // keybindings.json row rebinds exactly what the default keymap declares.
                if (IsCodeActive && Enum.TryParse<Services.PaneCommand>(action, true, out var paneAction))
                {
                    RunPaneCommand(paneAction);
                }

                break;
        }
    }

    public void StartNewSession(string? workingDirectory = null)
    {
        ActiveSurface.StartNew(workingDirectory);
        RefreshSessionList();
    }

    /// <summary>
    /// The reference's "drag it out from the sidebar, or cmd+click it": the session
    /// opens in a window of its own, leaving this one where it was.
    /// </summary>
    private async Task OpenInOwnWindowAsync(string sessionId)
    {
        var session = await _services.Sessions.LoadAsync(sessionId);
        if (session is null)
        {
            return;
        }

        var surface = new ChatSurface();
        var window = new Window
        {
            Title = session.Title,
            Width = 1080,
            Height = 760,
            Content = surface,
        };
        window.SetResourceReference(BackgroundProperty, "Bg100Brush");
        window.Show();
        surface.Initialize(_services, isCodeSurface: true);
        surface.LoadSession(session);
        _services.SessionGroups.SetUnread(sessionId, false);
        RefreshSessionList();
    }

    public void ToggleQuickEntry()
    {
        if (_quickEntry is { IsVisible: true })
        {
            _quickEntry.HideEntry();
            return;
        }

        if (_quickEntry is null)
        {
            _quickEntry = new QuickEntryWindow();
            _quickEntry.Submitted += (_, submission) =>
            {
                try
                {
                    RestoreFromTray();
                    _chatSurface.StartNew();
                    if (ChatTab.IsChecked != true)
                    {
                        ChatTab.IsChecked = true;
                    }

                    _chatSurface.AttachImages(submission.ImagePaths);
                    _chatSurface.SubmitExternal(submission.Text);
                }
                catch (Exception ex) when (ex is System.IO.IOException
                    or UnauthorizedAccessException or InvalidOperationException)
                {
                    // The reference reports a quick entry it could not act on
                    // rather than dropping it silently.
                    ShowToast("Failed to process quick entry");
                }
            };
        }

        _quickEntry.ShowOnCursorMonitor();
    }

    /// <summary>Dev/verification hook: renders an artifact through the tool's own path.</summary>
    public void PublishSampleArtifact()
        => _codeWorkspace.PublishArtifact(new Services.ArtifactDocument(
            "Preview",
            """
            <html><body style="font:16px system-ui;background:#141414;color:#eee;padding:24px">
            <h1>Artifact preview</h1><p>Rendered by the Artifact tile.</p></body></html>
            """));

    /// <summary>Dev/verification hook: pre-fills the active composer.</summary>
    public void PrefillComposer(string text) => ActiveSurface.PrefillInput(text);

    /// <summary>A widget's connector suggestion opens the real connector's existing Connect flow.</summary>
    public void OpenWidgetConnector(string connectorId)
    {
        EnterCustomize("connectors");
        if (_customizeSurface?.ShowConnectorSuggestion(connectorId) != true)
            _services.Toasts.AddError("This connector is not configured on this device.");
    }

    /// <summary>
    /// Dev/verification hook: the Code home view, either with a row of every kind the
    /// reference lists or in the landing-clear state that shows the usage stats card.
    /// </summary>
    public void PoseHomeView(bool withRows)
    {
        CodeTab.IsChecked = true;
        _codeWorkspace.ChatSurface.PoseHomeView(withRows);
    }

    /// <summary>
    /// Dev/verification hook: the Code sidebar's list, seeded so every part of it is on
    /// screen — a group header carrying the filter and the menu, a running row, a row
    /// waiting on the user, an unread one, a pull-request row, a nested side session,
    /// and past twenty rows the "Show more" that folds the rest.
    /// </summary>
    public void PoseSidebar(string? variant)
    {
        CodeTab.IsChecked = true;
        var groups = _services.SessionGroups;
        var now = DateTimeOffset.Now;
        var rows = new List<SessionSummary>
        {
            new("pose-running", "Port the sidebar row geometry", now.AddMinutes(-1), 12, @"C:\work\jarvis-code"),
            new("pose-awaiting", "Rebuild the release pipeline", now.AddMinutes(-4), 30, @"C:\work\jarvis-code"),
            new("pose-unread", "mcp connection", now.AddMinutes(-9), 8, @"C:\work\jarvis-code"),
            new("pose-pr", "Fix the streaming usage accounting", now.AddMinutes(-20), 44, @"C:\work\jarvis-code"),
            new("pose-long",
                "A session whose title is far too long to fit in the sidebar column and has to fade out",
                now.AddMinutes(-30), 3, @"C:\work\jarvis-code"),
            new("pose-child", "Follow-up: add the regression test", now.AddMinutes(-2), 5,
                @"C:\work\jarvis-code"),
        };

        if (variant == "showmore")
        {
            rows.AddRange(Enumerable.Range(0, 24).Select(i =>
                new SessionSummary($"pose-bulk-{i:00}", $"Earlier session {i + 1}",
                    now.AddHours(-2 - i), 4, @"C:\work\jarvis-code")));
        }

        groups.SetUnread("pose-unread", true);
        // The nested row is a real side session: the pose records where it came from
        // where the running one does, rather than handing the sidebar a parent by hand.
        groups.RecordSpawnedFrom("pose-child", "pose-running", "task_00000001");
        _services.UiSettings.Current.SidebarGroupBy = variant == "state" ? "state" : "custom";
        _services.UiSettings.Save();

        Sidebar.SetSurface(true);

        // The list refreshes itself from the store on its own schedule, so the pose has
        // to be the store rather than one call: rebinding after the next refresh is how
        // it would otherwise vanish.
        _poseSessions = rows;
        Sidebar.ShowSessions(rows, groupByProject: true, "pose-unread", null,
            runningSessionIds: ["pose-running"], awaitingSessionIds: ["pose-awaiting"]);
        if (variant == "hints")
        {
            Sidebar.UpdateJumpHints(true);
        }
    }

    /// <summary>The rows --open=sidebar seeded, which the normal refresh must not drop.</summary>
    private IReadOnlyList<SessionSummary>? _poseSessions;

    /// <summary>Dev/verification hook: the PR bar in the mode the flag names.</summary>
    public void PoseGitBar(string mode)
    {
        CodeTab.IsChecked = true;
        _codeWorkspace.ChatSurface.PoseGitBar(mode);
    }

    /// <summary>
    /// Dev/verification hook: the Code session's titlebar. The bar only draws over a
    /// started session, so the sample transcript comes first; the state then poses one of
    /// the things nothing in a posed profile would otherwise reach — its skeletons, its
    /// agent badge, or the two pane controls the host gates on.
    /// </summary>
    public void PoseTitleBar(string state)
    {
        CodeTab.IsChecked = true;
        ShowSampleTranscript();
        _codeWorkspace.ChatSurface.PoseTitleBar(state);
    }

    /// <summary>
    /// Dev/verification hook: writes a sample plugin declaring two monitors into the
    /// posed profile and opens its detail page, so the Contents section's Monitors
    /// rows have something to draw.
    /// </summary>
    public void PosePluginMonitors()
    {
        var root = System.IO.Path.Combine(_services.Paths.PluginsDirectory, "sample");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "monitors"));
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(root, "plugin.json"),
            """{"name":"sample","description":"A plugin that watches things","version":"1.0.0"}""");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(root, "monitors", "monitors.json"),
            """
            [
              {"name":"ci","command":"gh run watch","description":"Watches the branch's CI run","when":"always"},
              {"name":"deploy-log","command":"tail -f deploy.log","description":"Streams the deploy log","when":"on-skill-invoke:deploy"}
            ]
            """);
        EnterCustomize("plugins");
        _customizeSurface?.ShowPlugins("sample");
    }

    /// <summary>Dev/verification hook: the Import GitHub issue picker over this folder.</summary>
    public void PoseIssuePicker() =>
        Views.IssuePickerDialog.Pick(this, ActiveSurface.ViewModel.Session.WorkingDirectory);

    /// <summary>Dev/verification hook: the reference's "Session not found on disk" card.</summary>
    public void PoseSessionNotFound()
    {
        CodeTab.IsChecked = true;
        _codeWorkspace.ChatSurface.PoseSessionNotFound();
    }

    /// <summary>Dev/verification hook: the branch-switch dialog over a dirty tree.</summary>
    public void PoseBranchSwitch()
    {
        CodeTab.IsChecked = true;
        Views.BranchSwitchDialog.Ask(
            this,
            "wp/r3-ui",
            "master",
            2,
            new Services.WorkingTreeStatus(
                [
                    new Services.WorkingTreeEntry(" M", "src/JarvisCode.App/Services/BranchSwitch.cs", false),
                    new Services.WorkingTreeEntry("??", "src/JarvisCode.App/Views/BranchSwitchDialog.cs", false),
                    new Services.WorkingTreeEntry(" M", "CLAUDE.md", false),
                ],
                214,
                37));
    }

    /// <summary>Dev/verification hook: fills the transcript with the renderer's hard cases.</summary>
    /// <summary>
    /// Dev/verification hook: the thinking cells of whichever surface is asked for —
    /// the Chat surface's conversation cell, or the Code surface's italic block.
    /// </summary>
    /// <summary>
    /// Poses the Chat surface for a screen grab: its empty screen, the first-chat
    /// onboarding, an incognito chat, the waiting line, the queued stack or the
    /// activity drawer.
    /// </summary>
    public void ShowSampleChat(string variant)
    {
        CodeTab.IsChecked = false;
        ChatTab.IsChecked = true;
        ApplySurface();
        _chatSurface.ShowSampleChat(variant);
    }

    /// <summary>`--open=comparison[:vote|:reason|:saved]`: the side-by-side view, posed.</summary>
    public void PoseComparison(string state)
    {
        CodeTab.IsChecked = false;
        ChatTab.IsChecked = true;
        ApplySurface();
        _services.UiSettings.Current.NewChatComparisonView = true;
        _chatSurface.PoseComparison(state);
    }

    public void ShowSampleThinking(bool onCodeSurface = false)
    {
        const string longThinking = """
            ## Chọn ngưỡng

            Reference kẹp nội dung ở **200px** rồi mới hiện *Show more*. Vài điểm:

            - đo `scrollHeight`, không đo phần đã bị cắt
            - chỉ kẹp khi turn đã xong, đang stream thì để chạy tự do
            - nút chỉ hiện khi hover, trừ khi đang mở

            ```csharp
            var clamped = !item.IsStreaming && !item.ShowsFullText;
            ```

            Sau đó còn phải xử lý gradient 40px ở đáy, và caret xoay -90 độ khi
            đóng. Cả hai đều là chuyển động 200-300ms, easing cubic-bezier(0,0,.2,1),
            nên phải viết easing riêng vì WPF không có sẵn đường cong đó.

            Cuối cùng là nhãn: cộng thời lượng từng block rồi mới chọn bậc thang.
            """;
        const string shortThinking = "Ngắn thôi, không cần kẹp.";

        if (onCodeSurface)
        {
            StartCodeSessionIn(Environment.CurrentDirectory);
            ActiveSurface.ViewModel.ShowSampleThinking(longThinking, shortThinking);
            // Normal hides reasoning on this surface; step to the thinking view so
            // the cells this pose exists to show are actually on screen.
            ActiveSurface.CycleTranscriptView();
            return;
        }

        ChatTab.IsChecked = true;
        ApplySurface();
        _chatSurface.ViewModel.ShowSampleThinking(longThinking, shortThinking);
    }

    /// <summary>
    /// Poses one answer per section of markdown constructs, so the two
    /// renderer profiles can be looked at directly. The transcript pins to its
    /// end, so the sections are posed separately rather than as one message.
    /// </summary>
    public void ShowSampleMarkdown(string section)
    {
        const string headings = """"
            # Heading one, the largest
            ## Heading two
            ### Heading three
            #### Heading four
            ##### Heading five
            ###### Heading six

            A paragraph with **bold**, *italic*, ***both***, `inline code`, ~~struck out~~,
            a soft line break above this one, _underscore italic_, __underscore bold__, and
            snake_case_name which must stay literal. A bare link https://example.com/docs
            and a labelled [**bold link**](https://example.com) sit here, with an escaped
            \*not italic\* beside them. A colour chip: `#c96442`.

            Press <kbd>Ctrl</kbd> plus <kbd>Shift</kbd> to run it, per the note[^1].

            Setext heading
            ==============
            """";

        const string lists = """"
                        - Bullet item one
            - Bullet item two
              - Nested bullet, second level
                - Third level
            - Item with a continuation line
              that wrapped onto a second source line

            1. Ordered item
            2. Second, with a nested bullet list
               - alpha
               - beta
            3. Third, with a fence under it

               ```bash
               dotnet test --nologo
               ```

            7. A list that starts at seven
            8. and continues

            - [x] Completed task
            - [ ] Open task

            > A quote with **inline styles**.
            >
            > ## A heading inside the quote
            >
            > - a list inside the quote
            > - second entry
            >
            > > A nested quote.
            
            
            """";

        const string blocks = """"
                        | Left | Centre | Right | Plain |
            |:-----|:------:|------:|-------|
            | `a \| b` | middle | 42 | text |
            | second | row | 7 | more |

            ```python
            def scan(path: str) -> int:
                """Counts matching lines."""
                total = 0  # running tally
                with open(path) as handle:
                    for line in handle:
                        if "error" in line:
                            total += 1
                return total
            ```

            ```
            A fence with no language declared, which the chat renderer wraps
            rather than scrolling because it could not name a grammar for it.
            ```

            ```bash
            export API_TOKEN=sk-abcdefghijklmnopqrstuvwxyz012345
            curl -H "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payloadpayload.signature" https://api.example.com
            ```

            Display math sits on its own line:

            $$ \sigma(z)_i = \frac{e^{z_i}}{\sum_{j=1}^{K} e^{z_j}} $$

            ---

            ![a placeholder image](https://example.com/diagram.png)

            The last paragraph, so the trailing gap can be checked.

            [^1]: The footnote definition, which the reference sets a size down
              in the secondary colour.

            """";

        // The chat renderer's diagram fences: one that renders, one whose theme
        // colours are the ones the reference passes mermaid, and one mermaid
        // cannot parse, which falls back to the plain block the reference draws.
        const string diagrams = """"
                        ```mermaid
            flowchart LR
              A[Prompt] --> B{Tools?}
              B -- yes --> C[Run tool]
              C --> D[Answer]
              B -- no --> D
            ```

            ```mermaid
            sequenceDiagram
              participant U as User
              participant J as Jarvis
              U->>J: ask
              J-->>U: answer
              Note over J: thinking
            ```

            ```mermaid
            flowchart LR
              this is not a diagram [[[
            ```

            """";

        ActiveSurface.ViewModel.ShowSampleMarkdown(section switch
        {
            "top" => headings,
            "mid" => lists,
            "mermaid" => diagrams,
            _ => blocks,
        });
    }

    /// <summary>The sample transcript's Agent call, which the subagent pose opens.</summary>
    private const string SampleAgentCallId = "sample-8";

    /// <summary>The sample's failed agent, which reported nothing but an error.</summary>
    private const string SampleFailedAgentCallId = "sample-9";

    /// <summary>A 200x120 fill, so the posed screenshot row has an image to draw.</summary>
    private const string SampleScreenshot =
        "iVBORw0KGgoAAAANSUhEUgAAAMgAAAB4CAYAAAC3kr3rAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAJ/SURBVHhe7dOxaQMAEENRb5nNvJunSUqDMNfIEBDvw2uuPj1+nq9f4LNHHoA3A4GDgcDBQOBgIHAwEDgYCBwMBA4GAgcDgYOBwMFA4GAgcDAQOBgIHAwEDgYCBwOBg4HA4esDkf6z/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MeWgWiq/MfW1wcCSwwEDgYCBwOBg4HAwUDgYCBwMBA4GAgcDAQOBgIHA4GDgcDBQOBgIHAwEDgYCBwMBA4GAgcDgcMfgNh1gTCHAvIAAAAASUVORK5CYII=";

    public void ShowSampleTranscript()
    {
        const string markdown = """"
            Năng lượng nghỉ \(e = mc^2\), còn chuẩn hóa softmax:

            $$ \sigma(z)_i = \frac{e^{z_i}}{\sum_{j=1}^{K} e^{z_j}} $$

            Chốt lại: giữ ~~cách cũ~~ **cách mới**, checklist:

            - [x] Parser đọc `~~strikethrough~~`
            - [ ] Caret nhấp nháy khi streaming
            - Bước chạy thử:

              ```bash
              dotnet test --nologo
              ```

            Ba khối để soi màu:

            ```csharp
            // Retry with backoff.
            public async Task<Stream> SendAsync(int attempt = 0)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                if (attempt >= 4) throw new TimeoutException($"gave up after {attempt}");
                return await _http.GetStreamAsync(@"https://api.example.com/v1?q=""quoted""");
            }
            ```

            ```python
            def scan(path: str) -> int:
                """Counts matching lines."""
                total = 0  # running tally
                with open(path) as f:
                    for line in f:
                        total += 1 if 'needle' in line else 0
                return total
            ```

            ```xaml
            <!-- The approval card's confirm button -->
            <Button Style="{StaticResource InvertedButton}" Padding="10,6"
                    Command="{Binding AllowOnceOption.Command}" />
            ```
            """";

        var shellCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-1",
            ToolName = "PowerShell",
            Description = "Run the App test suite",
            ArgumentsJson = """{"command":"dotnet test tests/App.Tests.csproj --nologo -v q","description":"Run the App test suite"}""",
            IsExpanded = true,
        };
        shellCall.Complete("Passed!  - Failed: 0, Passed: 181", isError: false);

        var editCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-2",
            ToolName = "Edit",
            Description = "Edit(ChangesPanel.xaml)",
            ArgumentsJson = """{"file_path":"src/Views/ChangesPanel.xaml","old_string":"<Grid Rows=\"2\">","new_string":"<Grid Rows=\"3\">\n<!-- new row -->"}""",
            IsExpanded = true,
        };
        editCall.Complete("ok", isError: false);

        // Two reads of one file coalesce into a single row with ranges.
        var readA = new ViewModels.ToolCallItem
        {
            CallId = "sample-3",
            ToolName = "Read",
            Description = "Read(ChatViewModel.cs)",
            ArgumentsJson = """{"file_path":"src/ViewModels/ChatViewModel.cs","offset":919,"limit":140}""",
        };
        readA.Complete("// …", isError: false);
        var readB = new ViewModels.ToolCallItem
        {
            CallId = "sample-4",
            ToolName = "Read",
            Description = "Read(ChatViewModel.cs)",
            ArgumentsJson = """{"file_path":"src/ViewModels/ChatViewModel.cs","offset":1058,"limit":120}""",
        };
        readB.Complete("// …", isError: false);

        var commitCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-5",
            ToolName = "PowerShell",
            Description = "Commit the change",
            ArgumentsJson = """{"command":"git commit -m \"Sample\""}""",
        };
        commitCall.Complete("[master 59fd9a6] Sample\n 2 files changed", isError: false);

        var todoCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-6",
            ToolName = "todo_write",
            Description = "Update todos",
            ArgumentsJson = """{"todos":[{"content":"Port the group header","status":"completed"},{"content":"Wire the diff body","status":"in_progress"},{"content":"Screenshot the result","status":"pending"}]}""",
            IsExpanded = true,
        };
        todoCall.Complete("Todos have been modified successfully.", isError: false);

        var previewCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-7",
            ToolName = "preview_start",
            Description = "Start the dev server",
            ArgumentsJson = """{"name":"vite"}""",
        };
        previewCall.Complete("vite started — serverId preview-1, preview open at http://localhost:5173.", isError: false);

        // An agent row with its subagent's nested transcript expanded.
        var agentCall = new ViewModels.ToolCallItem
        {
            CallId = SampleAgentCallId,
            ToolName = "Agent",
            Description = "Agent(explore)",
            ArgumentsJson = """{"prompt":"find the parser","description":"Search the parser","agent_type":"explore"}""",
            IsExpanded = true,
        };
        agentCall.AppendSubagentEvent(new JarvisCode.Core.Agent.ToolExecutionStarted(
            "sub-1", "Grep", "Grep(ParseInline)", """{"pattern":"ParseInline"}"""));
        agentCall.AppendSubagentEvent(new JarvisCode.Core.Agent.ToolExecutionCompleted(
            "sub-1", "Grep", "src/Markdown/Parser.cs:88", IsError: false));
        agentCall.AppendSubagentEvent(new JarvisCode.Core.Agent.AssistantMessageCompleted(
            new JarvisCode.Core.Models.ChatMessage(
                JarvisCode.Core.Models.Role.Assistant,
                [new JarvisCode.Core.Models.TextBlock("The parser lives in src/Markdown/Parser.cs (ParseInline, line 88).")])));
        agentCall.Complete("The parser lives in src/Markdown/Parser.cs.", isError: false);

        // An agent that failed before doing anything: the view's error tail.
        var failedAgentCall = new ViewModels.ToolCallItem
        {
            CallId = SampleFailedAgentCallId,
            ToolName = "Agent",
            Description = "Agent(general)",
            ArgumentsJson =
                """{"prompt":"summarize the release notes","description":"Summarize the release notes"}""",
        };
        failedAgentCall.Complete("The agent stopped: no release notes were found.", isError: true);

        // A shell call still running: the row that offers "Run in background".
        var runningCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-10",
            ToolName = "PowerShell",
            Description = "Build the release bundle",
            ArgumentsJson = """{"command":"dotnet publish -c Release","description":"Build the release bundle"}""",
            IsRunning = true,
        };

        // A call sitting behind the permission card, reference awaiting style.
        var awaitingCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-9",
            ToolName = "Edit",
            Description = "Edit(Parser.cs)",
            ArgumentsJson = """{"file_path":"src/Markdown/Parser.cs","old_string":"a","new_string":"b"}""",
            IsRunning = false,
            IsAwaitingApproval = true,
        };

        // A Read that answered with an image: the body has no file content to
        // show, so it falls through to the argument list — the "file_path:" row
        // over the screenshot, which is what the reference draws here.
        var readImage = new ViewModels.ToolCallItem
        {
            CallId = "sample-11",
            ToolName = "Read",
            Description = "Read(glow-blue-1.png)",
            ArgumentsJson = """{"file_path":"C:/Temp/scratchpad/glow-blue-1.png"}""",
            IsExpanded = true,
        };
        readImage.Complete(
            "",
            isError: false,
            images: [new JarvisCode.Core.Models.ImageBlock("image/png", SampleScreenshot)]);

        // The two trailing words a row wears when it did not simply finish.
        var stoppedCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-12",
            ToolName = "PowerShell",
            Description = "Stop the posed app",
            ArgumentsJson = """{"command":"taskkill /im jarvis.exe","description":"Stop the posed app"}""",
            IsRunning = true,
        };
        stoppedCall.Complete("[Request interrupted by user for tool use]", isError: false, interrupted: true);

        var deniedCall = new ViewModels.ToolCallItem
        {
            CallId = "sample-13",
            ToolName = "Write",
            Description = "Write(secrets.env)",
            ArgumentsJson = """{"file_path":"secrets.env","content":"TOKEN=..."}""",
        };
        deniedCall.IsRunning = false;
        deniedCall.IsDenied = true;

        // Two spawn_task calls: their own run, counted by what became of the chips
        // rather than by the tool that queued them, with the started one renamed.
        var spawnStarted = new ViewModels.ToolCallItem
        {
            CallId = "sample-17",
            ToolName = "mcp__ccd_session__spawn_task",
            Description = "spawn_task(Fix the stale README badge)",
            ArgumentsJson =
                """{"title":"Fix the stale README badge","prompt":"The badge points at the old workflow."}""",
        };
        spawnStarted.Complete(
            "Noted (position 1, task_id: task_5f3a91c0). A chip is showing for the user.", isError: false);

        var spawnQueued = new ViewModels.ToolCallItem
        {
            CallId = "sample-18",
            ToolName = "mcp__ccd_session__spawn_task",
            Description = "spawn_task(Remove the dead config option)",
            ArgumentsJson =
                """{"title":"Remove the dead config option","prompt":"Nothing reads it any more."}""",
        };
        spawnQueued.Complete(
            "Noted (position 2, task_id: task_9b21ed44). A chip is showing for the user.", isError: false);

        // The first chip was started and the second is still pending, which is what
        // splits the run's sentence into its two clauses. Started is a fact about
        // the session that was spawned, so it is posed where the real one lives.
        var posed = ActiveSurface.ViewModel;
        posed.Suggestions.Enqueue(new Services.TaskSuggestion(
            "task_9b21ed44", "Remove the dead config option", "Nothing reads it any more.", null, null));
        _services.SessionGroups.RecordSpawnedFrom(
            "posed-spawned-session", posed.Session.Id, "task_5f3a91c0");

        // Runs of one call, which the reference draws as a bare row: a shell card
        // with its own header bar, an edit whose diff is wrapped in an outlined
        // card, and a read whose image sits outside the disclosure.
        var bareShell = new ViewModels.ToolCallItem
        {
            CallId = "sample-14",
            ToolName = "PowerShell",
            Description = "List the changed files",
            ArgumentsJson =
                """{"command":"git status --porcelain","description":"List the changed files"}""",
            IsExpanded = true,
        };
        bareShell.Complete(" M src/App.xaml.cs\n M README.md", isError: false);

        var bareEdit = new ViewModels.ToolCallItem
        {
            CallId = "sample-15",
            ToolName = "Edit",
            Description = "Edit(Base.xaml)",
            ArgumentsJson =
                """{"file_path":"src/Styles/Base.xaml","old_string":"<Setter Padding=8,5 />","new_string":"<Setter Padding=10,8 />"}""",
            IsExpanded = true,
        };
        bareEdit.Complete("ok", isError: false);

        var bareRead = new ViewModels.ToolCallItem
        {
            CallId = "sample-16",
            ToolName = "Read",
            Description = "Read(glow-blue-2.png)",
            ArgumentsJson = """{"file_path":"C:/Temp/scratchpad/glow-blue-2.png"}""",
        };
        bareRead.Complete(
            "",
            isError: false,
            images: [new JarvisCode.Core.Models.ImageBlock("image/png", SampleScreenshot)]);

        ActiveSurface.ViewModel.ShowSampleTranscript(
            markdown,
            [shellCall, editCall, readA, readB, commitCall, todoCall, previewCall, agentCall,
             failedAgentCall, runningCall, awaitingCall, readImage, stoppedCall, deniedCall,
             spawnStarted, spawnQueued],
            [bareShell, bareEdit, bareRead]);
    }

    /// <summary>Dev/verification hook: renders the approval card in one of its shapes.</summary>
    public void ShowSamplePermission(string variant)
    {
        var prompt = variant switch
        {
            "escalated" => new Services.PermissionPrompt(
                "PowerShell", Detail: null, "git push --force origin master",
                DiffPreview: null, "This rewrites history that is already pushed.", CallRisk.Escalated),
            "edit" => new Services.PermissionPrompt(
                "Edit", @"ui-check-tmp\note.txt", @"D:\Project\lobby\ui-check-tmp\note.txt",
                new ToolDiffPreview.Preview(
                    @"D:\Project\lobby\ui-check-tmp\note.txt",
                    [
                        new DiffLine(DiffKind.Context, "alpha"),
                        new DiffLine(DiffKind.Context, "beta"),
                        new DiffLine(DiffKind.Removed, "gamma"),
                        new DiffLine(DiffKind.Added, "gamma changed"),
                        new DiffLine(DiffKind.Context, "delta"),
                    ]),
                Warning: null, CallRisk.Standard),
            _ => new Services.PermissionPrompt(
                "PowerShell", Detail: null, "npm test", DiffPreview: null, Warning: null, CallRisk.Standard),
        };
        ActiveSurface.ViewModel.ShowSamplePermission(prompt);
    }

    /// <summary>Dev/verification hook: opens the context-window popup.</summary>
    public void ShowContextPopup() => ActiveSurface.ShowContextPopup();

    /// <summary>Dev/verification hook: opens the composer's effort selector popover.</summary>
    public void OpenEffortSelector() => ActiveSurface.ToggleEffortMenu();

    /// <summary>Dev/verification hook: opens the composer's model menu.</summary>
    public void OpenModelSelector() => ActiveSurface.OpenModelMenu();

    /// <summary>Dev/verification hook: opens the composer's permission-mode menu.</summary>
    public void OpenPermissionModeSelector() => ActiveSurface.OpenModeMenu();

    /// <summary>Dev/verification hook: opens the composer's request inspector.</summary>
    public void OpenRequestInspector() => ActiveSurface.ToggleRequestInspector();

    /// <summary>Dev/verification hook: poses the AskUserQuestion card.</summary>
    /// <summary>--open=teach: the guided-tour overlay waiting on one sample step.</summary>
    public void ShowSampleTeachStep()
    {
        if (_services is null || !_services.Teach.Begin())
        {
            return;
        }

        // Runs the real await, so clicking Next flips the card to its working
        // state exactly as a model-driven step would.
        _ = _services.Teach.StepAsync(
            "This is the composer. Everything you ask Jarvis starts here — type a sentence, "
            + "press Enter, and the reply streams in above.",
            "Next: I'll click the model chip and pick Opus 5.",
            new System.Drawing.Point(720, 640),
            CancellationToken.None);
    }

    public void ShowSampleQuestion() =>
        ActiveSurface.ViewModel.ShowSampleQuestion(
        [
            new JarvisCode.Core.Tools.BuiltIn.UserQuestion(
                "Which library should we use for date formatting?",
                "Library",
                [
                    new JarvisCode.Core.Tools.BuiltIn.UserQuestionOption(
                        "NodaTime (Recommended)", "Purpose-built date/time model; larger dependency.",
                        "<div style=\"border:1px solid #ccc;border-radius:8px;padding:10px\">" +
                        "<b>2026-08-29 14:05</b><br><span style=\"color:#888\">in 3 hours · Instant + ZonedDateTime</span></div>"),
                    new JarvisCode.Core.Tools.BuiltIn.UserQuestionOption(
                        "Humanizer", "Lightweight and already referenced; fewer formats.",
                        "<div style=\"border:1px solid #ccc;border-radius:8px;padding:10px\">" +
                        "<b>3 hours from now</b><br><span style=\"color:#888\">DateTime.Humanize()</span></div>"),
                    new JarvisCode.Core.Tools.BuiltIn.UserQuestionOption(
                        "Hand-rolled", "No dependency; we own the edge cases.",
                        "<div style=\"border:1px solid #ccc;border-radius:8px;padding:10px\">" +
                        "<b>29/08 14:05</b><br><span style=\"color:#888\">custom format strings</span></div>"),
                ],
                MultiSelect: false),
        ]);

    /// <summary>Dev/verification hook: poses the exit-plan-mode approval card.</summary>
    /// <summary>Dev/E2E hook: shows a goal check-in as a waiting goal would deliver it.</summary>
    public void ShowSampleGoalCheckin() => ActiveSurface.ViewModel.ShowSampleGoalCheckin();

    public void ShowSamplePlanApproval(bool editing = false)
    {
        ShowSamplePlanApprovalCore();
        if (editing && ActiveSurface.ViewModel.PendingPlanApproval is { } prompt)
        {
            prompt.IsEditing = true;
        }
    }

    private void ShowSamplePlanApprovalCore() =>
        ActiveSurface.ViewModel.ShowSamplePlanApproval(
            "## Add date formatting\n\n1. Introduce `DateFormatter` in `src/Utils`\n" +
            "2. Replace the three call sites in `ReportBuilder`\n3. Cover relative dates with unit tests\n\n" +
            "**Risk:** locale-dependent output on CI — pin `CultureInfo.InvariantCulture`.");

    /// <summary>Dev/E2E hook: runs one real turn on the active surface; 0 = clean, 2 = turn error.</summary>
    public async Task<int> RunE2ETurnAsync(string prompt)
    {
        var vm = ActiveSurface.ViewModel;
        await ActiveSurface.SubmitTextAsync(prompt);
        // A failed turn now leaves an error card rather than an error notice,
        // so both count as a failure here.
        var failed = vm.Transcript.OfType<ViewModels.NoticeItem>().Any(static n => n.IsError) ||
            vm.Transcript.OfType<ViewModels.ErrorCardItem>().Any();
        return failed || vm.IsRunning || !Answered(vm) ? 2 : 0;
    }

    /// <summary>
    /// --open=toast: one card of every variant, so the stack's peek and its
    /// action button can be looked at without waiting for something to fail.
    /// </summary>
    public void ShowSampleToasts()
    {
        _services.Toasts.AddSuccess(Services.ToastText.PathCopied);
        _services.Toasts.AddError(Services.ToastText.ArchiveFailed);
        _services.Toasts.AddWithAction(
            Services.ToastText.Unpinned("Parser rewrite"), Services.ToastText.Undo, () => { },
            Services.ToastVariant.Danger);
    }

    /// <summary>
    /// --open=errorcard[:kind]: the API-error card posed for one category, so
    /// its wording and its action row can be looked at.
    /// </summary>
    public void ShowSampleErrorCard(string? kind)
    {
        if (!IsCodeActive)
        {
            CodeTab.IsChecked = true;
        }

        var category = kind switch
        {
            "overloaded" => Services.ApiErrorCategory.Overloaded,
            "ratelimit" => Services.ApiErrorCategory.RateLimit,
            "auth" => Services.ApiErrorCategory.Auth,
            "network" => Services.ApiErrorCategory.Network,
            _ => Services.ApiErrorCategory.ContextLength,
        };
        var presentation = Services.ErrorCards.Describe(category);
        ActiveSurface.ViewModel.Transcript.Add(new ViewModels.ErrorCardItem
        {
            Headline = presentation.Headline,
            Hint = presentation.Hint,
            Details = "API Error: 400 {\"type\":\"error\"}\nRequest ID: req_011CT9sample",
            RequestId = "req_011CT9sample",
            Category = category,
            RetryHelps = presentation.RetryHelps,
            RewindHelps = presentation.RewindHelps,
            CompactHelps = presentation.CompactHelps,
        });

        // The home view stands in for an empty transcript; adding an item is
        // what takes it down.
        ActiveSurface.ViewModel.NotifyTranscriptChanged();
    }

    /// <summary>--open=browser: fronts the Code surface's Browser panel.</summary>
    public void ShowBrowserPanel() => _codeWorkspace.ShowPanel("browser");

    /// <summary>
    /// --open=panes[:name]: poses the tile mosaic. With no name the terminal and the diff
    /// are tiled beside the conversation, which is what shows the gap and the drag handles.
    /// </summary>
    public void ShowSamplePanes(string pane)
    {
        CodeTab.IsChecked = true;
        if (pane.Length > 0)
        {
            _codeWorkspace.ShowPanel(pane);
            return;
        }

        _codeWorkspace.ShowPanel("terminal");
        _codeWorkspace.ShowPanel("changes");
    }

    /// <summary>
    /// --open=emulator: the Android Emulator pane attached to whichever emulator
    /// is booted, so the live view can be looked at. With none running it poses
    /// the attach prompt, which is what --open=panes:simulator already shows.
    /// </summary>
    public async Task ShowSampleEmulatorAsync()
    {
        CodeTab.IsChecked = true;
        _codeWorkspace.ShowPanel(Services.SidePanes.Simulator);
        await _codeWorkspace.AttachFirstEmulatorAsync();
    }

    /// <summary>
    /// --open=backgroundtasks: the Background tasks pane posed with one row of
    /// every kind and state, so the pane can be looked at without waiting for
    /// real work to be running.
    /// </summary>
    public void ShowSampleBackgroundTasks(bool empty = false)
    {
        _codeWorkspace.ShowPanel("runs");
        _codeWorkspace.ShowSampleBackgroundTasks(empty);
    }

    /// <summary>
    /// The pane pushed onto a real agent row's own transcript, posed from the
    /// transcript sample's Agent call.
    /// </summary>
    public void ShowSampleSubagentView(bool failed = false)
    {
        StartCodeSessionIn(Environment.CurrentDirectory);
        ShowSampleTranscript();
        _codeWorkspace.ShowPanel("runs");
        _codeWorkspace.ShowSampleSubagentView(
            failed ? SampleFailedAgentCallId : SampleAgentCallId);
    }

    /// <summary>
    /// --open=taskboard: the Tasks pane posed with a board a team is working
    /// through — claimed, blocked and finished rows plus a plain checklist.
    /// </summary>
    public void ShowSampleTaskBoard()
    {
        _codeWorkspace.ShowPanel("tasks");
        _codeWorkspace.ShowSampleTaskBoard();
    }

    /// <summary>
    /// --open=widget: a Code session whose transcript holds one rendered
    /// visualize widget, so the row and the page inside it can be looked at.
    /// The widget code is HTML in the shape read_me asks for — CDS variables,
    /// 0.5px borders, no gradients — and needs no network to render.
    /// </summary>
    public void ShowSampleWidget()
    {
        const string code = """
            <div style="font-family:var(--font-sans);color:var(--text-primary)">
              <div style="font-size:13px;color:var(--text-secondary);margin-bottom:12px">Deploys this week</div>
              <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px">
                <div style="border:0.5px solid var(--border);border-radius:12px;padding:1rem 1.25rem;background:var(--surface-2)">
                  <div style="font-family:var(--font-voice);font-size:48px;line-height:1;font-variant-numeric:tabular-nums lining-nums">128</div>
                  <div style="font-size:12px;color:var(--text-muted);margin-top:6px">shipped</div>
                </div>
                <div style="border:0.5px solid var(--border);border-radius:12px;padding:1rem 1.25rem;background:var(--surface-2)">
                  <div style="font-family:var(--font-voice);font-size:48px;line-height:1;font-variant-numeric:tabular-nums lining-nums">3</div>
                  <div style="font-size:12px;color:var(--text-muted);margin-top:6px">rolled back</div>
                </div>
              </div>
              <svg viewBox="0 0 320 64" style="width:100%;height:64px;margin-top:16px" role="img" aria-label="Deploys per day">
                <polyline fill="none" stroke="var(--text-primary)" stroke-width="2" stroke-linecap="round"
                          stroke-linejoin="round" points="4,52 56,40 108,44 160,22 212,28 264,12 316,18" />
                <circle cx="316" cy="18" r="4" fill="var(--text-primary)" />
              </svg>
              <button type="button" style="margin-top:16px" onclick="sendPrompt('Why did three deploys roll back?')">
                Ask about the rollbacks
              </button>
            </div>
            """;

        ActiveSurface.ViewModel.ShowSampleWidget("deploys_this_week", code);
    }

    /// <summary>
    /// --pane-selftest (needs --screenshot to arm, like --e2e): drives the Browser
    /// pane's own toolset end to end against a local page — navigate, read_page,
    /// a ref click, console capture, REPL eval, screenshot, viewport emulation —
    /// and returns 0/2. Failures land in the diagnostic log.
    /// </summary>
    public async Task<int> RunPaneSelfTestAsync()
    {
        StartCodeSessionIn(Environment.CurrentDirectory);
        await Task.Delay(400);
        _codeWorkspace.ShowPanel("browser");
        await Task.Delay(200);
        if (_codeWorkspace.PaneDriver is not { } driver)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write("pane-selftest: no driver");
            return 2;
        }

        var pagePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"jarvis-pane-selftest-{Environment.ProcessId}.html");
        await System.IO.File.WriteAllTextAsync(pagePath, """
            <!doctype html><title>Pane selftest</title>
            <button onclick="console.log('selftest-click')">Sign in</button>
            <input placeholder="Email">
            <script>console.log('selftest-loaded')</script>
            """);
        var checks = new List<string>();

        void Check(string name, bool ok)
        {
            if (!ok)
            {
                checks.Add(name);
            }
        }

        try
        {
            // Drives the same handler layer the mcp__Claude_Browser__* tools call,
            // asserting the reference's exact result shapes.
            var handlers = new JarvisCode.App.Services.BrowserPaneHandlers(driver);
            var none = CancellationToken.None;

            var navigated = await handlers.NavigateAsync(
                new System.Text.Json.Nodes.JsonObject { ["url"] = new Uri(pagePath).AbsoluteUri }, none);
            Check("navigate answers with the reference text",
                !navigated.IsError && navigated.Content.StartsWith("navigated to "));
            await Task.Delay(1500);

            var tree = await handlers.ReadPageAsync([], none);
            Check("read_page lists the button with a ref",
                tree.Content.Contains("button \"Sign in\" [ref_") && tree.Content.Contains("Viewport: "));
            Check("read_page carries the Tab Context trailer", tree.Content.Contains("Tab Context:"));

            var found = await handlers.FindAsync(
                new System.Text.Json.Nodes.JsonObject { ["query"] = "sign in" }, none);
            Check("find reports the match count", found.Content.StartsWith("Found 1 match(es) for \"sign in\":"));

            var click = await handlers.ComputerAsync(
                new System.Text.Json.Nodes.JsonObject { ["action"] = "left_click", ["ref"] = "ref_1" }, none);
            Check("ref click echoes coordinates and the ref",
                !click.IsError && click.Content.StartsWith("left_click at (") && click.Content.Contains("[ref_1]"));
            await Task.Delay(300);

            var console = await handlers.ConsoleAsync([], none);
            Check("console captured the load", console.Content.Contains("[log] selftest-loaded"));
            Check("console captured the click", console.Content.Contains("[log] selftest-click"));

            var eval = await handlers.JavaScriptAsync(
                new System.Text.Json.Nodes.JsonObject { ["action"] = "javascript_exec", ["text"] = "6*7" }, none);
            Check("REPL eval", eval.Content.StartsWith("42"));
            Check("browser popout preserves the live tab and page state", await _codeWorkspace.VerifyBrowserPopoutContinuityAsync());
            Check("side chat popout preserves its session and returns or closes cleanly", _codeWorkspace.VerifySideChatPopoutContinuity());

            var shot = await handlers.ComputerAsync(
                new System.Text.Json.Nodes.JsonObject { ["action"] = "screenshot" }, none);
            Check("screenshot returns a JPEG with its size",
                shot.Content.StartsWith("Screenshot size: ") &&
                (shot.Images?.FirstOrDefault()?.Base64Data.Length ?? 0) > 1000);

            var text = await handlers.GetPageTextAsync([], none);
            Check("get_page_text uses the reference header",
                text.Content.StartsWith("Title: Pane selftest") && text.Content.Contains("Source element: <"));
            if (!text.Content.StartsWith("Title: Pane selftest"))
            {
                JarvisCode.Core.Utilities.DiagnosticLog.Write(
                    $"pane-selftest get_page_text content: {text.Content[..Math.Min(300, text.Content.Length)]}");
            }

            var resized = await handlers.ResizeAsync(
                new System.Text.Json.Nodes.JsonObject { ["preset"] = "mobile" }, none);
            Check("mobile emulation applied", resized.Content.Contains("Viewport set to 375x812 (mobile)"));
            await handlers.ResizeAsync(
                new System.Text.Json.Nodes.JsonObject { ["preset"] = "desktop" }, none);

            var context = await handlers.TabsContextAsync(none);
            Check("tabs_context reads open + displayed",
                context.Content.Contains("\"browserOpen\": true") &&
                context.Content.Contains("The Browser pane is currently displayed."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write($"pane-selftest: {ex.Message}");
            return 2;
        }
        finally
        {
            try
            {
                System.IO.File.Delete(pagePath);
            }
            catch (System.IO.IOException)
            {
            }
        }

        foreach (var failure in checks)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write($"pane-selftest failed: {failure}");
        }

        return checks.Count == 0 ? 0 : 2;
    }

    /// <summary>Every element of type T under <paramref name="root"/>, depth first.</summary>
    /// <summary>
    /// The row control a tool call actually presents. The template keeps both a
    /// toggle and a button and collapses one, and a collapsed element is still
    /// in the visual tree, so only the shown one answers.
    /// </summary>
    private System.Windows.Controls.Primitives.ButtonBase? VisibleRowFor(
        Func<ViewModels.ToolCallItem, bool> match) =>
        FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(_codeWorkspace)
            .FirstOrDefault(button =>
                button.IsVisible &&
                button.DataContext is ViewModels.ToolCallItem call &&
                IsRowHeader(button) &&
                match(call));

    /// <summary>
    /// The tool row's own control: the toggle and the button that share the row
    /// header template, and not the Copy or the image buttons its expanded body
    /// draws under the same call.
    /// </summary>
    private static bool IsRowHeader(System.Windows.Controls.Primitives.ButtonBase button) =>
        button is System.Windows.Controls.ContentControl content &&
        content.ContentTemplate is not null &&
        ReferenceEquals(content.ContentTemplate, button.TryFindResource("ToolRowHeaderContent"));

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// The metrics this UI was measured against Claude Code Desktop for, checked
    /// on the rendered visual tree rather than in the XAML: a style or a
    /// container change can move a control without touching the literal it was
    /// declared with, and only the arranged size says what the user sees.
    /// </summary>
    private static readonly (string Name, double Expected)[] ReferenceMetrics =
    [
        ("sidebar width", 288),
        ("title bar height", 36),
        ("session header height", 48),
        // The Code session's own titlebar, which is a different element than the window
        // header row above: the reference's `epitaxy-titlebar` is `h-[32px]`.
        ("epitaxy titlebar height", 32),
        ("settings nav width", 192),
        ("theme picker width", 920),
        ("theme picker height", 660),
        ("quick entry width", 606),
        // The pane mosaic: the reference's Tq.padding around it and Tq.gap between two
        // tiles, both read off the laid-out rectangles rather than off the constants.
        ("tile padding", 8),
        ("tile gap", 12),
        // The chat thinking cell: a 20px gutter, 8px spacers, the 200px clamp and
        // the 40px fade over it.
        ("thinking gutter width", 20),
        ("thinking spacer height", 8),
        ("thinking clamp height", 200),
        ("thinking fade height", 40),
    ];

    /// <summary>
    /// --ui-selftest (needs --screenshot to arm, like --pane-selftest): asserts
    /// the reference-measured geometry against what actually laid out, and
    /// returns 0/2. Failures name the metric and both numbers in the log.
    /// </summary>
    /// <summary>
    /// --subagent-selftest: drives the Background tasks pane's subagent view on
    /// the real controls — opening an agent row pushes the view under the call's
    /// own description, the real Back button pops it, and a call the transcript
    /// no longer holds leaves the pane on its list instead of pushing an empty
    /// view. Exits 0 when all three hold, 2 otherwise.
    /// </summary>
    public async Task<int> RunSubagentSelfTestAsync()
    {
        // The sample goes to the active surface, and a fresh profile opens on
        // Chat, so the Code session comes first.
        StartCodeSessionIn(Environment.CurrentDirectory);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        ShowSampleTranscript();
        _codeWorkspace.ShowPanel("runs");
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(400);

        var failures = new List<string>();

        // The row the user actually presses. Both controls sit in the tree with
        // one collapsed, so the check is on the one that is really shown: an
        // agent row is a plain button, so a mouse press, Space or Enter, and an
        // assistive tool's Invoke all land on the same handler — and it must not
        // advertise a toggle for a row that never expands.
        var agentRow = VisibleRowFor(call => call.CallId == SampleAgentCallId);
        // The row's own control, not everything its expanded body draws: the body
        // has a Copy of its own, and the two that share the row header template
        // are the pair one of which must be hidden.
        var shownForAgent = FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(_codeWorkspace)
            .Count(button =>
                button.IsVisible &&
                button.DataContext is ViewModels.ToolCallItem { CallId: SampleAgentCallId } &&
                IsRowHeader(button));
        if (shownForAgent != 1)
        {
            failures.Add($"the agent's row presents {shownForAgent} controls, not one");
        }

        if (agentRow is null)
        {
            failures.Add("the sample agent's row never appeared");
        }
        else if (agentRow is System.Windows.Controls.Primitives.ToggleButton)
        {
            failures.Add("the agent's row is still a toggle");
        }
        else
        {
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(agentRow);
            if (peer is null)
            {
                failures.Add("the agent's row exposes no automation peer");
            }
            else
            {
                if (peer.GetAutomationControlType() !=
                    System.Windows.Automation.Peers.AutomationControlType.Button)
                {
                    failures.Add(
                        $"the agent's row reads as {peer.GetAutomationControlType()} rather than a button");
                }

                if (peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle) is not null)
                {
                    failures.Add("the agent's row still advertises a toggle it cannot honour");
                }

                if (peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
                    is not System.Windows.Automation.Provider.IInvokeProvider invoke)
                {
                    failures.Add("the agent's row cannot be invoked by assistive tech");
                }
                else
                {
                    var wasExpanded = (agentRow.DataContext as ViewModels.ToolCallItem)?.IsExpanded;
                    invoke.Invoke();
                    await Dispatcher.InvokeAsync(
                        static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    if (_codeWorkspace.OpenSubagentCallId != SampleAgentCallId)
                    {
                        failures.Add("invoking the agent's row did not open its view");
                    }

                    if ((agentRow.DataContext as ViewModels.ToolCallItem)?.IsExpanded != wasExpanded)
                    {
                        failures.Add("invoking the agent's row expanded it as well");
                    }
                }
            }
        }

        // A row that really does expand keeps its toggle.
        var shellRow = VisibleRowFor(call => call.ToolName == "PowerShell");
        if (shellRow is null)
        {
            failures.Add("no ordinary tool row appeared to check against");
        }
        else if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(shellRow)
                 ?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle) is null)
        {
            failures.Add("an ordinary tool row lost the toggle it needs");
        }

        _codeWorkspace.OpenSubagentViewForTest(SampleAgentCallId, description: null);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (_codeWorkspace.OpenSubagentCallId != SampleAgentCallId)
        {
            failures.Add("opening an agent row did not push its view onto the pane");
        }

        var title = FindDescendants<System.Windows.Controls.TextBlock>(_codeWorkspace)
            .FirstOrDefault(block => block.Name == "PanelTitle")?.Text;
        if (title != "Search the parser")
        {
            failures.Add($"the pushed view is titled \"{title}\", not the call's own description");
        }

        var back = _codeWorkspace.SubagentBackButton;
        if (back.Visibility != Visibility.Visible)
        {
            failures.Add("the pushed view offers no way back");
        }
        else
        {
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(back);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (_codeWorkspace.OpenSubagentCallId is not null)
            {
                failures.Add("pressing Back did not return the pane to its task list");
            }
        }

        // A row whose transcript is gone: the pane belongs on its list.
        _codeWorkspace.OpenSubagentViewForTest("no-such-call", description: null);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (_codeWorkspace.OpenSubagentCallId is not null)
        {
            failures.Add("a call the transcript no longer holds still pushed a view");
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"subagent self-test: {failure}");
        }

        Console.WriteLine(failures.Count == 0
            ? "subagent self-test: the agent row invokes as a button, the view pushes, "
            + "Back pops it, and a missing call pushes nothing"
            : $"subagent self-test: {failures.Count} check(s) failed");
        return failures.Count == 0 ? 0 : 2;
    }

    /// <summary>
    /// Drives the composer's command menu on the real controls: the reference's
    /// geometry, its one-line row, the description card that replaced the
    /// second line, the alias in parentheses, the bolded match, the caret hint
    /// and the completion Tab performs. Exits 0 when every check holds, 2
    /// otherwise.
    /// </summary>
    public async Task<int> RunSlashSelfTestAsync()
    {
        StartCodeSessionIn(Environment.CurrentDirectory);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(400);

        var surface = ActiveSurface;
        var failures = new List<string>();

        async Task Pose(string text)
        {
            surface.PoseCommandMenu(text);
            await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            await Task.Delay(120);
        }

        await Pose("/");

        if (!surface.CommandMenuPopup.IsOpen)
        {
            Console.Error.WriteLine("slash self-test: the menu never opened for \"/\"");
            Console.WriteLine("slash self-test: 1 check(s) failed");
            return 2;
        }

        // The reference's own bounds. The width cap is the one the old popup
        // lacked, which is what let a long skill description stretch it across
        // the whole window.
        var box = surface.CommandMenuSurface;
        if (box.MinWidth != 240 || box.MaxWidth != 512)
        {
            failures.Add($"the menu is bounded {box.MinWidth}..{box.MaxWidth} wide, not 240..512");
        }

        if (box.MaxHeight > 384)
        {
            failures.Add($"the menu may grow to {box.MaxHeight}, past the reference's 384");
        }

        if (box.ActualWidth > 512)
        {
            failures.Add($"the menu rendered {box.ActualWidth:F0} wide, past its own cap");
        }

        if (surface.CommandMenuItems.Count == 0)
        {
            failures.Add("the menu opened with no rows");
        }

        var firstRow = surface.CommandMenuRows.FirstOrDefault();
        var firstItem = surface.CommandMenuItems.FirstOrDefault();
        if (firstRow is null || firstItem is null)
        {
            failures.Add("the menu's first row never rendered");
        }
        else
        {
            if (firstRow.Height != 32)
            {
                failures.Add($"a row is {firstRow.Height} tall, not the reference's 32");
            }

            if (firstItem.Label.StartsWith('/'))
            {
                failures.Add($"the row repeats the slash the user typed: \"{firstItem.Label}\"");
            }

            // The description belongs in the card beside the row, never in it.
            var inRow = FindDescendants<System.Windows.Controls.TextBlock>(firstRow)
                .Any(block => !string.IsNullOrEmpty(firstItem.SkillDescription)
                    && block.Text == firstItem.SkillDescription);
            if (inRow)
            {
                failures.Add("the row still carries its description instead of the card");
            }

            var lines = FindDescendants<System.Windows.Controls.TextBlock>(firstRow).Count(block => block.IsVisible);
            if (lines > 2)
            {
                failures.Add($"the row renders {lines} text blocks; the reference's is one line");
            }
        }

        if (!surface.CommandMenuDescriptionCard.IsOpen)
        {
            failures.Add("the highlighted row raised no description card");
        }
        else if (firstItem is not null && surface.CommandMenuDescriptionText.Text != firstItem.SkillDescription)
        {
            failures.Add("the card does not show the highlighted row's description");
        }

        if (surface.CommandMenuFilterGhost.Visibility != Visibility.Visible)
        {
            failures.Add("the empty query showed no \"Type to filter\" hint");
        }

        // Arrowing moves the highlight, repaints both rows and takes the card
        // with it — the highlight is swapped in place rather than re-rendered.
        var before = surface.CommandMenuSelection;
        surface.SendCommandMenuKey(System.Windows.Input.Key.Down);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var after = surface.CommandMenuSelection;
        if (after == before)
        {
            failures.Add("Down did not move the highlight");
        }
        else
        {
            var left = surface.CommandMenuRows[before] as System.Windows.Controls.Border;
            var arrived = surface.CommandMenuRows[after] as System.Windows.Controls.Border;
            if (!Equals(left?.Background, System.Windows.Media.Brushes.Transparent))
            {
                failures.Add("the row the highlight left is still painted");
            }

            if (arrived?.Background is null || Equals(arrived.Background, System.Windows.Media.Brushes.Transparent))
            {
                failures.Add("the row the highlight arrived at was not painted");
            }

            if (!surface.CommandMenuDescriptionCard.IsOpen)
            {
                failures.Add("the card did not follow the highlight");
            }
        }

        // Escape shuts the menu and keeps it shut while the command line stands.
        surface.SendCommandMenuKey(System.Windows.Input.Key.Escape);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (surface.CommandMenuPopup.IsOpen)
        {
            failures.Add("Escape did not close the menu");
        }

        await Pose("/co");
        if (surface.CommandMenuPopup.IsOpen)
        {
            failures.Add("the menu reopened on the next keystroke after Escape");
        }

        await Pose("hello");
        await Pose("/");
        if (!surface.CommandMenuPopup.IsOpen)
        {
            failures.Add("leaving the command line did not clear the dismissal");
        }

        await Pose("/bg");
        if (surface.CommandMenuFilterGhost.Visibility == Visibility.Visible)
        {
            failures.Add("the hint stayed up once a query was typed");
        }

        var aliased = surface.CommandMenuItems.FirstOrDefault();
        if (aliased?.Label != "background")
        {
            failures.Add($"\"/bg\" resolved to \"{aliased?.Label}\", not the command it aliases");
        }
        else if (surface.CommandMenuRows.FirstOrDefault() is { } aliasRow
            && !FindDescendants<System.Windows.Controls.TextBlock>(aliasRow).Any(block => block.Text == "(bg)"))
        {
            failures.Add("the matched alias is not shown in parentheses");
        }

        await Pose("/rec");
        var boldRow = surface.CommandMenuRows.FirstOrDefault();
        var bolded = boldRow is null
            ? null
            : FindDescendants<System.Windows.Controls.TextBlock>(boldRow)
                .SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Run>().ToList())
                .FirstOrDefault(run => run.FontWeight == FontWeights.SemiBold)?.Text;
        if (!string.Equals(bolded, "rec", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"the matched run is bolded as \"{bolded}\", not the typed \"rec\"");
        }

        // Tab completes the composer rather than running the command, which is
        // what the reference's chip insertion does.
        var target = surface.CommandMenuItems.FirstOrDefault()?.Label;
        surface.SendCommandMenuKey(System.Windows.Input.Key.Tab);
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (surface.ComposerText != $"/{target} ")
        {
            failures.Add($"Tab left the composer at \"{surface.ComposerText}\", not \"/{target} \"");
        }

        if (surface.CommandMenuPopup.IsOpen)
        {
            failures.Add("the menu stayed open after a completion");
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"slash self-test: {failure}");
        }

        Console.WriteLine(failures.Count == 0
            ? "slash self-test: the menu is bounded, one line per row, described in its card, "
            + "aliased, highlighted, hinted, and completes on Tab"
            : $"slash self-test: {failures.Count} check(s) failed");
        return failures.Count == 0 ? 0 : 2;
    }

    public async Task<int> RunUiSelfTestAsync()
    {
        StartCodeSessionIn(Environment.CurrentDirectory);
        // The session titlebar only draws over a started session, so a metric taken on an
        // empty one would measure a collapsed element and pass for the wrong reason.
        ShowSampleTranscript();
        // One layout pass at Loaded is not enough: the sidebar and header arrive
        // with the session, so the measurement waits for the tree to settle.
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(400);

        OpenSettings();
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(200);

        var measured = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["sidebar width"] = SidebarColumn.ActualWidth,
            ["title bar height"] = TitleBarRow.ActualHeight,
            ["session header height"] = HeaderRow.ActualHeight,
            ["epitaxy titlebar height"] = _codeWorkspace.Chat.SessionTitleBar.ActualHeight,
            ["settings nav width"] = OverlayHost.Content is Settings.SettingsDialog dialog
                ? dialog.SettingsNavColumn.ActualWidth
                : double.NaN,
        };
        OverlayHost.Content = null;

        // The two windows that carry their own pinned sizes. Measured while
        // open, then closed again so the capture that follows is the shell.
        OpenThemePicker();
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var picker = System.Windows.Application.Current.Windows.OfType<ThemePickerWindow>().FirstOrDefault();
        measured["theme picker width"] = picker?.Width ?? double.NaN;
        measured["theme picker height"] = picker?.Height ?? double.NaN;
        picker?.Close();

        ToggleQuickEntry();
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var quickEntry = System.Windows.Application.Current.Windows.OfType<QuickEntryWindow>().FirstOrDefault();
        measured["quick entry width"] = quickEntry?.Width ?? double.NaN;
        quickEntry?.Close();

        // The pane mosaic, measured with a second tile open so the gap is real.
        _codeWorkspace.ShowPanel("terminal");
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(200);
        var tiles = _codeWorkspace.MeasuredTileRects;
        measured["tile padding"] = tiles.TryGetValue("chat", out var chatRect) ? chatRect.Left : double.NaN;
        measured["tile gap"] =
            tiles.TryGetValue("chat", out var left) && tiles.TryGetValue("terminal", out var right)
                ? right.Left - left.Right
                : double.NaN;
        _codeWorkspace.ShowPanel(null);

        // The chat thinking cell, measured on a posed transcript: its clamp only
        // reports 200 when it actually clipped something, so the pose carries a
        // cell taller than the limit.
        ShowSampleThinking();
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(400);
        var clamped = FindDescendants<Controls.ThinkingCell>(this).FirstOrDefault(cell => cell.IsClamped);
        foreach (var name in new[]
                 {
                     "thinking gutter width", "thinking spacer height",
                     "thinking clamp height", "thinking fade height",
                 })
        {
            measured[name] = clamped?.MeasuredGeometry()[name] ?? double.NaN;
        }

        var failures = new List<string>();
        foreach (var (name, expected) in ReferenceMetrics)
        {
            var actual = measured[name];
            // NaN is checked first and on its own: every comparison against NaN
            // is false, so a control that never appeared would otherwise pass.
            if (double.IsNaN(actual))
            {
                failures.Add($"{name}: the element it is measured on never appeared");
            }
            // Device-independent units, so an exact match is the expectation; the
            // tolerance only absorbs WPF's layout rounding.
            else if (Math.Abs(actual - expected) > 0.5)
            {
                failures.Add($"{name}: expected {expected}, laid out {actual}");
            }
        }

        foreach (var failure in failures)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write($"ui-selftest failed: {failure}");
        }

        JarvisCode.Core.Utilities.DiagnosticLog.Write(
            $"ui-selftest: {ReferenceMetrics.Length - failures.Count}/{ReferenceMetrics.Length} metrics match");
        return failures.Count == 0 ? 0 : 2;
    }

    /// <summary>
    /// Dev/verification hook for the three things about a text field a screenshot cannot
    /// settle: whether its caret can be seen at all (WPF draws an unset one in the inverse
    /// of the Background colour, which is near-black over anything transparent or
    /// alpha-composited), whether the placeholder starts where the first character will,
    /// and whether either of them fits the field. Every editable field on every surface
    /// this can pose is walked; exits 0 when they all pass and 2 otherwise.
    /// </summary>
    public async Task<int> RunInputSelfTestAsync()
    {
        var findings = new List<Services.InputFieldAudit.Finding>();
        var surfaces = 0;

        // Not an idle-priority await: this poses focused fields, and a blinking caret
        // keeps render-priority work queued, which can starve every idle tier forever.
        // A delay resumes at Normal priority and UpdateLayout settles the tree by hand.
        async Task Settle()
        {
            await Task.Delay(250);
            UpdateLayout();
        }

        void Check(System.Windows.Media.Visual? root, string surface)
        {
            if (root is null)
            {
                findings.Add(new Services.InputFieldAudit.Finding(surface, "the surface never appeared"));
                return;
            }

            surfaces++;
            List<Services.InputFieldAudit.Finding> found;
            try
            {
                (root as FrameworkElement)?.UpdateLayout();
                found = Services.InputFieldAudit.Audit(root, surface);
            }
            catch (Exception error)
            {
                found = [new Services.InputFieldAudit.Finding(surface, $"the audit threw: {error}")];
            }

            JarvisCode.Core.Utilities.DiagnosticLog.Write(
                $"input-selftest: {surface} - {found.Count} problem(s)");
            findings.AddRange(found);
        }

        foreach (var (group, label) in new[]
                 {
                     ("Settings", "General"), ("Settings", "Providers"),
                     ("Settings", "Permissions"), ("Settings", "Jarvis Code"),
                     ("Desktop app", "General"), ("Desktop app", "Developer"),
                 })
        {
            OpenSettings(group, label);
            await Settle();
            Check(OverlayHost.Content as System.Windows.Media.Visual, $"settings {group} / {label}");

        }


        StartCodeSessionIn(Environment.CurrentDirectory);
        ShowSampleTranscript();
        Sidebar.FocusSearch();
        await Settle();
        Check(this, "code session");

        ShowSamplePlanApproval(editing: true);
        ShowSampleQuestion();
        await Settle();
        Check(this, "plan approval and question card");

        // The Browser pane is deliberately not posed: opening it fetches the 374MB
        // engine, and its address box is the same PanelAddressBox the Files pane draws.
        foreach (var pane in new[] { "files", "terminal", "diff" })
        {
            _codeWorkspace.ShowPanel(pane);
            await Settle();
            Check(this, $"{pane} pane");
        }

        _codeWorkspace.ShowPanel(null);

        OverlayHost.Content = null;

        foreach (var page in new[] { "skills", "connectors", "plugins", "memory", "directory" })
        {
            EnterCustomize(page);
            await Settle();
            Check(this, $"customize {page}");
        }

        OpenRoutines();
        await Settle();
        Check(this, "scheduled");

        OpenCommandPalette();
        await Settle();
        Check(OverlayHost.Content as System.Windows.Media.Visual, "command palette");
        OverlayHost.Content = null;

        OpenThemePicker();
        await Settle();
        var picker = System.Windows.Application.Current.Windows.OfType<ThemePickerWindow>().FirstOrDefault();
        Check(picker, "theme picker");
        picker?.Close();

        ToggleQuickEntry();
        await Settle();
        var quickEntry = System.Windows.Application.Current.Windows.OfType<QuickEntryWindow>().FirstOrDefault();
        Check(quickEntry, "quick entry");
        quickEntry?.Close();

        foreach (var finding in findings)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write(
                $"input-selftest failed: {finding.Field} - {finding.Problem}");
            Console.Error.WriteLine($"input-selftest: {finding.Field} - {finding.Problem}");
        }

        var verdict = findings.Count == 0
            ? $"input-selftest: {surfaces} surfaces, every caret visible and every placeholder on the caret"
            : $"input-selftest: {findings.Count} problem(s) over {surfaces} surfaces";
        JarvisCode.Core.Utilities.DiagnosticLog.Write(verdict);
        Console.WriteLine(verdict);
        return findings.Count == 0 ? 0 : 2;
    }

    /// <summary>
    /// Dev/verification hook for the one bug a single screenshot cannot show: sends a
    /// prompt, starts a second session answering the same prompt while the first is still
    /// streaming, then comes back to the first. Exits 0 when the first turn was still live
    /// on the way back and both sessions ended up with an answer, 2 when the round trip
    /// cost either of them, and 3 when the model replied so fast there was never a live
    /// turn to switch away from.
    /// </summary>
    public async Task<int> RunE2ESessionSwitchAsync(string prompt)
    {
        var surface = ActiveSurface;
        var first = surface.ViewModel.Session;
        surface.SubmitExternal(prompt);
        if (!await WaitForAsync(() => surface.ViewModel.IsRunning, TimeSpan.FromSeconds(20)))
        {
            return 3;
        }

        StartNewSession();
        var second = surface.ViewModel.Session;
        surface.SubmitExternal(prompt);
        // Observe the second turn's real running boundary. A fixed delay can
        // let a fast first response finish before this check tests anything.
        if (!await WaitForAsync(() => surface.ViewModel.IsRunning, TimeSpan.FromSeconds(20)))
        {
            return 3;
        }
        if (!surface.RunningSessionIds.Contains(first.Id))
        {
            return 3;
        }

        surface.LoadSession(first);
        var firstViewModel = surface.ViewModel;
        var survived = firstViewModel.Session.Id == first.Id && firstViewModel.IsRunning;
        var firstCompleted = await WaitForAsync(() => !firstViewModel.IsRunning, TimeSpan.FromMinutes(3));

        surface.LoadSession(second);
        var secondViewModel = surface.ViewModel;
        var secondCompleted = await WaitForAsync(() => !secondViewModel.IsRunning, TimeSpan.FromMinutes(3));
        var bothAnswered = firstCompleted && secondCompleted && secondViewModel.Session.Id == second.Id
            && Answered(firstViewModel)
            && Answered(secondViewModel)
            && !firstViewModel.Transcript.OfType<ViewModels.ErrorCardItem>().Any()
            && !secondViewModel.Transcript.OfType<ViewModels.ErrorCardItem>().Any()
            && !firstViewModel.Transcript.OfType<ViewModels.NoticeItem>().Any(static item => item.IsError)
            && !secondViewModel.Transcript.OfType<ViewModels.NoticeItem>().Any(static item => item.IsError);

        // The screenshot should show the session that was streaming when we walked away.
        surface.LoadSession(first);
        return survived && bothAnswered ? 0 : 2;
    }

    private static bool Answered(ViewModels.ChatViewModel viewModel) =>
        viewModel.Transcript.OfType<ViewModels.AssistantTextItem>().Any(static a => a.Markdown.Length > 0);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    public void ShutdownCleanup()
    {
        _codeWorkspace.DisposeTerminal();
        _codeWorkspace.DisposeWebViews();
        MermaidRenderer.Current = null;
        _mermaid.Dispose();
        _routineRunner?.Dispose();
        _quickEntryHotkey?.Dispose();
        _tray?.Dispose();
        _quickEntry?.Close();
    }
}
