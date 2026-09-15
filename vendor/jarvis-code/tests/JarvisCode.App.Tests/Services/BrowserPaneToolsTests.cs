using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// Answers driver primitives with canned data so the handler layer — which
/// holds the reference desktop's exact result semantics — tests without a
/// WebView2. Evaluate is routed by script content (a11y call, ref point,
/// form_input, page text, viewport probe).
/// </summary>
internal sealed class StubPaneDriver : IBrowserPaneDriver
{
    public readonly List<string> Calls = [];
    public PaneContext Context = new(true, PaneVisibility.Displayed,
        [new PaneTabRow("tab_1", "http://localhost:5173", "App", true, true)]);
    public JsonObject TreeResult = new()
    {
        ["pageContent"] = "button \"Sign in\" [ref_1]\n textbox \"Email\" [ref_2]",
        ["viewport"] = new JsonObject { ["width"] = 800, ["height"] = 600 },
    };
    public JsonObject RefPointResult = new()
    {
        ["x"] = 120.0, ["y"] = 40.0, ["left"] = 100.0, ["top"] = 30.0, ["right"] = 140.0, ["bottom"] = 50.0,
        ["vw"] = 500.0, ["vh"] = 400.0,
    };
    public JsonObject FormInputResult = new() { ["ok"] = true };
    public JsonNode? JsResult = JsonValue.Create(42);
    public Exception? FirstJsError;
    public (int W, int H) EvalViewport = (500, 400);
    public PaneScreenshot Screenshot = new("aGkgdGhlcmU=", 1000, 800, 1000, 800);
    public List<JsonObject> Console = [];
    public List<JsonObject> Network = [];
    public PaneNavigation Navigation = new("to", "http://localhost:5173");

    public Task<string> ResolveTabIdAsync(string? tabId, CancellationToken ct) => Task.FromResult(tabId ?? "tab_1");

    public Task<PaneContext> TabsContextAsync(CancellationToken ct) => Task.FromResult(Context);

    public PaneTabNotes Notes = new("App", "http://localhost:5173/", []);

    public Task<PaneTabNotes> TabNotesAsync(string tabId, CancellationToken ct) => Task.FromResult(Notes);

    /// <summary>Set by a test to put the pane in the state a popup creates.</summary>
    public bool TargetIsPopup { get; set; }

    public bool AnyPopup { get; set; }

    public Task<(bool TargetIsPopup, bool AnyPopup)> PopupStateAsync(string? tabId, CancellationToken ct) =>
        Task.FromResult((TargetIsPopup, AnyPopup));

    /// <summary>The site the tab is treated as having come from.</summary>
    public string? LastExternalOrigin { get; set; }

    /// <summary>Where a back/forward move would land; null for an empty history.</summary>
    public string? HistoryTarget { get; set; }

    public Task<string?> LastExternalOriginAsync(string? tabId, CancellationToken ct) =>
        Task.FromResult(LastExternalOrigin);

    public Task<string?> HistoryTargetAsync(string? tabId, string direction, CancellationToken ct)
    {
        Calls.Add($"history-target:{direction}");
        return Task.FromResult(HistoryTarget);
    }

    /// <summary>Bumped by a test to make the page move under a running sequence.</summary>
    public int NavigationCount { get; set; }

    /// <summary>When set, the page moves once, on this call of the mark.</summary>
    public int NavigateOnMarkCall { get; set; } = -1;

    private int _markCalls;

    public Task<string> NavigationMarkAsync(string? tabId, CancellationToken ct)
    {
        if (_markCalls++ == NavigateOnMarkCall)
        {
            NavigationCount++;
        }

        return Task.FromResult(NavigationCount + ":stub");
    }

