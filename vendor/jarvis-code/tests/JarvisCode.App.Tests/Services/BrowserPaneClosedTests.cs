using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Panels;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// What the Browser pane's tools answer when there is no pane. The reference
/// guards every call: only `navigate` with a url may open one, a batch step may
/// not open one at all, and tabs_context/tabs_create answer with their own text
/// instead of an error.
/// </summary>
public sealed class BrowserPaneClosedTests
{
    private static readonly PaneContext Closed = new(false, PaneVisibility.NotOpen, []);

    private static (BrowserPaneHandlers Handlers, StubPaneDriver Driver) Pane(bool open)
    {
        var driver = new StubPaneDriver();
        if (!open)
        {
            driver.Context = Closed;
        }

        return (new BrowserPaneHandlers(driver), driver);
    }

    private static Task<ToolResult?> Guard(
        BrowserPaneHandlers handlers, string tool, JsonObject? args = null, bool inBatch = false,
        bool isFirstBatchAction = false) =>
        handlers.ClosedPaneAnswerAsync(
            tool, args ?? [], inBatch, isFirstBatchAction, CancellationToken.None);

    [Fact]
    public async Task An_open_pane_lets_every_call_through()
    {
        var (handlers, _) = Pane(open: true);

        foreach (var tool in new[] { "read_page", "computer", "navigate", "tabs_create", "tabs_context" })
        {
            Assert.Null(await Guard(handlers, tool, new JsonObject { ["url"] = "https://example.com" }));
        }
    }

    [Fact]
    public async Task A_page_facing_call_on_a_closed_pane_says_no_preview_is_open()
    {
        var (handlers, _) = Pane(open: false);

        var result = await Guard(handlers, "read_page");

        Assert.NotNull(result);
        Assert.True(result!.IsError);
        Assert.Equal(BrowserPaneHandlers.NoPreviewOpen, result.Content);
    }

