using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace JarvisCode.App.Services;

/// <summary>
/// The Browser pane's engine, as the pane's view uses it: one host window to
/// reparent, tabs to open and drive, and the events a tab produces raised on
/// the UI thread.
///
/// This is the counterpart of what the pane used to get from a WebView2 per
/// tab. Everything that was a control event there (title, address, new window,
/// a refused camera request) is an engine event here, and everything that was
/// <c>CallDevToolsProtocolMethodAsync</c> is <see cref="Cdp"/>.
/// </summary>
public sealed class ElectronPaneSession : IAsyncDisposable
{
    private readonly ElectronPaneHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly bool _ownsHost;
    private string? _storagePartition;
    private IntPtr _hostWindow;
    private string? _windowId;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _tabIds = new();

    /// <param name="ownsHost">
    /// False for a session over the app's shared engine: disposing it closes
    /// this surface's window and unsubscribes, but leaves the process running
    /// for the other surfaces.
    /// </param>
    public ElectronPaneSession(
        ElectronPaneHost host, Dispatcher dispatcher, bool ownsHost = true, string? storagePartition = null)
    {
        _host = host;
        _dispatcher = dispatcher;
        _ownsHost = ownsHost;
        _storagePartition = storagePartition;
        _host.EventReceived += OnEngineEvent;
    }

    /// <summary>A tab's document title changed.</summary>
    public event Action<string, string>? TabTitleChanged;

    /// <summary>A tab committed a navigation: tab id and the address it is on.</summary>
    public event Action<string, string>? TabNavigated;

    /// <summary>One DevTools event from a tab: tab id, method, parameters.</summary>
    public event Action<string, string, JsonObject>? CdpEvent;

    /// <summary>
    /// The page asked to open a url in a new window. The flag says whether it
    /// opened one for itself — a sign-in popup — rather than asking for a tab.
    /// </summary>
    public event Action<string, string, bool>? NewWindowRequested;

    /// <summary>The pane refused a camera or microphone request on this tab.</summary>
    public event Action<string, string>? MediaDenied;

    /// <summary>A context-menu click, with Electron's own parameters for it.</summary>
    public event Action<string, JsonObject>? ContextMenuRequested;

    /// <summary>The engine process ended on its own.</summary>
    public event Action<int>? EngineExited;

    public ElectronPaneHost Host => _host;

    /// <summary>The engine's Chromium, once it has connected. Null before that.</summary>
    public string? ChromiumVersion => _host.ChromiumVersion;

    private void OnEngineEvent(ElectronPaneEvent message)
    {
        var eventTab = message.Body["tabId"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(eventTab) && !_tabIds.ContainsKey(eventTab)) return;
        // The reader runs off the UI thread; everything the view listens to has
        // to arrive on it.
        _dispatcher.BeginInvoke(() =>
        {
            var tabId = message.Body["tabId"]?.GetValue<string>() ?? "";
            switch (message.Name)
            {
                case "tab-title":
                    TabTitleChanged?.Invoke(tabId, message.Body["title"]?.GetValue<string>() ?? "");
                    break;

                case "tab-navigated":
                    TabNavigated?.Invoke(tabId, message.Body["url"]?.GetValue<string>() ?? "");
                    break;

                case "tab-open-request":
                    NewWindowRequested?.Invoke(
                        tabId,
                        message.Body["url"]?.GetValue<string>() ?? "",
                        message.Body["popup"]?.GetValue<bool>() == true);
                    break;

                case "tab-permission-denied":
                    MediaDenied?.Invoke(tabId, message.Body["permission"]?.GetValue<string>() ?? "");
                    break;

                case "tab-context-menu":
                    if (message.Body["params"] is JsonObject menu)
                    {
                        ContextMenuRequested?.Invoke(tabId, menu);
                    }

                    break;

                case "cdp":
                    if (message.Body["params"] is JsonObject parameters &&
                        message.Body["method"]?.GetValue<string>() is { } method)
                    {
                        CdpEvent?.Invoke(tabId, method, parameters);
                    }

                    break;
            }
        });
    }

    /// <summary>
    /// Starts the engine, creates the host window and hands back its HWND for
    /// the view to reparent. Idempotent: the window is created once.
    /// </summary>
    public async Task<IntPtr> EnsureHostWindowAsync(
        bool persistSessions,
        IProgress<ElectronRuntimeProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool offscreen = false)
    {
        await _host.StartAsync(progress, cancellationToken).ConfigureAwait(false);

        // A transient surface gets an in-memory jar, never a destructive
        // clear of the app's default jar. Browser panes supply their measured
        // shared/session/workspace partition; legacy persistent consumers keep
        // the default profile so their existing logins remain available.
        _storagePartition ??= persistSessions ? "" : "jarvis-transient-" + Guid.NewGuid().ToString("N");

        if (_hostWindow != IntPtr.Zero)
        {
            return _hostWindow;
        }

        var created = await _host
            .RequestAsync("host.create", new JsonObject
            {
                ["show"] = false,
                ["partition"] = _storagePartition,
                ["x"] = offscreen ? -10000 : null,
                ["y"] = offscreen ? -10000 : null,
            }, cancellationToken)
            .ConfigureAwait(false);

        _windowId = created["windowId"]?.GetValue<string>();
        var handle = created["hwnd"]?.GetValue<string>();
        if (!ulong.TryParse(handle, out var value) || value == 0)
        {
            throw new ElectronRuntimeUnavailableException(
                "The Browser pane's engine did not produce a window to show.");
        }

        _hostWindow = (IntPtr)(long)value;
        return _hostWindow;
    }

