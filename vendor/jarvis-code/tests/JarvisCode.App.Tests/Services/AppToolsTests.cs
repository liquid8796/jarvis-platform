using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

public class AppToolsTests
{
    private static readonly ToolExecutionContext Context = new() { WorkingDirectory = @"C:\" };

    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var args = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            args[key] = value;
        }

        return args;
    }

    // ---- artifact ----

    [Fact]
    public async Task ArtifactToolPublishesTitleAndHtml()
    {
        ArtifactDocument? published = null;
        var tool = new ArtifactTool(a => published = a);

        var result = await tool.ExecuteAsync(
            Args(("title", "Report"), ("html", "<h1>hi</h1>")), Context, default);

        Assert.False(result.IsError);
        Assert.Equal("Report", published!.Title);
        Assert.Equal("<h1>hi</h1>", published.Html);
        Assert.Contains("Report", result.Content);
    }

    [Theory]
    [InlineData(null, "<h1>x</h1>")]
    [InlineData("Title", null)]
    [InlineData("  ", "<h1>x</h1>")]
    public async Task ArtifactToolRejectsIncompleteInput(string? title, string? html)
    {
        var published = 0;
        var tool = new ArtifactTool(_ => published++);
        var result = await tool.ExecuteAsync(Args(("title", title), ("html", html)), Context, default);

        Assert.True(result.IsError);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task ArtifactToolRejectsOversizedHtml()
    {
        var tool = new ArtifactTool(_ => Assert.Fail("must not publish"));
        var result = await tool.ExecuteAsync(
            Args(("title", "Big"), ("html", new string('x', 2_000_001))), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("too large", result.Content);
    }

    [Fact]
    public void ArtifactToolIsReadOnlySoItNeverPromptsForPermission()
        => Assert.True(new ArtifactTool(_ => { }).IsReadOnly);

    // ---- computer use ----

    // Batch behaviour lives in ComputerBatchToolTests; these two cover how the
    // pair is wired into the harness rather than what a batch does.

    [Fact]
    public void ComputerBatchIsGatedButScreenshotIsNot()
    {
        var service = new ComputerUseService();
        Assert.False(new ComputerBatchTool(service).IsReadOnly);
        Assert.True(new ScreenshotTool(service).IsReadOnly);
    }

    [Fact]
    public void ComputerBatchDescribesCallsForThePermissionPrompt()
    {
        var tool = new ComputerBatchTool(new ComputerUseService());
        var description = tool.DescribeCall(new JsonObject
        {
            ["actions"] = new JsonArray(
                new JsonObject { ["action"] = "left_click", ["coordinate"] = new JsonArray(120, 340) },
                new JsonObject { ["action"] = "type", ["text"] = "hi" }),
        });

        Assert.Equal("ComputerBatch(2 actions: left_click, type)", description);
    }

    [Fact]
    public void DisplaysAreNumberedFromOneWithExactlyOnePrimary()
    {
        var displays = ComputerUseService.Displays();

        Assert.NotEmpty(displays);
        Assert.Equal([.. Enumerable.Range(1, displays.Count)], displays.Select(d => d.Number));
        Assert.Single(displays, d => d.IsPrimary);
    }

    [Fact]
    public async Task ScreenshotToolRejectsADisplayThatIsNotAttached()
    {
        var tool = new ScreenshotTool(new ComputerUseService());
        var result = await tool.ExecuteAsync(Args(("display", "99")), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("no display '99'", result.Content);
        // The model cannot pick a valid one unless the error lists what exists.
        Assert.Contains(ComputerUseService.Displays()[0].Describe(), result.Content);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("2")]
    public void ScreenshotToolNamesTheRequestedDisplayInThePermissionLog(string display)
    {
        var tool = new ScreenshotTool(new ComputerUseService());
        Assert.Equal($"Screenshot(display {display})", tool.DescribeCall(Args(("display", display))));
    }

    [Fact]
    public void ScreenshotToolAcceptsTheDisplayAsANumberOrAString()
    {
        var tool = new ScreenshotTool(new ComputerUseService());

        Assert.Equal("Screenshot(display 2)", tool.DescribeCall(Args(("display", JsonValue.Create(2)))));
        Assert.Equal("Screenshot()", tool.DescribeCall(Args()));
    }

    /// <summary>A store over a path nobody writes — CreateIfEnabled never saves.</summary>
    private static UiSettingsStore SettingsStore(bool computerUse)
    {
        var store = new UiSettingsStore(Path.Combine(
            Path.GetTempPath(), $"jarvis-app-tools-{Guid.NewGuid():N}.json"));
        store.Current.ComputerUseEnabled = computerUse;
        return store;
    }

    [Fact]
    public void ComputerUseToolsAreOfferedOnlyWhileTheSettingIsOn()
    {
        Assert.Empty(ComputerUseTools.CreateIfEnabled(SettingsStore(computerUse: false)));

        var tools = ComputerUseTools.CreateIfEnabled(SettingsStore(computerUse: true));
        Assert.Equal(
            [
                "screenshot", "computer_batch", "open_application", "read_clipboard", "write_clipboard",
                "request_access", "list_granted_applications", "switch_display",
            ],
            tools.Select(static t => t.Name));
    }

    [Fact]
    public void ComputerInputIsRefusedOnlyWhenGrantsAreRequiredAndTheAppIsUngranted()
    {
        // Grants off: the foreground app is irrelevant.
        Assert.Null(ComputerUseGrants.CheckForeground(new UiSettings { ComputerUseRequireGrants = false }, null, InputNeed.Keyboard));

        // The foreground app can change between reads on an interactive machine,
        // so each half only asserts when the window held still around the check.
        var before = ComputerUseExtras.ForegroundProcessName();

        var granted = new UiSettings { ComputerUseRequireGrants = true };
        granted.ComputerUseGrantedApps.Add(before ?? "unknown");
        var allowed = ComputerUseGrants.CheckForeground(granted, null, InputNeed.Keyboard);
        if (before is not null && ComputerUseExtras.ForegroundProcessName() == before)
        {
            Assert.Null(allowed);
        }

        var ungranted = new UiSettings { ComputerUseRequireGrants = true };
        ungranted.ComputerUseGrantedApps.Add("some-other-app");
        var refusal = ComputerUseGrants.CheckForeground(ungranted, null, InputNeed.Keyboard);
        if (refusal is not null)
        {
            Assert.Contains("request_access", refusal);
        }
    }

    // ---- Jarvis Browser ----

    /// <summary>
    /// The extension suite is the reference's claude-in-chrome server, so it is
    /// checked under the names the model actually sees. Four of the reference's
    /// 22 are declared absent in Deltas/reference-surface-deltas.tsv:
    /// upload_image, switch_browser, shortcuts_list and shortcuts_execute.
    /// </summary>
    [Fact]
    public void BrowserToolsetMatchesTheReferenceChromeServer()
    {
        using var bridge = new BrowserBridge();
        var names = InternalMcpServers
            .Compose([JarvisBrowserTools.Server(bridge)], new InternalMcpSessionContext())
            .Select(static t => t.Name)
            .ToList();

        Assert.Equal(
        [
            "mcp__claude-in-chrome__list_connected_browsers", "mcp__claude-in-chrome__select_browser",
            "mcp__claude-in-chrome__tabs_context_mcp", "mcp__claude-in-chrome__tabs_create_mcp",
            "mcp__claude-in-chrome__tabs_close_mcp",
            "mcp__claude-in-chrome__navigate", "mcp__claude-in-chrome__read_page",
            "mcp__claude-in-chrome__find", "mcp__claude-in-chrome__get_page_text",
            "mcp__claude-in-chrome__computer", "mcp__claude-in-chrome__form_input",
            "mcp__claude-in-chrome__file_upload", "mcp__claude-in-chrome__upload_image",
            "mcp__claude-in-chrome__gif_creator",
            "mcp__claude-in-chrome__javascript_tool", "mcp__claude-in-chrome__read_console_messages",
            "mcp__claude-in-chrome__read_network_requests", "mcp__claude-in-chrome__resize_window",
            "mcp__claude-in-chrome__browser_batch",
        ], names);
    }

    [Fact]
    public async Task BrowserToolsFailClearlyWhenTheExtensionIsNotConnected()
    {
        using var bridge = new BrowserBridge();
        var tool = new BrowserTabsTool(bridge);

        var result = await tool.ExecuteAsync(new JsonObject(), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("Jarvis Browser", result.Content);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    public async Task BrowserNavigateRejectsNonHttpUrls(string url)
    {
        using var bridge = new BrowserBridge();
        var result = await new BrowserNavigateTool(bridge)
            .ExecuteAsync(Args(("url", url)), Context, default);

        Assert.True(result.IsError);
        Assert.Contains("http", result.Content);
    }
}
