using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The window's OS-facing half: the application menu the title bar pops, the
/// Windows jump list, the <c>jarvis://</c> routes, the notification levels that
/// decide badge and taskbar flash, the first-run notices and the troubleshooting
/// actions the Help menu offers. Ported from the reference's main process, whose
/// equivalents live in app.asar <c>index.chunk-DnlgCaT3.js</c>.
/// </summary>
public partial class MainWindow
{
    private AppUpdateService? _updates;
    private readonly HashSet<(string Type, string Session)> _blockingOnUser = [];

    /// <summary>Set while a relaunch is waiting for every running turn to finish.</summary>
    private bool _relaunchWhenIdle;

    /// <summary>Where a staged update is downloaded, beside the profile's own files.</summary>
    private string UpdateStagingRoot =>
        System.IO.Path.Combine(_services.Paths.Root, "updates");

    // ---- start-up ----

    /// <summary>
    /// Wires everything this file owns. Called from the constructor, after the
    /// surfaces exist, so a deep link that arrives during start-up has somewhere
    /// to land.
    /// </summary>
    private void InitializeShell()
    {
        var ui = _services.UiSettings.Current;

        _updates = new AppUpdateService(AppVersion, UpdateStagingRoot)
        {
            Disabled = !ui.AutoUpdateEnabled,
        };
        // The reference rebuilds its application menu on every updater transition,
        // because the Help menu's rows are what the state is shown in.
        _updates.States.Changed += _ => Dispatcher.BeginInvoke(RefreshUpdateSurfaces);

        Loaded += (_, _) =>
        {
            // A pose or a self-test neither wants a network call nor a state change
            // repainting the card it was told to draw.
            if (!AutomationMode)
            {
                _updates?.StartPolling();
            }

            _ = RefreshJumpListAsync();
            ShowFirstRunNotices();
        };

        // The jump list names the sessions waiting on the user, so it is rebuilt
        // whenever that set can have changed.
        _services.UiSettings.Saved += (_, _) => Dispatcher.BeginInvoke(() => _ = RefreshJumpListAsync());
    }

    private static string AppVersion =>
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";

    /// <summary>
    /// Set while the app is being driven by a dev flag (--screenshot, --open, --e2e,
    /// a self-test). Nothing modal may open in that mode: there is nobody to answer
    /// it, and it would sit over whatever the run was measuring.
    /// </summary>
    internal static bool AutomationMode { get; set; }

    // ---- transient notices ----

    /// <summary>
    /// Shows a transient notice, optionally with one action — the shape the
    /// reference's own "Session exported / Show in folder" toast has. It rides the
    /// window's toast queue rather than a second notice surface of its own.
    /// </summary>
    private void ShowToast(string text, string? actionLabel = null, Action? action = null) =>
        _services.Toasts.Add(text, actionLabel: actionLabel, actionInvoke: action);

    /// <summary>The same, in the queue's danger variant, for a notice that reports a failure.</summary>
    private void ShowErrorToast(string text) => _services.Toasts.AddError(text);

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => OpenAppMenu();

    // ---- the application menu ----

