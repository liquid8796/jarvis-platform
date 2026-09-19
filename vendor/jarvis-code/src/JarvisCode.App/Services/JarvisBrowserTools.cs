using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>Agent tools backed by the Jarvis Browser extension.</summary>
public static class JarvisBrowserTools
{
    /// <summary>Commands that touch a page, and so need the site's consent first.</summary>
    private static readonly HashSet<string> PageFacing =
    [
        "navigate", "read_page", "find", "get_page_text", "computer",
        "form_input", "file_upload", "upload_image", "gif_creator", "javascript_tool",
        "read_console_messages", "read_network_requests", "resize_window", "qa",
    ];

    /// <summary>
    /// The names these tools carried before they took the reference's, kept as
    /// resolvable aliases by <see cref="InternalMcpServers"/> so a stored session
    /// still replays. Never advertised.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> Legacy = new(StringComparer.Ordinal)
    {
        ["navigate"] = ["browser_navigate"],
        ["computer"] = ["browser_computer"],
        ["read_page"] = ["browser_read_page"],
        ["find"] = ["browser_find"],
        ["get_page_text"] = ["browser_get_page_text"],
        ["form_input"] = ["browser_form_input"],
        ["javascript_tool"] = ["browser_javascript"],
        ["read_console_messages"] = ["browser_console"],
        ["read_network_requests"] = ["browser_network"],
        ["resize_window"] = ["browser_resize"],
        ["file_upload"] = ["browser_file_upload"],
        ["gif_creator"] = ["browser_gif"],
        ["list_connected_browsers"] = ["browser_list_browsers"],
        ["select_browser"] = ["browser_select_browser"],
        ["tabs_context_mcp"] = ["browser_tabs"],
        ["tabs_create_mcp"] = ["browser_tab_new"],
        ["tabs_close_mcp"] = ["browser_tab_close"],
    };

    /// <summary>
    /// The extension suite as the reference's <c>claude-in-chrome</c> in-process
    /// MCP server. Like the reference's own definition, it contributes nothing
    /// while no browser is connected — its <c>tools</c> getter returns an empty
    /// list unless the extension bridge is up, and a server with no tools is
    /// skipped entirely.
    /// </summary>
    public static InternalMcpServerDefinition Server(
        BrowserBridge bridge,
        string? imageDirectory = null,
        BrowserOriginGate? origins = null) =>
        new(InternalMcpServerNames.ClaudeInChrome, Create(bridge, imageDirectory, origins))
        {
            LegacyNames = Legacy,
            // Deliberately not gated on the session type: the reference asks
            // shouldEnableChromeExtensionBridge() && !isDisabled(), which is a
            // question about the bridge rather than about the surface.
            IsEnabled = static context => context.ChromeExtensionEnabled,
            Instructions = McpServerInstructions.ClaudeInChrome,

            // The reference's Nln gates this block on tool search being
            // available and some tool of this server actually being deferred.
            // This server never sets AlwaysLoad, so those two are the same
            // question here: is deferral engaged this turn.
            AreInstructionsEnabled = static context => context.ToolSearchAvailable,
        };