    [Theory]
    [InlineData("computer")]
    [InlineData("find")]
    [InlineData("get_page_text")]
    [InlineData("form_input")]
    [InlineData("javascript_tool")]
    [InlineData("read_console_messages")]
    [InlineData("read_network_requests")]
    [InlineData("resize_window")]
    [InlineData("tabs_select")]
    [InlineData("tabs_close")]
    public async Task Every_other_pane_tool_refuses_the_same_way(string tool)
    {
        var (handlers, _) = Pane(open: false);

        var result = await Guard(handlers, tool);

        Assert.Equal(BrowserPaneHandlers.NoPreviewOpen, result!.Content);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Navigate_with_a_url_is_allowed_through_so_it_can_open_the_pane()
    {
        var (handlers, _) = Pane(open: false);

        Assert.Null(await Guard(handlers, "navigate", new JsonObject { ["url"] = "https://example.com" }));
    }

    [Theory]
    [InlineData("back")]
    [InlineData("forward")]
    [InlineData("FORWARD")]
    public async Task Navigate_cannot_open_the_pane_with_a_history_move(string url)
    {
        var (handlers, _) = Pane(open: false);

        var result = await Guard(handlers, "navigate", new JsonObject { ["url"] = url });

        Assert.Equal(BrowserPaneHandlers.NoPreviewOpen, result!.Content);
    }

    [Fact]
    public async Task Navigate_with_no_url_at_all_refuses_like_the_rest()
    {
        var (handlers, _) = Pane(open: false);

        Assert.Equal(
            BrowserPaneHandlers.NoPreviewOpen,
            (await Guard(handlers, "navigate", new JsonObject { ["url"] = "   " }))!.Content);
        Assert.Equal(
            BrowserPaneHandlers.NoPreviewOpen,
            (await Guard(handlers, "navigate"))!.Content);
    }

    [Fact]
    public async Task Only_the_first_batch_action_may_open_the_pane()
    {
        var (handlers, _) = Pane(open: false);
        var url = new JsonObject { ["url"] = "https://example.com" };

        Assert.Null(await Guard(handlers, "navigate", url, inBatch: true, isFirstBatchAction: true));

        var later = await Guard(handlers, "navigate", url, inBatch: true);

        Assert.NotNull(later);
        Assert.True(later!.IsError);
        Assert.Equal(BrowserPaneHandlers.BatchCannotOpenPane, later.Content);
    }

    [Fact]
    public async Task The_batch_itself_is_never_guarded_so_its_steps_answer_for_themselves()
    {
        var (handlers, _) = Pane(open: false);

        Assert.Null(await Guard(handlers, "browser_batch"));
    }

    [Fact]
    public async Task tabs_create_reports_that_it_created_nothing_and_is_not_an_error()
    {
        var (handlers, _) = Pane(open: false);

        var result = await Guard(handlers, "tabs_create");

        Assert.NotNull(result);
        Assert.False(result!.IsError);
        Assert.Equal(BrowserPaneHandlers.NoTabCreated, result.Content);
        Assert.StartsWith("No tab was created. The Browser pane isn't open yet", result.Content);
    }

    [Fact]
    public async Task tabs_context_answers_with_the_empty_listing_and_is_not_an_error()
    {
        var (handlers, _) = Pane(open: false);

        var result = await Guard(handlers, "tabs_context");

        Assert.NotNull(result);
        Assert.False(result!.IsError);
        Assert.Contains("\"browserOpen\": false", result.Content);
        Assert.Contains("\"tabs\": []", result.Content);
        Assert.EndsWith("to open it.", result.Content);
    }

    [Fact]
    public async Task The_closed_tabs_context_body_is_the_one_the_handler_itself_returns()
    {
        // One spelling for both routes, so the guard and the handler can never
        // disagree about what a closed pane's listing looks like.
        var (handlers, _) = Pane(open: false);

        var guarded = await Guard(handlers, "tabs_context");
        var direct = await handlers.TabsContextAsync(CancellationToken.None);

        Assert.Equal(direct.Content, guarded!.Content);
    }

    [Fact]
    public async Task The_guard_is_wired_into_the_tools_themselves()
    {
        // The handler having the right answer is worth nothing if the tools do
        // not ask it, so this runs the real tool objects.
        var driver = new StubPaneDriver { Context = Closed };
        var tools = BrowserPaneTools.Create(driver);
        var readPage = tools.First(t => t.Name.EndsWith("read_page", StringComparison.Ordinal));

        var result = await readPage.ExecuteAsync([], null!, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(BrowserPaneHandlers.NoPreviewOpen, result.Content);
        Assert.DoesNotContain(driver.Calls, call => call.StartsWith("read_page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_real_batch_led_by_navigate_is_allowed_to_open_the_pane()
    {
        var driver = new StubPaneDriver { Context = Closed };
        var tools = BrowserPaneTools.Create(driver);
        var batch = tools.First(t => t.Name.EndsWith("browser_batch", StringComparison.Ordinal));

        // The first action is the only position the reference lets open a pane,
        // and it is also the only one a closed pane can reach: every page tool
        // behind it would answer NoPreviewOpen and stop the batch first.
        var actions = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "navigate",
                ["input"] = new JsonObject { ["url"] = "https://example.com" },
            },
        };
        var result = await batch.ExecuteAsync(
            new JsonObject { ["actions"] = actions }, null!, CancellationToken.None);

        Assert.DoesNotContain(BrowserPaneHandlers.BatchCannotOpenPane, result.Content);
        Assert.Contains(driver.Calls, call => call.StartsWith("navigate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Navigate_on_its_own_still_reaches_the_driver_so_it_can_open_the_pane()
    {
        var driver = new StubPaneDriver { Context = Closed };
        var tools = BrowserPaneTools.Create(driver);
        var navigate = tools.First(t => t.Name.EndsWith("navigate", StringComparison.Ordinal));

        await navigate.ExecuteAsync(
            new JsonObject { ["url"] = "https://example.com" }, null!, CancellationToken.None);

        // The stub records the call with its argument, so the prefix is the check.
        Assert.Contains(driver.Calls, call => call.StartsWith("navigate", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hidden_preview_pane_starts_its_countdown_and_a_plain_tab_does_not()
    {
        // The reference's exclusion: only a pane something can rebuild is reaped.
        Assert.True(BrowserPanel.ShouldArmHiddenReap(isVisible: false, previewPort: 5173, paneOpen: true));
        Assert.False(BrowserPanel.ShouldArmHiddenReap(isVisible: false, previewPort: 0, paneOpen: true));
        Assert.False(BrowserPanel.ShouldArmHiddenReap(isVisible: true, previewPort: 5173, paneOpen: true));
        Assert.False(BrowserPanel.ShouldArmHiddenReap(isVisible: false, previewPort: 5173, paneOpen: false));
    }

    [Fact]
    public void A_pane_brought_back_before_the_countdown_ends_survives_it()
    {
        Assert.True(BrowserPanel.ShouldReapNow(isVisible: false, paneOpen: true));
        Assert.False(BrowserPanel.ShouldReapNow(isVisible: true, paneOpen: true));
        Assert.False(BrowserPanel.ShouldReapNow(isVisible: false, paneOpen: false));
    }

    [Fact]
    public void The_countdown_is_the_reference_five_minutes()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300_000), BrowserPanel.HiddenReapDelay);
    }

    [Fact]
    public async Task The_refusal_names_the_launch_json_this_app_actually_reads()
    {
        var (handlers, _) = Pane(open: false);

        var result = await Guard(handlers, "read_page");

        Assert.Contains(".jarvis/launch.json", result!.Content);
    }
}