    /// <summary>Pops the app menu under the title bar's menu button.</summary>
    public void OpenAppMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = MenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var item in AppMenu.Build(CurrentMenuState()))
        {
            menu.Items.Add(BuildMenuItem(item));
        }

        menu.IsOpen = true;
    }

    private AppMenuState CurrentMenuState() => new(
        OnCodeSurface: IsCodeActive,
        SidebarVisible: !_services.UiSettings.Current.SidebarCollapsed,
        // Panes are a tile layout rather than one slot, so a checkbox asks whether
        // its own pane is open and Close Pane asks whether any is.
        TerminalVisible: _codeWorkspace.IsPaneOpen(Services.SidePanes.Terminal),
        DiffVisible: _codeWorkspace.IsPaneOpen(Services.SidePanes.Diff),
        BrowserVisible: _codeWorkspace.IsPaneOpen(Services.SidePanes.Preview),
        SideChatVisible: _codeWorkspace.IsPaneOpen(Services.SidePanes.SideChat),
        ClosePaneEnabled: _codeWorkspace.HasOpenPanel,
        FullScreen: _services.UiSettings.Current.FullscreenMode,
        Zoomed: Math.Abs(_services.UiSettings.Current.ZoomFactor - 1.0) > 0.001,
        HardwareAccelerationDisabled: _services.UiSettings.Current.HardwareAccelerationDisabled,
        Update: _updates?.States.State ?? UpdateState.Idle);

    private object BuildMenuItem(AppMenuItem item)
    {
        if (item.Command == AppMenuCommand.Separator)
        {
            return new Separator();
        }

        var menuItem = new MenuItem
        {
            Header = item.Label,
            IsEnabled = item.Enabled,
            InputGestureText = Accelerators.Display(item.Accelerator),
        };

        if (item.Kind == AppMenuItemKind.Checkbox)
        {
            menuItem.IsCheckable = true;
            menuItem.IsChecked = item.Checked;
        }

        if (item.Items is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                menuItem.Items.Add(BuildMenuItem(child));
            }

            return menuItem;
        }

        var command = item.Command;
        menuItem.Click += (_, _) => RunMenuCommand(command);
        return menuItem;
    }

    /// <summary>Runs one menu row's action.</summary>
    private void RunMenuCommand(AppMenuCommand command)
    {
        switch (command)
        {
            case AppMenuCommand.NewConversation:
                StartNewSession();
                break;
            case AppMenuCommand.OpenFile:
                OpenFileInSession();
                break;
            case AppMenuCommand.OpenFolder:
                OpenFolderAsSession();
                break;
            case AppMenuCommand.Settings:
                OpenSettings();
                break;
            case AppMenuCommand.CloseWindow:
                Close();
                break;
            case AppMenuCommand.Exit:
                ExitApplication();
                break;

            case AppMenuCommand.Undo:
                System.Windows.Input.ApplicationCommands.Undo.Execute(null, FocusedTarget());
                break;
            case AppMenuCommand.Redo:
                System.Windows.Input.ApplicationCommands.Redo.Execute(null, FocusedTarget());
                break;
            case AppMenuCommand.Cut:
                System.Windows.Input.ApplicationCommands.Cut.Execute(null, FocusedTarget());
                break;
            case AppMenuCommand.Copy:
                System.Windows.Input.ApplicationCommands.Copy.Execute(null, FocusedTarget());
                break;
            case AppMenuCommand.Paste:
                System.Windows.Input.ApplicationCommands.Paste.Execute(null, FocusedTarget());
                break;
            case AppMenuCommand.SelectAll:
                System.Windows.Input.ApplicationCommands.SelectAll.Execute(null, FocusedTarget());
                break;
            case AppMenuCommand.Find:
                OpenFind();
                break;
            case AppMenuCommand.FindNext:
                StepFind(1);
                break;
            case AppMenuCommand.FindPrevious:
                StepFind(-1);
                break;

            case AppMenuCommand.CommandPalette:
                OpenCommandPalette();
                break;
            case AppMenuCommand.ToggleSidebar:
                SetSidebarCollapsed(!_services.UiSettings.Current.SidebarCollapsed);
                break;
            case AppMenuCommand.ToggleTerminal:
                _codeWorkspace.TogglePanel(Services.SidePanes.Terminal);
                break;
            case AppMenuCommand.ToggleDiff:
                _codeWorkspace.TogglePanel(Services.SidePanes.Diff);
                break;
            case AppMenuCommand.ToggleBrowser:
                _codeWorkspace.TogglePanel(Services.SidePanes.Preview);
                break;
            case AppMenuCommand.ToggleSideChat:
                _codeWorkspace.ToggleSideChat();
                break;
            case AppMenuCommand.ClosePane:
                _codeWorkspace.ShowPanel(null);
                break;
            case AppMenuCommand.PreviousSidebarTab:
                StepSurface(-1);
                break;
            case AppMenuCommand.NextSidebarTab:
                StepSurface(1);
                break;
            case AppMenuCommand.ActualSize:
                ApplyZoom(1.0);
                break;
            case AppMenuCommand.ZoomIn:
                StepZoom(1);
                break;
            case AppMenuCommand.ZoomOut:
                StepZoom(-1);
                break;
            case AppMenuCommand.FullScreen:
                SetFullscreen(!_services.UiSettings.Current.FullscreenMode);
                break;
            case AppMenuCommand.Reload:
                ReloadSurface();
                break;

            case AppMenuCommand.OpenDocumentation:
                ExternalLinks.Open(this, AppMenu.DocumentationUrl);
                break;
            case AppMenuCommand.GetSupport:
                ExternalLinks.Open(this, AppMenu.SupportUrl);
                break;
            case AppMenuCommand.About:
                AboutWindow.ShowFor(this);
                break;
            case AppMenuCommand.CheckForUpdates:
                _ = CheckForUpdatesAsync();
                break;
            case AppMenuCommand.RestartToUpdate:
                _ = RestartToUpdateAsync();
                break;
            case AppMenuCommand.ShowLogsInExplorer:
                ShowInExplorer(_services.Paths.Root);
                break;
            case AppMenuCommand.CopyInstallationId:
                CopyInstallationId();
                break;
            case AppMenuCommand.GenerateDiagnosticReport:
                GenerateDiagnosticReport();
                break;
            case AppMenuCommand.DisableHardwareAcceleration:
                ToggleHardwareAcceleration();
                break;
            case AppMenuCommand.ImportCliSessions:
                _ = ImportCliSessionsAsync();
                break;
            case AppMenuCommand.ClearCacheAndRestart:
                _ = ClearCacheAndRestartAsync();
                break;
            case AppMenuCommand.ResetAppData:
                ResetAppData();
                break;
        }
    }

    private IInputElement? FocusedTarget() =>
        System.Windows.Input.Keyboard.FocusedElement ?? ActiveSurface;

    private void StepSurface(int delta)
    {
        // Two surfaces, so either direction is a swap; the reference cycles its
        // three the same way.
        _ = delta;
        if (IsCodeActive)
        {
            ChatTab.IsChecked = true;
        }
        else
        {
            CodeTab.IsChecked = true;
        }
    }

    /// <summary>
    /// The reference's Reload re-renders its web view. The nearest honest thing
    /// here is to re-read the session list and re-open the session in front, which
    /// is what a stale view would need.
    /// </summary>
    private void ReloadSurface()
    {
        RefreshSessionList();
        if (ActiveSurface.ViewModel.Session.Id is { Length: > 0 } id)
        {
            _ = OpenSessionAsync(id);
        }
    }

    private void OpenFileInSession()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = AppMenu.FileLabel };
        if (dialog.ShowDialog(this) == true)
        {
            ActiveSurface.PrefillInput("@" + dialog.FileName);
        }
    }

    private void OpenFolderAsSession()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Open Folder" };
        if (dialog.ShowDialog(this) == true)
        {
            StartCodeSessionIn(dialog.FolderName);
        }
    }

    // ---- find, across the whole window ----

    /// <summary>
    /// The reference's <c>TIn()</c>: a focused browser pane finds inside the page,
    /// and everything else finds in the conversation.
    /// </summary>
    private void OpenFind()
    {
        if (BrowserPaneHasFocus())
        {
            _codeWorkspace.BrowserPanel.OpenFind();
            return;
        }

        ActiveSurface.ToggleFindBar();
    }

    private void StepFind(int direction)
    {
        if (BrowserPaneHasFocus())
        {
            _codeWorkspace.BrowserPanel.StepFind(direction);
            return;
        }

        ActiveSurface.StepFind(direction);
    }

    private bool BrowserPaneHasFocus() =>
        IsCodeActive && _codeWorkspace.IsPaneOpen(Services.SidePanes.Preview) &&
        _codeWorkspace.BrowserPanel.IsKeyboardFocusWithin;

    // ---- notifications, badge and taskbar flash ----

    /// <summary>
    /// Records that something is waiting on the user and repaints the taskbar
    /// badge. The reference counts blocking requests rather than kinds, so two
    /// sessions waiting count as two — and a type set to "Off" counts as none.
    /// </summary>
    private void NoteBlockedOnUser(string notificationType, string sessionId)
    {
        _blockingOnUser.Add((notificationType, sessionId));
        RefreshBadge();
    }

    /// <summary>
    /// Repaints the badge from what is still waiting, the reference's
    /// <c>updateBadge</c>: only the types whose level asks for a badge count.
    /// </summary>
    private void RefreshBadge()
    {
        var levels = _services.UiSettings.Current.NotificationLevels;
        var count = _blockingOnUser.Count(
            entry => Services.NotificationPolicy.CountsForBadge(levels, entry.Type));
        TaskbarAttention.SetBadgeCount(this, count);
    }

    /// <summary>
    /// Coming back to the window answers the two momentary types — a finished turn
    /// and a question already on screen — while a permission prompt keeps its place
    /// on the badge until it is actually answered.
    /// </summary>
    private void OnWindowActivatedForAttention()
    {
        var waiting = WaitingSessionIds();
        _blockingOnUser.RemoveWhere(entry =>
            entry.Type != Services.NotificationPolicy.Permission ||
            !waiting.Contains(entry.Session));
        RefreshBadge();
    }

    // ---- jump list ----

    private async Task RefreshJumpListAsync()
    {
        try
        {
            var summaries = await _services.Sessions.ListAsync();
            var waiting = WaitingSessionIds();
            var sessions = summaries.Select(s => new JumpListSession(
                s.Id,
                s.Title,
                s.WorkingDirectory,
                s.UpdatedAt,
                IsArchived: _services.SessionGroups.IsArchived(s.Id),
                IsWaiting: waiting.Contains(s.Id),
                FolderExists: string.IsNullOrEmpty(s.WorkingDirectory) ||
                    System.IO.Directory.Exists(s.WorkingDirectory)));
            var model = JumpList.Build(sessions, "jump_list");
            WindowsJumpList.Apply(JumpList.Categories(model));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            // A jump list that cannot be written changes nothing about the app.
        }
    }

    /// <summary>
    /// The sessions with a permission prompt on screen, which are the ones the
    /// reference lists under "Needs Your Input". Only open sessions can be in that
    /// state, since a prompt is a live turn waiting on an answer.
    /// </summary>
    private HashSet<string> WaitingSessionIds() =>
    [
        .. _codeWorkspace.AllCodeSurfaces
            .Where(static surface => surface.ViewModel.PendingPermission is not null)
            .Select(static surface => surface.ViewModel.Session.Id),
    ];

    /// <summary>Opens Explorer with the file selected, which is what "Show in folder" does.</summary>
    internal static void RevealInExplorer(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    ArgumentList = { "/select,", path },
                    UseShellExecute = true,
                });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or System.IO.FileNotFoundException)
        {
            // Explorer is not available; the file is still written.
        }
    }

    // ---- jarvis:// routes ----

    /// <summary>
    /// Acts on a link the protocol handler delivered. Every route is the
    /// reference's; an unrecognized one is dropped, as its handler drops it.
    /// </summary>
    public void HandleDeepLink(string link)
    {
        var route = DeepLinks.Parse(link);
        if (route.Kind == DeepLinkKind.Unrecognized)
        {
            return;
        }

        RestoreFromTray();
        switch (route.Kind)
        {
            case DeepLinkKind.NewChat:
                ChatTab.IsChecked = true;
                StartNewSession();
                break;

            case DeepLinkKind.NewCodeSession:
                CodeTab.IsChecked = true;
                if (route.Folders is { Count: > 0 } folders)
                {
                    StartCodeSessionIn(folders[0]);
                }
                else
                {
                    StartNewSession();
                }

                if (route.Prompt is { Length: > 0 } prompt)
                {
                    Dispatcher.BeginInvoke(
                        () => ActiveSurface.PrefillInput(prompt),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }

                break;

            case DeepLinkKind.ContinueCodeSession:
                CodeTab.IsChecked = true;
                if (route.Session is { Length: > 0 } and not "last" and var session)
                {
                    _ = OpenSessionAsync(session);
                }
                else
                {
                    _ = ContinueLastSessionAsync();
                }

                break;

            case DeepLinkKind.NeedsInput:
                CodeTab.IsChecked = true;
                _ = OpenWaitingSessionAsync(route.Session);
                break;

            case DeepLinkKind.ResumeCliSession:
                _ = ResumeCliSessionAsync(route.Session!);
                break;
        }
    }

    /// <summary>
    /// Opens the session a needs-input link named, or — as the reference does when
    /// the link names none — whichever has waited longest.
    /// </summary>
    private async Task OpenWaitingSessionAsync(string? sessionId)
    {
        if (sessionId is { Length: > 0 })
        {
            await OpenSessionAsync(sessionId);
            return;
        }

        var summaries = await _services.Sessions.ListAsync();
        var waiting = WaitingSessionIds();
        var oldest = summaries
            .Where(summary => waiting.Contains(summary.Id))
            .OrderBy(static summary => summary.UpdatedAt)
            .FirstOrDefault();
        if (oldest is not null)
        {
            await OpenSessionAsync(oldest.Id);
        }
    }

    /// <summary>
    /// The reference's <c>claude://resume</c>: import a Claude Code CLI transcript
    /// by its uuid and open it. Each way it can fail has its own toast, so the user
    /// is told what to try rather than that something went wrong.
    /// </summary>
    private async Task ResumeCliSessionAsync(string uuid)
    {
        try
        {
            var imported = await Services.CliSessionImporter.ImportOneAsync(
                _services.Sessions, uuid);
            if (imported is null)
            {
                ShowToast(ResumeFailureMessages.For(ResumeFailure.TranscriptMissing));
                return;
            }

            CodeTab.IsChecked = true;
            RefreshSessionList();
            await OpenSessionAsync(imported);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowToast(ResumeFailureMessages.For(ResumeFailure.Other));
        }
    }

    // ---- updates ----

    private void RefreshUpdateSurfaces()
    {
        var card = UpdateCard.For(_updates?.States.State ?? UpdateState.Idle);
        Sidebar.SetUpdateCard(card);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updates is null)
        {
            return;
        }

        var state = await _updates.CheckAsync(manual: true);
        switch (state.Status)
        {
            case UpdateStatus.Downloading or UpdateStatus.Ready:
                MessageDialog.Show(
                    this,
                    "Update Available",
                    "A new version is available. It will be downloaded and installed automatically.",
                    type: MessageDialogType.Info);
                break;
            case UpdateStatus.Idle:
                MessageDialog.Show(
                    this,
                    "No Update Available",
                    "You are running the latest version.",
                    type: MessageDialogType.Info);
                break;
            case UpdateStatus.Error:
                var message = state.Error ?? "";
                MessageDialog.Show(
                    this,
                    UpdateCheckFailure.Title,
                    UpdateCheckFailure.Detail(message, AppUpdateService.FeedHost),
                    type: UpdateCheckFailure.LooksIntercepted(message)
                        ? MessageDialogType.Warning
                        : MessageDialogType.Error);
                break;
        }
    }

    /// <summary>
    /// Installs the staged update, first asking about the sessions it would
    /// interrupt — the reference's "still working" dialog, which offers to wait
    /// rather than only to cancel.
    /// </summary>
    public async Task RestartToUpdateAsync()
    {
        if (_updates is null || _updates.States.State.Status != UpdateStatus.Ready)
        {
            return;
        }

        var running = RunningSessions();
        if (running.Count > 0 && !ConfirmRelaunchWhileWorking(running))
        {
            return;
        }

        if (_updates.BeginInstall())
        {
            _exitRequested = true;
            Application.Current.Shutdown();
            return;
        }

        await Task.CompletedTask;
    }

    private IReadOnlyList<string> RunningSessions() =>
        [.. _codeWorkspace.RunningSessionTitles()];

    /// <summary>
    /// The reference's three answers: cancel, wait for the running turns to finish
    /// and relaunch then, or interrupt them now.
    /// </summary>
    private bool ConfirmRelaunchWhileWorking(IReadOnlyList<string> running)
    {
        var response = MessageDialog.Show(
            this,
            RelaunchWhileWorking.Title,
            RelaunchWhileWorking.Detail(running.Count, running.Count == 1 ? running[0] : null),
            [
                RelaunchWhileWorking.Cancel,
                RelaunchWhileWorking.WaitForJarvis,
                RelaunchWhileWorking.UpdateAnyway,
            ],
            defaultId: 2,
            cancelId: 0,
            MessageDialogType.Warning);

        if (response == 1)
        {
            _relaunchWhenIdle = true;
            ShowToast(RelaunchWhileWorking.WaitingDetail);
        }

        return response == 2;
    }

    /// <summary>
    /// Called when a turn finishes: a relaunch that was told to wait takes its
    /// chance as soon as nothing is running.
    /// </summary>
    private void InstallStagedUpdateIfIdle()
    {
        if (!_relaunchWhenIdle || RunningSessions().Count > 0)
        {
            return;
        }

        _relaunchWhenIdle = false;
        if (_updates?.BeginInstall() == true)
        {
            _exitRequested = true;
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// Poses the sidebar's auto-updater banner for a screenshot
    /// (--open=update[:available|:downloading|:failed]). It drives the real state
    /// machine rather than the card directly, so what renders is what a live
    /// updater would produce.
    /// </summary>
    public void ShowSampleUpdateCard(string? variant)
    {
        var states = new UpdateStateMachine();
        switch (variant)
        {
            case "downloading":
                states.OnCheckingForUpdate();
                states.OnUpdateAvailable();
                break;
            case "failed":
                states.OnCheckingForUpdate();
                states.OnError("net::ERR_CONNECTION_REFUSED");
                break;
            case "checking":
                states.OnCheckingForUpdate();
                break;
            default:
                states.OnCheckingForUpdate();
                states.OnUpdateAvailable();
                states.OnUpdateDownloaded(AppVersion);
                break;
        }

        Sidebar.SetUpdateCard(UpdateCard.For(states.State));
    }

    // ---- troubleshooting ----

    private static void ShowInExplorer(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or System.IO.FileNotFoundException)
        {
            // Nothing to show; the folder is gone.
        }
    }

    private void CopyInstallationId()
    {
        var ui = _services.UiSettings.Current;
        ui.ClientDeviceId ??= Guid.NewGuid().ToString();
        _services.UiSettings.Save();
        try
        {
            Clipboard.SetText(ui.ClientDeviceId);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard.
        }
    }

    /// <summary>
    /// Help ▸ Troubleshooting ▸ Generate Diagnostic Report: packages this
    /// installation's own diagnostics behind the reference's modal.
    /// </summary>
    /// <summary>--open=diagnostics: the same modal, posed without blocking.</summary>
    public void PoseDiagnosticReport() => GenerateDiagnosticReport(pose: true);

    private void GenerateDiagnosticReport() => GenerateDiagnosticReport(pose: false);

    private void GenerateDiagnosticReport(bool pose)
    {
        var settings = _services.Settings.Current;
        var hosts = new List<string>(FirewallAllowlist.Hosts(settings));
        foreach (var url in _services.Mcp.RemoteServerUrls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                hosts.Add(parsed.Host);
            }
        }

        var ui = _services.UiSettings.Current;
        ui.ClientDeviceId ??= Guid.NewGuid().ToString();
        _services.UiSettings.Save();

        var context = new DiagnosticContext(
                typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "dev",
                ui.ClientDeviceId,
                _services.Paths.Root,
                SessionDebugLog.Path,
                _services.Paths.SettingsFile,
                hosts,
                [.. _services.Mcp.ConnectedToolCounts.Select(p => (p.Key, p.Value))],
                _services.ModelTraffic.Snapshot());

        if (pose)
        {
            DiagnosticReportDialog.Pose(this, context, _services.Http);
            return;
        }

        DiagnosticReportDialog.Show(this, context, _services.Http);
    }

    /// <summary>
    /// The reference's Disable Hardware Acceleration row: the setting is written and
    /// the app asks to restart, because the renderer reads it once at start-up.
    /// </summary>
    private void ToggleHardwareAcceleration()
    {
        var ui = _services.UiSettings.Current;
        ui.HardwareAccelerationDisabled = !ui.HardwareAccelerationDisabled;
        _services.UiSettings.Save();

        var response = MessageDialog.Show(
            this,
            "Restart Required",
            "The application must be restarted for this change to take effect.",
            ["Later", "Restart Now"],
            defaultId: 1,
            cancelId: 0,
            MessageDialogType.Info);
        if (response == 1)
        {
            Restart();
        }
    }

    private async Task ClearCacheAndRestartAsync()
    {
        try
        {
            await Task.Run(() => ClearCaches(_services.Paths.Root));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(
                this, "Error clearing cache", ex.Message, type: MessageDialogType.Error);
            return;
        }

        MessageDialog.Show(
            this,
            "Cache cleared successfully",
            "The application will now restart.",
            ["Restart"],
            type: MessageDialogType.Info);
        Restart();
    }

    /// <summary>
    /// The caches this app can drop without losing anything the user typed: the
    /// engine profiles the panes keep and the session-list summary cache.
    /// </summary>
    private static void ClearCaches(string root)
    {
        foreach (var name in new[] { "webview", "browser-profile", "cache" })
        {
            var directory = System.IO.Path.Combine(root, name);
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }

        var cache = System.IO.Path.Combine(root, "session-list-cache.json");
        if (System.IO.File.Exists(cache))
        {
            System.IO.File.Delete(cache);
        }
    }

    private void ResetAppData()
    {
        var response = MessageDialog.Show(
            this,
            "Reset Application Data",
            "This will delete all application data including settings, cache, and login " +
            "information. The application will restart after the reset. Are you sure you want " +
            "to continue?",
            ["Cancel", "Reset"],
            defaultId: 0,
            cancelId: 0,
            MessageDialogType.Warning);
        if (response != 1)
        {
            return;
        }

        // The reference deletes everything but its logs, then restarts. The delete
        // itself is left to the relaunched process, because this one still holds
        // files inside the profile open.
        Services.PendingAppDataReset.Arm(_services.Paths.Root);
        MessageDialog.Show(
            this,
            "Reset complete",
            "The application will now restart.",
            ["Restart"],
            type: MessageDialogType.Info);
        Restart();
    }

    private void Restart()
    {
        if (Environment.ProcessPath is { } exe)
        {
            var arguments = _services.Paths.ProfileName is { Length: > 0 } profile
                ? new[] { $"--profile={profile}" }
                : [];
            var info = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(info);
        }

        _exitRequested = true;
        Application.Current.Shutdown();
    }

    // ---- first-run notices ----

    /// <summary>
    /// The two notices the reference shows a new installation: that Auto is the
    /// default permission mode now, and — when the shell a local session needs is
    /// missing — that Git for Windows has to be installed.
    /// </summary>
    private void ShowFirstRunNotices()
    {
        if (AutomationMode)
        {
            // A screenshot, a pose or a self-test drives the window with nobody to
            // answer a modal dialog, and one raised over the surface being measured
            // would be what the run captured.
            return;
        }

        var ui = _services.UiSettings.Current;
        if (!ui.AutoModeNoticeSeen)
        {
            ui.AutoModeNoticeSeen = true;
            _services.UiSettings.Save();
            MessageDialog.Show(
                this,
                "Auto mode is now Jarvis Code’s default permission mode",
                null,
                ["Got it"],
                type: MessageDialogType.Info);
        }

        if (!GitBash.IsAvailable())
        {
            var response = MessageDialog.Show(
                this,
                "Install Git",
                "Git for Windows is required to run local sessions. If it’s already installed, " +
                $"set the {GitBash.PathVariable} environment variable to the full path of " +
                "bash.exe and restart the app — or switch to a remote environment.",
                ["Download Git", "Not now"],
                defaultId: 0,
                cancelId: 1,
                MessageDialogType.Warning);
            if (response == 0)
            {
                ExternalLinks.Open(this, GitBash.DownloadUrl);
            }
        }
    }
}