    /// <param name="imageDirectory">Where computer's save_to_disk writes; null uses the temp folder.</param>
    /// <param name="origins">
    /// The per-site consent list. Pass the app's own so a grant outlives a tool
    /// rebuild — the list is rebuilt on every settings save and session switch.
    /// </param>
    public static IReadOnlyList<ITool> Create(
        BrowserBridge bridge,
        string? imageDirectory = null,
        BrowserOriginGate? origins = null,
        bool enableQa = false)
    {
        var gate = origins ?? new BrowserOriginGate(bridge);
        List<ITool> tools =
        [
            new BrowserListBrowsersTool(bridge),
            new BrowserSelectBrowserTool(bridge),
            new BrowserTabsTool(bridge),
            new BrowserTabNewTool(bridge),
            new BrowserTabCloseTool(bridge),
            new BrowserNavigateTool(bridge),
            new BrowserReadPageTool(bridge),
            new BrowserFindTool(bridge),
            new BrowserGetPageTextTool(bridge),
            new BrowserComputerTool(bridge, imageDirectory),
            new BrowserFormInputTool(bridge),
            new BrowserFileUploadTool(bridge),
            new BrowserUploadImageTool(bridge),
            new BrowserGifTool(bridge),
            new BrowserJavaScriptTool(bridge),
            new BrowserConsoleTool(bridge),
            new BrowserNetworkTool(bridge),
            new BrowserResizeTool(bridge),
        ];
        if (enableQa) tools.Add(new BrowserQaTool(bridge, imageDirectory));
        for (var i = 0; i < tools.Count; i++)
        {
            if (PageFacing.Contains(tools[i].Name))
            {
                tools[i] = new OriginGatedTool(tools[i], gate);
            }
        }

        // "Inside browser_batch, navigate (and other tools that act on a page)
        // requires an explicit tabId": a batch step may not open a tab for
        // itself, so the step resolves to a navigate that refuses instead.
        ITool batchNavigate = new OriginGatedTool(new BrowserNavigateTool(bridge, inBatch: true), gate);

        // The batch resolves its steps out of this same list, so they are gated too.
        // Steps may name a tool bare or fully qualified; the reference normalises
        // the server prefix off before resolving, so both forms work.
        tools.Add(new BrowserBatchTool(name =>
        {
            var shortName = InternalMcpServers.ShortName(name);
            return shortName == "navigate"
                ? batchNavigate
                : tools.FirstOrDefault(t => t.Name == shortName);
        }));
        return tools;
    }

    internal static async Task<ToolResult> Run(
        BrowserBridge bridge, string cmd, JsonObject? args, Func<JsonObject, string> render,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await bridge.RequestAsync(cmd, args, cancellationToken);
            return ToolResult.Success(render(data));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    /// <summary>
    /// The reference spells this argument <c>tabId</c> on every tool of this
    /// server. The old <c>tab_id</c> is still read so a session stored before
    /// the rename replays, the same accommodation the legacy tool names get.
    /// </summary>
    internal static int? GetTabId(JsonObject arguments)
    {
        var raw = JarvisBrowserFormat.AsNumber(arguments["tabId"])
                  ?? JarvisBrowserFormat.AsNumber(arguments["tab_id"]);
        return raw is { } id ? (int)id : null;
    }

    internal static JsonObject TabArgs(JsonObject arguments)
    {
        var args = new JsonObject();
        if (GetTabId(arguments) is { } tabId)
        {
            args["tabId"] = tabId;
        }

        return args;
    }
}

public sealed class BrowserListBrowsersTool(BrowserBridge bridge) : ITool
{
    public string Name => "list_connected_browsers";

    public string Description =>
        "List all Chrome browsers (extension instances) currently connected to this account. Returns each " +
        "browser's deviceId, display name, OS platform, and whether it appears to be on this computer. Use " +
        "this before select_browser to present choices to the user.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "list_connected_browsers");

    public bool IsReadOnly => true;
    public string DescribeCall(JsonObject arguments) => "BrowserListBrowsers()";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var connections = bridge.Connections;

        // The reference's four fields, answered from what a local pipe knows: the
        // connection id is the deviceId, the name comes from the UA the extension
        // announced, and every connection is by definition on this computer —
        // there is no relay here for a remote one to arrive over.
        return Task.FromResult(ToolResult.Success(connections.Count == 0
            ? "No browser is connected. The extension connects when a browser with it installed is running."
            : string.Join('\n', connections.Select(static c =>
                $"[{c.Id}] {(c.Active ? "* " : "")}{(c.Name.Length > 0 ? c.Name : "(no name yet)")} — " +
                $"{Environment.OSVersion.Platform}, on this computer" +
                (c.Ready ? "" : " — extension still starting")))));
    }
}

public sealed class BrowserSelectBrowserTool(BrowserBridge bridge) : ITool
{
    public string Name => "select_browser";

