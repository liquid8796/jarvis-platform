using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.Services;

/// <summary>
/// The Browser pane's agent tool surface — the reference desktop's
/// mcp__Claude_Browser__* suite, with the reference tool docs, schemas, result
/// strings and batch semantics (measured from the installed app's asar and its
/// live pane), driving the in-app Browser side panel through
/// <see cref="BrowserPaneHandlers"/>. Distinct from the browser_* family, which
/// drives the user's own browser via the Jarvis Browser extension.
/// preview_start and friends complete the pane suite from PreviewTools.
/// </summary>
public static class BrowserPaneTools
{
    /// <summary>
    /// The wire prefix these tools reach the model under. They are declared bare
    /// and <see cref="InternalMcpServers"/> applies it, so this constant exists
    /// for callers that need the full name rather than as the source of it.
    /// </summary>
    public static readonly string Prefix =
        InternalMcpServers.Prefix + InternalMcpServerNames.ClaudeBrowser + "__";

    /// <summary>
    /// The pane suite as an in-process MCP server definition. Whether it reaches
    /// the model at all is Settings › Jarvis Code › Browser › "Browser tools",
    /// which the session context carries as <c>BrowserPaneLaunchEnabled</c>.
    /// </summary>
    public static InternalMcpServerDefinition Server(
        IBrowserPaneDriver driver,
        PreviewServers? previews = null,
        BrowserPaneDomainTransitions? transitions = null,
        PreviewOriginConsent? consent = null) =>
        new(InternalMcpServerNames.ClaudeBrowser, Create(driver, previews, transitions, consent))
        {
            // The reference's own predicate for a ccd session: the session type,
            // a local host, and its launchEnabled setting read as "!== false".
            IsEnabled = static context =>
                context.SessionType == InternalMcpSessionContext.CodeSessionType &&
                !context.IsSsh &&
                context.BrowserPaneLaunchEnabled &&
                context.HasBrowserPane,

            // The reference marks every tool of this server alwaysLoad at the
            // site where it pushes the definition, so the pane is drivable
            // without a tool_search round-trip first.
            AlwaysLoad = true,
        };

    public static IReadOnlyList<ITool> Create(
        IBrowserPaneDriver driver,
        PreviewServers? previews = null,
        BrowserPaneDomainTransitions? transitions = null,
        PreviewOriginConsent? consent = null)
    {
        // One card counter per pane: the reference keeps its origin policy per
        // window, so its suppression counts are the window's rather than a call's.
        var handlers = new BrowserPaneHandlers(
            driver, transitions, consent is null ? null : new PreviewOriginPrompt());
        if (consent is { } origins)
        {
            handlers.OriginCard = origins.Card;
            handlers.IsOriginAllowed = origins.IsAllowed;
            handlers.OriginAllowed = origins.Allow;
        }

        List<ITool> tools =
        [
            new PaneTool("navigate",
                "Navigate the Browser pane to a URL, or go \"back\"/\"forward\" in history. If the Browser pane " +
                "isn't open yet, this opens it at the URL (no dev server needed).",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["url"] = Prop("string", "The URL to navigate to. Can be provided with or without protocol " +
                        "(defaults to https://). Use \"forward\" to go forward in history or \"back\" to go back in history."),
                    ["force"] = Prop("boolean", "If the page shows a \"Leave site?\" dialog because of unsaved " +
                        "changes, discard those changes and navigate anyway. Defaults to false."),
                }, "url"),
                isReadOnly: false,
                static a => $"navigate({JsonArgs.GetString(a, "url")})",
                handlers.NavigateAsync)
            {
                // A domain transition is consented to on the turn's own card, so
                // navigate needs the context to reach the user. A browser_batch
                // step has none: the batch's own doc is what tells the model to
                // send that navigation on its own, where it can be asked about.
                ContextualExecute = (a, context, token) =>
                    handlers.NavigateAsync(a, DomainTransitionPrompt(context), token),
            },

            new PaneTool("computer",
                "Mouse/keyboard automation in the Browser pane. Clicks accept either `coordinate` (pixels in the " +
                "coordinate frame of the most recent `computer{action:\"screenshot\"}` — reported with every " +
                "scaled screenshot; equal to the image's pixels for unscaled ones) or `ref` (a `ref_N` from " +
                "read_page/find).",
                ComputerSchema(),
                isReadOnly: false,
                static a =>
                {
                    var action = JsonArgs.GetString(a, "action") ?? "?";
                    var target = JsonArgs.GetString(a, "ref") is { } r
                        ? $" {r}"
                        : a["coordinate"] is JsonArray { Count: 2 } p ? $" {p[0]},{p[1]}" : "";
                    if (action == "key" && JsonArgs.GetString(a, "text") is { } combo)
                    {
                        target = $" {combo}";
                    }

                    return $"computer({action}{target})";
                },
                handlers.ComputerAsync),

