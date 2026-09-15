using System.Text.Json.Nodes;
using System.Windows.Threading;
using JarvisCode.App.Views.Panels;

namespace JarvisCode.App.Services;

/// <summary>One row of tabs_context / the Tab Context trailer.</summary>
public sealed record PaneTabRow(string Id, string Origin, string Title, bool Active, bool HasPage);

/// <summary>The pane's visibility, phrased like the reference's dPn.</summary>
public enum PaneVisibility
{
    NotOpen,
    Displayed,
    NotFronted,
    Hidden,
}

public sealed record PaneContext(bool BrowserOpen, PaneVisibility Visibility, IReadOnlyList<PaneTabRow> Tabs);

/// <summary>navigate's outcome: where it went, or which history end was missing.</summary>
public sealed record PaneNavigation(string Kind, string? Origin);

/// <summary>A captured screenshot: JPEG plus the full-resolution frame size.</summary>
public sealed record PaneScreenshot(string JpegBase64, int Width, int Height, int FrameWidth, int FrameHeight);

/// <summary>
/// What one tab contributes to its result's Tab Context trailer: its title and address,
/// and the camera/microphone requests the pane refused while the page was open.
/// </summary>
public sealed record PaneTabNotes(
    string Title, string Url, IReadOnlyList<string> DeniedMediaKinds, bool ClipboardChanged = false);

/// <summary>
/// The Browser pane primitives the mcp__Claude_Browser__* tools drive — the
/// counterpart of the reference desktop's CDPTools, separated from WPF so the
/// tool handlers (which hold the reference's exact result semantics) unit-test
/// against a stub.
/// </summary>
public interface IBrowserPaneDriver
{
    /// <summary>Resolves a tabId (null = the active tab); throws for an unknown tab.</summary>
    Task<string> ResolveTabIdAsync(string? tabId, CancellationToken cancellationToken);

    Task<PaneContext> TabsContextAsync(CancellationToken cancellationToken);

    /// <summary>The executed tab's contribution to the trailer; throws for an unknown tab.</summary>
    Task<PaneTabNotes> TabNotesAsync(string tabId, CancellationToken cancellationToken);