    public string Description =>
        "Select a specific Chrome browser by deviceId for browser automation, without broadcasting a " +
        "pairing request. Use this after list_connected_browsers when the user has chosen one from the list.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "select_browser");

    public bool IsReadOnly => true;
    public string DescribeCall(JsonObject arguments) => $"BrowserSelectBrowser({Target(arguments)})";

    /// <summary>
    /// The reference's <c>deviceId</c>. The old <c>browser</c> spelling still
    /// resolves so a stored session replays, and a name is still accepted
    /// because this bridge's ids and names are both stable local handles.
    /// </summary>
    private static string? Target(JsonObject arguments) =>
        JsonArgs.GetString(arguments, "deviceId") ?? JsonArgs.GetString(arguments, "browser");

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var target = Target(arguments);
        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult(ToolResult.Error("deviceId is required (from list_connected_browsers)."));
        }

        var error = bridge.SelectBrowser(target.Trim());
        return Task.FromResult(error is null
            ? ToolResult.Success($"Browser commands now go to {target.Trim()}.")
            : ToolResult.Error(error));
    }
}

public sealed class BrowserTabsTool(BrowserBridge bridge) : ITool
{
    public string Name => "tabs_context_mcp";

    public string Description =>
        "Get context information about the current MCP tab group. Returns all tab IDs inside the group if " +
        "it exists. CRITICAL: You must get the context at least once before using other browser automation " +
        "tools so you know what tabs exist. Each new conversation should create its own new tab (using " +
        "tabs_create_mcp) rather than reusing existing tabs, unless the user explicitly asks to use an " +
        "existing tab.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "tabs_context_mcp");

    public bool IsReadOnly => true;
    public string DescribeCall(JsonObject arguments) => "BrowserTabs()";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        // "Returns all tab IDs inside the group if it exists" — and with
        // createIfEmpty the reference starts one rather than answering empty,
        // which is what lets a standalone navigate omit its tabId.
        var listing = await JarvisBrowserTools.Run(bridge, "tabs", null, Render, cancellationToken);
        if (arguments["createIfEmpty"]?.GetValue<bool>() != true ||
            listing.IsError ||
            listing.Content.Length > 0)
        {
            return listing;
        }

        var created = await JarvisBrowserTools.Run(
            bridge, "create_tab", [], static data => $"Opened tab {data["tabId"]}.", cancellationToken);
        return created.IsError
            ? created
            : await JarvisBrowserTools.Run(bridge, "tabs", null, Render, cancellationToken);

        static string Render(JsonObject data) => data["result"] is JsonArray tabs
            ? string.Join("\n", tabs.Select(static t =>
                $"[{t?["id"]}] {(t?["active"]?.GetValue<bool>() == true ? "* " : "")}{t?["title"]} — {t?["url"]}"))
            : data.ToJsonString();
    }
}

public sealed class BrowserNavigateTool(BrowserBridge bridge, bool inBatch = false) : ITool
{
    public string Name => "navigate";

    public string Description =>
        "Navigate to a URL, or go forward/back in browser history. tabId may be omitted for URL navigation " +
        "when calling navigate STANDALONE (not inside browser_batch): tabs_context_mcp{createIfEmpty:true} " +
        "is called for you and the first tab in the session's group is navigated — its result is appended to " +
        "this call's output so you have the tab list and ids for subsequent calls. Inside browser_batch, " +
        "navigate (and other tools that act on a page) requires an explicit tabId. Pass an explicit tabId " +
        "when you need a specific tab or when the session's group has multiple tabs whose state you must " +
        "preserve. tabId is required for url:\"back\"/\"forward\". A tab opened for you this way is yours to " +
        "clean up, the same as one from tabs_create_mcp: close it with tabs_close_mcp once you no longer " +
        "need it and before finishing your task, unless the user asked to see it or wants it kept open.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "navigate");

