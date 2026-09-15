using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Panels;
using JarvisCode.Core.BackgroundTasks;
using JarvisCode.Core.Utilities;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.Views;

/// <summary>
/// Code surface layout: the conversation on the left, one side panel on the right. The
/// session header's rail picks terminal · changes · browser; the session menu opens
/// artifacts and files; the panel menu reaches the runs and tasks panels.
/// </summary>
public partial class CodeWorkspace : UserControl
{
    private AppServices? _services;
    private readonly DispatcherTimer _runsTimer;

    public CodeWorkspace()
    {
        InitializeComponent();
        _runsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runsTimer.Tick += (_, _) => RefreshRuns();
        _tasksChipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _tasksChipTimer.Tick += (_, _) => RefreshTasksChip();
        WireTranscriptActions();
    }

    /// <summary>
    /// The two host actions the transcript's markdown offers: Run on a
    /// shell-tagged fence, and a click on a relative file link. Both are
    /// window-level, and both are what make the harness prompt's sentences
    /// about them true — <see cref="Services.HostPromptSections"/> only sends
    /// those sentences because these exist.
    /// </summary>
    private void WireTranscriptActions()
    {
        Controls.MarkdownView.ShellCommandRequested = command =>
        {
            ShowPanel("terminal");
            TerminalPanel.Send(command);
        };

        // "Open in terminal" shows the command without running it, which is the
        // reference's second fence action beside Run.
        Controls.MarkdownView.TerminalOpenRequested = command =>
        {
            ShowPanel("terminal");
            TerminalPanel.Show(command);
        };

        // A filename in the transcript opens in the diff pane when the file is
        // one this session changed, and is revealed in Explorer otherwise.
        Controls.MarkdownView.FileActivationRequested = (path, line) => ShowFileFromTranscript(path, line);

        // An image address in an answer is text the model wrote, and fetching it
        // tells that host the user read the message. The reference's answer to
        // that is the "Show Image" button alone: pressing it is the consent, and
        // there is no second dialog behind it.
        Controls.MarkdownView.ImageLoadRequested = static _ => true;
    }

    /// <summary>
    /// Where a filename clicked in the transcript goes: the diff pane if the
    /// file is among this session's changes, and Explorer otherwise. A line
    /// number rides along, which is what a "#L5-L20" link carries.
    /// </summary>
    private void ShowFileFromTranscript(string path, int? line)
    {
        ShowPanel("changes");
        if (ChangesPanel.TryFocusFile(path))
        {
            return;
        }

        ShowPanel(null);
        RevealFile(path);
        _ = line;
    }