    /// <summary>
    /// A value that changes whenever the tab commits a navigation. Input that
    /// takes more than one dispatch compares it around the run, because a page
    /// that went somewhere else mid-sequence is not the page the rest of the
    /// keystrokes were meant for.
    /// </summary>
    Task<string> NavigationMarkAsync(string? tabId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the tab this call resolves to is a page-opened popup, and
    /// whether any tab in the pane is one. Both in a single answer, because
    /// every guarded call needs both.
    /// </summary>
    Task<(bool TargetIsPopup, bool AnyPopup)> PopupStateAsync(string? tabId, CancellationToken cancellationToken);

    /// <summary>
    /// The last ordinary web origin this tab committed, or null when it has not
    /// been to one — the site a domain transition would be leaving.
    /// </summary>
    Task<string?> LastExternalOriginAsync(string? tabId, CancellationToken cancellationToken);

    /// <summary>
    /// Where a back/forward move would land, without moving; null when that end
    /// of the history is empty. Consent is asked for before the move happens.
    /// </summary>
    Task<string?> HistoryTargetAsync(string? tabId, string direction, CancellationToken cancellationToken);

    /// <summary>Evaluates JS in the tab's MAIN frame; throws InvalidOperationException with the page's message.</summary>
    Task<JsonNode?> EvaluateAsync(string? tabId, string expression, bool replMode, CancellationToken cancellationToken);

    Task<PaneScreenshot> ScreenshotAsync(string? tabId, double scale, CancellationToken cancellationToken);

    Task ClickAsync(string? tabId, double x, double y, string button, int clickCount, int modifiers, CancellationToken cancellationToken);
    Task HoverAsync(string? tabId, double x, double y, CancellationToken cancellationToken);
    Task WheelAsync(string? tabId, double x, double y, double deltaX, double deltaY, CancellationToken cancellationToken);
    Task DragAsync(string? tabId, double startX, double startY, double endX, double endY, CancellationToken cancellationToken);
    Task InsertTextAsync(string? tabId, string text, CancellationToken cancellationToken);
    Task PressKeyAsync(string? tabId, string key, int modifiers, string? text, CancellationToken cancellationToken);

    Task SetViewportAsync(string? tabId, int width, int height, bool? mobile, CancellationToken cancellationToken);
    Task ClearViewportAsync(string? tabId, CancellationToken cancellationToken);
    Task SetColorSchemeAsync(string? tabId, string scheme, CancellationToken cancellationToken);

    Task<PaneNavigation> NavigateAsync(string? tabId, string url, CancellationToken cancellationToken);
    Task<string> CreateTabAsync(bool foreground, CancellationToken cancellationToken);
    Task<bool> SelectTabAsync(string tabId, CancellationToken cancellationToken);

    /// <summary>Closes a tab; (found, wasLast).</summary>
    Task<(bool Found, bool WasLast)> CloseTabAsync(string tabId, CancellationToken cancellationToken);

    Task<IReadOnlyList<JsonObject>> ConsoleEntriesAsync(string? tabId, CancellationToken cancellationToken);
    Task<IReadOnlyList<JsonObject>> NetworkListAsync(string? tabId, CancellationToken cancellationToken);
    Task<(string Body, bool Base64Encoded)?> NetworkBodyAsync(string? tabId, string requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Drives the Browser side panel: every operation marshals onto the UI thread,
/// resolves its tab (active when tabId is null), makes sure the tab's engine view
/// exists, and speaks CDP through <see cref="BrowserPaneCdp"/>. Navigating and
/// tab creation open the pane first — an engine view outside the visible tree
/// cannot finish initializing.
/// </summary>
public sealed class BrowserPaneDriver(
    Dispatcher dispatcher, Func<BrowserPanel> panel, Action openPane) : IBrowserPaneDriver
{
    private async Task<T> OnUiAsync<T>(Func<BrowserPanel, Task<T>> action)
    {
        var operation = await dispatcher.InvokeAsync(() => action(panel()));
        return await operation;
    }

    private static BrowserPanel.PaneTab RequireTab(BrowserPanel view, string? tabId) =>
        view.FindTab(tabId)
        ?? throw new InvalidOperationException($"Tab {tabId} not found.");

    public Task<string> ResolveTabIdAsync(string? tabId, CancellationToken cancellationToken) =>
        OnUiAsync(view => Task.FromResult(RequireTab(view, tabId).Id));

    public Task<PaneContext> TabsContextAsync(CancellationToken cancellationToken) =>
        OnUiAsync(view => Task.FromResult(new PaneContext(
            view.PaneOpen,
            !view.PaneOpen ? PaneVisibility.NotOpen : view.PaneDisplayed ? PaneVisibility.Displayed : PaneVisibility.Hidden,
            [.. view.ListTabs().Select(static t => new PaneTabRow(t.Id, Origin(t.Url), t.Title, t.Active, t.HasPage))])));

    public Task<PaneTabNotes> TabNotesAsync(string tabId, CancellationToken cancellationToken) =>
        OnUiAsync(view => Task.FromResult(view.NotesFor(RequireTab(view, tabId))));

    public Task<string> NavigationMarkAsync(string? tabId, CancellationToken cancellationToken) =>
        OnUiAsync(view => Task.FromResult(BrowserPanel.NavigationMark(RequireTab(view, tabId))));

    public Task<string?> LastExternalOriginAsync(string? tabId, CancellationToken cancellationToken) =>
        OnUiAsync(view => Task.FromResult(view.FindTab(tabId)?.LastExternalOrigin));

    public Task<string?> HistoryTargetAsync(
        string? tabId, string direction, CancellationToken cancellationToken) =>
        OnUiAsync(view => view.DriverHistoryTargetAsync(RequireTab(view, tabId), direction));

    public Task<(bool TargetIsPopup, bool AnyPopup)> PopupStateAsync(
        string? tabId, CancellationToken cancellationToken) =>
        OnUiAsync(view =>
        {
            // An unknown tab is not a popup; the call fails on its own terms
            // further down rather than being refused as one.
            var target = view.FindTab(tabId);
            return Task.FromResult((
                target is not null && view.IsPopup(target),
                view.HasLivePopup));
        });

    /// <summary>Origin for http(s), the bare protocol for other schemes, "" for a blank tab.</summary>
    internal static string Origin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "";
        }

        return uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : uri.Scheme + ":";
    }

    public Task<JsonNode?> EvaluateAsync(string? tabId, string expression, bool replMode, CancellationToken cancellationToken) =>
        OnUiAsync(async view =>
        {
            var core = await view.DriverCoreAsync(RequireTab(view, tabId));
            return await BrowserPaneCdp.EvaluateAsync(core, expression, replMode);
        });

    public Task<PaneScreenshot> ScreenshotAsync(string? tabId, double scale, CancellationToken cancellationToken) =>
        OnUiAsync(async view =>
        {
            var core = await view.DriverCoreAsync(RequireTab(view, tabId));
            return await BrowserPaneCdp.ScreenshotJpegAsync(core, scale);
        });

    public Task ClickAsync(string? tabId, double x, double y, string button, int clickCount, int modifiers, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var tab = RequireTab(view, tabId);
            var core = await view.DriverCoreAsync(tab);
            var clipboard = ClipboardWatch.Token();
            await BrowserPaneCdp.MouseClickAsync(core, x, y, button, clickCount, modifiers);
            view.NoteClipboardChange(tab, clipboard);
            return true;
        });

