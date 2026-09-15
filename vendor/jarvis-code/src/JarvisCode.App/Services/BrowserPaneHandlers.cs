using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The Browser pane tool handlers — a C# port of the reference desktop's
/// handler layer (app.asar 1.40609.0.0: r2n/c2n/p2n/m2n/h2n/g2n and the
/// tabs/resize/console/network dispatch), with the reference's exact result
/// and error strings. Runs against <see cref="IBrowserPaneDriver"/> primitives
/// so it unit-tests without an engine. Refs are plain main-frame "ref_N"
/// strings owned by the page (the reference never traverses iframes; embedded
/// content is reached by screenshot coordinates).
/// </summary>
public sealed class BrowserPaneHandlers(
    IBrowserPaneDriver driver,
    BrowserPaneDomainTransitions? transitions = null,
    PreviewOriginPrompt? originPrompt = null)
{
    /// <summary>
    /// Per-tab coordinate frame of the most recent screenshot: viewport size at
    /// capture and the full-resolution frame size. Coordinate clicks are pixels
    /// in this frame, scaled to the live viewport at dispatch (the reference's
    /// SQ/wQ/TQ), and a resize clears it.
    /// </summary>
    private readonly Dictionary<string, (int ViewportWidth, int ViewportHeight, int FrameWidth, int FrameHeight)> _frames = [];

    private static ToolResult Error(string message) => ToolResult.Error(message);

    // ---- shared pieces ---------------------------------------------------------

    /// <summary>Sanitizes page-authored text (the reference's MOn).</summary>
    internal static string CleanPageText(string? text) => BrowserPaneTrailer.Clean(text);

    /// <summary>The reference's boolean coercion for checkbox values (NOn).</summary>
    internal static bool CoerceChecked(JsonNode? value) =>
        value switch
        {
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<double>(out var d) => d == 1,
            JsonValue v when v.TryGetValue<string>(out var s) => s is "true" or "1" or "on" or "yes",
            _ => false,
        };

    /// <summary>
    /// The Tab Context trailer (the reference's hQ), appended to every
    /// successful page-facing result — never to tabs_* results.
    /// </summary>
    private async Task<ToolResult> WithTrailerAsync(ToolResult result, string? tabId, CancellationToken cancellationToken)
    {
        if (result.IsError)
        {
            return result;
        }

        string executed;
        PaneTabNotes notes;
        try
        {
            executed = await driver.ResolveTabIdAsync(tabId, cancellationToken);
            notes = await driver.TabNotesAsync(executed, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return result;
        }

        return result with
        {
            Content = result.Content
                      + BrowserPaneTrailer.Build(
                          executed, notes.Title, notes.Url, notes.DeniedMediaKinds, notes.ClipboardChanged),
        };
    }

    // ---- read_page / find (the reference's n2n/r2n/p2n) ------------------------

    private async Task<(string Tree, (int Width, int Height)? Viewport, ToolResult? Failed)> RunTreeAsync(
        string? tabId, string toolName, string filter, double depth, double maxChars, string? refId,
        CancellationToken cancellationToken)
    {
        var script = BrowserPaneScripts.A11yCall(
            JsonSerializer.Serialize(filter),
            JsonSerializer.Serialize(depth),
            JsonSerializer.Serialize(maxChars),
            JsonSerializer.Serialize(refId));
        JsonNode? node;
        try
        {
            node = await driver.EvaluateAsync(tabId, script, replMode: false, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return ("", null, Error($"{toolName} failed: {ex.Message}"));
        }

        if (node is not JsonObject result || result["error"] is not null)
        {
            return ("", null, Error(
                $"{toolName} failed: {(node as JsonObject)?["error"]?.GetValue<string>() ?? "no result from page"}"));
        }

        var viewport = result["viewport"] as JsonObject;
        return (
            result["pageContent"]?.GetValue<string>() ?? "",
            viewport is null
                ? null
                : ((int)(JarvisBrowserFormat.AsNumber(viewport["width"]) ?? 0),
                    (int)(JarvisBrowserFormat.AsNumber(viewport["height"]) ?? 0)),
            null);
    }

    public async Task<ToolResult> ReadPageAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var tabId = JsonArgs.GetString(args, "tabId");
        var (tree, viewport, failed) = await RunTreeAsync(
            tabId, "read_page",
            JsonArgs.GetString(args, "filter") ?? "all",
            JarvisBrowserFormat.AsNumber(args["depth"]) ?? 15,
            JarvisBrowserFormat.AsNumber(args["max_chars"]) ?? 50000,
            JsonArgs.GetString(args, "ref_id"),
            cancellationToken);
        if (failed is not null)
        {
            return failed;
        }

        var suffix = viewport is { } v ? $"\n\nViewport: {v.Width}x{v.Height}" : "";
        return await WithTrailerAsync(
            ToolResult.Success((tree.Length > 0 ? tree : "(empty page)") + suffix), tabId, cancellationToken);
    }

    public async Task<ToolResult> FindAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (JsonArgs.GetString(args, "query") is not { } query)
        {
            return Error("`find` requires a string `query`");
        }

        var tabId = JsonArgs.GetString(args, "tabId");
        var (tree, _, failed) = await RunTreeAsync(tabId, "find", "all", 15, 5_000_000, null, cancellationToken);
        if (failed is not null)
        {
            return failed;
        }

        var needle = query.ToLowerInvariant();
        var lines = tree.Split('\n');
        var matches = new List<string>();
        foreach (var line in lines)
        {
            if (line.ToLowerInvariant().Contains(needle) && Regex.IsMatch(line, @"\[ref_\d+\]"))
            {
                matches.Add(line.Trim());
                if (matches.Count >= 20)
                {
                    break;
                }
            }
        }

        var note = lines.Any(static l => l.StartsWith("[truncated at ", StringComparison.Ordinal)
                                         || l.StartsWith("[output truncated at ", StringComparison.Ordinal))
            ? "\n(Page is very large; only the first part of it was searched.)"
            : "";
        return await WithTrailerAsync(
            ToolResult.Success(matches.Count == 0
                ? $"No matches for \"{query}\".{note}"
                : $"Found {matches.Count} match(es) for \"{query}\":\n{string.Join("\n", matches.Select(static m => $"- {m}"))}{note}"),
            tabId, cancellationToken);
    }

    // ---- get_page_text (m2n) ---------------------------------------------------

    public async Task<ToolResult> GetPageTextAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var tabId = JsonArgs.GetString(args, "tabId");
        var maxChars = (int)(JarvisBrowserFormat.AsNumber(args["max_chars"]) ?? 50000);
        JsonNode? node;
        try
        {
            node = await driver.EvaluateAsync(
                tabId, BrowserPaneScripts.PageText.Replace("__MAXCHARS__", JsonSerializer.Serialize(maxChars)),
                replMode: false, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Error($"get_page_text failed: {ex.Message}");
        }

        if (node is not JsonObject result)
        {
            return Error("get_page_text: no result from page");
        }

        if (result.ContainsKey("loading"))
        {
            return Error(result["loading"]?.GetValue<bool>() == true
                ? "The page is still loading; retry in a moment."
                : "This page has no HTML body to read (for example an XML or SVG document).");
        }

        var url = result["url"]?.GetValue<string>() ?? "";
        var origin = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority)
            : "(non-http)";
        var tag = result["tag"]?.GetValue<string>() is { } raw && Regex.IsMatch(raw, "^[a-z][a-z0-9]{0,15}$")
            ? raw
            : "body";
        var truncated = result["truncated"]?.GetValue<bool>() == true ? $"\n\n[truncated to {maxChars} chars]" : "";
        return await WithTrailerAsync(
            ToolResult.Success(
                $"Title: {CleanPageText(result["title"]?.GetValue<string>())}\nURL: {origin}\n" +
                $"Source element: <{tag}>\n---\n{result["text"]?.GetValue<string>() ?? ""}{truncated}"),
            tabId, cancellationToken);
    }

    // ---- form_input (h2n) ------------------------------------------------------

    public async Task<ToolResult> FormInputAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var tabId = JsonArgs.GetString(args, "tabId");
        var reference = JsonArgs.GetString(args, "ref") ?? "undefined";
        var value = args["value"];
        var asString = value is JsonValue v && v.TryGetValue<bool>(out var b)
            ? b ? "true" : "false"
            : value is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "undefined";
        var script = BrowserPaneScripts.FormInput
            .Replace("__REF__", JsonSerializer.Serialize(reference))
            .Replace("__VALUE__", JsonSerializer.Serialize(asString))
            .Replace("__CHECKED__", CoerceChecked(value) ? "true" : "false");
        try
        {
            var node = await driver.EvaluateAsync(tabId, script, replMode: false, cancellationToken) as JsonObject;
            if (node?["ok"]?.GetValue<bool>() != true)
            {
                return Error($"form_input failed: {node?["error"]?.GetValue<string>() ?? "no result from page"}");
            }

            return await WithTrailerAsync(
                ToolResult.Success($"filled {reference} with value"), tabId, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Error($"form_input failed: {ex.Message}");
        }
    }

    // ---- ref → viewport point (g2n) --------------------------------------------

    internal async Task<(double X, double Y, string? Error)> ResolveRefAsync(
        string? tabId, string reference, bool forClick, CancellationToken cancellationToken)
    {
        var script = BrowserPaneScripts.RefPoint.Replace("__REF__", JsonSerializer.Serialize(reference));
        var node = await driver.EvaluateAsync(tabId, script, replMode: false, cancellationToken) as JsonObject;
        if (node is null)
        {
            return (0, 0, "ref resolution returned null");
        }

        if (node["error"]?.GetValue<string>() is { } pageError)
        {
            return (0, 0, pageError);
        }

        double Get(string name) => JarvisBrowserFormat.AsNumber(node[name]) ?? 0;
        var (x, y) = (Get("x"), Get("y"));
        var (vw, vh) = (Get("vw"), Get("vh"));
        if (forClick && !(Get("right") > 0 && Get("left") <= vw - 1 && Get("bottom") > 0 && Get("top") <= vh - 1))
        {
            return (0, 0,
                $"ref {reference} is entirely outside the viewport (center ({Math.Round(x)}, {Math.Round(y)})) — " +
                "likely hidden or off-canvas, so a click cannot reach it. Interact with what opens it first, or " +
                "re-run read_page and pick a visible element.");
        }

        return (Math.Max(0, Math.Min(x, vw - 1)), Math.Max(0, Math.Min(y, vh - 1)), null);
    }

    // ---- computer (c2n) --------------------------------------------------------

    /// <summary>Screenshot-frame bounds check (TQ): +2px tolerance, exact message.</summary>
    private string? FrameBoundsError(string tabId, double x, double y)
    {
        if (!_frames.TryGetValue(tabId, out var frame) || frame.FrameWidth == 0 || frame.FrameHeight == 0)
        {
            return null;
        }

        return !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0
               || x >= frame.FrameWidth + 2 || y >= frame.FrameHeight + 2
            ? $"coordinate ({Math.Round(x)}, {Math.Round(y)}) is outside the coordinate frame " +
              $"({frame.FrameWidth}x{frame.FrameHeight}). Coordinates are pixels in the full-resolution frame — " +
              "if the page changed, take a new screenshot first."
            : null;
    }

    /// <summary>Frame → live viewport scaling (wQ); null without a prior screenshot.</summary>
    private (int X, int Y)? ScaleToViewport(string tabId, double x, double y)
    {
        if (!_frames.TryGetValue(tabId, out var frame) || frame.FrameWidth == 0 || frame.FrameHeight == 0)
        {
            return null;
        }

        return (
            Math.Max(0, Math.Min((int)Math.Round(x * ((double)frame.ViewportWidth / frame.FrameWidth)), frame.ViewportWidth - 1)),
            Math.Max(0, Math.Min((int)Math.Round(y * ((double)frame.ViewportHeight / frame.FrameHeight)), frame.ViewportHeight - 1)));
    }

    /// <summary>Validates the screenshot scale factor (pV).</summary>
    internal static (double Scale, string? Error) ParseScale(JsonNode? node)
    {
        if (node is null)
        {
            return (1, null);
        }

        var scale = JarvisBrowserFormat.AsNumber(node);
        return scale is null || !double.IsFinite(scale.Value) || scale < 0.1 || scale > 1
            ? (1, "scale must be a number in [0.1, 1] — e.g. 0.5 for a half-size image")
            : (scale.Value, null);
    }

    private async Task<ToolResult> CaptureAsync(
        string? tabId, string resolvedTab, double scale, string? zoomText, CancellationToken cancellationToken)
    {
        var shot = await driver.ScreenshotAsync(tabId, scale, cancellationToken);
        var viewport = await driver.EvaluateAsync(
            tabId, "({w: window.innerWidth, h: window.innerHeight})", replMode: true, cancellationToken) as JsonObject;
        var vw = (int)(JarvisBrowserFormat.AsNumber(viewport?["w"]) ?? 0);
        var vh = (int)(JarvisBrowserFormat.AsNumber(viewport?["h"]) ?? 0);
        _frames[resolvedTab] = (vw > 0 ? vw : 1280, vh > 0 ? vh : 800, shot.FrameWidth, shot.FrameHeight);
        var note = scale < 1 ? $" {scale}-scale view; coordinate frame: {shot.FrameWidth}x{shot.FrameHeight}." : "";
        var text = zoomText is not null
            ? $"{zoomText}{note}"
            : $"Screenshot size: {shot.Width}x{shot.Height}{note}";
        return ToolResult.WithImage(text, new ImageBlock("image/jpeg", shot.JpegBase64));
    }

    public async Task<ToolResult> ComputerAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var action = JsonArgs.GetString(args, "action") ?? "";
        var tabId = JsonArgs.GetString(args, "tabId");
        string resolvedTab;
        try
        {
            resolvedTab = await driver.ResolveTabIdAsync(tabId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }

        var modifiers = BrowserPaneKeys.Modifiers(JsonArgs.GetString(args, "modifiers") ?? "");

        // Resolves ref or coordinate to a dispatch point; echoes what the model wrote.
        async Task<((int X, int Y) Dispatch, (int X, int Y) Echo, string? Error)> PointAsync()
        {
            if (JsonArgs.GetString(args, "ref") is { } reference)
            {
                var (x, y, error) = await ResolveRefAsync(tabId, reference, forClick: true, cancellationToken);
                if (error is not null)
                {
                    return (default, default, error);
                }

                var point = ((int)Math.Round(x), (int)Math.Round(y));
                return (point, point, null);
            }

            if (args["coordinate"] is JsonArray { Count: 2 } coordinate)
            {
                var (cx, cy) = (JarvisBrowserFormat.AsNumber(coordinate[0]) ?? double.NaN,
                    JarvisBrowserFormat.AsNumber(coordinate[1]) ?? double.NaN);
                if (FrameBoundsError(resolvedTab, cx, cy) is { } bounds)
                {
                    return (default, default, $"{action}: {bounds}");
                }

                var scaled = ScaleToViewport(resolvedTab, cx, cy);
                return scaled is null
                    ? (default, default,
                        $"{action} with `coordinate` requires a prior computer{{action:\"screenshot\"}} (no screenshot dimensions cached)")
                    : (scaled.Value, ((int)Math.Round(cx), (int)Math.Round(cy)), null);
            }

            return (default, default, $"{action} requires either `ref` or `coordinate`");
        }

        try
        {
            switch (action)
            {
                case "screenshot":
                {
                    var (scale, invalid) = ParseScale(args["scale"]);
                    if (invalid is not null)
                    {
                        return Error(invalid);
                    }

                    return await WithTrailerAsync(
                        await CaptureAsync(tabId, resolvedTab, scale, null, cancellationToken), tabId, cancellationToken);
                }

                case "zoom":
                {
                    var (scale, invalid) = ParseScale(args["scale"]);
                    if (invalid is not null)
                    {
                        return Error(invalid);
                    }

                    return await WithTrailerAsync(
                        await CaptureAsync(tabId, resolvedTab, scale,
                            "zoom: region crop not yet supported in the Browser pane; full screenshot returned",
                            cancellationToken),
                        tabId, cancellationToken);
                }

                case "left_click" or "right_click" or "double_click" or "triple_click":
                {
                    var (dispatch, echo, error) = await PointAsync();
                    if (error is not null)
                    {
                        return Error(error);
                    }

                    var button = action == "right_click" ? "right" : "left";
                    var count = action == "double_click" ? 2 : action == "triple_click" ? 3 : 1;
                    await driver.ClickAsync(tabId, dispatch.X, dispatch.Y, button, count, modifiers, cancellationToken);
                    var refSuffix = JsonArgs.GetString(args, "ref") is { } r ? $" [{r}]" : "";
                    return await WithTrailerAsync(
                        ToolResult.Success($"{action} at ({echo.X}, {echo.Y}){refSuffix}"), tabId, cancellationToken);
                }

                case "hover":
                {
                    var (dispatch, echo, error) = await PointAsync();
                    if (error is not null)
                    {
                        return Error(error);
                    }

                    await driver.HoverAsync(tabId, dispatch.X, dispatch.Y, cancellationToken);
                    return await WithTrailerAsync(
                        ToolResult.Success($"hover at ({echo.X}, {echo.Y})"), tabId, cancellationToken);
                }

                case "type":
                {
                    if (JsonArgs.GetString(args, "text") is not { } text)
                    {
                        return Error("`type` requires `text`");
                    }

                    await driver.InsertTextAsync(tabId, text, cancellationToken);
                    return await WithTrailerAsync(
                        ToolResult.Success($"typed {text.Length} chars"), tabId, cancellationToken);
                }

                case "key":
                {
                    if (JsonArgs.GetString(args, "text") is not { } combo)
                    {
                        return Error("`key` requires `text`");
                    }

                    var repeat = JarvisBrowserFormat.AsNumber(args["repeat"]) is { } n && double.IsFinite(n)
                        ? Math.Clamp((int)n, 1, 100)
                        : 1;
                    var tokens = combo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length > 100)
                    {
                        return Error($"`key` accepts at most 100 tokens (got {tokens.Length})");
                    }

                    // A key sequence is meant for one page. If the page moves
                    // part-way through, the rest of the keys would land somewhere
                    // the caller never looked at, so they are not sent.
                    var before = await driver.NavigationMarkAsync(tabId, cancellationToken);
                    var sent = 0;
                    for (var i = 0; i < repeat; i++)
                    {
                        foreach (var token in tokens)
                        {
                            if (BrowserPaneKeys.ParseToken(token) is { } stroke)
                            {
                                if (await driver.NavigationMarkAsync(tabId, cancellationToken) != before)
                                {
                                    return Error("Page navigated during key sequence; remaining keys not dispatched.");
                                }

                                await driver.PressKeyAsync(tabId, stroke.Key, stroke.ModifierBits, stroke.Text, cancellationToken);
                                sent++;
                            }
                        }
                    }

                    return sent == 0
                        ? Error($"`key` parsed no valid tokens from \"{combo}\"")
                        : await WithTrailerAsync(
                            ToolResult.Success($"pressed {combo} x{repeat}"), tabId, cancellationToken);
                }

                case "scroll":
                {
                    if (args["coordinate"] is not JsonArray { Count: 2 } coordinate)
                    {
                        return Error("`scroll` requires `coordinate`");
                    }

                    var (cx, cy) = (JarvisBrowserFormat.AsNumber(coordinate[0]) ?? double.NaN,
                        JarvisBrowserFormat.AsNumber(coordinate[1]) ?? double.NaN);
                    if (FrameBoundsError(resolvedTab, cx, cy) is { } bounds)
                    {
                        return Error($"scroll: {bounds}");
                    }

                    var scaled = ScaleToViewport(resolvedTab, cx, cy);
                    if (scaled is null)
                    {
                        return Error("`scroll` with `coordinate` requires a prior computer{action:\"screenshot\"}");
                    }

                    var direction = JsonArgs.GetString(args, "scroll_direction") ?? "down";
                    if (direction is not ("up" or "down" or "left" or "right"))
                    {
                        return Error("`scroll` requires `scroll_direction` of up/down/left/right");
                    }

                    var amount = (JarvisBrowserFormat.AsNumber(args["scroll_amount"]) is { } a && double.IsFinite(a) ? a : 3) * 100;
                    await driver.WheelAsync(tabId, scaled.Value.X, scaled.Value.Y,
                        direction switch { "left" => -amount, "right" => amount, _ => 0 },
                        direction switch { "up" => -amount, "down" => amount, _ => 0 },
                        cancellationToken);
                    return await WithTrailerAsync(
                        ToolResult.Success($"scrolled {direction} at ({Math.Round(cx)}, {Math.Round(cy)})"),
                        tabId, cancellationToken);
                }

                case "scroll_to":
                {
                    if (JsonArgs.GetString(args, "ref") is not { } reference)
                    {
                        return Error("`scroll_to` requires `ref`");
                    }

                    var (_, _, error) = await ResolveRefAsync(tabId, reference, forClick: false, cancellationToken);
                    return error is not null
                        ? Error(error)
                        : await WithTrailerAsync(
                            ToolResult.Success($"scrolled {reference} into view"), tabId, cancellationToken);
                }

                case "left_click_drag":
                {
                    if (args["start_coordinate"] is not JsonArray { Count: 2 } start ||
                        args["coordinate"] is not JsonArray { Count: 2 } end)
                    {
                        return Error("`left_click_drag` requires `start_coordinate` and `coordinate`");
                    }

                    var (sx, sy) = (JarvisBrowserFormat.AsNumber(start[0]) ?? double.NaN,
                        JarvisBrowserFormat.AsNumber(start[1]) ?? double.NaN);
                    var (ex, ey) = (JarvisBrowserFormat.AsNumber(end[0]) ?? double.NaN,
                        JarvisBrowserFormat.AsNumber(end[1]) ?? double.NaN);
                    if ((FrameBoundsError(resolvedTab, sx, sy) ?? FrameBoundsError(resolvedTab, ex, ey)) is { } bounds)
                    {
                        return Error($"left_click_drag: {bounds}");
                    }

                    var from = ScaleToViewport(resolvedTab, sx, sy);
                    var to = ScaleToViewport(resolvedTab, ex, ey);
                    if (from is null || to is null)
                    {
                        return Error("`left_click_drag` requires a prior computer{action:\"screenshot\"}");
                    }

                    var beforeDrag = await driver.NavigationMarkAsync(tabId, cancellationToken);
                    await driver.DragAsync(tabId, from.Value.X, from.Value.Y, to.Value.X, to.Value.Y, cancellationToken);
                    if (await driver.NavigationMarkAsync(tabId, cancellationToken) != beforeDrag)
                    {
                        // The drop landed on a page that is no longer there.
                        return Error("Page navigated during drag; result may be unreliable.");
                    }

                    return await WithTrailerAsync(
                        ToolResult.Success(
                            $"dragged ({Math.Round(sx)},{Math.Round(sy)}) → ({Math.Round(ex)},{Math.Round(ey)})"),
                        tabId, cancellationToken);
                }

                case "wait":
                {
                    var seconds = Math.Min(JarvisBrowserFormat.AsNumber(args["duration"]) ?? 1, 10);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds)), cancellationToken);
                    return await WithTrailerAsync(
                        ToolResult.Success($"waited {seconds}s"), tabId, cancellationToken);
                }

                default:
                    return Error($"Unknown computer action: {action}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Error($"{action} failed: {ex.Message}");
        }
    }

    // ---- javascript_tool -------------------------------------------------------

    public async Task<ToolResult> JavaScriptAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var code = JsonArgs.GetString(args, "text") ?? "";
        var tabId = JsonArgs.GetString(args, "tabId");

        async Task<ToolResult> RunAsync(string expression, bool replMode)
        {
            var node = await driver.EvaluateAsync(tabId, expression, replMode, cancellationToken);
            var text = node is null
                ? "undefined"
                : node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            return await WithTrailerAsync(ToolResult.Success(text), tabId, cancellationToken);
        }

        try
        {
            try
            {
                return await RunAsync($"{{{code}\n}}", replMode: true);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Illegal return statement"))
            {
                // A top-level return: re-run as a function body, like the reference.
                return await RunAsync($"(async()=>{{\n{code}\n}})()", replMode: false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            var tail = Regex.IsMatch(ex.Message, "navigated or closed|context was destroyed|Promise was collected",
                RegexOptions.IgnoreCase)
                ? " The page navigated or was closed mid-evaluation (if the script itself navigated, it likely " +
                  "succeeded; otherwise re-run against the current page)."
                : "";
            return Error($"javascript_tool failed: {ex.Message}{tail}");
        }
    }

    // ---- console / network -----------------------------------------------------

    public async Task<ToolResult> ConsoleAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var tabId = JsonArgs.GetString(args, "tabId");
        IReadOnlyList<JsonObject> entries;
        try
        {
            entries = await driver.ConsoleEntriesAsync(tabId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }

        IEnumerable<JsonObject> filtered = entries;
        if (JsonArgs.GetBool(args, "onlyErrors"))
        {
            filtered = filtered.Where(static e => e["level"]?.GetValue<string>() == "error");
        }

        if (JsonArgs.GetString(args, "pattern") is { } pattern)
        {
            filtered = filtered.Where(e => (e["text"]?.GetValue<string>() ?? "").Contains(pattern));
        }

        var limit = Math.Min(Math.Max(1, (int)(JarvisBrowserFormat.AsNumber(args["limit"]) ?? 50)), 200);
        var lines = filtered.ToList();
        return await WithTrailerAsync(
            ToolResult.Success(lines.Count == 0
                ? "No console logs."
                : string.Join("\n", lines.TakeLast(limit)
                    .Select(static e => $"[{e["level"]?.GetValue<string>()}] {e["text"]?.GetValue<string>()}"))),
            tabId, cancellationToken);
    }

    public async Task<ToolResult> NetworkAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var tabId = JsonArgs.GetString(args, "tabId");
        try
        {
            if (JsonArgs.GetString(args, "requestId") is { } requestId)
            {
                var body = await driver.NetworkBodyAsync(tabId, requestId, cancellationToken);
                if (body is not { } found)
                {
                    return Error($"Response body not available for request {requestId}.");
                }

                return await WithTrailerAsync(
                    ToolResult.Success(found.Base64Encoded
                        ? $"(binary, {found.Body.Length} chars base64)"
                        : found.Body.Length > 10000 ? found.Body[..10000] : found.Body),
                    tabId, cancellationToken);
            }

            var requests = await driver.NetworkListAsync(tabId, cancellationToken);
            IEnumerable<JsonObject> filtered = requests;
            if (JsonArgs.GetString(args, "urlPattern") is { } pattern)
            {
                filtered = filtered.Where(e => (e["url"]?.GetValue<string>() ?? "").Contains(pattern));
            }

            var limit = Math.Max(1, (int)(JarvisBrowserFormat.AsNumber(args["limit"]) ?? 50));
            var lines = filtered.TakeLast(limit).Select(static e =>
            {
                var line = $"[{e["requestId"]?.GetValue<string>()}] {e["method"]?.GetValue<string>()} {e["url"]?.GetValue<string>()}";
                if (JarvisBrowserFormat.AsNumber(e["status"]) is { } status && status != 0)
                {
                    line += $" → {status} {e["statusText"]?.GetValue<string>()}";
                }

                if (e["failed"]?.GetValue<bool>() == true)
                {
                    line += $" [FAILED: {e["errorText"]?.GetValue<string>()}]";
                }

                return line;
            }).ToList();
            return await WithTrailerAsync(
                ToolResult.Success(lines.Count == 0 ? "No network requests recorded." : string.Join("\n", lines)),
                tabId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    // ---- resize_window ---------------------------------------------------------

    public async Task<ToolResult> ResizeAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var tabId = JsonArgs.GetString(args, "tabId");
        var preset = JsonArgs.GetString(args, "preset");
        var width = JarvisBrowserFormat.AsNumber(args["width"]);
        var height = JarvisBrowserFormat.AsNumber(args["height"]);
        var colorScheme = JsonArgs.GetString(args, "colorScheme");
        try
        {
            if (colorScheme is not null && colorScheme is not ("light" or "dark"))
            {
                return Error($"Unknown colorScheme \"{Truncate40(colorScheme)}\". Use light or dark.");
            }

            var sentences = new List<string>();
            if (preset == "desktop")
            {
                await driver.ClearViewportAsync(tabId, cancellationToken);
                sentences.Add("Viewport emulation cleared; the tab is back to the pane's responsive size (desktop)");
            }
            else if (preset is not null || width is not null || height is not null)
            {
                (int W, int H) size;
                if (preset is not null)
                {
                    if (preset is not ("mobile" or "tablet"))
                    {
                        return Error($"Unknown preset \"{Truncate40(preset)}\". Use mobile, tablet, or desktop.");
                    }

                    size = preset == "mobile" ? (375, 812) : (768, 1024);
                }
                else if (IsViewportNumber(width) && IsViewportNumber(height))
                {
                    size = ((int)Math.Round(width!.Value), (int)Math.Round(height!.Value));
                }
                else
                {
                    return Error(
                        "A custom viewport needs both width and height, each a number from 1 to 9999. Use preset " +
                        "\"mobile\" or \"tablet\" for a device size, or preset \"desktop\" to clear the emulation " +
                        "and return to the pane's responsive size.");
                }

                await driver.SetViewportAsync(tabId, size.W, size.H, mobile: null, cancellationToken);
                sentences.Add(
                    $"Viewport set to {size.W}x{size.H}{(preset is not null ? $" ({preset})" : "")} on this tab. " +
                    "It stays (scaled down to fit if larger than the pane) until you call this tool with preset " +
                    "\"desktop\", so reset it when you finish testing");
            }

            if (colorScheme is not null)
            {
                await driver.SetColorSchemeAsync(tabId, colorScheme, cancellationToken);
                sentences.Add(
                    $"Color scheme emulation set to {colorScheme} on this tab; it survives reloads until you set " +
                    "the other value or the pane re-syncs the tab to the app theme");
            }

            if (sentences.Count == 0)
            {
                return Error("Provide a preset (mobile/tablet/desktop), width/height, or colorScheme.");
            }

            // A successful resize invalidates the screenshot coordinate frame.
            try
            {
                _frames.Remove(await driver.ResolveTabIdAsync(tabId, cancellationToken));
            }
            catch (InvalidOperationException)
            {
            }

            return await WithTrailerAsync(
                ToolResult.Success(string.Join(". ", sentences) + "."), tabId, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Error($"Resize failed: {ex.Message}");
        }

        static bool IsViewportNumber(double? value) =>
            value is { } v && double.IsFinite(v) && v >= 1 && v <= 9999;

        static string Truncate40(string value) => value.Length > 40 ? value[..40] : value;
    }

    // ---- navigate / tabs -------------------------------------------------------

    /// <summary>
    /// The reference's own refusal, and its own reason: a URL carrying
    /// <c>user:password@</c> would have the pane sign in on the user's behalf,
    /// which it does not do for anyone — including the agent asking.
    /// </summary>
    internal const string CredentialsRefused =
        "the URL embeds credentials (user:password@); the Browser pane never submits embedded " +
        "credentials on the user's behalf — navigate without them and let the user sign in on the page.";

    /// <summary>
    /// True for a URL with a userinfo component. The string is checked before
    /// <see cref="Uri"/> parses it, because a host that fails to parse must not
    /// slip past the check by being unparseable.
    /// </summary>
    internal static bool EmbedsCredentials(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return false;
        }

        var authority = url[(scheme + 3)..];
        var end = authority.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
        {
            authority = authority[..end];
        }

        return authority.Contains('@', StringComparison.Ordinal);
    }

    /// <summary>
    /// The popup guard, asked once per call before the tool runs.
    /// <see cref="BrowserPanePopupGuard"/> decides; this only fetches the two
    /// facts it needs, and only for a call it has an opinion about.
    /// </summary>
    public async Task<ToolResult?> PopupRefusalAsync(
        string tool, JsonObject args, CancellationToken cancellationToken)
    {
        var action = tool == "computer" ? JsonArgs.GetString(args, "action") : null;
        if (!BrowserPanePopupGuard.IsGuarded(tool, action))
        {
            return null;
        }

        try
        {
            var (targetIsPopup, anyPopup) = await driver.PopupStateAsync(
                JsonArgs.GetString(args, "tabId"), cancellationToken);
            return BrowserPanePopupGuard.Refusal(tool, action, targetIsPopup, anyPopup) is { } refusal
                ? Error(refusal)
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // The pane could not be asked. The call fails on its own terms
            // rather than being refused for a popup nobody confirmed.
            return null;
        }
    }

    public Task<ToolResult> NavigateAsync(JsonObject args, CancellationToken cancellationToken) =>
        NavigateAsync(args, card: null, cancellationToken);

    /// <summary>
    /// navigate, with the card a domain transition is consented on. The
    /// reference asks in both of this handler's branches — before a history move
    /// and before a URL navigation — and answers the tool with the outcome's own
    /// sentence rather than navigating.
    /// </summary>
    public async Task<ToolResult> NavigateAsync(
        JsonObject args, DomainTransitionCard? card, CancellationToken cancellationToken)
    {
        if (JsonArgs.GetString(args, "url") is not { } url)
        {
            return Error("`navigate` requires a string `url`");
        }

        var tabId = JsonArgs.GetString(args, "tabId");
        try
        {
            var trimmed = url.Trim();
            var lower = trimmed.ToLowerInvariant();
            if (lower is not ("back" or "forward") &&
                JarvisCode.App.Views.Panels.BrowserPanel.Normalize(trimmed) is null)
            {
                return Error("`navigate` requires a valid URL (http://, https:// or file://), or \"back\"/\"forward\".");
            }

            if (EmbedsCredentials(trimmed))
            {
                return Error(CredentialsRefused);
            }

            // A history move is consented to like any other: the reference reads
            // the entry it would land on first, then asks about that address.
            var destination = trimmed;
            if (lower is "back" or "forward" && transitions is not null)
            {
                if (await driver.HistoryTargetAsync(tabId, lower, cancellationToken) is not { } entry)
                {
                    return Error($"no {lower} history");
                }

                destination = entry;
            }

            if (await OriginRefusalAsync(destination, cancellationToken) is { } declined)
            {
                return Error(declined);
            }

            if (await TransitionRefusalAsync(tabId, destination, card, cancellationToken) is { } refused)
            {
                return Error(refused);
            }

            var outcome = await driver.NavigateAsync(
                tabId, lower is "back" or "forward" ? lower : trimmed, cancellationToken);
            return outcome.Kind switch
            {
                "no-history" => Error($"no {outcome.Origin} history"),
                "pinned-file" => Error(
                    $"Tab {outcome.Origin} is pinned to a local file preview and cannot navigate. " +
                    "Open a new tab with `tabs_create` and navigate there instead."),
                // The reference answers this one as an ordinary result, not an
                // error: nothing went wrong, the address was simply a file.
                "downloaded" => await WithTrailerAsync(
                    ToolResult.Success(
                        $"{(outcome.Origin is { Length: > 0 } d ? d : "(target)")} responded with a file " +
                        "download instead of a page; the user was shown a save dialog and the Browser pane " +
                        "did not navigate. Do not retry this URL."),
                    tabId, cancellationToken),
                "back" or "forward" => await WithTrailerAsync(
                    ToolResult.Success($"navigated {outcome.Kind}"), tabId, cancellationToken),
                _ => await WithTrailerAsync(
                    ToolResult.Success($"navigated to {(outcome.Origin is { Length: > 0 } o ? o : "(target)")}"),
                    tabId, cancellationToken),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Error($"navigate failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The site the pane is being sent to, consented to before it is opened —
    /// the reference's requestPreviewOriginPermission, which unlike the
    /// domain-transition sibling beside it is real in the shipped build. Only a
    /// tool reaches here, which is the source its own gate asks about; a user
    /// typing a URL into the pane never passes through this.
    ///
    /// <para>
    /// Null lets the navigation run. The refusal is worded with the pane's own
    /// declined builder over a subject this build chooses, which is declared:
    /// the reference returns a verdict here and words it at a call site that
    /// could not be read out of the bundle.
    /// </para>
    /// </summary>
    private async Task<string?> OriginRefusalAsync(string destination, CancellationToken cancellationToken)
    {
        if (originPrompt is not { } prompt || OriginCard is null)
        {
            return null;
        }

        // A blank tab, a file:// page or a history keyword has no site to consent
        // to, and an origin already granted is never asked about again.
        var origin = BrowserPaneDomainTransitions.NormalizeOrigin(destination);
        if (origin is null || IsOriginAllowed?.Invoke(origin) == true)
        {
            return null;
        }

        var decision = await prompt.AskAsync(
            origin,
            // The two riskier categories are read from lists Anthropic serves, so
            // nothing here can classify a site as either.
            PreviewOriginCategory.Ordinary,
            (message, detail, token) => OriginCard(message, detail, token),
            cancellationToken);

        switch (decision)
        {
            case PreviewOriginDecision.Allowed:
                OriginAllowed?.Invoke(origin);
                return null;
            case PreviewOriginDecision.DeniedByUser:
                return BrowserPaneDomainTransitions.Declined($"acting on {origin}");
            default:
                return null;
        }
    }

    /// <summary>
    /// Shows the origin card and answers with the button index. Null leaves the
    /// pane ungated, which is what a headless run and a subagent get.
    /// </summary>
    public Func<string, string, CancellationToken, Task<int>>? OriginCard { get; set; }

    /// <summary>Whether a site is already granted, asked of the host.</summary>
    public Func<string, bool>? IsOriginAllowed { get; set; }

    /// <summary>Records a site the user allowed, so it is not asked about again.</summary>
    public Action<string>? OriginAllowed { get; set; }

    /// <summary>
    /// Consents to moving this tab to another site, and answers with the
    /// sentence that outcome refuses with — or null to let the navigation run.
    /// The reference's <c>msr</c>, over its <c>requestPreviewDomainTransition</c>.
    /// </summary>
    private async Task<string?> TransitionRefusalAsync(
        string? tabId, string destination, DomainTransitionCard? card, CancellationToken cancellationToken)
    {
        if (transitions is not { } gate)
        {
            return null;
        }

        string? lastExternal;
        try
        {
            lastExternal = await driver.LastExternalOriginAsync(tabId, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // Whatever is wrong with the tab, the navigation itself is about to
            // report it properly; consent has nothing to add.
            return null;
        }

        // Where the tab stood when the card went up. Read only if a card is
        // actually shown, so an ordinary navigation costs no extra round trip.
        string? mark = null;
        DomainTransitionCard? watched = card is null
            ? null
            : async (source, target, token) =>
            {
                mark = await driver.NavigationMarkAsync(tabId, token);
                return await card(source, target, token);
            };

        var outcome = await gate.RequestAsync(
            lastExternal,
            destination,
            watched,
            async token => mark is null ||
                string.Equals(await driver.NavigationMarkAsync(tabId, token), mark, StringComparison.Ordinal),
            cancellationToken);

        return BrowserPaneDomainTransitions.Refusal(outcome);
    }

    /// <summary>The pane-closed answer for tabs_context/tabs_create (the reference's dEr).</summary>
    private const string PaneNotOpen =
        "The Browser pane isn't open yet, so there are no tabs. Call preview_start or navigate with " +
        "{\"url\": \"https://…\"} to open it.";

    /// <summary>What tabs_create answers on a closed pane — not an error, as in the reference.</summary>
    public const string NoTabCreated = "No tab was created. " + PaneNotOpen;

    /// <summary>
    /// The reference's $$r. A batch may open the pane now, but only as its FIRST
    /// action - so this answers a `navigate` that arrived later in one, and names
    /// the two places the url would have worked.
    /// </summary>
    public const string BatchCannotOpenPane =
        "The Browser pane isn't open. Open it with `navigate` and this url — on its own, or as the FIRST " +
        "`browser_batch` action — then batch the rest.";

    /// <summary>
    /// What every other pane tool answers on a closed pane. The reference tags
    /// this result `pane_not_open` in _meta, which it strips again before the
    /// result is sent, so only the sentence is portable — and the launch.json it
    /// names is the one preview_start actually reads here.
    /// </summary>
    public const string NoPreviewOpen =
        "No preview is open. Use `preview_start` or `navigate` with {\"url\": \"https://…\"} to open a browser " +
        "tab at a URL, or `preview_start` with {\"name\": \"…\"} to start a dev server from .jarvis/launch.json.";

    /// <summary>tabs_context's own body when there is no pane.</summary>
    private static string ClosedContextText()
    {
        var closed = new JsonObject { ["browserOpen"] = false, ["tabs"] = new JsonArray() };
        return $"{closed.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}\n{PaneNotOpen}";
    }

    /// <summary>
    /// The reference's guard ahead of every pane tool: with no pane open the call
    /// is answered here instead of running. `navigate` with a url is the one
    /// exception — it opens the pane — and since 1.46388.1.0 a batch may do that
    /// too, but only as its FIRST action; tabs_context/tabs_create answer with
    /// their own text rather than an error. Returns null when the call should go
    /// ahead.
    /// </summary>
    public async Task<ToolResult?> ClosedPaneAnswerAsync(
        string tool, JsonObject args, bool inBatch, bool isFirstBatchAction,
        CancellationToken cancellationToken)
    {
        // The batch is a container, not a page-facing call: its steps come back
        // through here one at a time and answer for themselves.
        if (tool == "browser_batch")
        {
            return null;
        }

        var pane = await driver.TabsContextAsync(cancellationToken);
        if (pane.BrowserOpen)
        {
            return null;
        }

        if (tool == "navigate" && OpeningUrl(args) is not null)
        {
            // A batch opens the pane only from its first action; a navigate that
            // arrived later in one is told where the url would have worked.
            return inBatch && !isFirstBatchAction ? ToolResult.Error(BatchCannotOpenPane) : null;
        }

        // preview_start is the other opener the closed-pane text names, so it is
        // never answered — and it is not batchable at all, which the batch
        // validator refuses before a step ever reaches here. The three
        // server-management tools do not touch the pane.
        if (tool == "preview_start")
        {
            return inBatch ? ToolResult.Error(BatchCannotOpenPane) : null;
        }

        if (tool is "preview_stop" or "preview_list" or "preview_logs")
        {
            return null;
        }

        return tool switch
        {
            "tabs_context" => ToolResult.Success(ClosedContextText()),
            "tabs_create" => ToolResult.Success(NoTabCreated),
            _ => ToolResult.Error(NoPreviewOpen),
        };
    }

    /// <summary>
    /// The url that would open the pane, or null. A history move cannot open one,
    /// which is the reference's own test — its lEr rejects back and forward.
    /// </summary>
    private static string? OpeningUrl(JsonObject args)
    {
        var url = JsonArgs.GetString(args, "url")?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        return url.Equals("back", StringComparison.OrdinalIgnoreCase) ||
               url.Equals("forward", StringComparison.OrdinalIgnoreCase)
            ? null
            : url;
    }

    public async Task<ToolResult> TabsContextAsync(CancellationToken cancellationToken)
    {
        var pane = await driver.TabsContextAsync(cancellationToken);
        if (!pane.BrowserOpen)
        {
            return ToolResult.Success(ClosedContextText());
        }

        var json = new JsonObject
        {
            ["browserOpen"] = true,
            ["tabs"] = new JsonArray([.. pane.Tabs.Select(static t => (JsonNode)new JsonObject
            {
                ["tabId"] = t.Id,
                ["origin"] = t.Origin,
                ["isActive"] = t.Active,
            })]),
        };
        var visibility = pane.Visibility switch
        {
            PaneVisibility.Displayed => "The Browser pane is currently displayed.",
            PaneVisibility.NotFronted => "The Browser pane is currently displayed, but this tab is not fronted.",
            PaneVisibility.Hidden => "The Browser pane is currently hidden.",
            _ => "The Browser pane is not open.",
        };
        return ToolResult.Success(
            $"{json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}\n{visibility}");
    }

    public async Task<ToolResult> TabsCreateAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var foreground = JsonArgs.GetBool(args, "foreground");
        var tabId = await driver.CreateTabAsync(foreground, cancellationToken);
        var json = new JsonObject { ["tabId"] = tabId, ["reused"] = false, ["type"] = "browser" };
        var guidance = foreground
            ? $"\nOpened tab {tabId} in the foreground. Use `navigate` with tabId \"{tabId}\" to load a URL."
            : $"\nOpened tab {tabId} in the background — the user's current tab stays in front. Use `navigate` " +
              $"with tabId \"{tabId}\" to load a URL; front it with `tabs_select` when the user should look.";
        return ToolResult.Success(
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + guidance);
    }

    public async Task<ToolResult> TabsSelectAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (JsonArgs.GetString(args, "tabId") is not { } tabId)
        {
            return Error("`tabs_select` requires a string `tabId`.");
        }

        return await driver.SelectTabAsync(tabId, cancellationToken)
            ? ToolResult.Success($"Fronted tab {tabId}.")
            : Error($"Tab {tabId} not found.");
    }

    public async Task<ToolResult> TabsCloseAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (JsonArgs.GetString(args, "tabId") is not { } tabId)
        {
            return Error("`tabs_close` requires a string `tabId`.");
        }

        var (found, wasLast) = await driver.CloseTabAsync(tabId, cancellationToken);
        if (!found)
        {
            return Error($"Tab {tabId} not found.");
        }

        return ToolResult.Success(wasLast
            ? $"Closed tab {tabId}. That was the last tab, so the Browser pane is now closed — use " +
              "`preview_start` to open it again."
            : $"Closed tab {tabId}.");
    }
}
