using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

// The inspection half of the Jarvis Browser toolset, ported from the reference
// app's Browser pane: page text, find, form input, JS eval, console, network,
// viewport emulation and tab management. Formatting lives in
// JarvisBrowserFormat so it stays testable without a browser.

public sealed class BrowserGetPageTextTool(BrowserBridge bridge) : ITool
{
    public string Name => "get_page_text";

    public string Description =>
        "Extract raw text content from the page, prioritizing article content. Ideal for reading articles, " +
        "blog posts, or other text-heavy pages. Returns plain text without HTML formatting. If you don't " +
        "have a valid tab ID, use tabs_context_mcp first to get available tabs.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "get_page_text");

    public bool IsReadOnly => true;
    public string DescribeCall(JsonObject arguments) => "BrowserGetPageText()";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JarvisBrowserTools.TabArgs(arguments);
        args["maxChars"] = JsonArgs.GetInt(arguments, "max_chars") ?? 60000;
        return JarvisBrowserTools.Run(bridge, "read_page", args,
            data =>
            {
                var text = data["text"]?.GetValue<string>() ?? "";
                var tail = data["truncated"]?.GetValue<bool>() == true ? "\n[text truncated — raise max_chars]" : "";
                return $"# {data["title"]}\n{data["url"]}\n\n{context.Truncate(text, "page text")}{tail}";
            },
            cancellationToken);
    }
}

public sealed class BrowserFindTool(BrowserBridge bridge) : ITool
{
    public string Name => "find";

    public string Description =>
        "Find elements on the page using natural language. Can search for elements by their purpose (e.g., " +
        "\"search bar\", \"login button\") or by text content (e.g., \"organic mango product\"). Returns up " +
        "to 20 matching elements with references that can be used with other tools. If more than 20 matches " +
        "exist, you'll be notified to use a more specific query. If you don't have a valid tab ID, use " +
        "tabs_context_mcp first to get available tabs.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "find");

    public bool IsReadOnly => true;
    public string DescribeCall(JsonObject arguments) => $"BrowserFind({JsonArgs.GetString(arguments, "query")})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var query = JsonArgs.GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(ToolResult.Error("query is required."));
        }

        var args = JarvisBrowserTools.TabArgs(arguments);
        args["filter"] = "all";
        return JarvisBrowserTools.Run(bridge, "a11y", args,
            data => JarvisBrowserFormat.FindMatches(data, query),
            cancellationToken);
    }
}

public sealed class BrowserFormInputTool(BrowserBridge bridge) : ITool
{
    public string Name => "form_input";

    public string Description =>
        "Set values in form elements using element reference ID from the read_page tool. If you don't have " +
        "a valid tab ID, use tabs_context_mcp first to get available tabs.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "form_input");

    public bool IsReadOnly => false;
    public string DescribeCall(JsonObject arguments) => $"BrowserFormInput({JarvisBrowserFormat.ParseRef(arguments["ref"])})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (JarvisBrowserFormat.ParseRef(arguments["ref"]) is not { } reference || arguments["value"] is null)
        {
            return Task.FromResult(ToolResult.Error("ref and value are required."));
        }

        var args = JarvisBrowserTools.TabArgs(arguments);
        reference.ApplyTo(args);
        args["value"] = arguments["value"]!.DeepClone();
        return JarvisBrowserTools.Run(bridge, "form_input", args,
            data => data["checked"] is not null
                ? $"{reference} is now {(data["checked"]!.GetValue<bool>() ? "checked" : "unchecked")}."
                : $"Set {reference}.",
            cancellationToken);
    }
}

public sealed class BrowserJavaScriptTool(BrowserBridge bridge) : ITool
{
    public string Name => "javascript_tool";
    public string Description =>
        "Execute JavaScript code in the context of the current page. The code runs in the page's context " +
        "and can interact with the DOM, window object, and page variables. Returns the result of the last " +
        "expression or any thrown errors. If you don't have a valid tab ID, use tabs_context_mcp first to " +
        "get available tabs.";
    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "javascript_tool");

    public bool IsReadOnly => false;
    public string DescribeCall(JsonObject arguments)
    {
        var code = JsonArgs.GetString(arguments, "code") ?? "";
        return $"BrowserJavaScript({(code.Length > 60 ? code[..60] + "…" : code)})";
    }

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        // The reference names this argument `text`; `code` is still read so a
        // session stored before the rename replays.
        var code = JsonArgs.GetString(arguments, "text") ?? JsonArgs.GetString(arguments, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            return Task.FromResult(ToolResult.Error("text is required."));
        }

        var args = JarvisBrowserTools.TabArgs(arguments);
        args["code"] = code;
        return JarvisBrowserTools.Run(bridge, "js_exec", args,
            data =>
            {
                var value = data["value"];
                var rendered = value is JsonValue json && json.TryGetValue<string>(out var text)
                    ? text
                    : value?.ToJsonString() ?? "undefined";
                return context.Truncate(rendered, "result");
            },
            cancellationToken);
    }
}