    public Task<JsonNode?> EvaluateAsync(string? tabId, string expression, bool replMode, CancellationToken ct)
    {
        if (expression.Contains("__generateAccessibilityTree"))
        {
            Calls.Add($"tree:{expression[expression.IndexOf(";window.__generateAccessibilityTree", StringComparison.Ordinal)..]}");
            return Task.FromResult<JsonNode?>(TreeResult.DeepClone());
        }

        if (expression.Contains("__claudeElementMap") && expression.Contains("getBoundingClientRect"))
        {
            Calls.Add("refpoint");
            return Task.FromResult<JsonNode?>(RefPointResult.DeepClone());
        }

        if (expression.Contains("nativeSet"))
        {
            Calls.Add("forminput");
            return Task.FromResult<JsonNode?>(FormInputResult.DeepClone());
        }

        if (expression.Contains("window.innerWidth") && expression.Contains("({w:"))
        {
            return Task.FromResult<JsonNode?>(new JsonObject { ["w"] = EvalViewport.W, ["h"] = EvalViewport.H });
        }

        if (expression.Contains("document.readyState"))
        {
            Calls.Add("pagetext");
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["title"] = "App", ["url"] = "http://localhost:5173/page", ["tag"] = "main",
                ["text"] = "Hello world", ["truncated"] = false,
            });
        }

        Calls.Add($"js:{replMode}:{expression}");
        if (FirstJsError is { } error)
        {
            FirstJsError = null;
            throw error;
        }

        return Task.FromResult(JsResult?.DeepClone());
    }

    public Task<PaneScreenshot> ScreenshotAsync(string? tabId, double scale, CancellationToken ct)
    {
        Calls.Add($"screenshot:{scale}");
        return Task.FromResult(Screenshot);
    }

    public Task ClickAsync(string? tabId, double x, double y, string button, int clickCount, int modifiers, CancellationToken ct)
    {
        Calls.Add($"click:{x},{y}:{button}:{clickCount}:{modifiers}");
        return Task.CompletedTask;
    }

    public Task HoverAsync(string? tabId, double x, double y, CancellationToken ct)
    {
        Calls.Add($"hover:{x},{y}");
        return Task.CompletedTask;
    }

    public Task WheelAsync(string? tabId, double x, double y, double deltaX, double deltaY, CancellationToken ct)
    {
        Calls.Add($"wheel:{x},{y}:{deltaX},{deltaY}");
        return Task.CompletedTask;
    }

    public Task DragAsync(string? tabId, double startX, double startY, double endX, double endY, CancellationToken ct)
    {
        Calls.Add($"drag:{startX},{startY}->{endX},{endY}");
        return Task.CompletedTask;
    }

    public Task InsertTextAsync(string? tabId, string text, CancellationToken ct)
    {
        Calls.Add($"type:{text}");
        return Task.CompletedTask;
    }

    public Task PressKeyAsync(string? tabId, string key, int modifiers, string? text, CancellationToken ct)
    {
        Calls.Add($"key:{key}:{modifiers}:{text}");
        return Task.CompletedTask;
    }

    public Task SetViewportAsync(string? tabId, int width, int height, bool? mobile, CancellationToken ct)
    {
        Calls.Add($"viewport:{width}x{height}");
        return Task.CompletedTask;
    }

    public Task ClearViewportAsync(string? tabId, CancellationToken ct)
    {
        Calls.Add("viewport:clear");
        return Task.CompletedTask;
    }

    public Task SetColorSchemeAsync(string? tabId, string scheme, CancellationToken ct)
    {
        Calls.Add($"scheme:{scheme}");
        return Task.CompletedTask;
    }

    /// <summary>Set by a test that wants one specific navigation outcome.</summary>
    public PaneNavigation? NextNavigation { get; set; }

    public Task<PaneNavigation> NavigateAsync(string? tabId, string url, CancellationToken ct)
    {
        Calls.Add($"navigate:{url}");
        return Task.FromResult(NextNavigation ?? Navigation);
    }

    public Task<string> CreateTabAsync(bool foreground, CancellationToken ct)
    {
        Calls.Add($"create:{foreground}");
        return Task.FromResult("tab_2");
    }

    public Task<bool> SelectTabAsync(string tabId, CancellationToken ct) => Task.FromResult(tabId == "tab_1");

    public Task<(bool Found, bool WasLast)> CloseTabAsync(string tabId, CancellationToken ct) =>
        Task.FromResult((tabId == "tab_1", tabId == "tab_1" && Context.Tabs.Count == 1));

    public Task<IReadOnlyList<JsonObject>> ConsoleEntriesAsync(string? tabId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JsonObject>>([.. Console]);

    public Task<IReadOnlyList<JsonObject>> NetworkListAsync(string? tabId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JsonObject>>([.. Network]);

    public Task<(string Body, bool Base64Encoded)?> NetworkBodyAsync(string? tabId, string requestId, CancellationToken ct) =>
        Task.FromResult<(string, bool)?>(requestId == "9.1" ? ("{\"ok\":true}", false) : null);
}

public class BrowserPaneToolsTests
{
    private const string Trailer = "\n\nTab Context:\n- Executed on tabId: tab_1\n- Available tabs:\n  • tabId tab_1: \"App\" (http://localhost:5173)";

    private static readonly ToolExecutionContext Context = new() { WorkingDirectory = @"C:\" };

    private static (IReadOnlyList<ITool> Tools, StubPaneDriver Driver, BrowserPaneHandlers Handlers) Create()
    {
        var driver = new StubPaneDriver();
        return (BrowserPaneTools.Create(driver), driver, new BrowserPaneHandlers(driver));
    }

    private static ITool Tool(IReadOnlyList<ITool> tools, string basename) =>
        tools.First(t => t.Name == basename);

    [Fact]
    public void CarriesTheReferenceToolSurface()
    {
        var (tools, _, _) = Create();
        string[] expected =
        [
            "navigate", "computer", "read_page", "find", "get_page_text", "form_input", "javascript_tool",
            "read_console_messages", "read_network_requests", "resize_window",
            "tabs_context", "tabs_create", "tabs_select", "tabs_close", "browser_batch",
        ];
        Assert.Equal(expected.Order(), tools.Select(t => t.Name).Order());

        // What the model actually sees is the composed set: the same tools under
        // the reference's mcp__Claude_Browser__ names.
        var wired = InternalMcpServers.Compose(
            [BrowserPaneTools.Server(new StubPaneDriver())], new InternalMcpSessionContext());
        Assert.Equal(
            expected.Select(n => BrowserPaneTools.Prefix + n).Order(),
            wired.Select(t => t.Name).Order());
        Assert.All(new[] { "read_page", "find", "get_page_text", "read_console_messages", "read_network_requests", "tabs_context" },
            name => Assert.True(Tool(tools, name).IsReadOnly, name));
    }

    [Fact]
    public async Task ReadPageAppendsViewportAndTrailer()
    {
        var (_, _, handlers) = Create();
        var result = await handlers.ReadPageAsync([], CancellationToken.None);
        Assert.Equal(
            "button \"Sign in\" [ref_1]\n textbox \"Email\" [ref_2]\n\nViewport: 800x600" + Trailer,
            result.Content);
    }

    [Fact]
    public async Task ReadPageDefaultsMatchTheReference()
    {
        var (_, driver, handlers) = Create();
        await handlers.ReadPageAsync([], CancellationToken.None);
        Assert.Contains("tree:;window.__generateAccessibilityTree(\"all\", 15, 50000, null)", driver.Calls);

        driver.TreeResult = new JsonObject { ["pageContent"] = "", ["viewport"] = new JsonObject { ["width"] = 1, ["height"] = 1 } };
        var empty = await handlers.ReadPageAsync([], CancellationToken.None);
        Assert.StartsWith("(empty page)", empty.Content);
    }

    [Fact]
    public async Task FindFormatsMatchesLikeTheReference()
    {
        var (_, driver, handlers) = Create();
        var found = await handlers.FindAsync(new JsonObject { ["query"] = "sign in" }, CancellationToken.None);
        Assert.StartsWith("Found 1 match(es) for \"sign in\":\n- button \"Sign in\" [ref_1]", found.Content);

        var none = await handlers.FindAsync(new JsonObject { ["query"] = "zzz" }, CancellationToken.None);
        Assert.StartsWith("No matches for \"zzz\".", none.Content);

        var missing = await handlers.FindAsync([], CancellationToken.None);
        Assert.Equal("`find` requires a string `query`", missing.Content);

        driver.TreeResult["pageContent"] = "button \"Sign in\" [ref_1]\n[truncated at 10000 elements — page is very large; use a refId or smaller depth to focus]";
        var noted = await handlers.FindAsync(new JsonObject { ["query"] = "sign" }, CancellationToken.None);
        Assert.Contains("(Page is very large; only the first part of it was searched.)", noted.Content);
    }

    [Fact]
    public async Task CoordinateClicksNeedAScreenshotFirst()
    {
        var (_, driver, handlers) = Create();
        var before = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "left_click", ["coordinate"] = new JsonArray(10, 10),
        }, CancellationToken.None);
        Assert.True(before.IsError);
        Assert.Equal(
            "left_click with `coordinate` requires a prior computer{action:\"screenshot\"} (no screenshot dimensions cached)",
            before.Content);

        var shot = await handlers.ComputerAsync(new JsonObject { ["action"] = "screenshot" }, CancellationToken.None);
        Assert.StartsWith("Screenshot size: 1000x800", shot.Content);
        Assert.Equal("image/jpeg", shot.Images!.Single().MediaType);

        // Frame 1000x800 → viewport 500x400: coordinates halve at dispatch, echo stays frame-relative.
        var after = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "left_click", ["coordinate"] = new JsonArray(1000, 800),
        }, CancellationToken.None);
        Assert.StartsWith("left_click at (1000, 800)", after.Content);
        Assert.Contains("click:499,399:left:1:0", driver.Calls);

        var outside = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "left_click", ["coordinate"] = new JsonArray(1500, 10),
        }, CancellationToken.None);
        Assert.Equal(
            "left_click: coordinate (1500, 10) is outside the coordinate frame (1000x800). Coordinates are pixels " +
            "in the full-resolution frame — if the page changed, take a new screenshot first.",
            outside.Content);
    }

    [Fact]
    public async Task RefClicksEchoTheRefAndClampToTheViewport()
    {
        var (_, driver, handlers) = Create();
        var click = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "left_click", ["ref"] = "ref_1",
        }, CancellationToken.None);
        Assert.StartsWith("left_click at (120, 40) [ref_1]", click.Content);

        driver.RefPointResult = new JsonObject
        {
            ["x"] = -50.0, ["y"] = -17.0, ["left"] = -60.0, ["top"] = -20.0, ["right"] = -40.0, ["bottom"] = -14.0,
            ["vw"] = 500.0, ["vh"] = 400.0,
        };
        var hidden = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "left_click", ["ref"] = "ref_1",
        }, CancellationToken.None);
        Assert.Equal(
            "ref ref_1 is entirely outside the viewport (center (-50, -17)) — likely hidden or off-canvas, so a " +
            "click cannot reach it. Interact with what opens it first, or re-run read_page and pick a visible element.",
            hidden.Content);

        var neither = await handlers.ComputerAsync(new JsonObject { ["action"] = "left_click" }, CancellationToken.None);
        Assert.Equal("left_click requires either `ref` or `coordinate`", neither.Content);
    }

    [Fact]
    public async Task ZoomIsAFullScreenshotAndScaleValidates()
    {
        var (_, _, handlers) = Create();
        var zoom = await handlers.ComputerAsync(new JsonObject { ["action"] = "zoom" }, CancellationToken.None);
        Assert.StartsWith("zoom: region crop not yet supported in the Browser pane; full screenshot returned", zoom.Content);

        var invalid = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "screenshot", ["scale"] = 5,
        }, CancellationToken.None);
        Assert.Equal("scale must be a number in [0.1, 1] — e.g. 0.5 for a half-size image", invalid.Content);
    }

    [Fact]
    public async Task ScaledScreenshotsReportTheCoordinateFrame()
    {
        var (_, driver, handlers) = Create();
        driver.Screenshot = new PaneScreenshot("aGk=", 500, 400, 1000, 800);
        var shot = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "screenshot", ["scale"] = 0.5,
        }, CancellationToken.None);
        Assert.StartsWith("Screenshot size: 500x400 0.5-scale view; coordinate frame: 1000x800.", shot.Content);
    }

    [Theory]
    // A read is left alone whatever is open, and so are the computer actions
    // that change nothing about the page.
    [InlineData("read_page", null, false)]
    [InlineData("get_page_text", null, false)]
    [InlineData("tabs_context", null, false)]
    [InlineData("computer", "screenshot", false)]
    [InlineData("computer", "hover", false)]
    [InlineData("computer", "scroll", false)]
    // Anything that writes to the page is.
    [InlineData("computer", "left_click", true)]
    [InlineData("computer", "type", true)]
    [InlineData("form_input", null, true)]
    [InlineData("navigate", null, true)]
    [InlineData("javascript_tool", null, true)]
    // A tool the map does not classify is guarded until it is.
    [InlineData("something_new", null, true)]
    public void TheGuardKnowsWhichCallsTouchThePage(string tool, string? action, bool guarded) =>
        Assert.Equal(guarded, BrowserPanePopupGuard.IsGuarded(tool, action));

    [Fact]
    public void APopupTabRefusesEveryCallThatWouldDriveIt()
    {
        Assert.Equal(
            BrowserPanePopupGuard.PopupTabRefusal,
            BrowserPanePopupGuard.Refusal("computer", "left_click", targetIsPopup: true, hasLivePopup: true));

        // Reading the popup is still allowed - the user may need to be told
        // what it is showing.
        Assert.Null(BrowserPanePopupGuard.Refusal("read_page", null, targetIsPopup: true, hasLivePopup: true));
    }

    [Fact]
    public void AnOpenPopupPausesNavigationAndScriptEverywhere()
    {
        Assert.Equal(
            BrowserPanePopupGuard.NavigationPausedRefusal,
            BrowserPanePopupGuard.Refusal("navigate", null, targetIsPopup: false, hasLivePopup: true));
        Assert.Equal(
            BrowserPanePopupGuard.ScriptPausedRefusal,
            BrowserPanePopupGuard.Refusal("javascript_tool", null, targetIsPopup: false, hasLivePopup: true));

        // A click on a regular tab is not paused by a popup elsewhere; only the
        // two that can reach across tabs are.
        Assert.Null(BrowserPanePopupGuard.Refusal("computer", "left_click", targetIsPopup: false, hasLivePopup: true));
        Assert.Null(BrowserPanePopupGuard.Refusal("navigate", null, targetIsPopup: false, hasLivePopup: false));
    }

    [Fact]
    public async Task TheGuardRefusesBeforeTheToolRuns()
    {
        var (_, driver, handlers) = Create();
        driver.AnyPopup = true;

        var refused = await handlers.PopupRefusalAsync(
            "navigate", new JsonObject { ["url"] = "https://example.com/" }, CancellationToken.None);

        Assert.NotNull(refused);
        Assert.True(refused!.IsError);
        Assert.Equal(BrowserPanePopupGuard.NavigationPausedRefusal, refused.Content);

        // A read is not asked about at all, so the pane is not queried for it.
        Assert.Null(await handlers.PopupRefusalAsync("read_page", [], CancellationToken.None));
    }

    [Fact]
    public async Task NavigateAnswersThePinnedTabAndTheDownloadTheReferenceWay()
    {
        var (_, driver, handlers) = Create();

        driver.NextNavigation = new PaneNavigation("pinned-file", "tab_1");
        var pinned = await handlers.NavigateAsync(
            new JsonObject { ["url"] = "https://example.com/" }, CancellationToken.None);
        Assert.True(pinned.IsError);
        Assert.Equal(
            "Tab tab_1 is pinned to a local file preview and cannot navigate. " +
            "Open a new tab with `tabs_create` and navigate there instead.",
            pinned.Content);

        // A download is not a failure: nothing went wrong, the address was a file.
        driver.NextNavigation = new PaneNavigation("downloaded", "https://example.com");
        var downloaded = await handlers.NavigateAsync(
            new JsonObject { ["url"] = "https://example.com/report.pdf" }, CancellationToken.None);
        Assert.False(downloaded.IsError);
        Assert.StartsWith(
            "https://example.com responded with a file download instead of a page; the user was shown " +
            "a save dialog and the Browser pane did not navigate. Do not retry this URL.",
            downloaded.Content);
    }

    [Fact]
    public async Task NavigateRefusesAUrlThatCarriesCredentials()
    {
        var (_, driver, handlers) = Create();

        var refused = await handlers.NavigateAsync(
            new JsonObject { ["url"] = "https://user:secret@example.com/inbox" }, CancellationToken.None);

        Assert.True(refused.IsError);
        Assert.Equal(BrowserPaneHandlers.CredentialsRefused, refused.Content);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("navigate:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://user:secret@example.com/", true)]
    [InlineData("https://user@example.com/", true)]
    // The userinfo is only userinfo before the path: an @ later in the URL is
    // an ordinary character, and refusing those would break real links.
    [InlineData("https://example.com/mail/user@example.com", false)]
    [InlineData("https://example.com/?to=a@b.com", false)]
    [InlineData("https://example.com/#a@b", false)]
    [InlineData("file:///C:/tmp/a@b.html", false)]
    [InlineData("about:blank", false)]
    public void CredentialsAreDetectedInTheAuthorityOnly(string url, bool embedded) =>
        Assert.Equal(embedded, BrowserPaneHandlers.EmbedsCredentials(url));

    [Fact]
    public async Task AKeySequenceStopsWhenThePageMovesUnderIt()
    {
        // The rest of a sequence would land on a page the caller never looked
        // at, so it is not sent.
        var (_, driver, handlers) = Create();
        driver.NavigateOnMarkCall = 2;

        var moved = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "key", ["text"] = "a b c",
        }, CancellationToken.None);

        Assert.True(moved.IsError);
        Assert.Equal("Page navigated during key sequence; remaining keys not dispatched.", moved.Content);
        Assert.DoesNotContain("key:c:0:", driver.Calls);
    }

    [Fact]
    public async Task ADragThatEndsOnAnotherPageIsReportedRatherThanClaimed()
    {
        var (_, driver, handlers) = Create();
        await handlers.ComputerAsync(new JsonObject { ["action"] = "screenshot" }, CancellationToken.None);
        driver.NavigateOnMarkCall = 1;

        var dragged = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "left_click_drag",
            ["start_coordinate"] = new JsonArray(1, 2),
            ["coordinate"] = new JsonArray(3, 4),
        }, CancellationToken.None);

        Assert.True(dragged.IsError);
        Assert.Equal("Page navigated during drag; result may be unreliable.", dragged.Content);
    }

    [Fact]
    public async Task KeySequencesFollowTheReference()
    {
        var (_, driver, handlers) = Create();
        var pressed = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "key", ["text"] = "ctrl+a Enter",
        }, CancellationToken.None);
        Assert.StartsWith("pressed ctrl+a Enter x1", pressed.Content);
        Assert.Contains("key:a:2:", driver.Calls);
        Assert.Contains("key:Enter:0:", driver.Calls);

        var missing = await handlers.ComputerAsync(new JsonObject { ["action"] = "key" }, CancellationToken.None);
        Assert.Equal("`key` requires `text`", missing.Content);

        Assert.Equal(("a", 2, null), BrowserPaneKeys.ParseToken("ctrl+a"));
        Assert.Equal(("z", 0, "z"), BrowserPaneKeys.ParseToken("z"));
        Assert.Equal(("+", 2, null), BrowserPaneKeys.ParseToken("ctrl++"));
        Assert.Equal(("Enter", 0, null), BrowserPaneKeys.ParseToken("Enter"));
        Assert.Equal(11, BrowserPaneKeys.Modifiers("alt+ctrl+shift"));
    }

    [Fact]
    public async Task ScrollRequiresCoordinateAndScalesLikeClicks()
    {
        var (_, driver, handlers) = Create();
        var missing = await handlers.ComputerAsync(new JsonObject { ["action"] = "scroll" }, CancellationToken.None);
        Assert.Equal("`scroll` requires `coordinate`", missing.Content);

        await handlers.ComputerAsync(new JsonObject { ["action"] = "screenshot" }, CancellationToken.None);
        var scrolled = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "scroll", ["coordinate"] = new JsonArray(100, 100), ["scroll_direction"] = "down",
        }, CancellationToken.None);
        Assert.StartsWith("scrolled down at (100, 100)", scrolled.Content);
        Assert.Contains("wheel:50,50:0,300", driver.Calls);
    }

    [Fact]
    public async Task WaitClampsToTenSeconds()
    {
        var (_, _, handlers) = Create();
        var waited = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "wait", ["duration"] = 0,
        }, CancellationToken.None);
        Assert.StartsWith("waited 0s", waited.Content);

        var unknown = await handlers.ComputerAsync(new JsonObject { ["action"] = "warp" }, CancellationToken.None);
        Assert.Equal("Unknown computer action: warp", unknown.Content);
    }

    [Fact]
    public async Task FormInputAnswersWithTheReferenceText()
    {
        var (_, driver, handlers) = Create();
        var filled = await handlers.FormInputAsync(new JsonObject
        {
            ["ref"] = "ref_2", ["value"] = "hello",
        }, CancellationToken.None);
        Assert.StartsWith("filled ref_2 with value", filled.Content);

        driver.FormInputResult = new JsonObject { ["ok"] = false, ["error"] = "option not found" };
        var failed = await handlers.FormInputAsync(new JsonObject
        {
            ["ref"] = "ref_2", ["value"] = "x",
        }, CancellationToken.None);
        Assert.Equal("form_input failed: option not found", failed.Content);
    }

    [Fact]
    public async Task JavaScriptRetriesTopLevelReturnsLikeTheReference()
    {
        var (_, driver, handlers) = Create();
        var value = await handlers.JavaScriptAsync(new JsonObject
        {
            ["action"] = "javascript_exec", ["text"] = "6*7",
        }, CancellationToken.None);
        Assert.StartsWith("42", value.Content);
        Assert.Contains(driver.Calls, static c => c.StartsWith("js:True:{6*7"));

        driver.FirstJsError = new InvalidOperationException("SyntaxError: Illegal return statement");
        driver.JsResult = JsonValue.Create("done");
        var retried = await handlers.JavaScriptAsync(new JsonObject
        {
            ["action"] = "javascript_exec", ["text"] = "return 1",
        }, CancellationToken.None);
        Assert.StartsWith("\"done\"", retried.Content);
        Assert.Contains(driver.Calls, static c => c.StartsWith("js:False:(async()=>{"));
    }

    [Fact]
    public async Task ConsoleAndNetworkUseTheReferenceLineFormats()
    {
        var (_, driver, handlers) = Create();
        var empty = await handlers.ConsoleAsync([], CancellationToken.None);
        Assert.StartsWith("No console logs.", empty.Content);

        driver.Console.Add(new JsonObject { ["level"] = "log", ["text"] = "hello" });
        driver.Console.Add(new JsonObject { ["level"] = "error", ["text"] = "boom" });
        var logs = await handlers.ConsoleAsync([], CancellationToken.None);
        Assert.StartsWith("[log] hello\n[error] boom", logs.Content);

        var errors = await handlers.ConsoleAsync(new JsonObject { ["onlyErrors"] = true }, CancellationToken.None);
        Assert.StartsWith("[error] boom", errors.Content);

        driver.Network.Add(new JsonObject
        {
            ["requestId"] = "9.1", ["method"] = "GET", ["url"] = "http://localhost:5173/api",
            ["status"] = 200, ["statusText"] = "OK",
        });
        driver.Network.Add(new JsonObject
        {
            ["requestId"] = "9.2", ["method"] = "POST", ["url"] = "http://localhost:5173/save",
            ["failed"] = true, ["errorText"] = "net::ERR_FAILED",
        });
        var requests = await handlers.NetworkAsync([], CancellationToken.None);
        Assert.StartsWith(
            "[9.1] GET http://localhost:5173/api → 200 OK\n[9.2] POST http://localhost:5173/save [FAILED: net::ERR_FAILED]",
            requests.Content);

        var body = await handlers.NetworkAsync(new JsonObject { ["requestId"] = "9.1" }, CancellationToken.None);
        Assert.StartsWith("{\"ok\":true}", body.Content);

        var missing = await handlers.NetworkAsync(new JsonObject { ["requestId"] = "9.9" }, CancellationToken.None);
        Assert.Equal("Response body not available for request 9.9.", missing.Content);
    }

    [Fact]
    public async Task ResizeSpeaksTheReferenceSentences()
    {
        var (_, driver, handlers) = Create();
        var mobile = await handlers.ResizeAsync(new JsonObject { ["preset"] = "mobile" }, CancellationToken.None);
        Assert.StartsWith(
            "Viewport set to 375x812 (mobile) on this tab. It stays (scaled down to fit if larger than the pane) " +
            "until you call this tool with preset \"desktop\", so reset it when you finish testing.",
            mobile.Content);
        Assert.Contains("viewport:375x812", driver.Calls);

        var desktop = await handlers.ResizeAsync(new JsonObject { ["preset"] = "desktop" }, CancellationToken.None);
        Assert.StartsWith("Viewport emulation cleared; the tab is back to the pane's responsive size (desktop).", desktop.Content);

        var scheme = await handlers.ResizeAsync(new JsonObject { ["colorScheme"] = "dark" }, CancellationToken.None);
        Assert.StartsWith(
            "Color scheme emulation set to dark on this tab; it survives reloads until you set the other value or " +
            "the pane re-syncs the tab to the app theme.",
            scheme.Content);

        var unknownScheme = await handlers.ResizeAsync(new JsonObject { ["colorScheme"] = "sepia" }, CancellationToken.None);
        Assert.Equal("Unknown colorScheme \"sepia\". Use light or dark.", unknownScheme.Content);

        var unknownPreset = await handlers.ResizeAsync(new JsonObject { ["preset"] = "watch" }, CancellationToken.None);
        Assert.Equal("Unknown preset \"watch\". Use mobile, tablet, or desktop.", unknownPreset.Content);

        var incomplete = await handlers.ResizeAsync(new JsonObject { ["width"] = 500 }, CancellationToken.None);
        Assert.StartsWith("A custom viewport needs both width and height, each a number from 1 to 9999.", incomplete.Content);

        var nothing = await handlers.ResizeAsync([], CancellationToken.None);
        Assert.Equal("Provide a preset (mobile/tablet/desktop), width/height, or colorScheme.", nothing.Content);
    }

    [Fact]
    public async Task ResizeInvalidatesTheScreenshotFrame()
    {
        var (_, _, handlers) = Create();
        await handlers.ComputerAsync(new JsonObject { ["action"] = "screenshot" }, CancellationToken.None);
        await handlers.ResizeAsync(new JsonObject { ["preset"] = "tablet" }, CancellationToken.None);
        var click = await handlers.ComputerAsync(new JsonObject
        {
            ["action"] = "left_click", ["coordinate"] = new JsonArray(10, 10),
        }, CancellationToken.None);
        Assert.Contains("requires a prior computer{action:\"screenshot\"}", click.Content);
    }

    [Fact]
    public async Task TabsContextRendersTheReferenceJson()
    {
        var (_, driver, handlers) = Create();
        var open = await handlers.TabsContextAsync(CancellationToken.None);
        Assert.Contains("\"browserOpen\": true", open.Content);
        Assert.Contains("\"tabId\": \"tab_1\"", open.Content);
        Assert.Contains("\"origin\": \"http://localhost:5173\"", open.Content);
        Assert.Contains("\"isActive\": true", open.Content);
        Assert.EndsWith("The Browser pane is currently displayed.", open.Content);

        driver.Context = driver.Context with { Visibility = PaneVisibility.Hidden };
        var hidden = await handlers.TabsContextAsync(CancellationToken.None);
        Assert.EndsWith("The Browser pane is currently hidden.", hidden.Content);

        driver.Context = new PaneContext(false, PaneVisibility.NotOpen, []);
        var closed = await handlers.TabsContextAsync(CancellationToken.None);
        Assert.Contains("\"browserOpen\": false", closed.Content);
        Assert.EndsWith(
            "The Browser pane isn't open yet, so there are no tabs. Call preview_start or navigate with " +
            "{\"url\": \"https://…\"} to open it.",
            closed.Content);
    }

    [Fact]
    public async Task TabLifecycleTextsMatchTheReference()
    {
        var (_, _, handlers) = Create();
        var created = await handlers.TabsCreateAsync([], CancellationToken.None);
        Assert.Contains("\"tabId\": \"tab_2\"", created.Content);
        Assert.Contains("Opened tab tab_2 in the background — the user's current tab stays in front.", created.Content);

        var fronted = await handlers.TabsCreateAsync(new JsonObject { ["foreground"] = true }, CancellationToken.None);
        Assert.Contains("Opened tab tab_2 in the foreground.", fronted.Content);

        var selected = await handlers.TabsSelectAsync(new JsonObject { ["tabId"] = "tab_1" }, CancellationToken.None);
        Assert.Equal("Fronted tab tab_1.", selected.Content);
        var missing = await handlers.TabsSelectAsync(new JsonObject { ["tabId"] = "tab_9" }, CancellationToken.None);
        Assert.Equal("Tab tab_9 not found.", missing.Content);

        var closedLast = await handlers.TabsCloseAsync(new JsonObject { ["tabId"] = "tab_1" }, CancellationToken.None);
        Assert.Equal(
            "Closed tab tab_1. That was the last tab, so the Browser pane is now closed — use `preview_start` to " +
            "open it again.",
            closedLast.Content);
    }

    [Fact]
    public async Task NavigateSpeaksTheReferenceTexts()
    {
        var (_, driver, handlers) = Create();
        var to = await handlers.NavigateAsync(new JsonObject { ["url"] = "http://localhost:5173" }, CancellationToken.None);
        Assert.StartsWith("navigated to http://localhost:5173", to.Content);

        driver.Navigation = new PaneNavigation("no-history", "back");
        var noHistory = await handlers.NavigateAsync(new JsonObject { ["url"] = "back" }, CancellationToken.None);
        Assert.Equal("no back history", noHistory.Content);

        driver.Navigation = new PaneNavigation("forward", null);
        var forward = await handlers.NavigateAsync(new JsonObject { ["url"] = "forward" }, CancellationToken.None);
        Assert.StartsWith("navigated forward", forward.Content);

        var missing = await handlers.NavigateAsync([], CancellationToken.None);
        Assert.Equal("`navigate` requires a string `url`", missing.Content);
    }

    [Fact]
    public async Task GetPageTextUsesTheReferenceHeader()
    {
        var (_, _, handlers) = Create();
        var text = await handlers.GetPageTextAsync([], CancellationToken.None);
        Assert.StartsWith("Title: App\nURL: http://localhost:5173\nSource element: <main>\n---\nHello world", text.Content);
    }

    [Fact]
    public async Task BatchValidatesAndReportsLikeTheReference()
    {
        var (tools, _, _) = Create();
        var batch = Tool(tools, "browser_batch");

        var empty = await batch.ExecuteAsync([], Context, CancellationToken.None);
        Assert.Equal("actions must be a non-empty array", empty.Content);

        var nested = await batch.ExecuteAsync(new JsonObject
        {
            ["actions"] = new JsonArray(new JsonObject { ["name"] = "browser_batch", ["input"] = new JsonObject() }),
        }, Context, CancellationToken.None);
        Assert.Equal("actions[0]: browser_batch cannot be nested", nested.Content);

        var unknown = await batch.ExecuteAsync(new JsonObject
        {
            ["actions"] = new JsonArray(new JsonObject { ["name"] = "teleport", ["input"] = new JsonObject() }),
        }, Context, CancellationToken.None);
        Assert.Equal("actions[0]: \"teleport\" cannot run in a batch", unknown.Content);

        // The reference's $Tr holds the ten page tools and nothing else: tab
        // management and the dev-server tools are refused by name.
        foreach (var outside in new[] { "tabs_context", "tabs_create", "preview_start", "preview_logs" })
        {
            var refused = await batch.ExecuteAsync(new JsonObject
            {
                ["actions"] = new JsonArray(
                    new JsonObject { ["name"] = outside, ["input"] = new JsonObject() }),
            }, Context, CancellationToken.None);
            Assert.Equal($"actions[0]: \"{outside}\" cannot run in a batch", refused.Content);
        }

        var noInput = await batch.ExecuteAsync(new JsonObject
        {
            ["actions"] = new JsonArray(new JsonObject { ["name"] = "read_page" }),
        }, Context, CancellationToken.None);
        Assert.Equal("actions[0].input must be an object", noInput.Content);

        var tooMany = await batch.ExecuteAsync(new JsonObject
        {
            ["actions"] = new JsonArray([.. Enumerable.Range(0, 26)
                .Select(static _ => (JsonNode)new JsonObject { ["name"] = "read_page", ["input"] = new JsonObject() })]),
        }, Context, CancellationToken.None);
        Assert.Equal("at most 25 actions per batch", tooMany.Content);

        var run = await batch.ExecuteAsync(new JsonObject
        {
            ["actions"] = new JsonArray(
                new JsonObject { ["name"] = "computer", ["input"] = new JsonObject { ["action"] = "screenshot" } },
                new JsonObject { ["name"] = "read_page", ["input"] = new JsonObject() }),
        }, Context, CancellationToken.None);
        Assert.False(run.IsError);
        Assert.StartsWith("[computer:screenshot] Screenshot size: 1000x800", run.Content);
        Assert.Contains("\n[read_page] button \"Sign in\" [ref_1]", run.Content);
        Assert.Single(run.Images!);

        var stopped = await batch.ExecuteAsync(new JsonObject
        {
            ["actions"] = new JsonArray(
                new JsonObject { ["name"] = "computer", ["input"] = new JsonObject { ["action"] = "screenshot" } },
                new JsonObject { ["name"] = "computer", ["input"] = new JsonObject { ["action"] = "key" } },
                new JsonObject { ["name"] = "read_page", ["input"] = new JsonObject() }),
        }, Context, CancellationToken.None);
        Assert.True(stopped.IsError);
        Assert.Contains("actions[1] (computer:key) failed: `key` requires `text` (1 completed, 1 remaining)", stopped.Content);
        Assert.Contains("[Image omitted due to error]", stopped.Content);
    }

    [Fact]
    public void CleanPageTextStripsControlCharacters()
    {
        // Each stripped character becomes one space, uncollapsed — the reference's MOn.
        Assert.Equal("a b  c", BrowserPaneHandlers.CleanPageText("a\nb\t\"c\""));
        Assert.Equal(200, BrowserPaneHandlers.CleanPageText(new string('x', 400)).Length);
    }

    [Fact]
    public void OriginKeepsHttpAuthorityAndBareSchemes()
    {
        Assert.Equal("http://localhost:5173", BrowserPaneDriver.Origin("http://localhost:5173/app/page?x=1"));
        Assert.Equal("file:", BrowserPaneDriver.Origin("file:///D:/site/index.html"));
        Assert.Equal("", BrowserPaneDriver.Origin(""));
    }
}