    /// <summary>
    /// Shows a file the model linked to. The app has no editor of its own, so
    /// the file is revealed in Explorer rather than opened: a link's target is
    /// text the model wrote, and shell-executing it would run whatever the
    /// extension is associated with. Selecting it in a folder view cannot.
    /// </summary>
    private static void RevealFile(string path)
    {
        if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                // /select, takes the path as one argument; quoting keeps spaces.
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = false,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No shell to reveal it in; nothing sensible to do.
        }
    }

    private readonly DispatcherTimer _tasksChipTimer;

    private void RefreshTasksChip()
    {
        if (_services is null)
        {
            return;
        }

        // The reference chip is per-session: each pane counts its own session's
        // background tasks and workers, never another session's.
        var running = _services.BackgroundTasks.List()
            .Where(static t => t.Status == JarvisCode.Core.BackgroundTasks.BackgroundTaskStatus.Running)
            .ToList();
        foreach (var pane in AllCodeSurfaces)
        {
            var sessionId = pane.ViewModel.Session.Id;
            pane.SetRunningTaskCount(
                running.Count(t => string.Equals(t.SessionId, sessionId, StringComparison.Ordinal)) +
                pane.ViewModel.Workers.RunningCount +
                pane.ViewModel.Loops.Count);
        }
    }

    public ChatSurface ChatSurface => Chat;

    /// <summary>The second pane of the split view (always initialized, shown on demand).</summary>
    public ChatSurface SplitChatSurface => SplitChat;

    /// <summary>
    /// The second pane keeps its old fixed identity; further panes are created
    /// on demand. Every created pane stays in the pool even when its tile
    /// closes, so a background turn keeps running and can be refocused.
    /// </summary>
    private readonly ChatSurface SplitChat = new();

    private const int MaxPanes = 4;
    private readonly List<ChatSurface> _panes = [];
    private readonly List<ChatSurface> _extraPool = [];

    /// <summary>A dynamically created pane needs the window's wiring too.</summary>
    public event Action<ChatSurface>? PaneAdded;

    private ChatSurface? _activePane;

    /// <summary>The pane the keyboard was in last — where sidebar clicks and shortcuts land.</summary>
    public ChatSurface ActiveChatSurface =>
        _activePane is { } pane && _panes.Contains(pane) ? pane : Chat;

    public bool IsSplitOpen => _panes.Count > 1;

    /// <summary>The visible tiles, primary first.</summary>
    public IReadOnlyList<ChatSurface> Panes => _panes;

    /// <summary>Every code surface ever created, visible or parked — for unions and cleanup.</summary>
    public IEnumerable<ChatSurface> AllCodeSurfaces => [Chat, .. _extraPool];

    public void Initialize(AppServices services)
    {
        _services = services;
        Chat.Initialize(services, isCodeSurface: true);
        Chat.SessionPersisted += (_, _) => RefreshActivePanel();
        Chat.ViewModel.Todos.CollectionChanged += (_, _) => RefreshTasks();
        Chat.ViewModel.Tasks.Changed += () => Dispatcher.InvokeAsync(RefreshTasks);
        Chat.HeaderRail.ShowPanelToggles = true;
        Chat.PanelRequested += (_, key) => OnPanelRequested(key);
        Chat.ChapterPinToggleRequested += ToggleChapterPin;
        Chat.IsChapterPinned = IsChapterPinnedAt;
        Chat.PanelToggleRequested += (_, key) => TogglePane(key);
        Chat.SubagentViewRequested += OnSubagentViewRequested;
        WireTitleBar(Chat);
        SplitChat.Initialize(services, isCodeSurface: true);
        SplitChat.HeaderRail.ShowPanelToggles = true;
        SplitChat.PanelRequested += (_, key) => OnPanelRequested(key);
        SplitChat.PanelToggleRequested += (_, key) => TogglePane(key);
        SplitChat.SubagentViewRequested += OnSubagentViewRequested;
        WireTitleBar(SplitChat);
        RunsPanel.StopRequested += OnRunStopRequested;
        RunsPanel.ViewTranscriptRequested += OnRunViewTranscript;
        RunsPanel.FinishedExpandedChanged += (_, _) => SaveFinishedExpanded();
        // A cleared row's workflow progress has nowhere left to show, so it goes.
        RunsPanel.TaskCleared += (_, id) => _services?.WorkflowRuns.Forget(id);
        Chat.GotKeyboardFocus += (_, _) => _activePane = Chat;
        SplitChat.GotKeyboardFocus += (_, _) => _activePane = SplitChat;
        RebuildExtraTools();
        // Desktop control is a switch in Settings, so the tool list has to follow it live —
        // and every session switch binds a fresh view model that needs the tools re-joined.
        services.UiSettings.Saved += (_, _) => RebuildExtraTools();
        Chat.ViewModelBound += (_, _) => RebuildExtraTools();
        SplitChat.ViewModelBound += (_, _) => RebuildExtraTools();
        // A session switch should not wait out the 2s timer to drop the old
        // session's task count from the chip.
        Chat.ViewModelBound += (_, _) => RefreshTasksChip();
        SplitChat.ViewModelBound += (_, _) => RefreshTasksChip();
        // A pushed subagent view belongs to the session that opened it, and any
        // pane can be the one that opened it, so every rebind drops it.
        Chat.ViewModelBound += (_, _) => RunsPanel.PopSubagentView();
        SplitChat.ViewModelBound += (_, _) => RunsPanel.PopSubagentView();

        InitializeTiles();

        // Panel content flows into the composer: a picked element becomes a
        // context chip, a diff-tree file an @-mention.
        BrowserPanel.ElementPicked += (_, context) => ActiveChatSurface.AttachContextText(context);

        // The Browser pane's dev-server flow: Detect/Use this/Try again start a
        // configuration, the menu shows the latest server's logs, and closing
        // the last tab closes the pane itself.
        BrowserPanel.StartPreviewRequested += (_, configuration) => StartPreviewFromPanel(configuration);
        BrowserPanel.ShowDevServerLogsRequested += (_, _) => ShowDevServerLogs();
        BrowserPanel.LastTabClosed += (_, _) =>
        {
            ClosePane("browser");
        };

        // A dev server that dies becomes the pane's error state, and the log
        // tail reaches Jarvis ("Error details were sent to Jarvis").
        services.BackgroundTasks.TaskExited += info => Dispatcher.InvokeAsync(() =>
        {
            // An MCP call the turn stopped waiting for reports back the way any
            // background work does: as a task notification carrying its result.
            if (info.Kind == JarvisCode.Core.Mcp.McpAutoBackground.TaskKind)
            {
                // The reference reports one of two statuses and files an aborted
                // call under the failing one, with the reason inside.
                var failed = info.Status != BackgroundTaskStatus.Completed || info.ExitCode != 0;
                var status = failed ? "failed" : "completed";
                ActiveChatSurface.ViewModel.DeliverTaskNotification(
                    info.Id, status, $"MCP tool {info.Command} {status}", info.Output, isError: failed);
                return;
            }

            if (info.Status == BackgroundTaskStatus.Killed || info.ExitCode is 0 or null ||
                _previewServers?.FindByTask(info.Id) is not { } server)
            {
                return;
            }

            var tail = string.Join('\n',
                JarvisCode.Core.Utilities.AnsiText.Strip(info.Output).Split('\n').TakeLast(40));
            BrowserPanel.ShowServerError(server.Name, tail);
            ShowPanel("browser");
            ActiveChatSurface.ViewModel.DeliverTaskNotification(
                server.ServerId, "failed", $"Dev server failed to start: {server.Name}", tail, isError: true);
        });
        ChangesPanel.AttachFileRequested += (_, path) => ActiveChatSurface.AppendFileMention(path);
        Chat.SideChatSendRequested += (_, text) => SendToSideChat(text);
        SplitChat.SideChatSendRequested += (_, text) => SendToSideChat(text);
        Chat.SideChatAskRequested += (_, text) => AskSideChat(text);
        SplitChat.SideChatAskRequested += (_, text) => AskSideChat(text);
        Chat.PreviewActionRequested += (_, action) => HandlePreviewAction(action);
        SplitChat.PreviewActionRequested += (_, action) => HandlePreviewAction(action);

        _panes.Add(Chat);
        _extraPool.Add(SplitChat);
        Retile();

        // Startup opens no side panel, the way the reference app starts — the rail
        // opens one on demand. The call still has to run: the XAML lays the panel
        // column out at 1.1* with the card visible, and this collapses it while
        // keeping that width as the one a later open restores.
        ShowPanel(null);

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && IsPaneOpen("runs"))
            {
                _runsTimer.Start();
            }
            else
            {
                _runsTimer.Stop();
            }

            if (IsVisible)
            {
                RefreshTasksChip();
                _tasksChipTimer.Start();
            }
            else
            {
                _tasksChipTimer.Stop();
            }
        };
    }

    /// <summary>The host-side tools joined into every Code turn, on both panes.</summary>
    private void RebuildExtraTools()
    {
        if (_services is null)
        {
            return;
        }

        _previewServers ??= new Services.PreviewServers(
            _services.BackgroundTasks,
            url => Dispatcher.InvokeAsync(() =>
            {
                ShowPanel("browser");
                BrowserPanel.OpenPreviewUrl(url);
            }));

        _paneDriver ??= new Services.BrowserPaneDriver(
            Dispatcher, () => BrowserPanel, () => ShowPanel("browser"));

        // The reference keeps its origin policy per window rather than per
        // session, so one gate serves every session this workspace opens, and
        // clearing the pane's browsing data clears what it consented to.
        if (_paneTransitions is null)
        {
            _paneTransitions = new Services.BrowserPaneDomainTransitions(
                () => _services?.UiSettings.Current.BrowserAllowedOrigins ?? []);
            BrowserPanel.BrowsingDataCleared += (_, _) => _paneTransitions.Reset();
        }

        // The pane asks before a tool opens a site it has not been allowed for,
        // and an allowed site is written to the same list Settings edits - which
        // is what makes consent, rather than typing, the ordinary way in.
        _paneOriginConsent ??= new Services.PreviewOriginConsent(
            Card: (message, detail, _) => Dispatcher.InvokeAsync(() => Views.MessageDialog.Show(
                Window.GetWindow(this),
                message,
                detail,
                Services.PreviewOriginPrompt.Buttons,
                defaultId: Services.PreviewOriginPrompt.DefaultButton,
                cancelId: Services.PreviewOriginPrompt.DefaultButton,
                Views.MessageDialogType.Question)).Task,
            IsAllowed: origin => _services?.UiSettings.Current.BrowserAllowedOrigins
                .Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)) == true,
            Allow: origin =>
            {
                if (_services is not { } services)
                {
                    return;
                }

                var allowed = services.UiSettings.Current.BrowserAllowedOrigins;
                if (!allowed.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)))
                {
                    allowed.Add(origin);
                    services.UiSettings.Save();
                }
            });

        // The Browser pane has no server registry of its own; this is what lets it ask
        // whether closing a tab would leave a dev server without a window.
        BrowserPanel.ServersForUrl = url => _previewServers is null
            ? []
            : [.. _previewServers.Snapshot(null).Where(server => MatchesServer(server, url))];
        BrowserPanel.StopPreviewServer = id => _previewServers?.Stop(id);

        // The pane's driver can run before the panel was ever shown, so the
        // profile/cwd configuration cannot wait for RefreshActivePanel.
        BrowserPanel.Configure(
            Chat.ViewModel.Session.WorkingDirectory,
            Services.BrowserStorageProfile.EngineDirectory(_services.Paths.Root),
            _services.UiSettings,
            Chat.ViewModel.Session.Id);

        _prActivity ??= new Services.PrActivityManager();

        foreach (var surface in AllCodeSurfaces)
        {
            JoinTools(surface);
        }
    }

    /// <summary>
    /// Shows a file the pane can render (an HTML page, an image, a PDF) in the
    /// Browser panel. Answers false when there is nothing there to show, so the
    /// caller does not claim a preview that never opened.
    /// </summary>
    private bool OpenFileInBrowserPane(string workingDirectory, string path)
    {
        string full;
        try
        {
            full = System.IO.Path.IsPathRooted(path)
                ? path
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(workingDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.PathTooLongException or NotSupportedException)
        {
            return false;
        }

        if (!System.IO.File.Exists(full))
        {
            return false;
        }

        Dispatcher.InvokeAsync(() =>
        {
            ShowPanel("browser");
            BrowserPanel.OpenUrl(new Uri(full).AbsoluteUri);
        });
        return true;
    }

    private void JoinTools(ChatSurface surface)
    {
        // The delivery target is pinned per rebuild, so a PR subscribed from one
        // session keeps notifying that session after the surface moves on.
        var viewModel = surface.ViewModel;

        // The reference registers its preview-verification hooks on the session,
        // not on the turn: the nudge fires once per turn and the Stop hook arms
        // it again, so the instance has to outlive a single turn.
        viewModel.FunctionHooks ??= new Services.PreviewVerification(
            () => Services.PreviewServers.ReadAutoVerify(viewModel.Session.WorkingDirectory),
            () => _previewServers?.Snapshot(viewModel.Session.Id).Count > 0,
            () => _previewServers is { } servers &&
                  servers.Snapshot(null).Count > servers.Snapshot(viewModel.Session.Id).Count,
            path => OpenFileInBrowserPane(viewModel.Session.WorkingDirectory, path)).Hooks;
        // Built once: the composed tools and the instruction blocks are two
        // readings of the same server definitions.
        var internalServers = InternalServers(viewModel);
        viewModel.ExtraTools =
        [
            new Services.ArtifactTool(artifact => Dispatcher.InvokeAsync(() => PublishArtifact(artifact))),
            .. Services.AdvisorPrompt.IsEnabled(_services!.UiSettings.Current.AdvisorModelId)
                ? [new Services.AdvisorTool(_services, () => _services.UiSettings.Current.AdvisorModelId)]
                : Array.Empty<JarvisCode.Core.Tools.ITool>(),
            new Services.ConfigTool(
                () => _services!.Settings.Current,
                () => _services!.Settings.Save(),
                (key, value) => viewModel.FireHook(
                    JarvisCode.Core.Hooks.HookEvent.ConfigChange,
                    new System.Text.Json.Nodes.JsonObject { ["key"] = key, ["value"] = value }),
                () => _services!.UiSettings.Current,
                () => _services!.UiSettings.Save()),
            .. Services.WorktreeTools.Create(
                () => viewModel.Session.WorkingDirectory,
                path => Dispatcher.Invoke(() => viewModel.SetWorkingDirectory(path)),
                viewModel.FireHook,
                _services!.UiSettings),
            new Services.ScheduleWakeupTool(viewModel),
            .. Services.CronTools.Create(new JarvisCode.Core.Routines.RoutineStore(_services.Paths.RoutinesFile)),
            new Services.ListAgentsTool(_services, viewModel),

            // search_mcp_registry is the mcp-registry server's, and used to be
            // registered bare here as well — two tools with one name, of which
            // the model saw whichever the registry kept. The bare name survives
            // as that server's alias so a stored session still replays.
            .. Services.ReportingTools.Create(viewModel, action => Dispatcher.InvokeAsync(action)),
            .. Services.DiscoveryTools.Create(
                () => viewModel.Session.WorkingDirectory,
                _services.Paths.UserSkillsDirectory,
                _services.Paths.PluginsDirectory,
                _services.Paths.Root),
            .. Services.SuggestionTools.Create(
                text => Dispatcher.InvokeAsync(
                    () => viewModel.Transcript.Add(new ViewModels.NoticeItem { Text = text })),
                () => viewModel.Session.WorkingDirectory,
                _services.Paths.UserSkillsDirectory,
                _services.Paths.PluginsDirectory,
                _services.Paths.Root,
                () => _services!.UiSettings.Current),
            // android_logcat has no counterpart action on the reference's control
            // tool, so it stays this app's own bare tool; the other four went into
            // control as aliases (Services/AndroidEmulatorTools.cs).
            .. Services.AndroidTools.CreateIfAvailable(_services!.UiSettings.Current),
            // This port's own single-action screenshot: the reference takes one
            // through computer_batch, so it stays bare rather than wearing an
            // mcp__computer-use__ name the reference has no tool for.
            .. Services.ComputerUseTools.BareTools(_services.UiSettings),
            // Everything the desktop shell exposes as an in-process MCP server
            // goes through one composer, which applies the mcp__server__tool
            // names, the per-tool switches and the per-session enablement.
            .. Services.InternalMcpServers.Compose(
                internalServers,
                SessionContext(viewModel) with
                {
                    // The stall timer re-arms rather than firing while the user
                    // is looking at this session's permission card.
                    HasPendingPermission = () => viewModel.Gate.HasPendingPermission,
                },
                _services.UiSettings.Current.InternalMcpTools,
                _services.Mcp.ConnectedToolCounts.Keys),
            .. Services.PrActivityTools.Create(_prActivity!, (id, status, summary, body) =>
                Dispatcher.InvokeAsync(() =>
                {
                    viewModel.DeliverTaskNotification(id, status, summary, body);

                    // "Auto-archive after PR merge or close", when switched on.
                    if (status is "merged" or "closed" &&
                        _services?.UiSettings.Current.AutoArchiveOnPrClose == true)
                    {
                        _services.SessionGroups.SetArchived(viewModel.Session.Id, true);
                    }
                })),
        ];

        // The widget row reads its runtime page out of the same server list the
        // tools were composed from — the reference's own handleReadResource.
        viewModel.ReadInternalMcpResource = uri =>
            Services.InternalMcpServers.ReadResource(internalServers, SessionContext(viewModel), uri);

        // The MCP servers' own instructions, rendered from the same definitions
        // and gated on the same deferral answer the turn factory will reach —
        // one rule, so the chrome block cannot claim tools are deferred on a
        // turn that loads them all.
        viewModel.McpServerInstructionBlocks = Services.McpServerInstructions.Collect(
                internalServers,
                SessionContext(viewModel) with
                {
                    // Measured over this window's tools rather than the turn's
                    // whole set, which also carries Core's built-ins and any
                    // configured MCP server. Deferral is a per-model answer now,
                    // not a size one, so this asks it about the session's model;
                    // a false can only happen when the shell contributed almost
                    // nothing, which is when the chrome block has nothing to say.
                    ToolSearchAvailable = Services.TurnContextFactory.WillDefer(
                        viewModel.ExtraTools, viewModel.CurrentModel?.ModelId),
                },
                // A server the composer dropped for having no enabled tools
                // contributes no instructions either, which is the reference's
                // own rule and what this port used to get wrong for computer-use.
                viewModel.ExtraTools.Select(static tool => tool.Name));
        viewModel.McpServerInstructions =
            Services.McpServerInstructions.Render(viewModel.McpServerInstructionBlocks);
    }

    /// <summary>
    /// What the shell answers each server's isEnabled against. Every one of
    /// these is read here rather than snapshotted inside a tool, because the
    /// reference resolves them per turn: a switch flipped mid-session takes its
    /// server away on the next model call.
    /// </summary>
    private Services.InternalMcpSessionContext SessionContext(ViewModels.ChatViewModel viewModel) => new()
    {
        HasBrowserPane = true,
        BrowserPaneLaunchEnabled = _services!.UiSettings.Current.BrowserPaneLaunchEnabled,
        ChromeExtensionEnabled = _services.UiSettings.Current.ChromeExtensionEnabled,
        ComputerUseEnabled = _services.UiSettings.Current.ComputerUseEnabled,

        // The reference's `JD().type !== "3p"`: the connector directory it
        // searches belongs to the account, so a session on somebody else's
        // provider gets no registry.
        IsThirdPartyProvider = viewModel.CurrentModel is { } model &&
            !string.Equals(model.ProviderId, "anthropic", StringComparison.Ordinal),
    };

    /// <summary>
    /// The MCP servers the user has configured, as list_connectors and the
    /// registry search read them: a connector's own name, whether it answered,
    /// and how many tools it brought.
    /// </summary>
    private IReadOnlyList<Services.InstalledConnector> Connectors() =>
    [
        .. _services!.Mcp.ConnectedToolCounts.Select(static entry =>
            new Services.InstalledConnector(entry.Key, null, entry.Value > 0, entry.Value)),
    ];

    /// <summary>Chapters marked in each session, in the order mark_chapter recorded them.</summary>
    private readonly Dictionary<string, List<Services.SessionChapter>> _chapters = new(StringComparer.Ordinal);

    /// <summary>
    /// What this session's rendered widgets currently hold, for
    /// read_widget_context. A widget reports itself through the MCP Apps
    /// <c>ui/update-model-context</c> request, which the row stores; one that has
    /// reported nothing is not listed, so the tool's "no widget context" answer
    /// names only widgets that could have answered.
    /// </summary>
    private IReadOnlyList<Services.WidgetToolState> WidgetStates(ViewModels.ChatViewModel viewModel) =>
        Dispatcher.Invoke(() => (IReadOnlyList<Services.WidgetToolState>)
        [
            .. viewModel.Transcript
                .OfType<ViewModels.WidgetItem>()
                .Where(static widget => widget.ModelContext.Count > 0)
                .Select(static widget => new Services.WidgetToolState(widget.WireName, widget.ModelContext)),
        ]);

    /// <summary>
    /// The in-process MCP servers this window offers a session. Order is the
    /// order the tools reach the model in.
    /// </summary>
    private IReadOnlyList<Services.InternalMcpServerDefinition> InternalServers(ViewModels.ChatViewModel viewModel)
    {
        var sessionId = viewModel.Session.Id;
        Panels.BrowserPanel SessionBrowser()
        {
            BrowserPanel.Configure(viewModel.Session.WorkingDirectory,
                Services.BrowserStorageProfile.EngineDirectory(_services!.Paths.Root), _services.UiSettings, sessionId);
            return BrowserPanel;
        }
        var browserDriver = new Services.BrowserPaneDriver(Dispatcher, SessionBrowser, () =>
        {
            ShowPanel("browser");
            // Showing a pane refreshes it against the visible conversation.
            // A background caller still owns its own tabs and cookie jar.
            SessionBrowser();
        });

        // The Android emulator server exists only where adb does — the reference
        // removes a server its isEnabled refuses, and "no adb on this machine" is
        // the local half of the flag it gates that server on.
        var android = Services.AndroidEmulatorTools.Server(_services!.UiSettings.Current, EmulatorPanel);
        return
        [
            .. android is null ? Array.Empty<Services.InternalMcpServerDefinition>() : [android],
            Services.BrowserPaneTools.Server(
                browserDriver, _previewServers!, _paneTransitions, _paneOriginConsent),
            Services.JarvisBrowserTools.Server(
                _services.Browser,
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_services.UiSettings.FilePath) ?? System.IO.Path.GetTempPath(),
                    "screenshots"),
                _services.BrowserOrigins),
            Services.ComputerUseTools.Server(_services.UiSettings, _services.Teach),
            Services.TerminalMcpTools.Server(new Services.TerminalPanelReader(
                tabId => TerminalPanel.ReadTab(tabId),
                (tabId, timeout, token) => TerminalPanel.WaitForOutputAsync(tabId, timeout, token),
                async work => await Dispatcher.InvokeAsync(work))),
            Services.CcdDirectoryTools.Server(new Services.CcdDirectoryHost(
                PickFolderAsync,
                viewModel.AddAdditionalDirectory,
                path =>
                {
                    Dispatcher.InvokeAsync(() => viewModel.SetWorkingDirectory(path));
                    return true;
                })),
            Services.CcdSessionTools.Server(new Services.CcdSessionHost(
                viewModel.Suggestions,
                (title, summary) => Dispatcher.InvokeAsync(() => MarkChapter(viewModel, title, summary)),
                () => WidgetStates(viewModel))),
            Services.ScheduledTaskTools.Server(
                new Services.ScheduledTaskStore(_services.Paths.ScheduledTasksDirectory),
                static () => DateTimeOffset.Now),
            Services.ConnectorMcpTools.Server(
                new Services.McpRegistrySearchTool(_services.Http, Connectors),
                Connectors,
                _ => Dispatcher.InvokeAsync(() =>
                    (Window.GetWindow(this) as MainWindow)?.EnterCustomize("connectors"))),
            Services.VisualizeTools.Server(),
            Services.CcdSessionMgmtTools.Server(new Services.CcdSessionMgmtHost(
                () => sessionId,
                () => viewModel.Session.Title,
                () => true,
                () => _services.SessionGroups.Data.Groups.Count > 0,
                ListSessionsAsync,
                LoadTranscriptAsync,
                ArchiveSessionAsync,
                RenameSessionAsync,
                async (target, body, _) =>
                {
                    if (viewModel.CrossSessionSend is not { } send)
                    {
                        return new Services.SessionSendResult(false, Reason: "cross-session delivery is unavailable");
                    }

                    var failure = await send(target, body);
                    return failure is null
                        ? new Services.SessionSendResult(true)
                        : new Services.SessionSendResult(false, Reason: failure);
                })),
        ];
    }

    /// <summary>
    /// Records a chapter and draws its divider. The reference shows both a
    /// divider in the transcript and a floating table of contents; the list kept
    /// here is what that table reads.
    /// </summary>
    private void MarkChapter(ViewModels.ChatViewModel viewModel, string title, string? summary)
    {
        var sessionId = viewModel.Session.Id;
        if (!_chapters.TryGetValue(sessionId, out var chapters))
        {
            chapters = [];
            _chapters[sessionId] = chapters;
        }

        var chapterId = $"{sessionId}/{chapters.Count}";
        var ui = _services!.UiSettings.Current;
        if (ui.HiddenChapters.Contains(chapterId, StringComparer.Ordinal))
        {
            // A hidden chapter still counts — hiding it removes the divider, not
            // the chapter — so the table of contents keeps its row.
            chapters.Add(new Services.SessionChapter(title, summary, DateTimeOffset.Now)
            {
                TranscriptIndex = viewModel.Transcript.Count,
            });
            return;
        }

        viewModel.Transcript.Add(new ViewModels.ChapterItem
        {
            Id = chapterId,
            Title = title,
            Summary = summary,
            DisplayTitle = ui.ChapterRenames.TryGetValue(chapterId, out var renamed) ? renamed : title,
        });
        chapters.Add(new Services.SessionChapter(title, summary, DateTimeOffset.Now)
        {
            TranscriptIndex = viewModel.Transcript.Count - 1,
        });
    }

    /// <summary>
    /// Pins the assistant turn starting at <paramref name="index"/> as a chapter,
    /// or unpins the one already there - the reference's own toggle. Its title is
    /// the turn's own text through <see cref="Services.ChapterTitles"/>, and the
    /// divider goes immediately above the turn, which is what makes "is pinned"
    /// answerable by looking at the item before it.
    /// </summary>
    private void ToggleChapterPin(ViewModels.ChatViewModel viewModel, int index)
    {
        var transcript = viewModel.Transcript;
        if (index < 0 || index > transcript.Count)
        {
            return;
        }

        var sessionId = viewModel.Session.Id;
        if (!_chapters.TryGetValue(sessionId, out var chapters))
        {
            chapters = [];
            _chapters[sessionId] = chapters;
        }

        if (IsChapterPinnedAt(viewModel, index))
        {
            transcript.RemoveAt(index - 1);
            chapters.RemoveAll(c => c.TranscriptIndex == index - 1);
            Shift(chapters, index - 1, -1);
            return;
        }

        var seed = Services.ChapterTitles.Seed(TurnTexts(transcript, index));
        var title = Services.ChapterTitles.FromSeed(seed);
        if (title.Length == 0)
        {
            return;
        }

        var chapterId = $"{sessionId}/{chapters.Count}";
        var ui = _services!.UiSettings.Current;
        transcript.Insert(index, new ViewModels.ChapterItem
        {
            Id = chapterId,
            Title = title,
            DisplayTitle = ui.ChapterRenames.TryGetValue(chapterId, out var renamed) ? renamed : title,
        });
        Shift(chapters, index, 1);
        chapters.Add(new Services.SessionChapter(title, null, DateTimeOffset.Now)
        {
            TranscriptIndex = index,
        });
    }

    /// <summary>Whether a chapter divider already sits above this turn.</summary>
    private static bool IsChapterPinnedAt(ViewModels.ChatViewModel viewModel, int index) =>
        index > 0 && index <= viewModel.Transcript.Count
        && viewModel.Transcript[index - 1] is ViewModels.ChapterItem;

    /// <summary>Every text run the turn at this index produced, in order.</summary>
    private static IEnumerable<string> TurnTexts(
        System.Collections.Generic.IList<ViewModels.TranscriptItem> transcript, int index)
    {
        for (var i = index; i < transcript.Count; i++)
        {
            if (transcript[i] is ViewModels.UserMessageItem or ViewModels.ChapterItem)
            {
                yield break;
            }

            if (transcript[i] is ViewModels.AssistantTextItem text && text.Markdown.Length > 0)
            {
                yield return text.Markdown;
            }
        }
    }

    /// <summary>Moves the chapters below an insertion or a removal with it.</summary>
    private static void Shift(List<Services.SessionChapter> chapters, int from, int by)
    {
        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].TranscriptIndex >= from)
            {
                chapters[i] = chapters[i] with { TranscriptIndex = chapters[i].TranscriptIndex + by };
            }
        }
    }

    /// <summary>The chapters marked in a session, for the table of contents.</summary>
    public IReadOnlyList<Services.SessionChapter> ChaptersFor(string sessionId) =>
        _chapters.TryGetValue(sessionId, out var chapters) ? chapters : [];

    /// <summary>Every stored session, with the sidebar's own group, pin and archive state.</summary>
    private async Task<IReadOnlyList<Services.SessionMgmtEntry>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var summaries = await _services!.Sessions.ListAsync(cancellationToken);
        var running = ActiveChatSurface?.SessionViewModels is { } vms
            ? new HashSet<string>(vms.RunningSessionIds, StringComparer.Ordinal)
            : [];
        return
        [
            .. summaries.Select(s =>
            {
                var group = _services.SessionGroups.GroupOf(s.Id);
                return new Services.SessionMgmtEntry
                {
                    SessionId = s.Id,
                    Title = s.Title,
                    Cwd = s.WorkingDirectory,
                    IsArchived = _services.SessionGroups.IsArchived(s.Id),
                    IsRunning = running.Contains(s.Id),
                    LastActivityAt = s.UpdatedAt,
                    GroupId = group?.Id,
                    GroupName = group?.Name,
                    Pinned = _services.SessionGroups.IsPinned(s.Id),
                };
            }),
        ];
    }

    private async Task<IReadOnlyList<JarvisCode.Core.Models.ChatMessage>?> LoadTranscriptAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            return (await _services!.Sessions.LoadAsync(sessionId, cancellationToken))?.Messages;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Task<bool> ArchiveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        Dispatcher.InvokeAsync(() => _services!.SessionGroups.SetArchived(sessionId, true));
        return Task.FromResult(true);
    }

    private async Task<bool> RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken)
    {
        try
        {
            if (await _services!.Sessions.LoadAsync(sessionId, cancellationToken) is not { } session)
            {
                return false;
            }

            session.Title = title;
            await _services.Sessions.SaveAsync(session, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// request_directory / change_directory's picker. A path the model supplied
    /// is validated and shown for approval; without one the native picker opens.
    /// </summary>
    private async Task<Services.FolderPickResult> PickFolderAsync(
        string? providedPath, CancellationToken cancellationToken)
    {
        if (providedPath is not null)
        {
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(providedPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                return Services.FolderPickResult.Failed($"\"{providedPath}\" is not a usable path: {ex.Message}");
            }

            if (!System.IO.Directory.Exists(full))
            {
                return Services.FolderPickResult.Failed(
                    $"\"{providedPath}\" does not exist or is not a directory.");
            }

            var approved = await Dispatcher.InvokeAsync(() => ConfirmFolderGrant(full));
            return approved
                ? Services.FolderPickResult.Granted(providedPath)
                : Services.FolderPickResult.Cancelled();
        }

        return await Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Services.CcdDirectoryTools.DialogTitle,
                Multiselect = false,
            };
            return dialog.ShowDialog() == true
                ? Services.FolderPickResult.Granted(dialog.FolderName)
                : Services.FolderPickResult.Cancelled();
        });
    }

    private bool ConfirmFolderGrant(string path) =>
        ConfirmDialog.Ask(
            Window.GetWindow(this),
            Services.CcdDirectoryTools.DialogTitle,
            Services.CcdDirectoryTools.DialogMessage,
            "Allow",
            subject: path,
            focusCancel: true,
            danger: false);

    // ---- split view: an adaptive grid of session tiles ----

    /// <summary>
    /// Opens another tile, optionally pointing it at a session. A session
    /// already on a tile is focused instead of opened twice; at the pane cap
    /// the last tile is repointed.
    /// </summary>
    public void OpenSplitView(JarvisCode.Core.Sessions.Session? session = null)
    {
        if (session is not null &&
            _panes.FirstOrDefault(p => p.ViewModel.Session.Id == session.Id) is { } showing)
        {
            _activePane = showing;
            showing.FocusInput();
            return;
        }

        ChatSurface pane;
        if (_panes.Count >= MaxPanes)
        {
            pane = _panes[^1];
        }
        else
        {
            pane = _extraPool.FirstOrDefault(p => !_panes.Contains(p)) ?? CreateExtraPane();
            _panes.Add(pane);
        }

        if (session is not null)
        {
            pane.LoadSession(session);
        }

        Retile();
        _activePane = pane;
        pane.FocusInput();
    }

    public void CloseSplitView()
    {
        _panes.RemoveRange(1, _panes.Count - 1);
        Retile();
        _activePane = Chat;
        Chat.FocusInput();
    }

    /// <summary>
    /// Closes one tile. Closing the primary hands its tile to the next pane's
    /// session (the primary surface itself hosts the home view and never goes
    /// away); a parked pane keeps any running turn alive in the pool.
    /// </summary>
    public void ClosePane(ChatSurface pane)
    {
        if (!_panes.Contains(pane) || _panes.Count <= 1)
        {
            return;
        }

        if (ReferenceEquals(pane, Chat))
        {
            var donor = _panes[1];
            Chat.LoadSession(donor.ViewModel.Session);
            _panes.Remove(donor);
        }
        else
        {
            _panes.Remove(pane);
        }

        Retile();
        _activePane = _panes[^1];
        _activePane.FocusInput();
    }

    /// <summary>Ctrl+] / Ctrl+[ — cycle keyboard focus through the tiles.</summary>
    public void CycleFocus(int delta)
    {
        if (_panes.Count < 2)
        {
            return;
        }

        var index = _panes.IndexOf(ActiveChatSurface);
        var next = _panes[(index + delta + _panes.Count) % _panes.Count];
        _activePane = next;
        next.FocusInput();
    }

    /// <summary>
    /// The inline preview card's buttons, resolved against the workspace's preview
    /// servers and Browser panel (the reference card's open/logs/stop trio, plus a
    /// best-effort thumbnail of the previewed page).
    /// </summary>
    private async void HandlePreviewAction(ChatSurface.PreviewCardAction action)
    {
        var item = action.Item;
        var serverId = item.PreviewServerId;
        if (_previewServers is null || serverId is null)
        {
            return;
        }

        switch (action.Action)
        {
            case "open":
                if (item.PreviewUrl is { } url)
                {
                    BrowserPanel.OpenUrl(url);
                    ShowPanel("browser");
                }

                break;

            case "logs":
                var logs = _previewServers.Logs(serverId, 60, level: null, search: null);
                item.Result = logs.Content;
                item.IsExpanded = true;
                break;

            case "stop":
                var stopped = _previewServers.Stop(serverId);
                item.Result = stopped.Content;
                break;

            case "thumbnail":
                if (item.PreviewThumbnailPath is null && item.PreviewUrl is { } previewUrl)
                {
                    var path = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), $"jarvis-preview-{serverId}.png");
                    item.PreviewThumbnailPath = await BrowserPanel.TryCapturePreviewAsync(previewUrl, path);
                }

                break;
        }
    }

    /// <summary>
    /// The two titlebar controls the reference gates on its host rather than on the session:
    /// its "Close pane" × removes this pane, and its `Qp` expander fills the host with the
    /// artifact tile.
    /// </summary>
    private void WireTitleBar(ChatSurface surface)
    {
        surface.ClosePaneRequested += (_, _) => ClosePane(surface);
        surface.ArtifactExpandRequested += (_, _) => ExpandArtifact();
    }

    /// <summary>
    /// Tells every surface which of those two controls it may show. The reference draws the ×
    /// only where a pane can actually be removed, and the expander only while the artifact
    /// pane is in the layout without already filling the host.
    /// </summary>
    private void RefreshTitleBarControls()
    {
        var canExpandArtifact = IsPaneOpen("artifact") && !IsSolo("artifact");
        foreach (var surface in AllCodeSurfaces)
        {
            surface.SetPaneControls(_panes.Count > 1 && _panes.Contains(surface), canExpandArtifact);
        }
    }

    private ChatSurface CreateExtraPane()
    {
        var pane = new ChatSurface();
        pane.Initialize(_services!, isCodeSurface: true);
        pane.HeaderRail.ShowPanelToggles = true;
        pane.PanelRequested += (_, key) => OnPanelRequested(key);
        pane.PanelToggleRequested += (_, key) => TogglePane(key);
        pane.SubagentViewRequested += OnSubagentViewRequested;
        WireTitleBar(pane);
        pane.GotKeyboardFocus += (_, _) => _activePane = pane;
        pane.ViewModelBound += (_, _) => RebuildExtraTools();
        pane.ViewModelBound += (_, _) => RefreshTasksChip();
        pane.ViewModelBound += (_, _) => RunsPanel.PopSubagentView();
        pane.SideChatSendRequested += (_, text) => SendToSideChat(text);
        pane.SideChatAskRequested += (_, text) => AskSideChat(text);
        pane.PreviewActionRequested += (_, action) => HandlePreviewAction(action);
        _extraPool.Add(pane);
        PaneAdded?.Invoke(pane);
        return pane;
    }

    /// <summary>Lays the visible panes out as the reference's adaptive grid.</summary>
    private void Retile()
    {
        foreach (var pane in _panes)
        {
            if (pane.Parent is Border oldHost)
            {
                oldHost.Child = null;
            }
            else if (pane.Parent is Panel oldPanel)
            {
                oldPanel.Children.Remove(pane);
            }
        }

        PanesGrid.Children.Clear();
        PanesGrid.RowDefinitions.Clear();
        PanesGrid.ColumnDefinitions.Clear();

        var count = _panes.Count;
        var columns = count <= 1 ? 1 : 2;
        var rows = (count + columns - 1) / columns;
        for (int c = 0; c < columns; c++)
        {
            PanesGrid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 160 });
        }

        for (int r = 0; r < rows; r++)
        {
            PanesGrid.RowDefinitions.Add(new RowDefinition());
        }

        for (int i = 0; i < count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            var host = BuildPaneHost(_panes[i], row, column);
            Grid.SetRow(host, row);
            Grid.SetColumn(host, column);
            if (i == count - 1 && count > 1 && count % columns == 1)
            {
                Grid.SetColumnSpan(host, columns);
            }

            PanesGrid.Children.Add(host);
        }

        RefreshTitleBarControls();
    }

    /// <summary>
    /// A tile is the surface plus the rule that separates it from its neighbours. Its
    /// "Close pane" is not drawn here: the reference puts that control in the session
    /// titlebar (its `onSessionRemoved` slot), which is where RefreshTitleBarControls
    /// turns it on, so a pill floating over the transcript would be a second one.
    /// </summary>
    private FrameworkElement BuildPaneHost(ChatSurface pane, int row, int column)
    {
        var inner = new Grid();
        inner.Children.Add(pane);

        var host = new Border
        {
            BorderThickness = new Thickness(column > 0 ? 1 : 0, row > 0 ? 1 : 0, 0, 0),
            Child = inner,
        };
        host.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return host;
    }

    private Services.PreviewServers? _previewServers;
    private Services.BrowserPaneDriver? _paneDriver;
    private Services.BrowserPaneDomainTransitions? _paneTransitions;
    private Services.PreviewOriginConsent? _paneOriginConsent;
    private Services.PrActivityManager? _prActivity;

    /// <summary>The pane driver, for the --pane-selftest verification flag.</summary>
    internal Services.BrowserPaneDriver? PaneDriver => _paneDriver;

    /// <summary>The pane's Detect/Use this/Try again: start the configuration and preview it.</summary>
    private void StartPreviewFromPanel(Services.LaunchConfiguration configuration)
    {
        if (_previewServers is null)
        {
            return;
        }

        var cwd = ActiveChatSurface.ViewModel.Session.WorkingDirectory;
        var result = configuration.AttachOnly && configuration.Port == 0 && configuration.Url is not null
            ? _previewServers.Start(configuration.Name, configuration.Url, cwd)
            : _previewServers.Start(configuration.Name, null, cwd);
        if (result.IsError)
        {
            BrowserPanel.ShowServerError(configuration.Name, result.Content);
        }
    }

    /// <summary>The pane menu's "Show dev server logs": the latest server's output in a window.</summary>
    private void ShowDevServerLogs()
    {
        if (_previewServers?.LatestServerId is not { } serverId)
        {
            Services.ToastQueue.Current?.AddWarning("No dev server has been started in this session.");
            return;
        }

        var logs = _previewServers.Logs(serverId, 400, level: null, search: null);
        var box = new TextBox
        {
            Text = logs.Content,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
        };
        box.SetResourceReference(Control.BackgroundProperty, "Bg100Brush");
        box.SetResourceReference(Control.ForegroundProperty, "Text200Brush");
        var window = new Window
        {
            Title = "Dev server logs",
            Width = 720,
            Height = 480,
            Content = box,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        window.Show();
    }

    /// <summary>
    /// The titles of the sessions with a turn in flight, which is what the
    /// reference's "still working" dialog counts before it relaunches.
    /// </summary>
    public IReadOnlyList<string> RunningSessionTitles() =>
    [
        .. new[] { ChatSurface, SplitChatSurface }
            .Concat(_panes)
            .Where(static pane => pane?.ViewModel is { IsRunning: true })
            .Select(static pane => pane!.ViewModel.Session.Title)
            .Distinct(StringComparer.Ordinal),
    ];


    private bool _sideChatReady;

    /// <summary>Show Side Chat ⇄ Hide Side Chat, from the palette and the panel menu.</summary>
    public void ToggleSideChat() => TogglePane("sidechat");

    private Window? _sideChatWindow;

    private void OnSideChatClearClick(object sender, RoutedEventArgs e)
    {
        if (_sideChatReady)
        {
            SideChat.StartNew();
        }
    }

    private void OnSideChatWindowClick(object sender, RoutedEventArgs e) => MoveSideChatToWindow();

    /// <summary>
    /// "Open in new window": the surface leaves the panel for its
    /// own window; closing that window returns it to the panel.
    /// </summary>
    public void MoveSideChatToWindow()
    {
        if (_sideChatWindow is not null)
        {
            _sideChatWindow.Activate();
            return;
        }

        if (!_sideChatReady && _services is not null)
        {
            SideChat.Initialize(_services, isCodeSurface: false);
            _sideChatReady = true;
        }

        SideChatPanel.Children.Remove(SideChat);
        var previousLayout = _layout;
        _layout = TileLayoutOps.Remove(_layout, "sidechat");
        var detachedLayout = TileLayoutOps.ToJson(_layout.Root).ToJsonString();
        if (_soloPane == "sidechat") _soloPane = null;
        var window = new Window
        {
            Title = "Side Chat",
            Width = 430,
            Height = 640,
            Owner = Window.GetWindow(this),
        };
        window.SetResourceReference(BackgroundProperty, "Bg100Brush");
        var root = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
        var back = new Button { Content = "Return to main window", Padding = new Thickness(8, 4, 8, 4) };
        back.SetResourceReference(StyleProperty, "SecondaryButton"); back.Click += (_, _) => window.Close();
        var pin = new System.Windows.Controls.Primitives.ToggleButton { Content = "Always on top", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0) };
        pin.SetResourceReference(StyleProperty, "PanelTab"); pin.Checked += (_, _) => window.Topmost = true; pin.Unchecked += (_, _) => window.Topmost = false;
        actions.Children.Add(back); actions.Children.Add(pin); DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        var host = new ContentControl { Content = SideChat }; root.Children.Add(host); window.Content = root;
        window.Closed += (_, _) =>
        {
            _sideChatWindow = null;
            _popoutWindows.Remove("sidechat");
            host.Content = null;
            Grid.SetRow(SideChat, 1);
            SideChatPanel.Children.Add(SideChat);
            if (previousLayout.Contains("sidechat") && TileLayoutOps.ToJson(_layout.Root).ToJsonString() == detachedLayout) _layout = previousLayout;
            else if (!_layout.Contains("sidechat")) _layout = TileLayoutOps.AppendRight(_layout, "sidechat");
            ApplyLayout();
        };
        _sideChatWindow = window;
        _popoutWindows["sidechat"] = window;
        ApplyLayout();

        window.Show();
        SideChat.FocusInput();
    }

    /// <summary>Selection → "Send to side chat": opens it (panel or window) with the text staged.</summary>
    public void SendToSideChat(string text)
    {
        if (_sideChatWindow is not null)
        {
            _sideChatWindow.Activate();
        }
        else
        {
            ShowPanel("sidechat");
        }

        SideChat.PrefillInput(text);
    }

    /// <summary>/btw — open Side Chat and ask right away.</summary>
    public void AskSideChat(string question)
    {
        if (_sideChatWindow is not null)
        {
            _sideChatWindow.Activate();
        }
        else
        {
            ShowPanel("sidechat");
        }

        _ = SideChat.SubmitTextAsync(question);
    }

    public void DisposeTerminal() => TerminalPanel.DisposeShells();

    /// <summary>
    /// Releases the engine surfaces this workspace owns on the way out. Views opened
    /// elsewhere (a popped-out panel, a question preview) go with the engine
    /// itself, which the app ends on exit.
    /// </summary>
    public void DisposeWebViews()
    {
        BrowserPanel.DisposeTabs();
        var session = _artifactSession;
        _artifactSession = null;
        _artifactView = null;
        _artifactTab = null;
        if (session is not null)
        {
            _ = session.DisposeAsync().AsTask();
        }
    }

    /// <summary>The visible terminal's selected output, for Ctrl+Shift+L.</summary>
    public string? TerminalSelection => TerminalPanel.ActiveSelection;

    // ---- artifacts ----

    private Services.ElectronPaneSession? _artifactSession;
    private Controls.ElectronPaneView? _artifactView;
    private string? _artifactTab;
    private Services.ArtifactDocument? _artifact;

    /// <summary>Called by the agent's artifact tool (marshalled to the UI thread).</summary>
    public void PublishArtifact(Services.ArtifactDocument artifact)
    {
        _artifact = artifact;
        ShowPanel("artifacts");
    }

    private async Task ShowArtifactAsync()
    {
        if (_artifact is null)
        {
            ArtifactEmpty.Visibility = Visibility.Visible;
            ArtifactTitle.Text = "Artifacts";
            return;
        }

        ArtifactEmpty.Visibility = Visibility.Collapsed;
        ArtifactTitle.Text = _artifact.Title;

        if (_artifactSession is null)
        {
            var view = new Controls.ElectronPaneView();

            // The element has to be in a loaded tree before the engine's window
            // is reparented into it, or there is no container to reparent into.
            ArtifactHost.Content = view;
            try
            {
                Services.ElectronEngine.UseProfile(
                    Services.BrowserStorageProfile.EngineDirectory(_services!.Paths.Root));
                var session = Services.ElectronEngine.CreateSession(Dispatcher);
                var window = await session.EnsureHostWindowAsync(persistSessions: false);
                view.Attach(window);
                await session.ShowAsync();
                _artifactTab = await session.CreateTabAsync(foreground: true);
                _artifactSession = session;
                _artifactView = view;
            }
            catch (Exception ex) when (ex is Services.ElectronRuntimeUnavailableException
                                           or System.IO.IOException or InvalidOperationException)
            {
                ArtifactHost.Content = null;
                _artifactView = null;
                ArtifactEmpty.Visibility = Visibility.Visible;
                ArtifactEmpty.Text = ex is Services.ElectronRuntimeUnavailableException
                    ? ex.Message
                    : "Artifacts need the app's browser engine, and it could not be started.";
                return;
            }
        }

        // The engine navigates where the control had NavigateToString, so the
        // document travels as a data: URL.
        await _artifactSession.NavigateAsync(
            _artifactTab!,
            "data:text/html;charset=utf-8;base64," +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(_artifact.Html)));
    }

    /// <summary>
    /// "Download all": the artifact's own HTML, written where the user picks it.
    /// The reference downloads every artifact of the conversation; this tile holds
    /// the one that is showing, which is what there is to write.
    /// </summary>
    private void OnArtifactDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_artifact is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Download all",
            FileName = Services.ArtifactDocument.SafeFileName(_artifact.Title) + ".html",
            Filter = "HTML page (*.html)|*.html",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            System.IO.File.WriteAllText(dialog.FileName, _artifact.Html);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Chat.ViewModel.Transcript.Add(new ViewModels.NoticeItem
            {
                Text = $"Could not write {dialog.FileName}: {ex.Message}",
                IsError = true,
            });
        }
    }

    /// <summary>"Copy as image": the rendered artifact on the clipboard as a PNG.</summary>
    private async void OnArtifactCopyImageClick(object sender, RoutedEventArgs e)
    {
        if (ArtifactCdp() is not { } core)
        {
            return;
        }

        try
        {
            var captured = await Services.BrowserPaneCdp.CallAsync(
                core, "Page.captureScreenshot",
                new System.Text.Json.Nodes.JsonObject { ["format"] = "png" });
            using var stream = new System.IO.MemoryStream(
                Convert.FromBase64String(captured["data"]?.GetValue<string>() ?? ""));
            stream.Position = 0;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Clipboard.SetImage(image);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException
                                       or FormatException or TimeoutException)
        {
            Chat.ViewModel.Transcript.Add(new ViewModels.NoticeItem
            {
                Text = $"Could not copy the artifact: {ex.Message}",
                IsError = true,
            });
        }
    }

    /// <summary>"Print as PDF": the rendered artifact written as a PDF.</summary>
    private async void OnArtifactPrintClick(object sender, RoutedEventArgs e)
    {
        if (ArtifactCdp() is not { } core || _artifact is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Print as PDF",
            FileName = Services.ArtifactDocument.SafeFileName(_artifact.Title) + ".pdf",
            Filter = "PDF document (*.pdf)|*.pdf",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            var printed = await _artifactSession!.Host.RequestAsync(
                "tab.printToPDF",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["tabId"] = _artifactTab,
                    ["printBackground"] = true,
                },
                CancellationToken.None);
            await System.IO.File.WriteAllBytesAsync(
                dialog.FileName, Convert.FromBase64String(printed["data"]?.GetValue<string>() ?? ""));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException
                                       or FormatException or TimeoutException)
        {
            Chat.ViewModel.Transcript.Add(new ViewModels.NoticeItem
            {
                Text = $"Could not print the artifact: {ex.Message}",
                IsError = true,
            });
        }
    }

    /// <summary>The artifact tile's DevTools endpoint, or null when nothing is showing.</summary>
    private Services.IPaneCdp? ArtifactCdp() =>
        _artifactSession is { } session && _artifactTab is { } tab ? session.Cdp(tab) : null;

    // ---- background tasks ----

    /// <summary>
    /// Feeds the Background tasks panel: preview servers, loops, shell tasks and
    /// background agents for the pane's own session, in the reference's order.
    /// </summary>
    private void RefreshRuns()
    {
        if (_services is null)
        {
            return;
        }

        var sessionId = Chat.ViewModel.Session.Id;
        var servers = _previewServers?.Snapshot(sessionId) ?? [];
        var serverTaskIds = new HashSet<string>(StringComparer.Ordinal);
        var tasks = _services.BackgroundTasks.List()
            .Where(t => t.SessionId is null || string.Equals(t.SessionId, sessionId, StringComparison.Ordinal))
            .ToList();
        if (_previewServers is not null)
        {
            foreach (var task in tasks)
            {
                if (_previewServers.FindByTask(task.Id) is not null)
                {
                    serverTaskIds.Add(task.Id);
                }
            }
        }

        var loops = Chat.ViewModel.Loops
            .Select(l => new Services.LoopRowInfo(
                LoopRowId(l.Id),
                l.Prompt.Length > 60 ? l.Prompt[..60] + "…" : l.Prompt,
                l.Dynamic ? "self-paced (ScheduleWakeup)" : $"every {l.Interval}",
                l.NextRunAt))
            .ToList();

        var rows = Services.BackgroundTaskPresentation.Build(
            servers,
            loops,
            tasks,
            Chat.ViewModel.Workers.List(),
            serverTaskIds,
            _stoppingRunIds,
            DateTimeOffset.Now,
            _services?.WorkflowRuns.Snapshot());

        // A stop has landed once its row is gone or has left Running.
        _stoppingRunIds.RemoveWhere(id => rows.FirstOrDefault(r => r.Id == id) is not { IsRunning: true });
        RunsPanel.Update(rows);
    }

    /// <summary>Loop ids share one id space with tasks and servers in the panel.</summary>
    private static string LoopRowId(int id) => $"loop-{id}";

    /// <summary>
    /// --open=backgroundtasks: poses the pane with one row of every kind and
    /// state. The refresh timer is stopped so the sample is not overwritten.
    /// </summary>
    public void ShowSampleBackgroundTasks(bool empty = false)
    {
        _runsTimer.Stop();
        if (empty)
        {
            RunsPanel.Update([]);
            return;
        }

        var now = DateTimeOffset.Now;
        RunsPanel.Update(Services.BackgroundTaskPresentation.Build(
            [new Services.PreviewServerInfo(
                "preview-1", "web", "http://localhost:5173", 5173,
                Services.PreviewServerStatus.Running, now.AddMinutes(-12))],
            [new Services.LoopRowInfo("loop-1", "check the deploy and report", "every 00:05:00", now.AddMinutes(2))],
            [
                new BackgroundTaskInfo(
                    "task-1", "dotnet test tests/JarvisCode.App.Tests", BackgroundTaskStatus.Running, null,
                    "  Determining projects to restore...\n  Restored in 1.2 sec.\n  Building...\n",
                    null, now.AddSeconds(-67), null, "Run the app test suite"),
                new BackgroundTaskInfo(
                    "task-2", "npm run lint", BackgroundTaskStatus.Completed, 1,
                    "src/app.ts:12:3  error  Unexpected any\n\n1 problem (1 error, 0 warnings)\n",
                    null, now.AddMinutes(-9), now.AddMinutes(-8), "Lint the web app"),
                new BackgroundTaskInfo(
                    "task-3", "find-flaky-tests: Find flaky tests and propose fixes",
                    BackgroundTaskStatus.Running, null,
                    "\u2500\u2500 Scan \u2500\u2500\n[scan-1] 3 candidates\n",
                    null, now.AddMinutes(-2), null,
                    "find-flaky-tests: Find flaky tests and propose fixes", "workflow"),
            ],
            [
                new JarvisCode.Core.Agent.WorkerInfo(
                    "agent-1", "explore", "Map the transcript renderer",
                    JarvisCode.Core.Agent.WorkerStatus.Running, null,
                    "call-1", "claude-opus-5", now.AddMinutes(-3), null, 12500, 3, "Grep"),
                new JarvisCode.Core.Agent.WorkerInfo(
                    "agent-2", "general", "Summarize the release notes",
                    JarvisCode.Core.Agent.WorkerStatus.Completed,
                    "Three user-facing changes landed: the effort selector, the loop keepalive and the "
                    + "Browser pane's tab strip.",
                    null, "claude-sonnet-5", now.AddMinutes(-20), now.AddMinutes(-18), 48200, 11, null),
            ],
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            now,
            SampleWorkflowRun(now)));
        RunsPanel.FinishedExpanded = true;
        // Both bodies open: the shell row's command and output, and the
        // workflow row's phase list.
        RunsPanel.FocusTask("task-3");
        RunsPanel.FocusTask("task-1");
    }

    /// <summary>
    /// The workflow feed behind the sample row: three declared phases, the first
    /// finished, the second running and the third not reached.
    /// </summary>
    private static IReadOnlyDictionary<string, JarvisCode.Core.Agent.WorkflowProgressFeed> SampleWorkflowRun(
        DateTimeOffset now)
    {
        var feed = new JarvisCode.Core.Agent.WorkflowProgressFeed();
        feed.Apply(
        [
            new JarvisCode.Core.Agent.WorkflowProgressEntry.Phase(1, "Scan", "grep test logs for retries"),
            new JarvisCode.Core.Agent.WorkflowProgressEntry.Phase(2, "Fix", "one agent per flaky test"),
            new JarvisCode.Core.Agent.WorkflowProgressEntry.Phase(3, "Verify", "re-run the suite"),
            new JarvisCode.Core.Agent.WorkflowProgressEntry.Agent(
                1, "scan", JarvisCode.Core.Agent.WorkflowAgentState.Done, 1, "Scan",
                "List the tests that retried in the last 20 CI runs", 18400, "claude-sonnet-5",
                TimeSpan.FromSeconds(42), now.AddMinutes(-1)),
            new JarvisCode.Core.Agent.WorkflowProgressEntry.Agent(
                2, "fix auth_test", JarvisCode.Core.Agent.WorkflowAgentState.Progress, 2, "Fix",
                "Propose a fix for the flaky auth_test", 9200, "claude-opus-5", null, now),
            new JarvisCode.Core.Agent.WorkflowProgressEntry.Agent(
                3, "fix upload_test", JarvisCode.Core.Agent.WorkflowAgentState.Start, 2, "Fix",
                "Propose a fix for the flaky upload_test", 0, "claude-opus-5", null, now),
        ]);
        return new Dictionary<string, JarvisCode.Core.Agent.WorkflowProgressFeed>(StringComparer.Ordinal)
        {
            ["task-3"] = feed,
        };
    }

    /// <summary>
    /// --open=backgroundtasks:subagent: the pane pushed onto a real agent row's
    /// transcript, so the shared templates are exercised for real.
    /// </summary>
    public void ShowSampleSubagentView(string callId)
    {
        OpenSubagentView(callId, description: null);
        // ShowPanel restarts the timer, so the sample is pinned afterwards.
        _runsTimer.Stop();
    }

    /// <summary>The pane's own state, for the subagent self-test to assert against.</summary>
    public string? OpenSubagentCallId => RunsPanel.SubagentCallId;

    /// <summary>Opens a subagent view exactly as a click on a Agent row does.</summary>
    public void OpenSubagentViewForTest(string callId, string? description) =>
        OpenSubagentView(callId, description);

    /// <summary>The pane's Back button, so the test presses the real control.</summary>
    public System.Windows.Controls.Button SubagentBackButton => RunsPanel.BackButton;

    /// <summary>A Agent row was clicked in the transcript.</summary>
    private void OnSubagentViewRequested(object? sender, ChatSurface.SubagentViewRequest request) =>
        OpenSubagentView(request.CallId, request.Description);

    /// <summary>The Finished section is sticky per session, like the reference's map.</summary>
    private void LoadFinishedExpanded()
    {
        if (_services is null)
        {
            return;
        }

        RunsPanel.FinishedExpanded =
            _services.UiSettings.Current.BackgroundFinishedExpandedBySession
                .TryGetValue(Chat.ViewModel.Session.Id, out var expanded) && expanded;
    }

    private void SaveFinishedExpanded()
    {
        if (_services is null)
        {
            return;
        }

        var settings = _services.UiSettings.Current;
        settings.BackgroundFinishedExpandedBySession[Chat.ViewModel.Session.Id] = RunsPanel.FinishedExpanded;
        _services.UiSettings.Save();
    }

    private readonly HashSet<string> _stoppingRunIds = new(StringComparer.Ordinal);

    /// <summary>
    /// A stop from the panel, routed to whichever manager owns the row. The id
    /// stays in the pending set until the row settles, which is what makes the
    /// row read "Stopping…" in the meantime.
    /// </summary>
    private void OnRunStopRequested(object? sender, string id)
    {
        _stoppingRunIds.Add(id);
        if (id.StartsWith("loop-", StringComparison.Ordinal) &&
            int.TryParse(id.AsSpan("loop-".Length), out var loopId))
        {
            Chat.ViewModel.StopLoop(loopId);
        }
        else if (id.StartsWith("preview-", StringComparison.Ordinal))
        {
            _previewServers?.Stop(id);
        }
        else if (id.StartsWith("agent-", StringComparison.Ordinal))
        {
            Chat.ViewModel.Workers.Kill(id);
        }
        else
        {
            _services?.BackgroundTasks.Kill(id);
        }

        RefreshRuns();
    }

    /// <summary>"View transcript" on an agent row: open its Agent call in the conversation.</summary>
    private void OnRunViewTranscript(object? sender, (string CallId, string Title) request)
    {
        // The press was inside the pane, so the keyboard stays there.
        OpenSubagentView(request.CallId, request.Title, takeFocus: true);
    }

    /// <summary>
    /// The reference's subagentOpener: the Background tasks pane navigates in
    /// place to that agent's own transcript, keeping the pane open and offering
    /// a way back. Both the pane's "View transcript" and a Agent row in the
    /// transcript come through here.
    /// </summary>
    private void OpenSubagentView(string callId, string? description, bool takeFocus = false)
    {
        // The pane lists the primary pane's tasks, so its rows resolve there
        // first; a split pane is only consulted when the row is not the
        // primary's.
        var surface = Chat;
        var call = surface.FindToolCall(callId);
        if (call is null)
        {
            foreach (var pane in _panes)
            {
                if (pane.FindToolCall(callId) is { } found)
                {
                    surface = pane;
                    call = found;
                    break;
                }
            }
        }

        ShowPanel("runs");
        if (call is null)
        {
            // The row outlived its transcript (a switched or reloaded session).
            // Whatever was showing is not what was asked for, so the pane goes
            // back to its list.
            RunsPanel.PopSubagentView();
            RefreshRuns();
            return;
        }

        RefreshRuns();
        RunsPanel.PushSubagentView(
            callId,
            string.IsNullOrWhiteSpace(description) ? call.SubagentTitle : description,
            surface.CreateSubagentTranscript(call),
            takeFocus);
    }

    // ---- tasks ----

    /// <summary>
    /// --open=taskboard: fills the session board with the shapes the panel has
    /// to render — an owned task in progress, one blocked behind it, a finished
    /// one and an unclaimed one — plus a todo checklist beneath.
    /// </summary>
    public void ShowSampleTaskBoard()
    {
        var board = Chat.ViewModel.Tasks;
        var scan = board.Create("Scan the CI logs for retries", "Grep the last 200 runs for retry markers",
            "Scanning the CI logs");
        var fix = board.Create("Fix the flaky parser test", "Make the tokenizer test deterministic");
        var release = board.Create("Cut the release notes", "Summarize what changed since 2.1.0");
        board.Create("Review the diff", "Adversarial pass over the whole change");
        board.Update(scan.Id, new JarvisCode.Core.Agent.TaskUpdateRequest
        {
            Status = JarvisCode.Core.Agent.TaskState.InProgress,
            Owner = "analyzer",
        });
        board.Update(fix.Id, new JarvisCode.Core.Agent.TaskUpdateRequest { AddBlockedBy = [scan.Id] });
        board.Update(release.Id, new JarvisCode.Core.Agent.TaskUpdateRequest
        {
            Status = JarvisCode.Core.Agent.TaskState.Completed,
            Owner = "researcher",
        });

        Chat.ViewModel.Todos.Clear();
        Chat.ViewModel.Todos.Add(new JarvisCode.Core.Tools.BuiltIn.TodoItem(
            "Port the task board", JarvisCode.Core.Tools.BuiltIn.TodoStatus.Completed));
        Chat.ViewModel.Todos.Add(new JarvisCode.Core.Tools.BuiltIn.TodoItem(
            "Render it in the pane", JarvisCode.Core.Tools.BuiltIn.TodoStatus.InProgress));
        RefreshTasks();
    }

    private void RefreshTasks()
    {
        TodoList.Items.Clear();
        var todos = Chat.ViewModel.Todos;
        var board = Chat.ViewModel.Tasks.Visible();
        if (todos.Count == 0 && board.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No tasks yet.\nThe agent posts its plan here while working on multi-step jobs.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            TodoList.Items.Add(empty);
            return;
        }

        // The structured board (TaskCreate/TaskUpdate) sits above the plain
        // checklist: it is what teammates claim work from.
        if (board.Count > 0)
        {
            AddTaskSectionHeader("Task board");
            foreach (var task in board)
            {
                TodoList.Items.Add(BuildBoardRow(task));
            }

            if (todos.Count > 0)
            {
                AddTaskSectionHeader("Checklist");
            }
        }

        foreach (var todo in todos)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var (glyph, colorKey) = todo.Status switch
            {
                TodoStatus.Completed => ("●", "Success100Brush"),
                TodoStatus.InProgress => ("◐", "AccentBrandBrush"),
                _ => ("○", "Text500Brush"),
            };
            var dot = new TextBlock { Text = glyph, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            row.Children.Add(dot);

            var label = new TextBlock
            {
                Text = todo.Content,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                todo.Status == TodoStatus.Completed ? "Text400Brush" : "Text200Brush");
            if (todo.Status == TodoStatus.Completed)
            {
                label.TextDecorations = TextDecorations.Strikethrough;
            }

            row.Children.Add(label);
            TodoList.Items.Add(row);
        }
    }

    private void AddTaskSectionHeader(string title)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, TodoList.Items.Count == 0 ? 6 : 14, 0, 4),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        TodoList.Items.Add(header);
    }

    /// <summary>
    /// One board task: status dot, "#id subject", then the owner and any open
    /// blockers — the fields the reference's task list shows.
    /// </summary>
    private UIElement BuildBoardRow(JarvisCode.Core.Agent.BoardTask task)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        var (glyph, colorKey) = task.Status switch
        {
            JarvisCode.Core.Agent.TaskState.Completed => ("●", "Success100Brush"),
            JarvisCode.Core.Agent.TaskState.InProgress => ("◐", "AccentBrandBrush"),
            _ => ("○", "Text500Brush"),
        };
        var dot = new TextBlock
        {
            Text = glyph,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        row.Children.Add(dot);

        var text = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Inlines.Add(Meta($"#{task.Id} "));

        // Only the subject is struck through: the id and the metadata after it
        // still have to be readable on a finished row.
        var subject = new System.Windows.Documents.Run(
            task.Status == JarvisCode.Core.Agent.TaskState.InProgress && task.ActiveForm is { Length: > 0 }
                ? task.ActiveForm
                : task.Subject);
        subject.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
            task.Status == JarvisCode.Core.Agent.TaskState.Completed ? "Text400Brush" : "Text200Brush");
        if (task.Status == JarvisCode.Core.Agent.TaskState.Completed)
        {
            subject.TextDecorations = TextDecorations.Strikethrough;
        }

        text.Inlines.Add(subject);

        if (!string.IsNullOrEmpty(task.Owner))
        {
            text.Inlines.Add(Meta($"  {task.Owner}"));
        }

        var blockers = Chat.ViewModel.Tasks.OpenBlockers(task);
        if (blockers.Count > 0)
        {
            text.Inlines.Add(Meta($"  blocked by {string.Join(", ", blockers.Select(b => "#" + b))}"));
        }

        row.Children.Add(text);
        return row;

        static System.Windows.Documents.Run Meta(string content)
        {
            var run = new System.Windows.Documents.Run(content) { FontSize = 11 };
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text500Brush");
            return run;
        }
    }
}
