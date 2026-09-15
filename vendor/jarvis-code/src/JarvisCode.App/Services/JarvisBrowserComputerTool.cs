using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// Mouse/keyboard automation and screenshots inside a browser tab, ported from
/// the reference Browser pane's computer tool. Input is dispatched through the
/// DevTools protocol, so events are trusted; clicks accept either a coordinate
/// (CSS pixels in the tab's viewport, as screenshotted) or a ref from
/// read_page/find.
/// </summary>
public sealed class BrowserComputerTool(BrowserBridge bridge, string? imageDirectory = null) : ITool
{
    public string Name => "computer";
    public string Description =>
        "Use a mouse and keyboard to interact with a web browser, and take screenshots. If you don't have a " +
        "valid tab ID, use tabs_context_mcp first to get available tabs.\n" +
        "* Whenever you intend to click on an element like an icon, you should consult a screenshot to " +
        "determine the coordinates of the element before moving the cursor.\n" +
        "* If you tried clicking on a program or link but it failed to load, even after waiting, try " +
        "adjusting your click location so that the tip of the cursor visually falls on the element that you " +
        "want to click.\n" +
        "* Make sure to click any buttons, links, icons, etc with the cursor tip in the center of the " +
        "element. Don't click boxes on their edges unless asked.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "computer");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
    {
        var action = JsonArgs.GetString(arguments, "action") ?? "?";
        var target = JarvisBrowserFormat.ParseRef(arguments["ref"]) is { } reference
            ? $" {reference}"
            : arguments["coordinate"] is JsonArray { Count: 2 } point ? $" {point[0]},{point[1]}" : "";
        var text = JsonArgs.GetString(arguments, "text");
        if (action is "key" && text is not null)
        {
            target = $" {text}";
        }

        return $"BrowserComputer({action}{target})";
    }

    /// <summary>Where a capture is written, whether or not save_to_disk asked for it.</summary>
    internal string ImageDirectory =>
        imageDirectory ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jarvis-code", "screenshots");

    /// <summary>Writes one screenshot beside the app's settings and names the file.</summary>
    private string SaveImage(string base64)
    {
        var directory = ImageDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, $"browser-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            System.IO.File.WriteAllBytes(path, Convert.FromBase64String(base64));
            return $"Saved to disk: {path}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or FormatException)
        {
            return $"Could not save the image to disk: {ex.Message}";
        }
    }

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var action = JsonArgs.GetString(arguments, "action");
        if (string.IsNullOrWhiteSpace(action))
        {
            return ToolResult.Error("action is required.");
        }

        var args = JarvisBrowserTools.TabArgs(arguments);
        args["action"] = action;
        if (arguments["coordinate"] is JsonArray { Count: 2 } coordinate)
        {
            args["x"] = JarvisBrowserFormat.AsNumber(coordinate[0]);
            args["y"] = JarvisBrowserFormat.AsNumber(coordinate[1]);
        }

        if (arguments["start_coordinate"] is JsonArray { Count: 2 } start)
        {
            args["startX"] = JarvisBrowserFormat.AsNumber(start[0]);
            args["startY"] = JarvisBrowserFormat.AsNumber(start[1]);
        }

        if (JarvisBrowserFormat.ParseRef(arguments["ref"]) is { } reference)
        {
            reference.ApplyTo(args);
        }

        foreach (var (from, to) in new[]
        {
            ("text", "text"), ("modifiers", "modifiers"), ("scroll_direction", "scrollDirection"),
        })
        {
            if (JsonArgs.GetString(arguments, from) is { } value)
            {
                args[to] = value;
            }
        }

        foreach (var (from, to) in new[]
        {
            ("scroll_amount", "scrollAmount"), ("duration", "duration"), ("repeat", "repeat"), ("scale", "scale"),
        })
        {
            if (JarvisBrowserFormat.AsNumber(arguments[from]) is { } number)
            {
                args[to] = number;
            }
        }

        if (arguments["region"] is JsonArray { Count: 4 } region)
        {
            args["region"] = region.DeepClone();
        }

