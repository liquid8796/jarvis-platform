using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Panels;

namespace JarvisCode.App.Views;

/// <summary>
/// The side panes as tiles. The reference lays a Code session out as one mosaic whose
/// tiles are the conversation and every open pane (ion-dist chunk c360a9e1c-DUoNQd2W.js,
/// its Eq host over the tq layout), so this half of the workspace owns the tile tree, the
/// per-pane cards, the drag/keyboard reordering and the resize boundaries.
/// </summary>
public partial class CodeWorkspace
{
    private TileLayout _layout = TileLayoutOps.Single(TileLayout.ChatTileId);
    private readonly Dictionary<string, PaneTile> _tiles = new(StringComparer.Ordinal);
    private string? _soloPane;
    private readonly Dictionary<string, Window> _popoutWindows = new(StringComparer.Ordinal);

    /// <summary>The panes whose existing control can move into a separate window.</summary>
    private static readonly string[] PopoutPanes = ["terminal", "changes", "browser", "files", "artifacts", "runs", "tasks", "sidechat", "plan", "pr", "runhistory", "session", "transcript", "file", "simulator"];

    private readonly System.Windows.Threading.DispatcherTimer _diffActivityTimer =
        new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>A run row in the Runs pane asked to open that session.</summary>
    public event EventHandler<string>? SessionOpenRequested;

    /// <summary>The Runs pane's Details link asked for the Scheduled page.</summary>
    public event EventHandler? ScheduledPageRequested;

    /// <summary>
    /// Whether a tab's address is served by this preview server. Matched on the origin
    /// alone: the tab may have navigated deeper into the app the server is serving.
    /// </summary>
    private static bool MatchesServer(PreviewServerInfo server, string url)
    {
        if (url.Length == 0 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var tabUri) ||
            !Uri.TryCreate(server.Url, UriKind.Absolute, out var serverUri))
        {
            return false;
        }

        return tabUri.Port == serverUri.Port &&
               string.Equals(tabUri.Host, serverUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshDiffActivityAsync()
    {
        var dirty = await GitWorkingTree.IsDirtyAsync(Chat.ViewModel.Session.WorkingDirectory);
        Chat.HeaderRail.DiffActivity = dirty;
        SplitChat.HeaderRail.DiffActivity = dirty;
        foreach (var surface in _panes)
        {
            surface.HeaderRail.DiffActivity = dirty;
        }
    }

    // ---- host wiring ----

    private void InitializeTiles()
    {
        // The new panes' own actions: a run row opens that session, Details opens the
        // Scheduled page, and Review changes swaps the diff pane in beside the request.
        RunHistoryPanel.RunSelected += (_, id) => SessionOpenRequested?.Invoke(this, id);
        RunHistoryPanel.DetailsRequested += (_, _) => ScheduledPageRequested?.Invoke(this, EventArgs.Empty);
        PullRequestPanel.ReviewChangesRequested += (_, _) => ShowPanel("changes");
        TranscriptPanel.UseTemplatesFrom(Chat.Resources);
        TerminalPanel.AskAboutRequested += (_, text) => ActiveChatSurface.AttachContextText(text);
        BrowserPanel.PageAnnotated += (_, path) => ActiveChatSurface.AttachImageFile(path);
        // The diff pane's review half: an annotation becomes a context chip, and Apply
        // fixes / Re-run review send on the session's behalf.
        ChangesPanel.AnnotationRequested += (_, text) => ActiveChatSurface.AttachContextText(text);
        ChangesPanel.ReviewMessageRequested += (_, text) => _ = ActiveChatSurface.SubmitTextAsync(text);
        Chat.ViewModelBound += (_, _) => ChangesPanel.BindSession(Chat.ViewModel.Session.Id, _services?.Paths.Root);
        ChangesPanel.BindSession(Chat.ViewModel.Session.Id, _services?.Paths.Root);
        FilesPanel.FileOpened += (_, file) => OpenFilePane(file.Path, file.Line);
        FilesPanel.AskAboutRequested += (_, text) => ActiveChatSurface.AttachContextText(text);
        FilesPanel.OpenInTerminalRequested += (_, folder) =>
        {
            ShowPanel(SidePanes.Terminal);
            TerminalPanel.Show($"cd \"{folder}\"");
        };
        Chat.IsPaneOpen = IsPaneOpen;
        SplitChat.IsPaneOpen = IsPaneOpen;

        // The diff toggle's activity dot: the reference reads it off the working tree, so
        // this polls git rather than waiting for the pane to be opened.
        _diffActivityTimer.Tick += async (_, _) => await RefreshDiffActivityAsync();
        _diffActivityTimer.Start();
        _ = RefreshDiffActivityAsync();

        Tiles.PreviewMouseLeftButtonDown += OnTilesMouseDown;
        Tiles.PreviewMouseMove += OnTilesMouseMove;
        Tiles.PreviewMouseLeftButtonUp += OnTilesMouseUp;
        Tiles.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || (_dragTile is null && _resize is null)) return;
            if (_resize is { } resize) _layout = TileLayoutOps.SetFlexes(_layout, resize.StackId, resize.Flexes);
            _dragTile = null; _dragging = false; _resize = null; _dropFeedback = null;
            Tiles.ReleaseMouseCapture(); ShowDropIndicator(null); ApplyLayout(); e.Handled = true;
        };
        Tiles.MouseLeave += (_, _) => UpdateResizeCursor(null);
        Tiles.DragOver += OnTilesDragOver;
        Tiles.DragLeave += OnTilesDragLeave;
        Tiles.Drop += OnTilesDrop;
        if (_services?.UiSettings.Current.TileLayout is { Length: > 0 } stored &&
            TileLayoutOps.FromJson(stored) is { } restored &&
            restored.Contains(TileLayout.ChatTileId))
        {
            // Only the panes this build still knows survive a restore; anything else would
            // arrange an empty rectangle nothing can fill.
            var known = restored;
            foreach (var id in restored.TileIds.ToList())
            {
                if (id != TileLayout.ChatTileId && PaneContent(id) is null)
                {
                    known = TileLayoutOps.Remove(known, id);
                }
            }

            _layout = known;
            foreach (var id in _layout.TileIds)
            {
                if (id != TileLayout.ChatTileId)
                {
                    EnsureTile(id);
                }
            }
        }