            new PaneTool("read_page",
                "Read the current page in the Browser pane as a YAML-style accessibility tree. Each interactive " +
                "element is tagged `[ref_N]` for use with `computer`/`form_input`/`find`. Prefer this over " +
                "screenshot for verifying text and structure. Output is limited to 50000 characters by default; " +
                "if it exceeds the limit it is truncated with a note — pass a larger max_chars, or use " +
                "ref_id/depth to focus.",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["filter"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("interactive", "all"),
                        ["description"] = "'interactive' returns only clickable/typable elements; 'all' (default) " +
                            "returns the full tree.",
                    },
                    ["depth"] = Prop("number", "Maximum tree depth to traverse (default: 15)."),
                    ["ref_id"] = Prop("string", "Restrict the tree to descendants of this `ref_N` (from a previous read_page)."),
                    ["max_chars"] = Prop("number", "Maximum characters of output (default: 50000)."),
                }),
                isReadOnly: true,
                static _ => "read_page()",
                handlers.ReadPageAsync),

            new PaneTool("find",
                "Search the current page in the Browser pane for elements whose accessibility-tree line (role / " +
                "name / text) contains `query`, case-insensitively. Returns up to 20 `ref_N` matches usable with " +
                "`computer`/`form_input`.",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["query"] = Prop("string", "Text to look for in an element's role, name or text " +
                        "(case-insensitive substring, e.g. \"Sign in\", \"search\", \"Add to cart\")."),
                }, "query"),
                isReadOnly: true,
                static a => $"find({JsonArgs.GetString(a, "query")})",
                handlers.FindAsync),

            new PaneTool("get_page_text",
                "Extract the visible text of the Browser pane's page (article/main content first, falls back to " +
                "body innerText).",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["max_chars"] = Prop("number", "Maximum characters of output (default: 50000)."),
                }),
                isReadOnly: true,
                static _ => "get_page_text()",
                handlers.GetPageTextAsync),

            new PaneTool("form_input",
                "Set the value of a form element identified by `ref` (from read_page/find). Handles " +
                "input/textarea/select/checkbox/contenteditable.",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["ref"] = Prop("string", "Element reference ID from the read_page tool (e.g., \"ref_1\", \"ref_2\")"),
                    ["value"] = new JsonObject
                    {
                        ["type"] = new JsonArray("string", "boolean", "number"),
                        ["description"] = "The value to set. For checkboxes use boolean, for selects use option " +
                            "value or text, for other inputs use appropriate string/number",
                    },
                }, "ref", "value"),
                isReadOnly: false,
                static a => $"form_input({JsonArgs.GetString(a, "ref")})",
                handlers.FormInputAsync),

            new PaneTool("javascript_tool",
                "Execute JavaScript in the Browser pane's page for DEBUGGING and INSPECTION only. Do NOT use this " +
                "to implement UI changes — edit source code instead.",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("javascript_exec"),
                        ["description"] = "Action to perform (only `javascript_exec` is supported).",
                    },
                    ["text"] = Prop("string", "The JavaScript code to execute. Evaluated in the page context with " +
                        "REPL semantics: top-level `await` works, and the result of the last expression is " +
                        "returned automatically — write the expression you want (e.g. `window.myData.value`, or " +
                        "`await fetch(url).then(r=>r.json())`) rather than `return ...`. Return values are " +
                        "serialized as JSON."),
                }, "action", "text"),
                isReadOnly: false,
                static a =>
                {
                    var code = JsonArgs.GetString(a, "text") ?? "";
                    return $"javascript_tool({(code.Length > 60 ? code[..60] + "…" : code)})";
                },
                handlers.JavaScriptAsync),

            new PaneTool("read_console_messages",
                "Get console output (log, info, warn, error, debug) from the Browser pane.",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["onlyErrors"] = Prop("boolean", "Return only error-level entries."),
                    ["pattern"] = Prop("string", "Substring filter on message text."),
                    ["limit"] = Prop("number", "Max entries to return (default: 50, max: 200)."),
                }),
                isReadOnly: true,
                static a => JsonArgs.GetBool(a, "onlyErrors") ? "read_console_messages(errors)" : "read_console_messages()",
                handlers.ConsoleAsync),

            new PaneTool("read_network_requests",
                "List network requests, or fetch a specific response body by `requestId`.",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["urlPattern"] = Prop("string", "Substring filter on request URL."),
                    ["requestId"] = Prop("string", "If provided, returns the response body for this request " +
                        "instead of listing."),
                    ["limit"] = Prop("number", "Max entries to return when listing (default: 50)."),
                }),
                isReadOnly: true,
                static a => JsonArgs.GetString(a, "requestId") is { } id
                    ? $"read_network_requests({id})"
                    : "read_network_requests()",
                handlers.NetworkAsync),

            new PaneTool("resize_window",
                "Emulate a viewport size in the Browser pane tab. Presets: mobile (375x812), tablet (768x1024), " +
                "or desktop, which clears the size emulation and returns the tab to the pane's own responsive " +
                "size. Custom sizes need both width and height. An emulated size stays on that tab across reloads " +
                "and navigation (scaled down to fit when it is larger than the pane) until you call this tool " +
                "again with preset \"desktop\", so reset it once you are done testing. colorScheme (light/dark) " +
                "emulates prefers-color-scheme on that tab; it survives reloads and preset \"desktop\" does not " +
                "touch it, but the pane re-syncs the tab to the app's light/dark theme when that theme changes or " +
                "the pane reopens; local documents and static HTML previews always render light. The mobile " +
                "preset (and any width < 768) also emulates a mobile device: Android " +
                "Chrome user agent, 5 touch points, and mouse-to-touch translation (hover stops producing hover " +
                "states). Reload the page after switching so load-time device gates re-run.",
                Schema(new JsonObject
                {
                    ["tabId"] = TabIdProp(),
                    ["preset"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("mobile", "tablet", "desktop") },
                    ["width"] = Prop("number"),
                    ["height"] = Prop("number"),
                    ["colorScheme"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("light", "dark") },
                }),
                isReadOnly: false,
                static a => $"resize_window({JsonArgs.GetString(a, "preset") ?? $"{JsonArgs.GetInt(a, "width")}x{JsonArgs.GetInt(a, "height")}"})",
                handlers.ResizeAsync),

            new PaneTool("tabs_context",
                "List every Browser pane tab (origin only — titles are page-authored). Returns {browserOpen, " +
                "tabs: [{tabId, origin, isActive}]} plus a line saying whether the pane is currently displayed or " +
                "hidden (a hidden pane still works; prefer `read_page` / `get_page_text` over screenshots while " +
                "it is hidden). browserOpen is false (and tabs empty) until `preview_start` or `navigate` opens " +
                "the pane, so you don't need to call this before opening it.",
                Schema([]),
                isReadOnly: true,
                static _ => "tabs_context()",
                (args, ct) => handlers.TabsContextAsync(ct)),

            new PaneTool("tabs_create",
                "Open a fresh blank Browser pane tab; returns the new tabId. Opens in the background by default — " +
                "set `foreground: true` when the user wants to watch. Prefer `preview_start` with a `url` when " +
                "you know the destination; use `navigate` to load a URL into a blank tab.",
                Schema(new JsonObject
                {
                    ["foreground"] = Prop("boolean", "Front the new tab (user asked to see it or is following " +
                        "along). Default false: open behind the user's current tab."),
                }),
                isReadOnly: false,
                static _ => "tabs_create()",
                handlers.TabsCreateAsync),

            new PaneTool("tabs_select",
                "Front the given Browser pane tab. Background tabs keep running while you drive them, so front " +
                "one only when the user should look — or for a page that pauses itself while hidden (e.g. a video " +
                "player).",
                Schema(new JsonObject { ["tabId"] = Prop("string", "Tab to front.") }, "tabId"),
                isReadOnly: false,
                static a => $"tabs_select({JsonArgs.GetString(a, "tabId")})",
                handlers.TabsSelectAsync),

            new PaneTool("tabs_close",
                "Close one Browser pane tab. Closing the last tab closes the Browser pane itself (reopen it with " +
                "`preview_start`).",
                Schema(new JsonObject { ["tabId"] = Prop("string", "Tab to close.") }, "tabId"),
                isReadOnly: false,
                static a => $"tabs_close({JsonArgs.GetString(a, "tabId")})",
                handlers.TabsCloseAsync),
        ];

        // The dev-server half of the same server. The reference declares these in
        // one array with the page tools (index.chunk-C5__TEgr.js, M5) — they wear
        // the Claude_Browser name, not a family of their own.
        if (previews is not null)
        {
            tools.AddRange(PreviewTools(previews));
        }

        tools.Add(BatchTool(tools));

        // Every pane tool answers a closed pane the way the reference does. The
        // guard sorts out which answer each one gets, batch steps included.
        foreach (var tool in tools.OfType<PaneTool>())
        {
            tool.ClosedPaneAnswer = handlers.ClosedPaneAnswerAsync;
            tool.PopupGuard = handlers.PopupRefusalAsync;
        }

        return tools;
    }

    /// <summary>
    /// The card a domain transition is consented on, or null when this turn has
    /// nowhere to ask — a subagent or a headless run, which is the state the
    /// reference's own unregistered card handler leaves it in and which its
    /// "cannot be shown in this context" answer names.
    /// </summary>
    private static DomainTransitionCard? DomainTransitionPrompt(ToolExecutionContext context)
    {
        if (context.AskUserAsync is not { } ask)
        {
            return null;
        }

        return async (source, destination, cancellationToken) =>
        {
            var question = new UserQuestion(
                BrowserPaneDomainTransitions.CardQuestion(source, destination),
                BrowserPaneDomainTransitions.CardHeader,
                [
                    new UserQuestionOption(
                        BrowserPaneDomainTransitions.CardAllow,
                        BrowserPaneDomainTransitions.CardAllowHint(destination)),
                    new UserQuestionOption(
                        BrowserPaneDomainTransitions.CardDeny,
                        BrowserPaneDomainTransitions.CardDenyHint),
                ],
                MultiSelect: false);

            var answers = await ask([question], cancellationToken);
            if (answers is null || !answers.Answers.TryGetValue(question.Question, out var picked))
            {
                // Dismissed rather than answered: the reference treats a card
                // that produced no decision as one it could not show.
                return null;
            }

            return picked.StartsWith(BrowserPaneDomainTransitions.CardAllow, StringComparison.OrdinalIgnoreCase);
        };
    }

    /// <summary>
    /// The launch.json format block the reference inlines in preview_start's doc
    /// (its <c>zM</c>), verbatim.
    /// </summary>
    internal const string LaunchJsonShape = """
        {
          "version": "0.0.1",
          "configurations": [
            {
              "name": "<unique-name>",
              "runtimeExecutable": "<command>",
              "runtimeArgs": ["<args>"],
              "port": <port>
            }
          ]
        }
        """;

    /// <summary>The reference's <c>BM</c>: what the fields mean.</summary>
    internal const string LaunchJsonFields =
        "Set \"runtimeExecutable\" to the command (e.g. \"npm\"), \"runtimeArgs\" to the arguments (e.g. " +
        "[\"run\", \"dev\"]), and \"port\" to the server port. An optional \"url\" (http/https) opens the preview " +
        "there instead of http://localhost:<port>. A localhost \"url\" must be just the server's origin — no path " +
        "or query, matching the entry's port — for example \"https://localhost:8443\" or " +
        "\"http://app.localhost:3000\"; to show a specific page, navigate after the preview opens. Non-localhost " +
        "URLs may carry paths and are subject to the user's permission and the organization's browsing policy. A " +
        "configuration with \"url\" and no command attaches to an already-running server. Only include servers you " +
        "actually need to preview.";

    /// <summary>
    /// The reference's <c>eEr</c> preamble, which its <c>tEr</c> prepends to
    /// preview_start's doc when the pane surface carries it.
    /// </summary>
    private const string PreviewStartPreamble =
        "Open the Browser pane: pass `url` to open a browser tab at a URL (no dev server needed — use this for " +
        "external sites, staging, docs, or your deployed app), OR pass `name` to start a dev server from " +
        ".jarvis/launch.json.\n\n";

    /// <summary>
    /// preview_start / preview_stop / preview_list / preview_logs, with the
    /// reference's docs and schemas. The one deliberate edit is the launch.json
    /// this app reads: `.jarvis/launch.json`, with `.claude/launch.json` as the
    /// compatibility fallback.
    /// </summary>
    private static IReadOnlyList<ITool> PreviewTools(PreviewServers previews) =>
    [
        new PaneTool("preview_start",
            PreviewStartPreamble +
            "Start a dev server by name from .jarvis/launch.json. If .jarvis/launch.json doesn't exist, create it " +
            "first with this format:\n" + LaunchJsonShape + "\n" + LaunchJsonFields +
            " Reuses the server if already running. ALWAYS use this instead of Bash for running servers. If the " +
            "deliverable is already published as an Artifact, update the Artifact instead of starting a server to " +
            "show it.",
            // tEr adds `url` and clears `required`, so either key alone is valid.
            SchemaWithNothingRequired(new JsonObject
            {
                ["name"] = Prop("string", "Server name from .jarvis/launch.json."),
                ["url"] = Prop("string",
                    "URL to open in the Browser pane without a dev server (instead of starting one by `name`)."),
            }),
            isReadOnly: false,
            static a => $"preview_start({JsonArgs.GetString(a, "name") ?? JsonArgs.GetString(a, "url")})",
            // A batch step carries no turn context; the reference's own batch
            // cannot open the pane either, so this path only ever reports that.
            (a, _) => Task.FromResult(previews.Start(
                JsonArgs.GetString(a, "name"), JsonArgs.GetString(a, "url"), Environment.CurrentDirectory, null)))
        {
            ContextualExecute = (a, context, _) => Task.FromResult(previews.Start(
                JsonArgs.GetString(a, "name"), JsonArgs.GetString(a, "url"),
                context.WorkingDirectory, context.SessionId)),
        },

        new PaneTool("preview_stop",
            "Stop a server started with preview_start.",
            Schema(new JsonObject { ["serverId"] = Prop("string", "Server ID to stop") }, "serverId"),
            isReadOnly: false,
            static a => $"preview_stop({JsonArgs.GetString(a, "serverId")})",
            (a, _) => Task.FromResult(previews.Stop(JsonArgs.GetString(a, "serverId") ?? ""))),

        new PaneTool("preview_list",
            "List servers started with preview_start. Returns serverIds for use with other preview_* tools.",
            Schema([]),
            isReadOnly: true,
            static _ => "preview_list()",
            (_, _) => Task.FromResult(previews.ListServers())),

        new PaneTool("preview_logs",
            "Get server stdout/stderr output. Use to check for build errors, verify server behavior, or read " +
            "debug output. Use 'level' to filter to errors only, or 'search' to filter for specific text. Use " +
            "after preview_start.",
            Schema(new JsonObject
            {
                ["serverId"] = Prop("string", "Server ID"),
                ["level"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("all", "error"),
                    ["description"] = "Filter by level: 'all' (default) shows all output, 'error' shows only " +
                        "lines containing error/exception/failed/fatal",
                },
                ["lines"] = Prop("number", "Max lines to return (default: 50)"),
                ["search"] = Prop("string", "Filter to lines containing this text (e.g., '[DEBUG]', 'POST /api')"),
            }, "serverId"),
            isReadOnly: true,
            static a => $"preview_logs({JsonArgs.GetString(a, "serverId")})",
            (a, _) => Task.FromResult(previews.Logs(
                JsonArgs.GetString(a, "serverId") ?? "",
                JsonArgs.GetInt(a, "lines") ?? PreviewServers.DefaultLogLines,
                JsonArgs.GetString(a, "level"),
                JsonArgs.GetString(a, "search")))),
    ];

    // ---- schema helpers --------------------------------------------------------

    private static JsonObject Prop(string type, string? description = null)
    {
        var prop = new JsonObject { ["type"] = type };
        if (description is not null)
        {
            prop["description"] = description;
        }

        return prop;
    }

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray([.. required.Select(static r => (JsonNode)r)]);
        }

        return schema;
    }

    /// <summary>
    /// A schema that declares <c>"required": []</c> rather than omitting the key.
    /// The reference's <c>tEr</c> writes the empty array onto preview_start when
    /// it adds <c>url</c>, because either key alone is a valid call, and an
    /// absent <c>required</c> is a different document from an empty one.
    /// </summary>
    private static JsonObject SchemaWithNothingRequired(JsonObject properties) =>
        new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray() };

    /// <summary>A number property carrying the reference's declared bounds.</summary>
    private static JsonObject Bounded(double minimum, double maximum, string description) => new()
    {
        ["type"] = "number",
        ["minimum"] = minimum,
        ["maximum"] = maximum,
        ["description"] = description,
    };

    /// <summary>
    /// A fixed-length coordinate array. The reference declares the length on both
    /// ends, which is what tells the model a click takes a pair and a zoom region
    /// a quadruple.
    /// </summary>
    private static JsonObject Tuple(int length, string description) => new()
    {
        ["type"] = "array",
        ["items"] = Prop("number"),
        ["minItems"] = length,
        ["maxItems"] = length,
        ["description"] = description,
    };

    private static JsonObject TabIdProp() =>
        Prop("string", "Tab to act on within the preview context. Omit for the fronted tab; get ids from tabs_context.");

    /// <summary>
    /// The pane's computer schema, in the reference's own key order: tabId is
    /// spread first (its <c>hK</c>), then the action and its arguments. Every
    /// coordinate is a declared tuple and every dial carries its declared
    /// bounds, which is what tells the model what it may send.
    /// </summary>
    private static JsonObject ComputerSchema() => Schema(new JsonObject
    {
        ["tabId"] = TabIdProp(),
        ["action"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("left_click", "right_click", "type", "screenshot", "wait", "scroll",
                "key", "left_click_drag", "double_click", "triple_click", "zoom", "scroll_to", "hover"),
            ["description"] =
                "The action to perform:\n" +
                "* `left_click`: Click the left mouse button at the specified coordinates.\n" +
                "* `right_click`: Click the right mouse button at the specified coordinates to open context menus.\n" +
                "* `double_click`: Double-click the left mouse button at the specified coordinates.\n" +
                "* `triple_click`: Triple-click the left mouse button at the specified coordinates.\n" +
                "* `type`: Type a string of text.\n" +
                "* `screenshot`: Take a screenshot of the screen.\n" +
                "* `wait`: Wait for a specified number of seconds.\n" +
                "* `scroll`: Scroll up, down, left, or right at the specified coordinates.\n" +
                "* `key`: Press a specific keyboard key.\n" +
                "* `left_click_drag`: Drag from start_coordinate to coordinate.\n" +
                "* `zoom`: Take a screenshot of a specific region for closer inspection.\n" +
                "* `scroll_to`: Scroll an element into view using its element reference ID from read_page or find tools.\n" +
                "* `hover`: Move the mouse cursor to the specified coordinates or element without clicking. " +
                "Useful for revealing tooltips, dropdown menus, or triggering hover states.",
        },
        ["coordinate"] = Tuple(2, "(x, y): The x (pixels from the left edge) and y (pixels from the top edge) " +
            "coordinates. Required for `left_click`, `right_click`, `double_click`, `triple_click`, and " +
            "`scroll`. For `left_click_drag`, this is the end position."),
        ["text"] = Prop("string", "The text to type (for `type` action) or the key(s) to press (for `key` " +
            "action). For `key` action: Provide space-separated keys (e.g., \"Backspace Backspace Delete\"). " +
            "Supports keyboard shortcuts using the platform's modifier key (use \"cmd\" on Mac, \"ctrl\" on " +
            "Windows/Linux, e.g., \"cmd+a\" or \"ctrl+a\" for select all). Page zoom shortcuts (e.g. " +
            "\"cmd+=\", \"ctrl+-\", \"cmd+0\") are not supported - use the `zoom` action to magnify a " +
            "region of the page instead."),
        ["duration"] = Bounded(0, 10, "The number of seconds to wait. Required for `wait`. Maximum 10 seconds."),
        ["scroll_direction"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("up", "down", "left", "right"),
            ["description"] = "The direction to scroll. Required for `scroll`.",
        },
        ["scroll_amount"] = Bounded(1, 10, "The number of scroll wheel ticks. Optional for `scroll`, defaults to 3."),
        ["start_coordinate"] = Tuple(2, "(x, y): The starting coordinates for `left_click_drag`."),
        ["region"] = Tuple(4, "(x0, y0, x1, y1): The rectangular region to capture for `zoom`. Coordinates " +
            "define a rectangle from top-left (x0, y0) to bottom-right (x1, y1) in pixels from the viewport " +
            "origin. Required for `zoom` action. Useful for inspecting small UI elements like icons, " +
            "buttons, or text."),
        ["scale"] = Bounded(0.1, 1, "For `screenshot` and `zoom` only. Scale factor in [0.1, 1] for the " +
            "returned image; 1 (default) uses the full " +
            "image token budget, 0.5 returns an image at half the width and height (~quarter of the tokens). " +
            "Coordinates are ALWAYS in the full-resolution coordinate frame (reported with every scaled " +
            "screenshot), never in the scaled image's own pixels."),
        ["repeat"] = Bounded(1, 100, "Number of times to repeat the key sequence. Only applicable for `key` " +
            "action. Must be a positive integer between 1 and 100. Default is 1. Useful for navigation tasks " +
            "like pressing arrow keys multiple times."),
        ["ref"] = Prop("string", "Element reference ID from read_page or find tools (e.g., \"ref_1\", " +
            "\"ref_2\"). Required for `scroll_to` action. Can be used as alternative to `coordinate` for " +
            "click actions."),
        ["modifiers"] = Prop("string", "Modifier keys for click actions. Supports: \"ctrl\", \"shift\", " +
            "\"alt\", \"cmd\" (or \"meta\"), \"win\" (or \"windows\"). Can be combined with \"+\" " +
            "(e.g., \"ctrl+shift\", \"cmd+alt\"). Optional."),
    }, "action");

    // ---- browser_batch (the reference's DEr/OEr) -------------------------------

    private const int MaxBatchActions = 25;

    /// <summary>
    /// The reference's <c>$Tr</c>: the only tools a batch step may name. The
    /// pane's dev-server tools and its tab management are deliberately outside
    /// it — a batch drives a page, and starting a server or opening a tab is
    /// what the surrounding call is for.
    /// </summary>
    private static readonly HashSet<string> Batchable = new(StringComparer.Ordinal)
    {
        "read_page", "computer", "form_input", "navigate", "find", "get_page_text",
        "javascript_tool", "read_console_messages", "read_network_requests", "resize_window",
    };

    /// <summary>
    /// The reference's <c>budgetMs</c> for one browser_batch call (18e4). It is
    /// checked before each step but the first, so a single slow action always
    /// runs and only the ones after it are held back.
    /// </summary>
    private static readonly TimeSpan BatchBudget = TimeSpan.FromMinutes(3);

    private static ITool BatchTool(IReadOnlyList<ITool> tools) => new PaneTool(
        "browser_batch",
        "Execute a sequence of Browser pane tool calls in ONE round trip. Each item is {name, input} where input " +
        "is exactly what you'd pass to that tool standalone. Actions execute SEQUENTIALLY (not in parallel) and " +
        "stop on the first error. Use this tool extensively to quickly execute work whenever you can predict two " +
        "or more steps ahead — e.g. navigate, click a field, type, press Return, screenshot. Each tool's own " +
        "permission check runs per item — a step on a site the user hasn't allowed either asks the user inline " +
        "(and continues if they allow) or is refused, which stops the batch; if a step is refused for a missing " +
        "permission, call that tool on its own (that call can ask the user), then batch the rest. Screenshots and " +
        "other images are returned interleaved with outputs; coordinates you write in THIS batch refer to the " +
        "screenshot taken BEFORE this call. browser_batch cannot be nested, and preview_start is not batchable " +
        "— but if the Browser pane isn't open yet, a batch whose FIRST action is navigate with a url opens " +
        "it (other actions still need an open page).",
        Schema(new JsonObject
        {
            ["actions"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = Prop("string", "Tool name (e.g. computer, navigate, find, form_input, " +
                            "read_page). browser_batch cannot be nested. A tabs_create's new tabId is only " +
                            "returned when the batch ends — load that tab in a later call."),
                        ["input"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["description"] = "That tool's input — same shape you'd pass when calling it directly.",
                        },
                    },
                    ["required"] = new JsonArray("name", "input"),
                },
                ["description"] = "List of tool calls to execute sequentially. Example: " +
                    "[{\"name\":\"computer\",\"input\":{\"action\":\"left_click\",\"ref\":\"ref_12\"}}," +
                    "{\"name\":\"computer\",\"input\":{\"action\":\"type\",\"text\":\"hello\"}}," +
                    "{\"name\":\"computer\",\"input\":{\"action\":\"screenshot\"}}]",
            },
        }, "actions"),
        isReadOnly: false,
        static a => a["actions"] is JsonArray actions
            ? $"browser_batch({string.Join(" → ", actions.OfType<JsonObject>().Select(static i => i["name"]?.GetValue<string>()))})"
            : "browser_batch()",
        async (arguments, cancellationToken) =>
        {
            // Validation pass (the reference's DEr).
            if (arguments["actions"] is not JsonArray actions || actions.Count == 0)
            {
                return ToolResult.Error("actions must be a non-empty array");
            }

            if (actions.Count > MaxBatchActions)
            {
                return ToolResult.Error($"at most {MaxBatchActions} actions per batch");
            }

            var items = new List<(string Name, JsonObject Input)>();
            for (var i = 0; i < actions.Count; i++)
            {
                if (actions[i] is not JsonObject item || item["name"]?.GetValue<string>() is not { } rawName)
                {
                    return ToolResult.Error($"actions[{i}].name must be a string");
                }

                var name = rawName.StartsWith(Prefix, StringComparison.Ordinal) ? rawName[Prefix.Length..] : rawName;
                if (name == "browser_batch")
                {
                    return ToolResult.Error($"actions[{i}]: browser_batch cannot be nested");
                }

                if (!Batchable.Contains(name) || tools.FirstOrDefault(t => t.Name == name) is null)
                {
                    return ToolResult.Error(
                        $"actions[{i}]: \"{(name.Length > 64 ? name[..64] : name)}\" cannot run in a batch");
                }

                if (item["input"] is not JsonObject input)
                {
                    return ToolResult.Error($"actions[{i}].input must be an object");
                }

                items.Add((name, input));
            }

            // Execution pass (the reference's OEr): "[name:action] text" per item,
            // images interleaved, first error stops with the completed/remaining tally.
            var output = new StringBuilder();
            var images = new List<ImageBlock>();
            var successLines = new List<string>();
            var startedAt = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The reference's per-call time budget, checked from the second
                // step on: what is left is handed back to the model to send again
                // rather than being run past the budget.
                if (i > 0 && startedAt.Elapsed > BatchBudget)
                {
                    output.AppendLine(
                        $"Stopped after {i} of {items.Count} actions (time budget for one call); " +
                        $"actions[{i}] onward did not run. Continue with the remaining actions in a new call.");
                    break;
                }

                var (name, input) = items[i];
                var label = input["action"]?.GetValue<string>() is { } act ? $"{name}:{act}" : name;
                var tool = tools.First(t => t.Name == name);
                ToolResult result;
                try
                {
                    result = await ((PaneTool)tool).InvokeAsync(
                        input.DeepClone() as JsonObject ?? [], isFirstAction: i == 0, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = ToolResult.Error($"{name} threw ({ex.GetType().Name})");
                }

                var text = result.Content.Length > 0 ? result.Content : "ok";
                if (result.IsError)
                {
                    // The reference drops collected images on a batch error —
                    // that is what its "[Image omitted due to error]" marker means.
                    var prior = successLines.Count > 0 ? string.Join("\n", successLines) + "\n\n" : "";
                    return ToolResult.Error(
                        $"{prior}actions[{i}] ({label}) failed: {text} ({i} completed, {items.Count - i - 1} remaining)");
                }

                var hadImages = result.Images is { Count: > 0 };
                if (hadImages)
                {
                    images.AddRange(result.Images!);
                }

                successLines.Add($"[{label}] {text}{(hadImages ? " [Image omitted due to error]" : "")}");
                output.AppendLine($"[{label}] {text}");
            }

            return new ToolResult(output.ToString().TrimEnd(), IsError: false, images.Count > 0 ? images : null);
        });

    private sealed class PaneTool(
        string name, string description, JsonObject schema, bool isReadOnly,
        Func<JsonObject, string> describe,
        Func<JsonObject, CancellationToken, Task<ToolResult>> execute) : ITool
    {
        public string Name => name;

        public string Description => description;

        public JsonObject InputSchema => schema;

        public bool IsReadOnly => isReadOnly;

        /// <summary>
        /// Answers the call itself when there is no pane to run it against.
        /// Assigned once for the whole family, since every tool needs it.
        /// </summary>
        public Func<string, JsonObject, bool, bool, CancellationToken, Task<ToolResult?>>? ClosedPaneAnswer { get; set; }

        /// <summary>
        /// What a page-opened popup refuses. It sits after the closed-pane
        /// answer and before dispatch, so every tool is guarded in one place
        /// rather than each remembering to ask.
        /// </summary>
        public Func<string, JsonObject, CancellationToken, Task<ToolResult?>>? PopupGuard { get; set; }

        /// <summary>
        /// The name the guard matches on. Pane tools are declared bare and get
        /// their mcp__Claude_Browser__ prefix from the server shell, so this is
        /// simply the name — kept as its own member because the guard's contract
        /// is "short name", not "whatever this tool is called".
        /// </summary>
        private string ShortName => name;

        /// <summary>
        /// Set for the dev-server tools, which need the turn's working directory
        /// and session id. A batch step has no context and falls back to
        /// <c>execute</c>, which is why both bodies exist.
        /// </summary>
        public Func<JsonObject, ToolExecutionContext, CancellationToken, Task<ToolResult>>? ContextualExecute
        {
            get;
            init;
        }

        public string DescribeCall(JsonObject arguments) => describe(arguments);

        /// <summary>
        /// A browser_batch step. Only the first action may open the pane, so
        /// the position travels with the call rather than being inferred.
        /// </summary>
        public Task<ToolResult> InvokeAsync(
            JsonObject arguments, bool isFirstAction, CancellationToken cancellationToken) =>
            RunAsync(arguments, inBatch: true, isFirstAction, context: null, cancellationToken);

        public Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            RunAsync(arguments, inBatch: false, isFirstBatchAction: false, context, cancellationToken);

        private async Task<ToolResult> RunAsync(
            JsonObject arguments, bool inBatch, bool isFirstBatchAction, ToolExecutionContext? context,
            CancellationToken cancellationToken)
        {
            if (ClosedPaneAnswer is { } guard &&
                await guard(ShortName, arguments, inBatch, isFirstBatchAction, cancellationToken) is { } answer)
            {
                return answer;
            }

            if (PopupGuard is { } popup &&
                await popup(ShortName, arguments, cancellationToken) is { } refused)
            {
                return refused;
            }

            var call = ContextualExecute is { } contextual && context is not null
                ? contextual(arguments, context, cancellationToken)
                : execute(arguments, cancellationToken);

            return await RaceTimeoutAsync(call, cancellationToken);
        }

        /// <summary>
        /// The reference's <c>QEr</c>: a pane call that has not answered inside
        /// its budget is reported rather than waited on. javascript_tool gets the
        /// longer one because the page's own code is what it is waiting for.
        ///
        /// Like the reference it only stops waiting — the call is left running,
        /// so a renderer that wakes up later does not find its work torn down.
        /// </summary>
        private async Task<ToolResult> RaceTimeoutAsync(
            Task<ToolResult> call, CancellationToken cancellationToken)
        {
            if (!Batchable.Contains(ShortName) || call.IsCompleted)
            {
                return await call.ConfigureAwait(false);
            }

            var budget = ShortName == "javascript_tool" ? JavaScriptTimeout : PaneCallTimeout;
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = Task.Delay(budget, expiry.Token);
            if (await Task.WhenAny(call, timeout).ConfigureAwait(false) == call)
            {
                expiry.Cancel();
                return await call.ConfigureAwait(false);
            }

            _ = call.ContinueWith(
                static faulted => _ = faulted.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return ToolResult.Error(TimedOut(ShortName, budget));
        }
    }

    /// <summary>The reference's <c>XEr</c>: every pane call but one.</summary>
    private static readonly TimeSpan PaneCallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Its <c>ZEr</c>: javascript_tool waits on the page's own code.</summary>
    private static readonly TimeSpan JavaScriptTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The reference's timeout answer, verbatim. Its own literal carries a hole
    /// between the seconds and the advice — the tab context, which it inserts
    /// there — so the advice is a constant here as well.
    /// </summary>
    internal static string TimedOut(string toolName, TimeSpan budget) =>
        $"{toolName} timed out after {budget.TotalSeconds:0.###}s." + PaneMayBeStuck;

    /// <summary>
    /// What a timed-out call says after naming its budget. The reference words
    /// this about the page rather than the pane, and closes with the reader it
    /// actually offers - `read_console_messages` here, where a dev-server
    /// preview is told to read preview_console_logs instead.
    /// </summary>
    private const string PaneMayBeStuck =
        " The page may be stuck (modal dialog, navigation hang, or unresponsive renderer). Check " +
        "`read_console_messages` for errors.";
}