public sealed class BrowserConsoleTool(BrowserBridge bridge) : ITool
{
    public string Name => "read_console_messages";

    public string Description =>
        "Read browser console messages (console.log, console.error, console.warn, etc.) from a specific " +
        "tab. Useful for debugging JavaScript errors, viewing application logs, or understanding what's " +
        "happening in the browser console. Returns console messages from the current domain only. If you " +
        "don't have a valid tab ID, use tabs_context_mcp first to get available tabs. IMPORTANT: Always " +
        "provide a pattern to filter messages - without a pattern, you may get too many irrelevant messages.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "read_console_messages");

    public bool IsReadOnly => true;
    public string DescribeCall(JsonObject arguments) =>
        OnlyErrors(arguments) ? "BrowserConsole(errors)" : "BrowserConsole()";

    /// <summary>
    /// The reference's spelling first, this port's older one after it — the
    /// argument was renamed with the schemas and a stored session still replays.
    /// </summary>
    private static bool OnlyErrors(JsonObject arguments) =>
        JsonArgs.GetBool(arguments, "onlyErrors") || JsonArgs.GetBool(arguments, "only_errors");

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JarvisBrowserTools.TabArgs(arguments);

        // "clear the console messages after reading to avoid duplicates on
        // subsequent calls" — the extension empties the buffer it just returned.
        if (JsonArgs.GetBool(arguments, "clear"))
        {
            args["clear"] = true;
        }

        return JarvisBrowserTools.Run(bridge, "console_read", args,
            data => context.Truncate(
                JarvisBrowserFormat.FormatConsole(
                    data,
                    OnlyErrors(arguments),
                    JsonArgs.GetString(arguments, "pattern"),
                    JsonArgs.GetInt(arguments, "limit") ?? 100),
                "console output"),
            cancellationToken);
    }
}

public sealed class BrowserNetworkTool(BrowserBridge bridge) : ITool
{
    public string Name => "read_network_requests";

    public string Description =>
        "Read HTTP network requests (XHR, Fetch, documents, images, etc.) from a specific tab. Useful for " +
        "debugging API calls, monitoring network activity, or understanding what requests a page is making. " +
        "Returns all network requests made by the current page, including cross-origin requests. Requests " +
        "are automatically cleared when the page navigates to a different domain. If you don't have a valid " +
        "tab ID, use tabs_context_mcp first to get available tabs.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "read_network_requests");

    public bool IsReadOnly => true;
    public string DescribeCall(JsonObject arguments)
        => RequestId(arguments) is { } id ? $"BrowserNetwork({id})" : "BrowserNetwork()";

    /// <summary>The reference's spelling, then this port's older one.</summary>
    private static string? RequestId(JsonObject arguments) =>
        JsonArgs.GetString(arguments, "requestId") ?? JsonArgs.GetString(arguments, "request_id");

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JarvisBrowserTools.TabArgs(arguments);
        if (RequestId(arguments) is { } requestId)
        {
            args["requestId"] = requestId;
            return JarvisBrowserTools.Run(bridge, "network_read", args,
                data =>
                {
                    var request = data["request"] as JsonObject;
                    var head = $"{request?["method"]} {request?["url"]} → {request?["status"]}";
                    if (data["base64Encoded"]?.GetValue<bool>() == true)
                    {
                        return $"{head}\n[binary body ({request?["mimeType"]}) — not shown]";
                    }

                    return $"{head}\n\n{context.Truncate(data["body"]?.GetValue<string>() ?? "", "response body")}";
                },
                cancellationToken);
        }

        if (JsonArgs.GetBool(arguments, "clear"))
        {
            args["clear"] = true;
        }

        return JarvisBrowserTools.Run(bridge, "network_read", args,
            data => context.Truncate(
                JarvisBrowserFormat.FormatNetwork(
                    data,
                    JsonArgs.GetString(arguments, "urlPattern") ?? JsonArgs.GetString(arguments, "url_pattern"),
                    JsonArgs.GetInt(arguments, "limit") ?? 100),
                "network log"),
            cancellationToken);
    }
}