    /// <summary>This session's window in the engine, which several may share.</summary>
    private JsonObject Window() => new() { ["windowId"] = _windowId };

    /// <summary>Clears only this window's jar, leaving other accounts and sessions alone.</summary>
    public Task ClearBrowsingDataAsync(CancellationToken cancellationToken = default) =>
        _host.RequestAsync("session.clear", Window(), cancellationToken);

    public Task ShowAsync(CancellationToken cancellationToken = default) =>
        _host.RequestAsync("host.show", Window(), cancellationToken);

    public Task HideAsync(CancellationToken cancellationToken = default) =>
        _host.RequestAsync("host.hide", Window(), cancellationToken);

    /// <summary>Re-lays the active view out after the host window was moved or resized.</summary>
    public Task LayoutAsync(CancellationToken cancellationToken = default) =>
        _host.RequestAsync("host.layout", Window(), cancellationToken);

    public async Task<string> CreateTabAsync(
        bool foreground, bool popup = false, CancellationToken cancellationToken = default)
    {
        var created = await _host
            .RequestAsync(
                "tab.create",
                new JsonObject { ["windowId"] = _windowId, ["foreground"] = foreground, ["popup"] = popup },
                cancellationToken)
            .ConfigureAwait(false);

        var tabId = created["tabId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The Browser pane's engine created no tab.");
        _tabIds.TryAdd(tabId, 0);
        return tabId;
    }

    public Task SelectTabAsync(string tabId, CancellationToken cancellationToken = default) =>
        _host.RequestAsync("tab.select", new JsonObject { ["tabId"] = tabId }, cancellationToken);

    public async Task<(bool Found, bool WasLast)> CloseTabAsync(
        string tabId, CancellationToken cancellationToken = default)
    {
        var closed = await _host
            .RequestAsync("tab.close", new JsonObject { ["tabId"] = tabId }, cancellationToken)
            .ConfigureAwait(false);
        _tabIds.TryRemove(tabId, out _);

        return (closed["found"]?.GetValue<bool>() == true, closed["wasLast"]?.GetValue<bool>() == true);
    }

    public async Task<string> NavigateAsync(string tabId, string url, CancellationToken cancellationToken = default)
    {
        var (address, _) = await NavigateReportingDownloadAsync(tabId, url, cancellationToken).ConfigureAwait(false);
        return address;
    }

    /// <summary>
    /// Navigates, and says whether the address turned out to be a file the
    /// browser downloaded instead of a page it loaded.
    /// </summary>
    public async Task<(string Url, bool Downloaded)> NavigateReportingDownloadAsync(
        string tabId, string url, CancellationToken cancellationToken = default)
    {
        var navigated = await _host
            .RequestAsync("tab.navigate", new JsonObject { ["tabId"] = tabId, ["url"] = url }, cancellationToken)
            .ConfigureAwait(false);

        return (navigated["url"]?.GetValue<string>() ?? url,
                navigated["downloaded"]?.GetValue<bool>() == true);
    }

    /// <summary>
    /// Where a history move would land, without moving — null when that end of
    /// the history is empty. Consent is asked for before the move, so where it
    /// is going has to be known first.
    /// </summary>
    public async Task<string?> HistoryTargetAsync(
        string tabId, string direction, CancellationToken cancellationToken = default)
    {
        var target = await _host
            .RequestAsync(
                "tab.historyTarget",
                new JsonObject { ["tabId"] = tabId, ["direction"] = direction },
                cancellationToken)
            .ConfigureAwait(false);

        return target["url"]?.GetValue<string>();
    }

    /// <summary>A history move; false when that end of the history is empty.</summary>
    public async Task<bool> HistoryAsync(string tabId, string direction, CancellationToken cancellationToken = default)
    {
        var moved = await _host
            .RequestAsync("tab.history", new JsonObject { ["tabId"] = tabId, ["direction"] = direction }, cancellationToken)
            .ConfigureAwait(false);

        return moved["moved"]?.GetValue<bool>() == true;
    }

    /// <summary>The tab's live title and address, straight from the engine.</summary>
    public async Task<(string Title, string Url)> NotesAsync(string tabId, CancellationToken cancellationToken = default)
    {
        var notes = await _host
            .RequestAsync("tab.notes", new JsonObject { ["tabId"] = tabId }, cancellationToken)
            .ConfigureAwait(false);

        return (notes["title"]?.GetValue<string>() ?? "", notes["url"]?.GetValue<string>() ?? "");
    }

    /// <summary>The DevTools endpoint for one tab.</summary>
    public IPaneCdp Cdp(string tabId) => new TabCdp(_host, tabId);

    private sealed class TabCdp(ElectronPaneHost host, string tabId) : IPaneCdp
    {
        public async Task<JsonObject> SendAsync(
            string method,
            JsonObject? parameters,
            CancellationToken cancellationToken = default)
        {
            var answer = await host.RequestAsync("cdp.send", new JsonObject
            {
                ["tabId"] = tabId,
                ["method"] = method,
                ["params"] = parameters ?? [],
            }, cancellationToken).ConfigureAwait(false);

            return answer["result"] as JsonObject ?? [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _host.EventReceived -= OnEngineEvent;

        if (_windowId is not null)
        {
            try
            {
                await _host.RequestAsync("host.close", Window(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ObjectDisposedException)
            {
                // The engine is already gone; there is no window left to close.
            }
        }

        if (_ownsHost)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