    public Task HoverAsync(string? tabId, double x, double y, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var core = await view.DriverCoreAsync(RequireTab(view, tabId));
            await BrowserPaneCdp.CallAsync(core, "Input.dispatchMouseEvent", new JsonObject
            {
                ["type"] = "mouseMoved", ["x"] = x, ["y"] = y,
            });
            return true;
        });

    public Task WheelAsync(string? tabId, double x, double y, double deltaX, double deltaY, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var core = await view.DriverCoreAsync(RequireTab(view, tabId));
            await BrowserPaneCdp.CallAsync(core, "Input.dispatchMouseEvent", new JsonObject
            {
                ["type"] = "mouseWheel", ["x"] = x, ["y"] = y, ["deltaX"] = deltaX, ["deltaY"] = deltaY,
            });
            return true;
        });

    public Task DragAsync(string? tabId, double startX, double startY, double endX, double endY, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var tab = RequireTab(view, tabId);
            var core = await view.DriverCoreAsync(tab);
            var clipboard = ClipboardWatch.Token();
            await BrowserPaneCdp.DragAsync(core, startX, startY, endX, endY);
            view.NoteClipboardChange(tab, clipboard);
            return true;
        });

    public Task InsertTextAsync(string? tabId, string text, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var tab = RequireTab(view, tabId);
            var core = await view.DriverCoreAsync(tab);
            var clipboard = ClipboardWatch.Token();
            await BrowserPaneCdp.CallAsync(core, "Input.insertText", new JsonObject { ["text"] = text });
            view.NoteClipboardChange(tab, clipboard);
            return true;
        });

    public Task PressKeyAsync(string? tabId, string key, int modifiers, string? text, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var tab = RequireTab(view, tabId);
            var core = await view.DriverCoreAsync(tab);
            var clipboard = ClipboardWatch.Token();
            await BrowserPaneCdp.PressKeyAsync(core, key, modifiers, text);
            view.NoteClipboardChange(tab, clipboard);
            return true;
        });

    public Task SetViewportAsync(string? tabId, int width, int height, bool? mobile, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var tab = RequireTab(view, tabId);
            var core = await view.DriverCoreAsync(tab);
            var fit = view.PaneWidth > 0 && width > view.PaneWidth
                ? view.PaneWidth / width
                : (double?)null;
            await BrowserPaneCdp.SetViewportAsync(core, width, height, mobile, fit);
            return true;
        });

    public Task ClearViewportAsync(string? tabId, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var core = await view.DriverCoreAsync(RequireTab(view, tabId));
            await BrowserPaneCdp.ClearViewportAsync(core);
            return true;
        });

    public Task SetColorSchemeAsync(string? tabId, string scheme, CancellationToken cancellationToken) =>
        OnUiAsync<bool>(async view =>
        {
            var core = await view.DriverCoreAsync(RequireTab(view, tabId));
            await BrowserPaneCdp.SetEmulatedColorSchemeAsync(core, scheme);
            return true;
        });

    public Task<PaneNavigation> NavigateAsync(string? tabId, string url, CancellationToken cancellationToken) =>
        OnUiAsync(async view =>
        {
            openPane();
            var tab = RequireTab(view, tabId);
            if (url is "back" or "forward")
            {
                // The engine owns the history, so it is the one that says
                // whether that end of it had anything to move to.
                if (!await view.DriverHistoryAsync(tab, url))
                {
                    return new PaneNavigation("no-history", url);
                }

                return new PaneNavigation(url, null);
            }

            if (tab.PinnedFilePreview)
            {
                // The tab is showing a file the user opened; the agent opens its
                // own tab rather than taking this one away.
                return new PaneNavigation("pinned-file", tab.Id);
            }

            await view.NavigateTabAsync(tab, url);
            return tab.LastNavigationDownloaded
                ? new PaneNavigation("downloaded", Origin(url))
                : new PaneNavigation("to", Origin(tab.Url));
        });

    public Task<string> CreateTabAsync(bool foreground, CancellationToken cancellationToken) =>
        OnUiAsync(view =>
        {
            openPane();
            return view.DriverCreateTabAsync(null, foreground);
        });

    public Task<bool> SelectTabAsync(string tabId, CancellationToken cancellationToken) =>
        OnUiAsync(view =>
        {
            var tab = view.FindTab(tabId);
            if (tab is null)
            {
                return Task.FromResult(false);
            }

            view.DriverSelectTab(tab);
            return Task.FromResult(true);
        });

    public Task<(bool Found, bool WasLast)> CloseTabAsync(string tabId, CancellationToken cancellationToken) =>
        OnUiAsync(async view =>
        {
            var tab = view.FindTab(tabId);
            if (tab is null)
            {
                return (false, false);
            }

            var wasLast = view.ListTabs().Count <= 1;
            await view.CloseTabAsync(tab);
            return (true, wasLast);
        });

    public Task<IReadOnlyList<JsonObject>> ConsoleEntriesAsync(string? tabId, CancellationToken cancellationToken) =>
        OnUiAsync<IReadOnlyList<JsonObject>>(view =>
            Task.FromResult<IReadOnlyList<JsonObject>>(
                [.. RequireTab(view, tabId).ConsoleEntries.Select(static e => (JsonObject)e.DeepClone())]));

    public Task<IReadOnlyList<JsonObject>> NetworkListAsync(string? tabId, CancellationToken cancellationToken) =>
        OnUiAsync<IReadOnlyList<JsonObject>>(view =>
        {
            var tab = RequireTab(view, tabId);
            return Task.FromResult<IReadOnlyList<JsonObject>>(
                [.. tab.NetOrder
                    .Select(id => tab.Network.GetValueOrDefault(id))
                    .Where(static e => e is not null)
                    .Select(static e => (JsonObject)e!.DeepClone())]);
        });

    public Task<(string Body, bool Base64Encoded)?> NetworkBodyAsync(string? tabId, string requestId, CancellationToken cancellationToken) =>
        OnUiAsync<(string, bool)?>(async view =>
        {
            var tab = RequireTab(view, tabId);
            if (!tab.Network.ContainsKey(requestId))
            {
                return null;
            }

            var core = await view.DriverCoreAsync(tab);
            try
            {
                var body = await BrowserPaneCdp.CallAsync(core, "Network.getResponseBody",
                    new JsonObject { ["requestId"] = requestId });
                return (body["body"]?.GetValue<string>() ?? "", body["base64Encoded"]?.GetValue<bool>() == true);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        });
}