public sealed class BrowserResizeTool(BrowserBridge bridge) : ITool
{
    public string Name => "resize_window";

    public string Description =>
        "Resize the current browser window to specified dimensions. Useful for testing responsive designs " +
        "or setting up specific screen sizes. If you don't have a valid tab ID, use tabs_context_mcp first " +
        "to get available tabs.";

    /// <summary>
    /// The reference's three properties, all required. This port's own
    /// <c>preset</c> and <c>color_scheme</c> are still read by the body — a
    /// session stored before this replay — but they are no longer advertised,
    /// because the reference declares neither and the Browser pane's own
    /// resize_window, which does declare them, is where that contract lives.
    /// </summary>
    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "resize_window");

    public bool IsReadOnly => false;
    public string DescribeCall(JsonObject arguments)
        => $"BrowserResize({JsonArgs.GetString(arguments, "preset") ?? $"{JsonArgs.GetInt(arguments, "width")}x{JsonArgs.GetInt(arguments, "height")}"})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JarvisBrowserTools.TabArgs(arguments);
        var preset = JsonArgs.GetString(arguments, "preset");
        var width = JsonArgs.GetInt(arguments, "width");
        var height = JsonArgs.GetInt(arguments, "height");

        switch (preset)
        {
            case "mobile":
                args["width"] = 375; args["height"] = 812; args["mobile"] = true;
                break;
            case "tablet":
                args["width"] = 768; args["height"] = 1024;
                break;
            case "desktop":
                args["reset"] = true;
                break;
            default:
                if (width is null || height is null)
                {
                    return Task.FromResult(ToolResult.Error("Pass a preset (mobile/tablet/desktop) or both width and height."));
                }

                args["width"] = width; args["height"] = height;
                if (width < 768)
                {
                    args["mobile"] = true;
                }

                break;
        }

        if (JsonArgs.GetString(arguments, "color_scheme") is { } scheme)
        {
            args["colorScheme"] = scheme;
        }

        return JarvisBrowserTools.Run(bridge, "resize", args,
            data => data["applied"] is JsonArray applied && applied.Count > 0
                ? $"Applied: {string.Join("; ", applied.Select(static a => a?.GetValue<string>()))}."
                : "Nothing to apply.",
            cancellationToken);
    }
}

public sealed class BrowserTabNewTool(BrowserBridge bridge) : ITool
{
    public string Name => "tabs_create_mcp";

    public string Description =>
        "Creates a new empty tab in the MCP tab group. CRITICAL: You must get the context using " +
        "tabs_context_mcp at least once before using other browser automation tools so you know what tabs " +
        "exist. Tabs you create are yours to clean up: close each one with tabs_close_mcp as soon as you no " +
        "longer need it, and close any that remain before finishing your task. Leave a tab open only if the " +
        "user asked to see it or wants it kept open.";

    /// <summary>
    /// The reference takes no arguments — the tab it creates is empty. The old
    /// <c>url</c> is still read by the body so a stored session replays.
    /// </summary>
    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "tabs_create_mcp");

    public bool IsReadOnly => false;
    public string DescribeCall(JsonObject arguments) => "BrowserTabNew()";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = new JsonObject();
        if (JsonArgs.GetString(arguments, "url") is { } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                return Task.FromResult(ToolResult.Error("A valid http(s) url is required."));
            }

            args["url"] = url;
        }

        return JarvisBrowserTools.Run(bridge, "create_tab", args,
            data => $"Opened tab {data["tabId"]}.",
            cancellationToken);
    }
}

public sealed class BrowserTabCloseTool(BrowserBridge bridge) : ITool
{
    public string Name => "tabs_close_mcp";

    public string Description =>
        "Close a tab in the MCP tab group by its ID. Use to clean up tabs you're done with. Only tabs in " +
        "this session's group are closable; call tabs_context_mcp first to get valid IDs. If you close the " +
        "group's last tab, Chrome auto-removes the group — the next tabs_context_mcp with createIfEmpty " +
        "starts fresh.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "tabs_close_mcp");

    public bool IsReadOnly => false;
    public string DescribeCall(JsonObject arguments) =>
        $"BrowserTabClose({JarvisBrowserTools.GetTabId(arguments)})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (JarvisBrowserTools.GetTabId(arguments) is not { } tabId)
        {
            return Task.FromResult(ToolResult.Error("tabId is required."));
        }

        return JarvisBrowserTools.Run(bridge, "close_tab", new JsonObject { ["tabId"] = tabId },
            data => $"Closed tab {tabId}.",
            cancellationToken);
    }
}