    public bool IsReadOnly => false;
    public string DescribeCall(JsonObject arguments) => $"BrowserNavigate({JsonArgs.GetString(arguments, "url")})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var url = JsonArgs.GetString(arguments, "url");
        var isHistory = url is "back" or "forward";
        if (isHistory)
        {
            // "tabId is required for url:'back'/'forward'" — there is no
            // history to move through until a tab has been named.
            if (JarvisBrowserTools.GetTabId(arguments) is null)
            {
                return ToolResult.Error("tabId is required for url:\"back\"/\"forward\".");
            }
        }
        else
        {
            // "Can be provided with or without protocol (defaults to https://)."
            if (!string.IsNullOrWhiteSpace(url) &&
                !url.Contains("://", StringComparison.Ordinal))
            {
                url = "https://" + url.TrimStart();
            }

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return ToolResult.Error("A valid http(s) url (or \"back\"/\"forward\") is required.");
            }
        }

        var args = JarvisBrowserTools.TabArgs(arguments);
        args["url"] = url;

        // The doc's own promise for a standalone navigate: tabs_context_mcp
        // {createIfEmpty:true} runs first and the group's FIRST tab is
        // navigated — not a new one — and that listing is appended so the
        // caller has the ids it will need next.
        if (inBatch && JarvisBrowserTools.GetTabId(arguments) is null)
        {
            return ToolResult.Error(
                "tabId is required inside browser_batch. Call tabs_context_mcp first, then pass the tab id " +
                "on every step.");
        }

        ToolResult? listing = null;
        if (!isHistory && JarvisBrowserTools.GetTabId(arguments) is null)
        {
            listing = await new BrowserTabsTool(bridge).ExecuteAsync(
                new JsonObject { ["createIfEmpty"] = true }, context, cancellationToken);
            if (listing.IsError)
            {
                return listing;
            }
            var match = System.Text.RegularExpressions.Regex.Match(listing.Content, @"(?m)^\[(\d+)\]");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var selectedTab))
                return ToolResult.Error("Browser context did not return an owned tab ID.");
            args["tabId"] = selectedTab;
        }

        var navigated = await JarvisBrowserTools.Run(bridge, "navigate", args,
            data => isHistory
                ? $"Went {url} in tab {data["tabId"]}."
                : $"Opened {url} in tab {data["tabId"]}. Use read_page to read it.",
            cancellationToken);

        return listing is null || navigated.IsError
            ? navigated
            : navigated with { Content = navigated.Content + "\n\n" + listing.Content };
    }
}

public sealed class BrowserReadPageTool(BrowserBridge bridge) : ITool
{
    public string Name => "read_page";

    public string Description =>
        "Get an accessibility tree representation of elements on the page. By default returns all elements " +
        "including non-visible ones. Output is limited to 50000 characters by default. If the output " +
        "exceeds this limit it is truncated at a line boundary, with a note giving the full size — pass a " +
        "larger max_chars, or use depth/ref_id to focus on part of the page. Optionally filter for only " +
        "interactive elements. If you don't have a valid tab ID, use tabs_context_mcp first to get " +
        "available tabs.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "read_page");

    public bool IsReadOnly => true;
    public string DescribeCall(JsonObject arguments) => "BrowserReadPage()";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JarvisBrowserTools.TabArgs(arguments);
        // The reference's default is the whole tree; a caller has to ask for less.
        args["filter"] = JsonArgs.GetString(arguments, "filter") == "interactive" ? "interactive" : "all";

        if (JarvisBrowserFormat.ParseRef(arguments["ref_id"]) is { } rootRef)
        {
            args["rootRef"] = rootRef.Value;
            if (rootRef.DocumentId is not null) args["documentId"] = rootRef.DocumentId;
            if (rootRef.FrameId != 0)
            {
                args["frameId"] = rootRef.FrameId;
            }
        }

        var maxChars = JsonArgs.GetInt(arguments, "max_chars") ?? 50000;
        return JarvisBrowserTools.Run(bridge, "a11y", args,
            data => JarvisBrowserFormat.RenderTree(data, maxChars),
            cancellationToken);
    }
}