        try
        {
            var data = await bridge.RequestAsync("computer", args, cancellationToken);
            if (data["image"]?.GetValue<string>() is { } image)
            {
                var size = data["width"] is not null
                    ? $" ({data["width"]}x{data["height"]} viewport)"
                    : "";
                var saved = JsonArgs.GetBool(arguments, "save_to_disk") ? " " + SaveImage(image) : "";

                // upload_image takes "ID of a previously captured screenshot",
                // so the capture is remembered and its id printed here.
                var id = $" imageId: {CapturedImages.Register(image)}.";
                return ToolResult.WithImage(
                    $"{(action == "zoom" ? "Zoomed screenshot" : "Screenshot")}{size}.{saved}{id}",
                    new ImageBlock("image/png", image));
            }

            return ToolResult.Success(Summarize(action!, data));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    private static string Summarize(string action, JsonObject data) => action switch
    {
        "left_click" or "right_click" or "double_click" or "triple_click"
            => $"{action} at {data["x"]},{data["y"]}.",
        "hover" => $"Hovering at {data["x"]},{data["y"]}.",
        "scroll" => $"Scrolled {data["scrolled"]}.",
        "scroll_to" => $"Scrolled the element into view at {data["x"]},{data["y"]}.",
        "type" => "Typed the text.",
        "key" => $"Pressed {data["pressed"]}{(JarvisBrowserFormat.AsNumber(data["repeat"]) > 1 ? $" x{data["repeat"]}" : "")}.",
        "wait" => $"Waited {data["waited"]}s.",
        "left_click_drag" => "Dragged.",
        _ => data.ToJsonString(),
    };
}

/// <summary>
/// Executes a sequence of Jarvis Browser tool calls in one round trip, ported
/// from the reference browser_batch. Items run sequentially and stop on the
/// first error; the batch itself cannot be nested.
/// </summary>
public sealed class BrowserBatchTool(Func<string, ITool?> resolve) : ITool
{
    public string Name => "browser_batch";
    public string Description =>
        "Execute a sequence of browser tool calls in ONE round trip. Each item is {name, input} where input " +
        "is exactly what you'd pass to that tool standalone. Actions execute SEQUENTIALLY (not in parallel) " +
        "and stop on the first error. Use this tool extensively to quickly execute work whenever you can " +
        "predict two or more steps ahead — e.g. navigate, click a field, type, press Return, screenshot. " +
        "Each tool's own permission check runs per item — if an action navigates to a domain without " +
        "permission, the next item's check fails and the batch stops. Screenshots and other images are " +
        "returned interleaved with outputs; coordinates you write in THIS batch refer to the screenshot " +
        "taken BEFORE this call. browser_batch cannot be nested.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "browser_batch");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
        => arguments["actions"] is JsonArray actions
            ? $"BrowserBatch({string.Join(" → ", actions.OfType<JsonObject>().Select(static a => a["name"]?.GetValue<string>()))})"
            : "BrowserBatch()";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (arguments["actions"] is not JsonArray actions || actions.Count == 0)
        {
            return ToolResult.Error("actions is required: a list of {name, input} items.");
        }

        var output = new StringBuilder();
        var images = new List<ImageBlock>();
        var step = 0;
        foreach (var item in actions)
        {
            step++;
            cancellationToken.ThrowIfCancellationRequested();
            var name = (item as JsonObject)?["name"]?.GetValue<string>();
            if (name is null)
            {
                return Fail(output, images, step, "each action needs a name.");
            }

            if (name == Name)
            {
                return Fail(output, images, step, "browser_batch cannot be nested.");
            }

            var tool = resolve(name);
            if (tool is null)
            {
                return Fail(output, images, step, $"\"{name}\" is not a Jarvis Browser tool.");
            }

            var input = (item as JsonObject)?["input"] as JsonObject;
            var result = await tool.ExecuteAsync(
                input?.DeepClone() as JsonObject ?? [], context, cancellationToken);
            output.Append("→ ").Append(name).Append(": ").AppendLine(result.Content.TrimEnd());
            if (result.Images is not null)
            {
                images.AddRange(result.Images);
            }

            if (result.IsError)
            {
                output.Append($"Batch stopped at step {step} ({name}).");
                return new ToolResult(output.ToString(), IsError: true, images.Count > 0 ? images : null);
            }
        }

        return new ToolResult(output.ToString().TrimEnd(), IsError: false, images.Count > 0 ? images : null);
    }

    private static ToolResult Fail(StringBuilder output, List<ImageBlock> images, int step, string message)
    {
        output.Append($"Batch stopped at step {step}: {message}");
        return new ToolResult(output.ToString(), IsError: true, images.Count > 0 ? images : null);
    }
}