        ApplyLayout(persist: false);
    }

    /// <summary>The control a pane key hosts, or null when this build has no such pane.</summary>
    private FrameworkElement? PaneContent(string key) => key switch
    {
        "terminal" => TerminalPanel,
        "changes" => ChangesPanel,
        "browser" => BrowserPanel,
        "files" => FilesPanel,
        "artifacts" => ArtifactPanel,
        "runs" => RunsPanel,
        "tasks" => TasksPanel,
        "sidechat" => SideChatPanel,
        SidePanes.Plan => PlanPanel,
        SidePanes.PullRequest => PullRequestPanel,
        SidePanes.RunHistory => RunHistoryPanel,
        SidePanes.Session => SessionPanel,
        SidePanes.Transcript => TranscriptPanel,
        SidePanes.File => FileViewerPanel,
        SidePanes.Simulator => EmulatorPanel,
        _ => null,
    };

    private PaneTile? EnsureTile(string key)
    {
        if (_tiles.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (PaneContent(key) is not { } content)
        {
            return null;
        }

        // The control moves out of the parked store exactly once; from then on it lives in
        // its tile, which stays a child of the host even while the pane is closed.
        if (content.Parent is Panel store)
        {
            store.Children.Remove(content);
        }

        var tile = new PaneTile(key) { PaneContent = content };
        TileHostPanel.SetTileId(tile, key);
        tile.CanPopOut = PopoutPanes.Contains(key);
        tile.MenuRequested += (_, _) => ShowPaneMenu(tile);
        tile.PopoutRequested += (_, _) => PopOutPane(key);
        tile.ExpandRequested += (_, _) => ToggleSolo(key);
        tile.CloseRequested += (_, _) => ClosePane(key);
        tile.BackRequested += (_, _) => RunsPanel.PopSubagentView();
        tile.DragStarted += (_, point) => BeginTileDrag(key, tile.TranslatePoint(point, Tiles));
        tile.MoveRequested += (_, direction) => MoveTile(key, direction);
        tile.SplitCommitted += (_, direction) => CommitTileSplit(key, direction);
        _tiles[key] = tile;
        Tiles.Children.Add(tile);
        return tile;
    }

    // ---- opening and closing ----

    /// <summary>Whether a pane is one of the open tiles.</summary>
    public bool IsPaneOpen(string key) => _layout.Contains(key) || _popoutWindows.ContainsKey(key);

    /// <summary>Whether any pane besides the conversation is open — Ctrl+\ closes one first.</summary>
    public bool HasOpenPanel => _popoutWindows.Count > 0 || _layout.TileIds.Any(id => id != TileLayout.ChatTileId);

    public bool IsSideChatOpen => IsPaneOpen("sidechat");

    /// <summary>
    /// The rectangles the tile host gave each open pane, for --ui-selftest: the padding
    /// around the mosaic and the gap between two tiles are laid-out numbers, not literals.
    /// </summary>
    public IReadOnlyDictionary<string, Rect> MeasuredTileRects => Tiles.TileRects;

    /// <summary>The panes on screen, in layout order.</summary>
    public IReadOnlyList<string> OpenPanes =>
        [.. _layout.TileIds.Where(id => id != TileLayout.ChatTileId).Concat(_popoutWindows.Keys).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Opens a pane, or closes every pane when given null. An already-open pane is brought
    /// forward rather than opened twice, which is what the reference's setSidePane does.
    /// </summary>
    public void ShowPanel(string? key)
    {
        if (key is null)
        {
            CloseAllPanes();
            return;
        }

        if (_popoutWindows.TryGetValue(key, out var popped)) { popped.Activate(); return; }

        // A side chat living in its own window is fronted, not re-docked.
        if (key == "sidechat" && _sideChatWindow is not null)
        {
            _sideChatWindow.Activate();
            SideChat.FocusInput();
            return;
        }

        if (EnsureTile(key) is null)
        {
            return;
        }

        if (!_layout.Contains(key))
        {
            _layout = TileLayoutOps.AppendRight(_layout, key);
        }

        _soloPane = null;
        ApplyLayout();
        RefreshPane(key);
    }

    /// <summary>
    /// A surface asked for a pane. Every caller but the rail names the pane it wants open;
    /// the tasks chip is the one that passes null, and it means "close the Background tasks
    /// pane" rather than "close the whole side of the window".
    /// </summary>
    private void OnPanelRequested(string? key)
    {
        if (key is null)
        {
            ClosePane("runs");
            return;
        }

        ShowPanel(key);
    }

    /// <summary>The rail's own action: an open pane closes, a closed one opens.</summary>
    public void TogglePane(string key)
    {
        if (IsPaneOpen(key))
        {
            ClosePane(key);
        }
        else
        {
            ShowPanel(key);
        }
    }

    /// <summary>The older spelling of the rail's toggle, kept for the window's chords.</summary>
    public void TogglePanel(string key) => TogglePane(key);

    /// <summary>The Browser pane's element picker, which Ctrl+Shift+S arms.</summary>
    public void ToggleBrowserPicker()
    {
        ShowPanel(SidePanes.Preview);
        BrowserPanel.TogglePicker();
    }

    /// <summary>Ctrl+T: a new Browser pane tab.</summary>
    public void OpenBrowserTab()
    {
        ShowPanel(SidePanes.Preview);
        BrowserPanel.OpenNewTab();
    }

    /// <summary>Closes one pane, leaving the others where they are.</summary>
    public void ClosePane(string key)
    {
        if (_popoutWindows.TryGetValue(key, out var popped)) popped.Close();
        if (!_layout.Contains(key))
        {
            return;
        }

        _layout = TileLayoutOps.Remove(_layout, key);
        if (_soloPane == key)
        {
            _soloPane = null;
        }

        ApplyLayout();
    }

    /// <summary>Ctrl+\ with several panes open closes the last one; with one, closes it.</summary>
    public void CloseAllPanes()
    {
        foreach (var key in OpenPanes.ToArray())
        {
            if (_popoutWindows.TryGetValue(key, out var popped)) popped.Close();
            _layout = TileLayoutOps.Remove(_layout, key);
        }

        _soloPane = null;
        ApplyLayout();
    }

    /// <summary>The reference's expand: the pane fills the host and the conversation hides.</summary>
    private void ToggleSolo(string key)
    {
        _soloPane = _soloPane == key ? null : key;
        ApplyLayout();
    }

    /// <summary>Whether this pane is the one currently filling the host.</summary>
    private bool IsSolo(string key) => _soloPane == key;

    /// <summary>
    /// The action behind the titlebar's artifact expander — the reference's `Qp`, whose
    /// click is `toggleExpandedTile("artifact")`.
    /// </summary>
    public void ExpandArtifact()
    {
        if (IsPaneOpen("artifact"))
        {
            ToggleSolo("artifact");
        }
    }

    // ---- layout ----

    private void ApplyLayout(bool persist = true)
    {
        Tiles.Layout = _layout;
        Tiles.HiddenTileIds = _soloPane is null ? [] : [TileLayout.ChatTileId];

        var open = OpenPanes;
        var canDrag = _layout.TileIds.Count() > 1 && _soloPane is null;
        foreach (var (key, tile) in _tiles)
        {
            tile.CanDrag = canDrag && _layout.Contains(key);
            tile.SetExpanded(_soloPane == key);
        }

        foreach (var surface in _panes)
        {
            surface.HeaderRail.SetActivePanes(open);
        }

        Chat.HeaderRail.SetActivePanes(open);
        Chat.SetTasksPaneOpen(IsPaneOpen("runs"));
        if (IsPaneOpen("runs"))
        {
            _runsTimer.Start();
        }
        else
        {
            _runsTimer.Stop();
        }

        RefreshTitleBarControls();

        Tiles.InvalidateMeasure();
        if (persist && _services is not null)
        {
            _services.UiSettings.Current.TileLayout = TileLayoutOps.ToJson(_layout.Root).ToJsonString();
            _services.UiSettings.Save();
        }
    }

    /// <summary>Reloads every open pane against the session's working directory.</summary>
    private void RefreshActivePanel()
    {
        foreach (var key in OpenPanes)
        {
            RefreshPane(key);
        }
    }

    private void RefreshPane(string key)
    {
        var cwd = Chat.ViewModel.Session.WorkingDirectory;
        switch (key)
        {
            case "terminal":
                TerminalPanel.Start(cwd);
                break;
            case "changes":
                ChangesPanel.Load(cwd);
                break;
            case "browser":
                BrowserPanel.Configure(cwd, Services.BrowserStorageProfile.EngineDirectory(_services!.Paths.Root),
                    _services.UiSettings, Chat.ViewModel.Session.Id);
                break;
            case "files":
                FilesPanel.Load(cwd);
                break;
            case "artifacts":
                _ = ShowArtifactAsync();
                break;
            case "runs":
                LoadFinishedExpanded();
                RefreshRuns();
                break;
            case "tasks":
                RefreshTasks();
                break;
            case SidePanes.Plan:
                PlanPanel.Load(Chat.ViewModel);
                break;
            case SidePanes.PullRequest:
                _ = PullRequestPanel.LoadAsync(cwd);
                break;
            case SidePanes.RunHistory:
                RunHistoryPanel.Load(_services!, Chat.ViewModel.Session);
                break;
            case SidePanes.Session:
                SessionPanel.Load(_services!, SessionPanel.TargetSessionId ?? Chat.ViewModel.Session.Id);
                break;
            case SidePanes.Transcript:
                TranscriptPanel.Load(Chat.ViewModel);
                break;
            case SidePanes.File:
                FileViewerPanel.Reload();
                break;
            case SidePanes.Simulator:
                EmulatorPanel.Load();
                break;
            case "sidechat":
                if (!_sideChatReady && _services is not null)
                {
                    SideChat.Initialize(_services, isCodeSurface: false);
                    _sideChatReady = true;
                }

                SideChat.FocusInput();
                break;
        }
    }

    /// <summary>
    /// Attaches the emulator pane to the first booted emulator, for the
    /// --open=emulator pose. Nothing happens where none is running.
    /// </summary>
    public async Task AttachFirstEmulatorAsync()
    {
        if (Services.AndroidEmulator.FindAdb() is not { } adb)
        {
            return;
        }

        var bridge = new Services.AndroidEmulatorBridge(adb);
        var booted = (await bridge.ListDevicesAsync(System.Threading.CancellationToken.None))
            .FirstOrDefault(static d => d.State == "Booted");
        if (booted is null)
        {
            return;
        }

        await EmulatorPanel.AttachAsync(booted.Serial, null, System.Threading.CancellationToken.None);
    }

    /// <summary>Opens the File pane on a path, the way the reference's "Open in pane" does.</summary>
    public void OpenFilePane(string path, int? line = null, int? endLine = null)
    {
        FileViewerPanel.Show(path, line, endLine);
        ShowPanel(SidePanes.File);
    }

    /// <summary>Opens the Session pane on another session's transcript.</summary>
    public void OpenSessionPane(string sessionId)
    {
        SessionPanel.TargetSessionId = sessionId;
        ShowPanel(SidePanes.Session);
    }

    // ---- pane chrome ----

    internal async Task<bool> VerifyBrowserPopoutContinuityAsync()
    {
        if (PaneDriver is not { } driver || BrowserPanel.ActiveTab is not { } tab) return false;
        var handlers = new BrowserPaneHandlers(driver);
        var marker = "popout-" + Guid.NewGuid().ToString("N");
        await handlers.JavaScriptAsync(new System.Text.Json.Nodes.JsonObject
        { ["action"] = "javascript_exec", ["text"] = "window.__jarvisPopoutProbe = '" + marker + "'" }, CancellationToken.None);
        PopOutPane("browser");
        await Task.Delay(350);
        var floating = await handlers.JavaScriptAsync(new System.Text.Json.Nodes.JsonObject
        { ["action"] = "javascript_exec", ["text"] = "window.__jarvisPopoutProbe" }, CancellationToken.None);
        var preserved = ReferenceEquals(tab, BrowserPanel.ActiveTab) && floating.Content.Contains(marker) &&
            _popoutWindows.TryGetValue("browser", out var window) && ReferenceEquals(Window.GetWindow(BrowserPanel), window);
        if (_popoutWindows.TryGetValue("browser", out var opened)) opened.Close();
        await Task.Delay(350);
        var docked = await handlers.JavaScriptAsync(new System.Text.Json.Nodes.JsonObject
        { ["action"] = "javascript_exec", ["text"] = "window.__jarvisPopoutProbe" }, CancellationToken.None);
        return preserved && ReferenceEquals(tab, BrowserPanel.ActiveTab) && docked.Content.Contains(marker) &&
            ReferenceEquals(_tiles["browser"].PaneContent, BrowserPanel);
    }

    internal bool VerifySideChatPopoutContinuity()
    {
        ShowPanel("sidechat");
        var viewModel = SideChat.ViewModel;
        MoveSideChatToWindow();
        var preserved = ReferenceEquals(viewModel, SideChat.ViewModel) && IsPaneOpen("sidechat") &&
            _sideChatWindow is { } floating && ReferenceEquals(Window.GetWindow(SideChat), floating);
        _sideChatWindow?.Close();
        preserved &= _sideChatWindow is null && _layout.Contains("sidechat") && SideChatPanel.Children.Contains(SideChat);
        MoveSideChatToWindow();
        ClosePane("sidechat");
        return preserved && _sideChatWindow is null && !IsPaneOpen("sidechat") &&
            SideChatPanel.Children.Contains(SideChat) && ReferenceEquals(viewModel, SideChat.ViewModel);
    }

    /// <summary>The reference's ↗: move the existing pane into its own themed window.</summary>
    private void PopOutPane(string key, Point? screenPosition = null)
    {
        if (key == "sidechat") { MoveSideChatToWindow(); return; }
        if (_services is null)
        {
            return;
        }

        if (_popoutWindows.TryGetValue(key, out var existing)) { existing.Activate(); return; }
        if (EnsureTile(key) is not { } tile || tile.PaneContent is not FrameworkElement content) return;
        var cwd = Chat.ViewModel.Session.WorkingDirectory;
        var previousLayout = _layout;
        tile.PaneContent = null;
        _layout = TileLayoutOps.Remove(_layout, key);
        var detachedLayout = TileLayoutOps.ToJson(_layout.Root).ToJsonString();
        if (_soloPane == key) _soloPane = null;

        var title = SidePanes.Title(key);
        var project = System.IO.Path.GetFileName(cwd.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/'));
        var window = new Window
        {
            Title = project.Length > 0 ? $"{title} — {project}" : title,
            Width = 1000,
            Height = 740,
            Owner = Window.GetWindow(this),
        };
        window.SetResourceReference(BackgroundProperty, "Bg200Brush");
        var root = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
        var back = new Button { Content = "Return to main window", Padding = new Thickness(8, 4, 8, 4) };
        back.SetResourceReference(StyleProperty, "SecondaryButton"); back.Click += (_, _) => window.Close();
        var pin = new System.Windows.Controls.Primitives.ToggleButton { Content = "Always on top", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0) };
        pin.SetResourceReference(StyleProperty, "PanelTab"); pin.Checked += (_, _) => window.Topmost = true; pin.Unchecked += (_, _) => window.Topmost = false;
        actions.Children.Add(back); actions.Children.Add(pin); DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        var host = new ContentControl { Content = content }; root.Children.Add(host); window.Content = root;
        _popoutWindows[key] = window;
        window.Closed += (_, _) =>
        {
            host.Content = null; tile.PaneContent = content; _popoutWindows.Remove(key);
            if (previousLayout.Contains(key) && TileLayoutOps.ToJson(_layout.Root).ToJsonString() == detachedLayout) _layout = previousLayout;
            else if (!_layout.Contains(key)) _layout = TileLayoutOps.AppendRight(_layout, key);
            ApplyLayout();
        };
        if (screenPosition is { } position)
        {
            var transform = System.Windows.PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var logical = transform.Transform(position); window.Left = logical.X - 100; window.Top = logical.Y - 20;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }

        ApplyLayout();
        window.Show();
    }

    private void ShowPaneMenu(PaneTile tile)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = tile.MenuPlacementTarget,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HorizontalOffset = -128,
            VerticalOffset = 4,
            MinWidth = 158,
        };

        if (tile.PaneKey == "changes")
        {
            AddChangesItems(menu);
            menu.Items.Add(new Separator());
        }

        foreach (var (key, label) in new[]
                 {
                     ("runs", SidePanes.Title("runs")),
                     ("tasks", SidePanes.Title("tasks")),
                     ("sidechat", SidePanes.Title("sidechat")),
                 })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = IsPaneOpen(key) };
            item.Click += (_, _) => TogglePane(key);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => RefreshPane(tile.PaneKey);
        menu.Items.Add(refresh);

        var close = new MenuItem { Header = "Close panel" };
        close.Click += (_, _) => ClosePane(tile.PaneKey);
        menu.Items.Add(close);
        menu.IsOpen = true;
    }

    /// <summary>The changes panel's own menu: the file list toggle and the layout group.</summary>
    private void AddChangesItems(ContextMenu menu)
    {
        // The reference shows this row with its shortcut and no tick, so the state lives
        // in the panel rather than in a check mark.
        var showFiles = new MenuItem { Header = "Show files", InputGestureText = "Ctrl ⇧ Y" };
        showFiles.Click += (_, _) => ChangesPanel.ShowFiles = !ChangesPanel.ShowFiles;
        menu.Items.Add(showFiles);

        menu.Items.Add(SessionMenu.SectionHeader("Diff layout"));

        var unified = new MenuItem { Header = "Unified", IsCheckable = true, IsChecked = !ChangesPanel.IsSideBySide };
        unified.Click += (_, _) => ChangesPanel.IsSideBySide = false;
        menu.Items.Add(unified);

        var sideBySide = new MenuItem { Header = "Side by side", IsCheckable = true, IsChecked = ChangesPanel.IsSideBySide };
        sideBySide.Click += (_, _) => ChangesPanel.IsSideBySide = true;
        menu.Items.Add(sideBySide);
    }

    /// <summary>Ctrl+Shift+Y: the file list toggle, opening the changes panel when it is closed.</summary>
    public void ToggleChangesFiles()
    {
        if (!IsPaneOpen("changes"))
        {
            ShowPanel("changes");
            ChangesPanel.ShowFiles = true;
            return;
        }

        ChangesPanel.ShowFiles = !ChangesPanel.ShowFiles;
    }

    // ---- dragging a tile ----

    private string? _dragTile;
    private Point _dragOrigin;
    private bool _dragging;
    private TileDropFeedback? _dropFeedback;

    private (string StackId, int Index, bool Horizontal, Point Origin, IReadOnlyList<double> Flexes, double Total)? _resize;

    private void BeginTileDrag(string key, Point origin)
    {
        _dragTile = key;
        _dragOrigin = origin;
        _dragging = false;
    }

    private void OnTilesMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragTile is not null)
        {
            return;
        }

        var point = e.GetPosition(Tiles);
        if (Tiles.BoundaryAt(point) is not { } boundary)
        {
            return;
        }

        var horizontal = boundary.Stack.Direction == TileDirection.Row;
        var total = horizontal ? boundary.Rect.Height : boundary.Rect.Width;
        // The pair shares whatever the stack's own main axis measures, which is the
        // container extent minus the gaps between its children.
        var extent = horizontal
            ? SumWidths(boundary.Stack)
            : SumHeights(boundary.Stack);
        _resize = (boundary.Stack.Id, boundary.Index, horizontal, point,
            [.. boundary.Stack.Children.Select(c => c.Flex)], extent > 0 ? extent : total);
        Tiles.CaptureMouse();
        e.Handled = true;
    }

    private double SumWidths(TileStack stack) =>
        stack.Children.Sum(child => TileLayoutOps.TileIds(child)
            .Where(id => Tiles.TileRects.ContainsKey(id))
            .Select(id => Tiles.TileRects[id].Width)
            .DefaultIfEmpty(0)
            .Max());

    private double SumHeights(TileStack stack) =>
        stack.Children.Sum(child => TileLayoutOps.TileIds(child)
            .Where(id => Tiles.TileRects.ContainsKey(id))
            .Select(id => Tiles.TileRects[id].Height)
            .DefaultIfEmpty(0)
            .Max());

    private void OnTilesMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Tiles);

        if (_resize is { } resize && e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = resize.Horizontal ? point.X - resize.Origin.X : point.Y - resize.Origin.Y;
            if (TileLayoutOps.FindStack(_layout.Root, resize.StackId) is { } stack)
            {
                var min = TileLayout.MinTileBasePx;
                var flexes = TileLayoutOps.ResizeBoundary(
                    resize.Flexes, resize.Index, delta, resize.Total, min, min);
                _layout = TileLayoutOps.SetFlexes(_layout, stack.Id, flexes);
                Tiles.Layout = _layout;
                Tiles.InvalidateMeasure();
            }

            return;
        }

        if (_dragTile is { } key && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging &&
                (Math.Abs(point.X - _dragOrigin.X) > SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(point.Y - _dragOrigin.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                _dragging = true;
                Tiles.CaptureMouse();
            }

            if (_dragging)
            {
                _dropFeedback = Tiles.DropTargetAt(point, key);
                ShowDropIndicator(_dropFeedback);
            }

            return;
        }

        UpdateResizeCursor(Tiles.BoundaryAt(point));
    }

    private void UpdateResizeCursor((TileStack Stack, int Index, Rect Rect)? boundary)
    {
        Tiles.Cursor = boundary is null
            ? null
            : boundary.Value.Stack.Direction == TileDirection.Row ? Cursors.SizeWE : Cursors.SizeNS;
        Tiles.ToolTip = boundary is null ? null : "Resize";
    }

    private void OnTilesMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resize is not null)
        {
            _resize = null;
            Tiles.ReleaseMouseCapture();
            ApplyLayout();
            return;
        }

        if (_dragTile is { } key)
        {
            var dragged = _dragging;
            var point = e.GetPosition(Tiles);
            _dragTile = null;
            _dragging = false;
            Tiles.ReleaseMouseCapture();
            ShowDropIndicator(null);
            if (dragged && !new Rect(0, 0, Tiles.ActualWidth, Tiles.ActualHeight).Contains(point))
            {
                PopOutPane(key, Tiles.PointToScreen(point));
            }
            else if (dragged && _dropFeedback is { } feedback)
            {
                _layout = TileLayoutOps.Move(_layout, key, feedback.Target);
                ApplyLayout();
            }

            _dropFeedback = null;
        }
    }

    // ---- dragging a session row onto the mosaic ----

    /// <summary>
    /// The format the sidebar's session rows drag under. A row dropped on the mosaic
    /// gets the same overlay a tile does: "Split view" where the drop would make a new
    /// stack, "Open here" where it would take a slot.
    /// </summary>
    public const string SessionDragFormat = "jarvis/session-id";

    /// <summary>A session row was dropped on the mosaic; true when it should open beside the others.</summary>
    public event EventHandler<(string SessionId, bool Split)>? SessionDropped;

    private static string? DraggedSessionId(DragEventArgs e) =>
        e.Data.GetDataPresent(SessionDragFormat) ? e.Data.GetData(SessionDragFormat) as string : null;

    private void OnTilesDragOver(object sender, DragEventArgs e)
    {
        if (DraggedSessionId(e) is null)
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        // No tile is being dragged, so nothing is excluded from the targets.
        _dropFeedback = Tiles.DropTargetAt(e.GetPosition(Tiles), "");
        ShowDropIndicator(_dropFeedback);
    }

    private void OnTilesDragLeave(object sender, DragEventArgs e)
    {
        _dropFeedback = null;
        ShowDropIndicator(null);
    }

    private void OnTilesDrop(object sender, DragEventArgs e)
    {
        var sessionId = DraggedSessionId(e);
        var feedback = _dropFeedback;
        _dropFeedback = null;
        ShowDropIndicator(null);
        if (sessionId is null)
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        SessionDropped?.Invoke(this, (sessionId, feedback?.IsSplit ?? true));
    }

    /// <summary>
    /// The overlay the reference shows while a tile is over a drop target: "Split view"
    /// where the drop would make a new stack, "Open here" where it would take a slot.
    /// </summary>
    private void ShowDropIndicator(TileDropFeedback? feedback)
    {
        if (feedback is null)
        {
            DropIndicator.Visibility = Visibility.Collapsed;
            Tiles.DropIndicator = null;
            Tiles.InvalidateArrange();
            return;
        }

        DropIndicatorLabel.Text = feedback.IsSplit ? "Split view" : "Open here";
        DropIndicator.Visibility = Visibility.Visible;
        Tiles.DropIndicator = feedback.Rect;
        Tiles.InvalidateArrange();
    }

    // ---- keyboard move ----

    private void MoveTile(string key, TileMoveDirection direction)
    {
        var path = TileLayoutOps.Path(_layout.Root, key);
        var parent = path is null ? null : TileLayoutOps.StackAt(_layout.Root, path);
        if (path is null || parent is null || !_tiles.TryGetValue(key, out var tile))
        {
            return;
        }

        var along = parent.Direction == TileDirection.Row
            ? direction is TileMoveDirection.Left or TileMoveDirection.Right
            : direction is TileMoveDirection.Up or TileMoveDirection.Down;
        if (!along)
        {
            // A perpendicular arrow only previews; Enter is what commits the split.
            tile.PreviewSplit(direction);
            return;
        }

        var index = path[^1];
        var next = direction is TileMoveDirection.Left or TileMoveDirection.Up ? index - 1 : index + 1;
        if (next < 0 || next >= parent.Children.Count)
        {
            return;
        }

        _layout = TileLayoutOps.Move(_layout, key, new TileInsertTarget(parent.Id, next));
        ApplyLayout();
        tile.FocusHandle();
    }

    private void CommitTileSplit(string key, TileMoveDirection direction)
    {
        var path = TileLayoutOps.Path(_layout.Root, key);
        var parent = path is null ? null : TileLayoutOps.StackAt(_layout.Root, path);
        if (path is null || parent is null)
        {
            return;
        }

        var crossDirection = parent.Direction == TileDirection.Row ? TileDirection.Column : TileDirection.Row;
        var leading = direction is TileMoveDirection.Left or TileMoveDirection.Up;
        _layout = TileLayoutOps.Move(_layout, key, new TileSplitTarget(parent.Id, leading ? 0 : 1, crossDirection));
        ApplyLayout();
        if (_tiles.TryGetValue(key, out var tile))
        {
            tile.FocusHandle();
        }
    }
}
