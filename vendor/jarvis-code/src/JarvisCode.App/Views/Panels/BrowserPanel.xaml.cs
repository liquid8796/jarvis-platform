using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The browser side panel, a recreation of the reference app's Browser pane:
/// real tabs (each is a view in the engine, so background tabs keep running), the
/// navigation bar, the "Browse and verify" empty state with the detect-dev-server
/// flow, the pane options menu, and the driver surface the mcp__Claude_Browser__*
/// tools steer (Services/BrowserPaneDriver.cs).
/// </summary>
public partial class BrowserPanel : UserControl
{
    /// <summary>One pane tab and everything the agent tools need from it.</summary>
    public sealed class PaneTab
    {
        internal BrowserContext Owner { get; init; } = null!;
        public required string Id { get; init; }
        internal ToggleButton Button { get; init; } = null!;
        internal TextBlock Label { get; init; } = null!;
        /// <summary>
        /// This tab's view in the engine process, or null before it has one.
        /// A tab exists in the pane's chrome before it exists in the engine.
        /// </summary>
        public string? EngineTabId { get; internal set; }
        public string Url { get; internal set; } = "";

        /// <summary>
        /// How many navigations this tab has committed. What it counts to does
        /// not matter; that it changes is what tells a multi-dispatch input
        /// that the page underneath it moved.
        /// </summary>
        public int NavigationCount { get; internal set; }

        // Request identity, distinct from committed document count: even two opens of the same
        // URL supersede one another. Only the latest request may update navigation UI state.
        internal long NavigationRequestVersion { get; set; }

        /// <summary>
        /// Set when the user opened a local file in this tab. The agent may not
        /// navigate it away: the tab is showing what the user asked to see.
        /// </summary>
        public bool PinnedFilePreview { get; internal set; }

        /// <summary>Whether the last navigation this tab was asked for became a download.</summary>
        public bool LastNavigationDownloaded { get; internal set; }

        /// <summary>
        /// The last ordinary web origin this tab committed — the site a domain
        /// transition would be moving away from. A dev server or a local file
        /// never becomes one, and going past one leaves the previous value
        /// standing (the reference's lastExternalCommittedOrigin).
        /// </summary>
        public string? LastExternalOrigin { get; internal set; }

        public string Title { get; internal set; } = "New tab";

        /// <summary>
        /// Camera/microphone requests this page made and the pane refused, accumulated for
        /// the result trailer. The reference keeps the same per-tab set (deniedMediaKinds).
        /// </summary>
        public HashSet<string> DeniedMediaKinds { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Set when the OS clipboard was written while the pane was dispatching synthetic
        /// input, and consumed by the next result's trailer — the reference's one-shot
        /// clipboardWriteNotice, cleared the same way when the page commits a navigation.
        /// </summary>
        public bool ClipboardChanged { get; internal set; }

        // CDP capture buffers, in the shapes JarvisBrowserFormat renders.
        public List<JsonObject> ConsoleEntries { get; } = [];
        public List<string> NetOrder { get; } = [];
        public Dictionary<string, JsonObject> Network { get; } = [];

        internal Task? Initializing { get; set; }
    }

    public sealed record PaneTabInfo(string Id, string Url, string Title, bool Active, bool HasPage);

    private List<PaneTab> _tabs => _context.Tabs;
    private readonly DispatcherTimer _toastTimer;

    /// <summary>
    /// How long a hidden pane survives before it is released — the reference's
    /// own five minutes (its RNn = 300000ms).
    /// </summary>
    public static readonly TimeSpan HiddenReapDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a pane that just went out of sight should start its countdown.
    /// The reference reaps a hidden pane only when something can rebuild it — a
    /// dev server, a Claude page, exported content — so a plain browser tab the
    /// user hid is left alone however long it stays hidden.
    /// </summary>
    public static bool ShouldArmHiddenReap(bool isVisible, int previewPort, bool paneOpen) =>
        !isVisible && previewPort > 0 && paneOpen;

    /// <summary>
    /// Whether the countdown that just ran out should release the pane. The
    /// reference re-checks that the pane is still there and still hidden before
    /// destroying it, so a pane brought back in the meantime survives.
    /// </summary>
    public static bool ShouldReapNow(bool isVisible, bool paneOpen) => !isVisible && paneOpen;

    private readonly DispatcherTimer _hiddenReapTimer;

    /// <summary>
    /// The dev server this pane was opened for, or 0 when it is a plain browser
    /// tab. The reference reaps a hidden pane only when something can rebuild it
    /// — a dev server, a Claude page, exported content — and leaves a plain tab
    /// alone however long it stays hidden.
    /// </summary>
    private int _previewPort { get => _context.PreviewPort; set => _context.PreviewPort = value; }
    private PaneTab? _active { get => _context.Active; set => _context.Active = value; }
    private ElectronPaneSession? _session { get => _context.Session; set => _context.Session = value; }
    private ElectronPaneView? _paneView { get => _context.View; set => _context.View = value; }

    /// <summary>The reference rate-limits its clipboard toast to one every three seconds.</summary>
    private static readonly TimeSpan ClipboardToastInterval = TimeSpan.FromSeconds(3);

    private DateTime _lastClipboardToast = DateTime.MinValue;
    private string _userDataFolder = "";
    private string _workingDirectory = "";
    private UiSettingsStore? _settings;
    private bool _serverStateShowing;
    private LaunchConfiguration? _lastConfiguration;
    private Action? _confirmAction;

    public BrowserPanel()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _hiddenReapTimer = new DispatcherTimer { Interval = HiddenReapDelay };
        _hiddenReapTimer.Tick += (_, _) => ReapIfStillHidden();
        AddTab();
        ShowDetectIdle();
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                ResyncColorSchemes();
            }

            ArmHiddenReap();
        };
    }

    /// <summary>
    /// Starts the countdown when a preview pane goes out of sight and cancels it
    /// the moment it comes back, as the reference arms and clears its reap timer.
    /// </summary>
    private void ArmHiddenReap()
    {
        _hiddenReapTimer.Stop();
        if (ShouldArmHiddenReap(IsVisible, _previewPort, PaneOpen))
        {
            _hiddenReapTimer.Start();
        }
    }

    /// <summary>
    /// The countdown ran out. The pane is released only if it is still hidden —
    /// the reference re-checks the same way before destroying its view.
    /// </summary>
    private void ReapIfStillHidden()
    {
        _hiddenReapTimer.Stop();
        if (!ShouldReapNow(IsVisible, PaneOpen))
        {
            return;
        }

        foreach (var tab in _tabs.ToList())
        {
            Tabs.Items.Remove(tab.Button);
            ReleaseEngineTab(tab);
        }

        _tabs.Clear();
        _active = null;
        _previewPort = 0;
        // A blank tab waits behind it, exactly as closing the last one leaves
        // things; the next navigate builds an engine view into it.
        AddTab();
        ShowDetectIdle();
    }

    /// <summary>A picked element becomes composer context, the reference's Select element flow.</summary>
    public event EventHandler<string>? ElementPicked;

    /// <summary>Detect flow / Try again: start (or attach to) this dev server.</summary>
    public event EventHandler<LaunchConfiguration>? StartPreviewRequested;

    /// <summary>The pane menu's "Show dev server logs".</summary>
    public event EventHandler? ShowDevServerLogsRequested;

    /// <summary>Closing the last tab closes the Browser pane itself.</summary>
    public event EventHandler? LastTabClosed;

    /// <summary>
    /// Raised when the user clears the pane's browsing data, so the consent the
    /// session collected for it goes with the data it was about.
    /// </summary>
    public event EventHandler? BrowsingDataCleared;

    /// <summary>Points the panel at a project, an engine profile and the app's UI settings.</summary>
    public void Configure(
        string workingDirectory, string userDataFolder, UiSettingsStore? settings = null, string? sessionId = null)
    {
        _workingDirectory = workingDirectory;
        _userDataFolder = userDataFolder;
        _codeSessionId = sessionId;
        if (settings is not null && !ReferenceEquals(settings, _settings))
        {
            if (_settings is not null) _settings.Saved -= OnBrowserSettingsSaved;
            _settings = settings;
            // Theme changes re-sync every tab's color-scheme emulation (reference behavior).
            settings.Saved += OnBrowserSettingsSaved;
        }
        SelectBrowserContext();
    }

    // ---- tabs ------------------------------------------------------------------

    public bool PaneDisplayed => IsVisible;

    public bool PaneOpen => _tabs.Any(static t => t.EngineTabId is not null);

    public PaneTab? ActiveTab => _active;

    /// <summary>
    /// The width the page is laid out in, which resize_window's fit-to-pane
    /// scaling is measured against. It used to be the tab's own control; the
    /// engine's views all fill one host, so it is the host's width now.
    /// </summary>
    public double PaneWidth => WebHost.ActualWidth;

    /// <summary>Runs Chromium's own find in the active tab.</summary>
    internal async Task FindInActiveTabAsync(string query, bool forward, bool findNext)
    {
        if (_active?.EngineTabId is not { } engineTab || _session is not { } session)
        {
            return;
        }

        await session.Host.RequestAsync("tab.find", new System.Text.Json.Nodes.JsonObject
        {
            ["tabId"] = engineTab,
            ["query"] = query,
            ["forward"] = forward,
            ["findNext"] = findNext,
        }, CancellationToken.None);
    }

    /// <summary>
    /// A history move for the driver: false when that end of the history was
    /// empty, which is the answer navigate reports as "no back history".
    /// </summary>
    public async Task<bool> DriverHistoryAsync(PaneTab tab, string direction)
    {
        if (tab.EngineTabId is not { } engineTab || tab.Owner.Session is not { } session)
        {
            return false;
        }

        return await session.HistoryAsync(engineTab, direction);
    }

    /// <summary>Where a history move would land, without moving.</summary>
    public async Task<string?> DriverHistoryTargetAsync(PaneTab tab, string direction)
    {
        if (tab.EngineTabId is not { } engineTab || tab.Owner.Session is not { } session)
        {
            return null;
        }

        return await session.HistoryTargetAsync(engineTab, direction);
    }

    public IReadOnlyList<PaneTabInfo> ListTabs() =>
        [.. _tabs.Select(t => new PaneTabInfo(t.Id, t.Url, t.Title, ReferenceEquals(t, _active), t.EngineTabId is not null))];

    /// <summary>Resolves a tab id ("tab_2"); null means the active tab.</summary>
    public PaneTab? FindTab(string? tabId) =>
        string.IsNullOrEmpty(tabId) ? _active : _tabs.FirstOrDefault(t => t.Id == tabId);

    private void OnNewTabClick(object sender, RoutedEventArgs e) => AddTab();

    /// <summary>The annotated page, for the composer to attach as an image.</summary>
    public event EventHandler<string>? PageAnnotated;

    private PreviewAnnotationOverlay? _annotation;

    /// <summary>
    /// The pencil on the toolbar: the page is captured as it stands and the drawing
    /// surface opens over it, the way the reference's preview annotation does.
    /// </summary>
    private async void OnAnnotateClick(object sender, RoutedEventArgs e)
    {
        if (AnnotateButton.IsChecked != true)
        {
            CloseAnnotation();
            return;
        }

        if (ActiveCdp() is not { } core)
        {
            AnnotateButton.IsChecked = false;
            ShowToast("Open a page first.");
            return;
        }

        try
        {
            var captured = await BrowserPaneCdp.CallAsync(core, "Page.captureScreenshot",
                new JsonObject { ["format"] = "png", ["captureBeyondViewport"] = false });
            using var stream = new MemoryStream(
                Convert.FromBase64String(captured["data"]?.GetValue<string>() ?? ""));
            stream.Position = 0;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            _annotation ??= BuildAnnotationOverlay();
            _annotation.Load(image);
            AnnotationHost.Visibility = Visibility.Visible;
            _annotation.Focus();
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException
                                       or InvalidOperationException or NotSupportedException)
        {
            AnnotateButton.IsChecked = false;
            ShowToast("Page couldn’t be saved. Try again.");
        }
    }

    private PreviewAnnotationOverlay BuildAnnotationOverlay()
    {
        var overlay = new PreviewAnnotationOverlay();
        overlay.Annotated += (_, path) => PageAnnotated?.Invoke(this, path);
        overlay.Closed += (_, _) => CloseAnnotation();
        AnnotationHost.Children.Add(overlay);
        return overlay;
    }

    private void CloseAnnotation()
    {
        AnnotationHost.Visibility = Visibility.Collapsed;
        AnnotateButton.IsChecked = false;
    }

    /// <summary>Ctrl+T: the reference's newPreviewTab command.</summary>
    public void NewTab() => AddTab();

    /// <summary>Ctrl+Shift+S: the reference's toggleSelectionMode — the element picker.</summary>
    public void ToggleElementPicker()
    {
        PickButton.IsChecked = PickButton.IsChecked != true;
        OnPickClick(PickButton, new RoutedEventArgs());
    }

    private PaneTab AddTab(bool activate = true, BrowserContext? owner = null)
    {
        owner ??= _context;
        var label = new TextBlock { Text = "New tab", VerticalAlignment = VerticalAlignment.Center };
        var closeGlyph = new System.Windows.Shapes.Path();
        closeGlyph.SetResourceReference(StyleProperty, "PanelSmallGlyph");
        closeGlyph.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "CloseGlyph");
        closeGlyph.Width = 8;
        closeGlyph.Height = 8;
        var close = new Button { Content = closeGlyph, Padding = new Thickness(3), Margin = new Thickness(5, 0, 0, 0) };
        close.SetResourceReference(StyleProperty, "PanelActionButton");
        close.Width = 16;
        close.Height = 16;
        close.ToolTip = "Close tab";
        close.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Close tab");

        var button = new ToggleButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { label, close },
            },
        };
        button.SetResourceReference(StyleProperty, "PanelTab");
        var tab = new PaneTab { Id = $"tab_{++owner.NextTabId}", Owner = owner, Button = button, Label = label };
        button.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, tab.Title);
        button.Click += (_, _) => Activate(tab);
        button.PreviewMouseDown += (_, args) =>
        {
            if (args.ChangedButton == MouseButton.Middle)
            {
                args.Handled = true;
                _ = CloseTabAsync(tab);
            }
        };
        close.Click += (_, args) =>
        {
            args.Handled = true;
            _ = CloseTabAsync(tab);
        };
        button.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            ShowTabMenu(tab);
        };
        owner.Tabs.Add(tab);
        if (ReferenceEquals(owner, _context)) Tabs.Items.Add(button);
        if (activate && ReferenceEquals(owner, _context))
        {
            Activate(tab);
        }

        return tab;
    }

    /// <summary>The tab's own menu: Close tab and the reference's Close other tabs.</summary>
    private void ShowTabMenu(PaneTab tab)
    {
        var menu = new ContextMenu { PlacementTarget = tab.Button, MinWidth = 170 };
        var close = new MenuItem { Header = "Close tab" };
        close.Click += (_, _) => _ = CloseTabAsync(tab);
        menu.Items.Add(close);
        if (_tabs.Count > 1)
        {
            var others = new MenuItem { Header = "Close other tabs" };
            others.Click += (_, _) => _ = CloseOtherTabsAsync(tab);
            menu.Items.Add(others);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// The servers a tab's page is serving from, asked of the host. The Browser pane has
    /// no server registry of its own, so the workspace answers this from PreviewServers.
    /// </summary>
    public Func<string, IReadOnlyList<Services.PreviewServerInfo>>? ServersForUrl { get; set; }

    /// <summary>Stops one dev server by id, when the close dialog's danger button says so.</summary>
    public Action<string>? StopPreviewServer { get; set; }

    /// <summary>
    /// The reference's guard before a tab that hosts a running dev server closes: which
    /// servers would be left without a window, and what the user wants done with them.
    /// Returns false when the user cancelled.
    /// </summary>
    private bool ConfirmClose(Services.BrowserCloseMode mode, IReadOnlyList<PaneTab> closing)
    {
        if (ServersForUrl is null)
        {
            return true;
        }

        var keptUrls = _tabs.Where(t => !closing.Contains(t)).Select(t => t.Url).ToList();
        var orphaned = new List<Services.PreviewServerInfo>();
        foreach (var tab in closing)
        {
            foreach (var server in ServersForUrl(tab.Url))
            {
                if (orphaned.Any(s => s.ServerId == server.ServerId))
                {
                    continue;
                }

                if (keptUrls.Any(url => ServersForUrl(url).Any(s => s.ServerId == server.ServerId)))
                {
                    continue;
                }

                orphaned.Add(server);
            }
        }

        if (orphaned.Count == 0)
        {
            return true;
        }

        var first = orphaned[0];
        var choice = BrowserCloseDialog.Ask(
            Window.GetWindow(this),
            mode,
            orphaned.Count,
            first.Name,
            Services.BrowserCloseDialogText.HostLabel(first.Port));
        if (choice == BrowserCloseChoice.Cancel)
        {
            return false;
        }

        if (choice == BrowserCloseChoice.StopServers && StopPreviewServer is { } stop)
        {
            foreach (var server in orphaned)
            {
                stop(server.ServerId);
            }
        }

        return true;
    }

    /// <summary>"Close other tabs", with the reference's own dialog when servers would stop.</summary>
    public async Task CloseOtherTabsAsync(PaneTab keep)
    {
        var closing = _tabs.Where(t => !ReferenceEquals(t, keep)).ToList();
        if (closing.Count == 0 || !ConfirmClose(Services.BrowserCloseMode.Others, closing))
        {
            return;
        }

        foreach (var tab in closing)
        {
            await CloseTabAsync(tab, confirmed: true);
        }

        Activate(keep);
    }

    /// <summary>
    /// Fronts a tab. Every tab keeps its own view in the engine, so switching
    /// only swaps which one is laid out — background tabs keep running.
    /// </summary>
    private void Activate(PaneTab tab)
    {
        _active = tab;
        foreach (var candidate in _tabs)
        {
            candidate.Button.IsChecked = ReferenceEquals(candidate, tab);
        }

        if (tab.EngineTabId is { } engineTab && tab.Owner.Session is { } session)
        {
            _ = session.SelectTabAsync(engineTab);
        }

        AddressBox.Text = tab.Url;
        UpdateChrome();
    }

    /// <summary>
    /// Drops a tab's view in the engine. The pane's own row is the caller's to
    /// remove; this is only the engine half, and is safe for a tab that never
    /// got one.
    /// </summary>
    private void ReleaseEngineTab(PaneTab tab)
    {
        tab.NavigationRequestVersion++;
        if (tab.EngineTabId is { } engineTab && tab.Owner.Session is { } session)
        {
            _ = session.CloseTabAsync(engineTab);
        }

        tab.EngineTabId = null;
    }

    public Task CloseTabAsync(PaneTab tab) => CloseTabAsync(tab, confirmed: false);

    private async Task CloseTabAsync(PaneTab tab, bool confirmed)
    {
        if (!_tabs.Contains(tab))
        {
            return;
        }

        if (!confirmed && !ConfirmClose(Services.BrowserCloseMode.Single, [tab]))
        {
            return;
        }

        Tabs.Items.Remove(tab.Button);
        _tabs.Remove(tab);
        ReleaseEngineTab(tab);

        if (_tabs.Count == 0)
        {
            // Closing the last tab closes the pane itself; a fresh empty tab
            // waits behind it for the next open.
            AddTab();
            LastTabClosed?.Invoke(this, EventArgs.Empty);
        }
        else if (ReferenceEquals(tab, _active))
        {
            Activate(_tabs[^1]);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Camera and microphone are refused outright in the pane, as they are in
    /// the reference. The engine does the refusing - it owns the page - and
    /// this is the notice the user sees the first time one is asked for.
    /// </summary>
    internal static string MediaRefusedNotice(string kind) =>
        $"This page asked for {kind} access, which is blocked in the Browser pane. "
        + "Open the page in your default browser to use it.";

    /// <summary>
    /// The clipboard changed while this tab was being driven. The reference separates the
    /// page's own write from the user's by classifying the dispatch's activation origin,
    /// which the engine does not expose — so this reports the unattributed case the
    /// reference reports when it cannot tell either, and never accuses the page.
    /// </summary>
    internal void NoteClipboardChange(PaneTab tab, uint? before)
    {
        if (!ClipboardWatch.Changed(before, ClipboardWatch.Token()))
        {
            return;
        }

        tab.ClipboardChanged = true;
        var now = DateTime.UtcNow;
        if (now - _lastClipboardToast < ClipboardToastInterval)
        {
            return;
        }

        _lastClipboardToast = now;
        ShowToast("Your clipboard changed during Jarvis's interaction. "
                  + "If you didn't just copy something yourself, check it before pasting.");
    }

    /// <summary>Whether this tab is one a page opened for itself.</summary>
    public bool IsPopup(PaneTab tab) =>
        tab.EngineTabId is { } id &&
        id.StartsWith(BrowserPanePopupGuard.PopupTabPrefix, StringComparison.Ordinal);

    /// <summary>Whether any tab in the pane is a page-opened popup.</summary>
    public bool HasLivePopup => _tabs.Any(IsPopup);

    /// <summary>
    /// The tab's navigation mark: its address and how many times it has moved,
    /// so two visits to the same URL still read as two navigations.
    /// </summary>
    public static string NavigationMark(PaneTab tab) => tab.NavigationCount + ":" + tab.Url;

    /// <summary>What the executed tab contributes to a tool result's Tab Context trailer.</summary>
    public PaneTabNotes NotesFor(PaneTab tab)
    {
        var notes = new PaneTabNotes(tab.Title, tab.Url, [.. tab.DeniedMediaKinds], tab.ClipboardChanged);
        tab.ClipboardChanged = false;
        return notes;
    }

    /// <summary>
    /// Releases this pane's window in the engine and every tab in it. Called
    /// when the app is going down, so nothing rebuilds behind it; a tab
    /// reopened after this would come back empty. The engine process itself is
    /// shared with the app's other surfaces and is ended by the app, not here.
    /// </summary>
    public void DisposeTabs()
    {
        if (_settings is not null) _settings.Saved -= OnBrowserSettingsSaved;
        foreach (var context in _browserContexts.Values.Append(_context).Distinct())
        {
            foreach (var tab in context.Tabs) tab.EngineTabId = null;
            if (context.Session is { } session) _ = session.DisposeAsync().AsTask();
            context.Session = null;
        }
    }

    private void UpdateChrome()
    {
        if (_serverStateShowing)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            WebHostBorder.Visibility = Visibility.Collapsed;
            ServerState.Visibility = Visibility.Visible;
            return;
        }

        ServerState.Visibility = Visibility.Collapsed;
        var hasPage = _active?.EngineTabId is not null && (_active?.Url.Length ?? 0) > 0;
        EmptyState.Visibility = hasPage ? Visibility.Collapsed : Visibility.Visible;
        WebHostBorder.Visibility = hasPage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTabHeader(PaneTab tab)
    {
        tab.Label.Text = tab.Title.Length > 22 ? tab.Title[..21] + "…" : tab.Title;
        tab.Button.ToolTip = tab.Title;
        tab.Button.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, tab.Title);
    }

    // ---- navigation ------------------------------------------------------------

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || AddressBox.Text.Trim().Length == 0)
        {
            return;
        }

        e.Handled = true;
        OpenUrl(AddressBox.Text.Trim());
    }

    /// <summary>Preview tooling and the open action: point the active tab at a URL.</summary>
    public void OpenUrl(string url) => _ = ObserveNavigationAsync(() => NavigateActiveAsync(url));

    private Task ObserveNavigationAsync(Func<Task> navigate) =>
        BrowserNavigationUi.ObserveAsync(navigate, ShowToast);

    /// <summary>
    /// The preview flow's entry: shows "Setting up preview" and waits for the
    /// dev server to answer before pointing the active tab at it.
    /// </summary>
    public void OpenPreviewUrl(string url) => _ = ObserveNavigationAsync(() => OpenPreviewUrlAsync(url));

    private async Task OpenPreviewUrlAsync(string url)
    {
        // A pane opened for a dev server is the one the reference is willing to
        // reap once it has been hidden long enough.
        _previewPort = Uri.TryCreate(url, UriKind.Absolute, out var previewUri) ? previewUri.Port : 0;
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            ShowServerWaiting();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; attempt < 15 && _serverStateShowing; attempt++)
            {
                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            if (!_serverStateShowing)
            {
                return; // an error state replaced the wait (server failed to start)
            }

            _serverStateShowing = false;
        }

        await NavigateActiveAsync(url);
    }

    private async Task NavigateActiveAsync(string address)
    {
        var tab = _active ?? AddTab();
        await NavigateTabAsync(tab, address);
    }

    /// <summary>Navigates one tab; "back"/"forward" move through its history.</summary>
    public async Task NavigateTabAsync(PaneTab tab, string address)
    {
        var version = ++tab.NavigationRequestVersion;
        void VerifyCurrent()
        {
            if (version != tab.NavigationRequestVersion)
                throw new BrowserNavigationSupersededException();
        }

        try
        {
            if (address is "back" or "forward")
            {
                if (tab.EngineTabId is { } engineTab && tab.Owner.Session is { } session)
                {
                    await session.HistoryAsync(engineTab, address);
                    VerifyCurrent();
                }

                return;
            }

            var url = Normalize(address);
            if (url is null)
            {
                ShowToast("That does not look like a URL.");
                return;
            }

            var ready = await EnsureTabReadyAsync(tab);
            VerifyCurrent();
            if (!ready) return;

            tab.Url = url;
            if (ReferenceEquals(tab, _active))
            {
                AddressBox.Text = url;
            }

            _serverStateShowing = false;
            UpdateChrome();
            var (_, downloaded) = await tab.Owner.Session!.NavigateReportingDownloadAsync(tab.EngineTabId!, url);
            VerifyCurrent();
            tab.LastNavigationDownloaded = downloaded;

            // Sending the tab somewhere else is what retires the pin; an older completion must
            // never unpin a newer local-file preview or overwrite its download status.
            if (!downloaded)
            {
                tab.PinnedFilePreview = false;
            }
        }
        catch (Exception ex) when (version != tab.NavigationRequestVersion
            && ex is not BrowserNavigationSupersededException
            && ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            throw new BrowserNavigationSupersededException(ex);
        }
    }

    /// <summary>Adds a scheme when the user typed a bare host, and rejects anything else.</summary>
    public static string? Normalize(string address)
    {
        var text = address.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "file")
            ? uri.ToString()
            : null;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => MoveHistory("back");

    private void OnForwardClick(object sender, RoutedEventArgs e) => MoveHistory("forward");

    /// <summary>The engine answers whether that end of the history had anything.</summary>
    private void MoveHistory(string direction)
    {
        if (_active?.EngineTabId is { } engineTab && _session is { } session)
        {
            _ = session.HistoryAsync(engineTab, direction);
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (ActiveCdp() is { } core)
        {
            _ = BrowserPaneCdp.CallAsync(core, "Page.reload", null);
        }
    }

    /// <summary>The active tab's DevTools endpoint, or null when it has no view yet.</summary>
    private IPaneCdp? ActiveCdp() =>
        _active?.EngineTabId is { } engineTab && _session is { } session ? session.Cdp(engineTab) : null;

    // ---- engine lifecycle ------------------------------------------------------

    /// <summary>
    /// Brings up the engine, reparents its window into this panel and wires the
    /// events a tab produces. Idempotent; every tab creation goes through it.
    /// </summary>
    private async Task<ElectronPaneSession?> EnsureSessionAsync(BrowserContext context)
    {
        if (context.Session is { } existing) return existing;
        var pending = context.Initializing ??= CreateSessionAsync(context);
        try { return await pending; }
        finally
        {
            if (ReferenceEquals(context.Initializing, pending)) context.Initializing = null;
        }
    }

    private async Task<ElectronPaneSession?> CreateSessionAsync(BrowserContext context)
    {
        ElectronEngine.UseProfile(_userDataFolder);
        var session = ElectronEngine.CreateSession(Dispatcher, context.Partition);

        try
        {
            var window = await session.EnsureHostWindowAsync(context.Partition.StartsWith("persist:", StringComparison.Ordinal));

            context.View ??= new ElectronPaneView();
            context.View.Visibility = ReferenceEquals(context, _context) ? Visibility.Visible : Visibility.Collapsed;
            if (!WebHost.Children.Contains(context.View))
            {
                WebHost.Children.Add(context.View);
            }

            context.View.Attach(window);

            // Reparenting shows the window at the Win32 level, but Electron
            // still believes it is hidden, and a hidden BrowserWindow produces
            // no frames: Page.captureScreenshot never answers and synthetic
            // clicks land nowhere. Telling the engine it is visible is what
            // makes the pane render and be clickable.
            await session.ShowAsync();
        }
        catch (Exception ex) when (ex is ElectronRuntimeUnavailableException or InvalidOperationException or IOException)
        {
            await session.DisposeAsync();
            ShowToast(ex is ElectronRuntimeUnavailableException
                ? ex.Message
                : "The Browser pane's engine could not be started.");
            return null;
        }

        session.TabTitleChanged += OnEngineTabTitle;
        session.TabNavigated += OnEngineTabNavigated;
        session.CdpEvent += OnEngineCdpEvent;
        session.NewWindowRequested += OnEngineNewWindow;
        session.MediaDenied += OnEngineMediaDenied;
        session.ContextMenuRequested += OnEngineContextMenu;

        context.Session = session;
        return session;
    }

    private PaneTab? TabForEngine(string engineTabId) =>
        _browserContexts.Values.Append(_context).Distinct()
            .SelectMany(static c => c.Tabs).FirstOrDefault(t => t.EngineTabId == engineTabId);

    private void OnEngineTabTitle(string engineTabId, string title)
    {
        if (TabForEngine(engineTabId) is not { } tab)
        {
            return;
        }

        tab.Title = title.Length == 0 ? "New tab" : title;
        UpdateTabHeader(tab);
    }

    private void OnEngineTabNavigated(string engineTabId, string url)
    {
        if (TabForEngine(engineTabId) is not { } tab)
        {
            return;
        }

        tab.NavigationCount++;

        // about:blank is the document a fresh view is given so it can answer CDP
        // at all; it is not somewhere the user went.
        tab.Url = url == "about:blank" ? "" : url;
        if (BrowserPaneDomainTransitions.CommittedOrigin(tab.Url) is { } external)
        {
            tab.LastExternalOrigin = external;
        }

        if (ReferenceEquals(tab, _active))
        {
            AddressBox.Text = tab.Url;
        }

        UpdateChrome();
    }

    private void OnEngineCdpEvent(string engineTabId, string method, JsonObject parameters)
    {
        if (TabForEngine(engineTabId) is not { } tab)
        {
            return;
        }

        if (method == "Runtime.bindingCalled" &&
            parameters["name"]?.GetValue<string>() == PickBinding)
        {
            OnPickedElement(parameters["payload"]?.GetValue<string>() ?? "");
            return;
        }

        if (method == "Page.javascriptDialogOpening" &&
            DialogSuppressedNotice(parameters) is { } suppressed)
        {
            tab.ConsoleEntries.Add(new JsonObject
            {
                ["level"] = "warn",
                ["text"] = suppressed,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            return;
        }

        BrowserPaneCapture.Apply(method, parameters, tab.ConsoleEntries, tab.Network, tab.NetOrder);
        if (BrowserPaneCapture.IsMainFrameNavigation(method, parameters))
        {
            // A committed navigation retires the pending notice, as it does in
            // the reference: it described the page that has just gone.
            tab.ClipboardChanged = false;
        }
    }

    /// <summary>
    /// The console warning a suppressed page dialog leaves, the reference's own:
    /// native dialogs are off in this browser, so the page's call came straight
    /// back and the line says what it came back with.
    /// </summary>
    internal static string? DialogSuppressedNotice(JsonObject parameters)
    {
        var kind = parameters["type"]?.GetValue<string>() ?? "unknown";
        var reason = kind switch
        {
            "confirm" => "confirm() returned false to the page",
            "prompt" => "prompt() returned null to the page",
            "alert" => "the call returned immediately",
            _ => null,
        };
        if (reason is null)
        {
            // beforeunload is the fourth kind and is not a dialog the page asked
            // to show, so the reference says nothing about it.
            return null;
        }

        var message = parameters["message"]?.GetValue<string>() ?? "";
        if (message.Length > 200)
        {
            message = message[..200];
        }

        return $"[Jarvis browser] Page dialog suppressed ({kind}): " +
               System.Text.Json.JsonSerializer.Serialize(message) +
               " — native JavaScript dialogs are disabled in this browser; " + reason + ".";
    }

    private void OnEngineMediaDenied(string engineTabId, string kind)
    {
        if (TabForEngine(engineTabId) is not { } tab || !tab.DeniedMediaKinds.Add(kind))
        {
            // Already refused for this page; the notice was shown the first time.
            return;
        }

        ShowToast(MediaRefusedNotice(
            tab.DeniedMediaKinds.Count == 1 ? kind : "camera and microphone"));
    }

    /// <summary>
    /// Creates the tab's view in the engine on first use, turns on the CDP
    /// domains the capture buffers listen to, and applies the pane's colour
    /// scheme. Concurrent callers share one initialization.
    /// </summary>
    public async Task<bool> EnsureTabReadyAsync(PaneTab tab, bool popup = false)
    {
        if (tab.EngineTabId is not null)
        {
            return true;
        }

        if (tab.Initializing is { } pending)
        {
            await pending;
            return tab.EngineTabId is not null;
        }

        var initialization = InitializeTabAsync(tab, popup);
        tab.Initializing = initialization;
        try
        {
            return await initialization;
        }
        finally
        {
            tab.Initializing = null;
        }
    }

    private async Task<bool> InitializeTabAsync(PaneTab tab, bool popup = false)
    {
        if (await EnsureSessionAsync(tab.Owner) is not { } session)
        {
            return false;
        }

        try
        {
            tab.EngineTabId = await session.CreateTabAsync(ReferenceEquals(tab, _active), popup);
            var cdp = session.Cdp(tab.EngineTabId);
            await BrowserPaneCdp.EnableCaptureAsync(cdp);
            await BrowserPaneCdp.CallAsync(cdp, "Runtime.addBinding",
                new JsonObject { ["name"] = PickBinding });
            await ApplyColorSchemeAsync(cdp);
            return true;
        }
        catch (InvalidOperationException)
        {
            tab.EngineTabId = null;
            ShowToast("The Browser pane's engine could not open a tab.");
            return false;
        }
    }

    /// <summary>target=_blank and window.open land in a new pane tab (or the system browser).</summary>
    private void OnEngineNewWindow(string engineTabId, string url, bool popup) =>
        _ = ObserveNavigationAsync(() => OpenEngineNewWindowAsync(engineTabId, url, popup));

    private async Task OpenEngineNewWindowAsync(string engineTabId, string url, bool popup)
    {
        // Events from a parked session must not attach its sign-in popup to
        // the foreground session's jar or steal the user's current tabs.
        if (TabForEngine(engineTabId) is not { } opener) return;
        if (_settings?.Current.BrowserOpenLinksInPanel == false)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowToast("Could not open the link.");
            }

            return;
        }

        var tab = AddTab(owner: opener.Owner);
        if (await EnsureTabReadyAsync(tab, popup))
        {
            await NavigateTabAsync(tab, url);
        }
    }

    /// <summary>
    /// The pane's colour scheme, applied the way the engine takes it: emulating
    /// prefers-color-scheme on the page rather than setting a profile property.
    /// </summary>
    private Task ApplyColorSchemeAsync(IPaneCdp core) =>
        BrowserPaneCdp.SetEmulatedColorSchemeAsync(core, _settings?.Current.BrowserColorScheme switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "",
        });

    // ---- driver surface (Services/BrowserPaneDriver.cs calls these on the UI thread) ----

    /// <summary>navigate: the active tab (or tabId's) goes to the URL; the pane opens if needed.</summary>
    public async Task<string> DriverNavigateAsync(string? tabId, string url)
    {
        var tab = FindTab(tabId) ?? throw new InvalidOperationException($"No tab {tabId}.");
        await NavigateTabAsync(tab, url);
        return tab.Id;
    }

    public async Task<string> DriverCreateTabAsync(string? url, bool foreground)
    {
        var tab = AddTab(activate: foreground);
        if (url is not null)
        {
            await NavigateTabAsync(tab, url);
        }

        return tab.Id;
    }

    public void DriverSelectTab(PaneTab tab) => Activate(tab);

    /// <summary>Returns the tab's DevTools endpoint, creating its engine view if needed.</summary>
    public async Task<IPaneCdp> DriverCoreAsync(PaneTab tab)
    {
        if (!await EnsureTabReadyAsync(tab))
        {
            throw new InvalidOperationException("The Browser pane's engine could not open a tab.");
        }

        return tab.Owner.Session!.Cdp(tab.EngineTabId!);
    }

    // ---- toolbar actions -------------------------------------------------------

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        if (ActiveCdp() is not { } core)
        {
            PickButton.IsChecked = false;
            ShowToast("Open a page first.");
            return;
        }

        _ = BrowserPaneCdp.EvaluateAsync(core, PickButton.IsChecked != true
            ? "window.__jarvisPickCancel && window.__jarvisPickCancel()"
            : PickScript, replMode: false);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        var context = _context;
        ShowConfirm(
            "Clear browsing data?",
            "This will delete all saved cookies, local storage, and other session data.",
            "Clear data",
            async () =>
            {
                if (context.Session is not { } session)
                {
                    return;
                }

                await session.ClearBrowsingDataAsync();

                // The pane's isolation surface is what the origin policy's own
                // state hangs off, so clearing one clears the other: no tab has
                // committed a site any more, and nothing the session consented
                // to survives (the reference's resetPreviewOriginPolicyState).
                foreach (var tab in context.Tabs)
                {
                    tab.LastExternalOrigin = null;
                }

                BrowsingDataCleared?.Invoke(this, EventArgs.Empty);
                ShowToast("Browsing data cleared.");
            });
    }

    // ---- pane menu -------------------------------------------------------------

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuButton, Placement = PlacementMode.Bottom };

        var screenshot = new MenuItem { Header = "Save screenshot" };
        screenshot.Click += async (_, _) => await SaveScreenshotAsync();
        menu.Items.Add(screenshot);

        var logs = new MenuItem { Header = "Show dev server logs" };
        logs.Click += (_, _) => ShowDevServerLogsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(logs);

        menu.Items.Add(new Separator());

        var scheme = new MenuItem { Header = "Color scheme" };
        foreach (var (label, value) in new[] { ("Light", "light"), ("Dark", "dark"), ("System", "system") })
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = (_settings?.Current.BrowserColorScheme ?? "system") == value,
            };
            var chosen = value;
            item.Click += (_, _) => SetColorScheme(chosen);
            scheme.Items.Add(item);
        }

        menu.Items.Add(scheme);

        var viewport = new MenuItem { Header = "Viewport" };
        foreach (var (label, width, height, mobile) in new (string, int, int, bool)[]
                 {
                     ("Responsive", 0, 0, false), ("Mobile", 375, 812, true), ("Tablet", 768, 1024, false),
                 })
        {
            var item = new MenuItem { Header = label };
            var (w, h, m) = (width, height, mobile);
            item.Click += async (_, _) => await ApplyViewportAsync(w, h, m);
            viewport.Items.Add(item);
        }

        menu.Items.Add(viewport);
        menu.Items.Add(new Separator());

        var openLinks = new MenuItem
        {
            Header = "Open links in Browser panel",
            IsCheckable = true,
            IsChecked = _settings?.Current.BrowserOpenLinksInPanel ?? true,
        };
        openLinks.Click += (_, _) =>
        {
            if (_settings is not null)
            {
                _settings.Current.BrowserOpenLinksInPanel = openLinks.IsChecked;
                _settings.Save();
            }
        };
        menu.Items.Add(openLinks);

        var persist = new MenuItem
        {
            Header = "Persist sessions",
            IsCheckable = true,
            IsChecked = _settings?.Current.BrowserPersistSessions ?? false,
        };
        persist.Click += (_, _) =>
        {
            if (_settings is not null)
            {
                _settings.Current.BrowserPersistSessions = persist.IsChecked;
                // Settings › Jarvis Code › Browser keeps the same choice as the
                // reference's three-way "Persist sessions"; the pane's own toggle is
                // its Don't keep / Shared ends, so keep the two in step.
                _settings.Current.BrowserPreviewStorage = persist.IsChecked ? "shared" : "none";
                _settings.Save();
                ShowToast(persist.IsChecked
                    ? "Session data such as cookies and local storage will be saved across restarts."
                    : "New Browser tabs use temporary cookies and local storage. Saved profiles are kept.");
            }
        };
        menu.Items.Add(persist);

        var autoVerifyOn = PreviewServers.ReadAutoVerify(_workingDirectory);
        var autoVerify = new MenuItem { Header = autoVerifyOn ? "Disable auto verify" : "Enable auto verify" };
        autoVerify.Click += (_, _) => ToggleAutoVerify(!autoVerifyOn);
        menu.Items.Add(autoVerify);

        menu.Items.Add(new Separator());

        var clear = new MenuItem { Header = "Clear browsing data…" };
        clear.Click += (_, _) => OnClearClick(this, new RoutedEventArgs());
        menu.Items.Add(clear);

        menu.IsOpen = true;
    }

    private async Task SaveScreenshotAsync()
    {
        if (ActiveCdp() is not { } core)
        {
            ShowToast("Open a page first.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            Filter = "PNG image|*.png",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var captured = await BrowserPaneCdp.CallAsync(core, "Page.captureScreenshot",
                new JsonObject { ["format"] = "png" });
            await File.WriteAllBytesAsync(
                dialog.FileName, Convert.FromBase64String(captured["data"]?.GetValue<string>() ?? ""));
            ShowToast("Screenshot saved.");
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowToast("Page couldn’t be saved. Try again.");
        }
    }

    private void SetColorScheme(string value)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Current.BrowserColorScheme = value;
        _settings.Save();
        // The engine emulates prefers-color-scheme per page, so every open tab
        // is re-applied rather than one standing in for a profile-wide setting.
        foreach (var tab in _tabs)
        {
            if (tab.EngineTabId is { } engineTab && _session is { } session)
            {
                _ = ApplyColorSchemeAsync(session.Cdp(engineTab));
            }
        }
    }

    private async Task ApplyViewportAsync(int width, int height, bool mobile)
    {
        if (ActiveCdp() is not { } core)
        {
            ShowToast("Open a page first.");
            return;
        }

        try
        {
            if (width == 0)
            {
                await BrowserPaneCdp.ClearViewportAsync(core);
            }
            else
            {
                var fit = WebHost.ActualWidth > 0 && width > WebHost.ActualWidth
                    ? WebHost.ActualWidth / width
                    : (double?)null;
                await BrowserPaneCdp.SetViewportAsync(core, width, height, mobile, fit);
            }
        }
        catch (InvalidOperationException ex)
        {
            ShowToast(ex.Message);
        }
    }

    /// <summary>
    /// The reference pane re-syncs each tab's prefers-color-scheme emulation to
    /// the app's light/dark theme when the theme changes or the pane reopens; a
    /// resize_window colorScheme override lasts until then.
    /// </summary>
    private async void ResyncColorSchemes()
    {
        var mode = _settings?.Current.ThemeMode;
        if (mode is not (JarvisCode.App.Theming.ThemeMode.Light or JarvisCode.App.Theming.ThemeMode.Dark))
        {
            return;
        }

        var scheme = mode == JarvisCode.App.Theming.ThemeMode.Dark ? "dark" : "light";
        foreach (var tab in _tabs)
        {
            if (tab.EngineTabId is { } engineTab && _session is { } session)
            {
                try
                {
                    await BrowserPaneCdp.SetEmulatedColorSchemeAsync(session.Cdp(engineTab), scheme);
                }
                catch (InvalidOperationException)
                {
                    // a tab mid-navigation just keeps its scheme until the next sync
                }
            }
        }
    }

    private void ToggleAutoVerify(bool enable)
    {
        ShowConfirm(
            enable ? "Enable auto verify" : "Disable auto verify",
            enable
                ? "After editing code, Jarvis will automatically start the preview server, verify changes, and " +
                  "share proof before completing its response. This setting is saved in .jarvis/launch.json for " +
                  "this project."
                : "Jarvis will no longer automatically verify code changes using the preview. You can still ask " +
                  "Jarvis to verify manually. This setting is saved in .jarvis/launch.json for this project.",
            enable ? "Enable" : "Disable",
            () =>
            {
                try
                {
                    PreviewServers.WriteAutoVerify(_workingDirectory, enable);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    ShowToast($"Could not update launch.json: {ex.Message}");
                }
            });
    }

    // ---- confirm overlay -------------------------------------------------------

    private void ShowConfirm(string title, string body, string primary, Action action)
    {
        ConfirmTitle.Text = title;
        ConfirmBody.Text = body;
        ConfirmPrimaryText.Text = primary;
        ConfirmPrimaryButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, primary);
        _confirmAction = action;
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void OnConfirmCancel(object sender, RoutedEventArgs e)
    {
        _confirmAction = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnConfirmPrimary(object sender, RoutedEventArgs e)
    {
        var action = _confirmAction;
        _confirmAction = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        action?.Invoke();
    }

    // ---- dev-server detect flow ------------------------------------------------

    /// <summary>The resting empty state: "Preview your app instead? Detect dev server".</summary>
    private void ShowDetectIdle()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(MutedText("Preview your app instead?"));
        row.Children.Add(LinkButton("Detect dev server", () => _ = DetectDevServerAsync()));
        var stack = new StackPanel();
        stack.Children.Add(row);
        var html = LinkButton("Preview HTML", PreviewHtml);
        html.HorizontalAlignment = HorizontalAlignment.Center;
        html.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(html);
        DetectArea.Content = stack;
    }

    private async Task DetectDevServerAsync()
    {
        // An existing launch.json wins: its first configuration is the project's server.
        if (PreviewServers.FindLaunchFile(_workingDirectory) is { } launchFile)
        {
            try
            {
                var existing = PreviewServers.ParseLaunchFile(await File.ReadAllTextAsync(launchFile)).FirstOrDefault();
                if (existing is not null)
                {
                    RequestPreview(existing);
                    return;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                DetectArea.Content = MutedText($"Could not read launch.json: {ex.Message}");
                return;
            }
        }

        DetectArea.Content = MutedText("Reading your project files. This can take a moment.");
        var candidate = await Task.Run(() => DevServerDetector.Scan(_workingDirectory));
        if (candidate is null)
        {
            var none = new StackPanel();
            none.Children.Add(MutedText("Jarvis looked and found no dev server in this project."));
            var html = LinkButton("Preview HTML", PreviewHtml);
            html.HorizontalAlignment = HorizontalAlignment.Center;
            html.Margin = new Thickness(0, 8, 0, 0);
            none.Children.Add(html);
            DetectArea.Content = none;
            return;
        }

        var card = new StackPanel();
        card.Children.Add(MutedText("Dev server detected. This command runs in your shell when the preview starts."));
        var command = new TextBox
        {
            Text = candidate.CommandLine,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        command.SetResourceReference(ForegroundProperty, "Text200Brush");
        card.Children.Add(command);
        var use = LinkButton("Use this", () =>
        {
            try
            {
                DevServerDetector.WriteLaunchFile(_workingDirectory, candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DetectArea.Content = MutedText($"Could not write launch.json: {ex.Message}");
                return;
            }

            RequestPreview(candidate);
        });
        use.HorizontalAlignment = HorizontalAlignment.Center;
        use.Margin = new Thickness(0, 10, 0, 0);
        card.Children.Add(use);
        var note = MutedText("Your choice is saved to .jarvis/launch.json in this project.");
        note.Margin = new Thickness(0, 8, 0, 0);
        note.FontSize = 11;
        card.Children.Add(note);
        DetectArea.Content = card;
    }

    private void RequestPreview(LaunchConfiguration configuration)
    {
        _lastConfiguration = configuration;
        StartPreviewRequested?.Invoke(this, configuration);
    }

    /// <summary>"Preview HTML": open a local HTML file (file://) in the pane.</summary>
    private void PreviewHtml()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "HTML files|*.html;*.htm",
            InitialDirectory = Directory.Exists(_workingDirectory) ? _workingDirectory : "",
        };
        if (dialog.ShowDialog() == true)
        {
            _ = ObserveNavigationAsync(() => PreviewFileAsync(new Uri(dialog.FileName).AbsoluteUri));
        }
    }

    private async Task PreviewFileAsync(string url)
    {
        var tab = _active ?? AddTab();
        var navigation = NavigateTabAsync(tab, url);
        var version = tab.NavigationRequestVersion;
        await navigation;
        if (version == tab.NavigationRequestVersion && !tab.LastNavigationDownloaded)
            tab.PinnedFilePreview = true;
    }

    // ---- dev-server states -----------------------------------------------------

    private void ShowServerWaiting()
    {
        _serverStateShowing = true;
        ServerStateTitle.Text = "Setting up preview";
        ServerStateBody.Text = "";
        ServerLogBorder.Visibility = Visibility.Collapsed;
        ServerStateButtons.Children.Clear();
        ServerStateNote.Visibility = Visibility.Collapsed;
        UpdateChrome();
    }

    /// <summary>The reference's dev-server error state; the caller has already sent the log to Jarvis.</summary>
    public void ShowServerError(string name, string logTail)
    {
        _serverStateShowing = true;
        ServerStateTitle.Text = "Dev server failed to start";
        ServerStateBody.Text = name;
        ServerLogBox.Text = logTail;
        ServerLogBorder.Visibility = logTail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerStateButtons.Children.Clear();
        ServerStateButtons.Children.Add(LinkButton("Copy error log", () =>
        {
            try
            {
                Clipboard.SetText(logTail);
                ShowToast("Copied");
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // clipboard briefly held by another process
            }
        }));
        ServerStateButtons.Children.Add(LinkButton("Try again", () =>
        {
            if (_lastConfiguration is not null)
            {
                RequestPreview(_lastConfiguration);
            }
        }));
        ServerStateButtons.Children.Add(LinkButton("Back to browsing", () =>
        {
            _serverStateShowing = false;
            UpdateChrome();
        }));
        ServerStateNote.Text = "Error details were sent to Jarvis";
        ServerStateNote.Visibility = Visibility.Visible;
        UpdateChrome();
    }

    private TextBlock MutedText(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return block;
    }

    private Button LinkButton(string label, Action onClick)
    {
        var text = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.Medium };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        var button = new Button { Content = text, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "RowButton");
        button.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---- capture / thumbnails --------------------------------------------------

    /// <summary>
    /// Captures the tab showing (a page of) the given URL as a PNG — the inline
    /// preview card's thumbnail. Null when nothing shows it or the engine is not up.
    /// </summary>
    public async Task<string?> TryCapturePreviewAsync(string urlPrefix, string outputPath)
    {
        var prefix = urlPrefix.TrimEnd('/');
        var showing = _tabs.FirstOrDefault(t =>
            t.EngineTabId is not null && t.Url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (showing?.EngineTabId is not { } engineTab || _session is not { } session)
        {
            return null;
        }

        try
        {
            var captured = await BrowserPaneCdp.CallAsync(
                session.Cdp(engineTab), "Page.captureScreenshot", new JsonObject { ["format"] = "png" });
            await File.WriteAllBytesAsync(
                outputPath, Convert.FromBase64String(captured["data"]?.GetValue<string>() ?? ""));
            return outputPath;
        }
        catch (Exception ex) when (ex is IOException or FormatException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---- element picker --------------------------------------------------------

    /// <summary>Ctrl+Shift+S: arms or disarms the picker, as its toolbar button does.</summary>
    public void TogglePicker() => PickButton.IsChecked = PickButton.IsChecked != true;

    /// <summary>Ctrl+T: a new pane tab, as its "+" does.</summary>
    public void OpenNewTab() => AddTab();


    /// <summary>
    /// The picked element arrives as a Runtime.bindingCalled event: the engine
    /// exposes window.__jarvisPicked on the page, and the picker script calls
    /// it. WebView2's window.chrome.webview bridge has no counterpart here, and
    /// a binding is what CDP offers in its place.
    /// </summary>
    internal const string PickBinding = "__jarvisPicked";

    private void OnPickedElement(string payload)
    {
        PickButton.IsChecked = false;
        string selector, tag, text, html, url;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("selector", out var value) || value.GetString() is not { } picked)
            {
                return;
            }

            selector = picked;
            string Read(string name) =>
                document.RootElement.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            tag = Read("tag");
            text = Read("text");
            html = Read("html");
            url = Read("url");
        }
        catch (JsonException)
        {
            return;
        }

        var context = ElementContext(selector, tag, text, html, url);
        ElementPicked?.Invoke(this, context);
        ShowToast($"Attached <{(tag.Length > 0 ? tag : "element")}> as context");
    }

    /// <summary>The context payload for a picked element: what it is, where, and its markup.</summary>
    internal static string ElementContext(string selector, string tag, string text, string html, string url)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("Selected element on ").AppendLine(url.Length > 0 ? url : "the page");
        builder.Append("On a “").Append(tag.Length > 0 ? tag : "?").Append("” element: ").AppendLine(selector);
        if (text.Length > 0)
        {
            builder.Append("Text: ").AppendLine(text);
        }

        if (html.Length > 0)
        {
            builder.AppendLine("```html").AppendLine(html).Append("```");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Element picker: the next click reports its target's selector instead of activating it.
    /// </summary>
    private const string PickScript = """
        (() => {
          const selector = el => {
            if (el.id) { return '#' + CSS.escape(el.id); }
            const parts = [];
            while (el && el.nodeType === 1 && parts.length < 4) {
              let part = el.tagName.toLowerCase();
              if (el.classList.length) {
                part += '.' + [...el.classList].slice(0, 2).map(c => CSS.escape(c)).join('.');
              }
              parts.unshift(part);
              el = el.parentElement;
            }
            return parts.join(' > ');
          };
          const handler = event => {
            event.preventDefault();
            event.stopPropagation();
            window.__jarvisPickCancel();
            const el = event.target;
            window.__jarvisPicked(JSON.stringify({
              selector: selector(el),
              tag: el.tagName.toLowerCase(),
              text: (el.innerText || el.value || '').trim().slice(0, 400),
              html: (el.outerHTML || '').slice(0, 1200),
              url: location.href,
            }));
          };
          window.__jarvisPickCancel = () => {
            document.removeEventListener('click', handler, true);
            document.body.style.cursor = '';
          };
          document.addEventListener('click', handler, true);
          document.body.style.cursor = 'crosshair';
        })();
        """;
}
