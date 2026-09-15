using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// File upload and GIF recording for the Jarvis Browser toolset, ported from
/// the reference extension's file_upload / upload_image / gif_creator.
/// </summary>
public sealed class BrowserFileUploadTool(BrowserBridge bridge) : ITool
{
    /// <summary>The reference caps one call's combined payload at 10 MB.</summary>
    internal const long MaxTotalBytes = 10 * 1024 * 1024;

    public string Name => "file_upload";
    /// <summary>
    /// Three literals, as the reference composes it: <c>`${U0t} Only files… ${W0t}`</c>.
    /// The joined sentence is nowhere in the bundle, so keeping the halves apart
    /// is what lets each be compared against it.
    /// </summary>
    public string Description => Preamble + " " + SharedFilesRule + " " + SizeLimitRule;

    /// <summary>The reference's <c>U0t</c>, shared with upload_image.</summary>
    private const string Preamble =
        "Upload one or multiple files to a file input element on the page. Do not click on file upload " +
        "buttons or file inputs — clicking opens a native file picker dialog that you cannot see or " +
        "interact with. Instead, use read_page or find to locate the file input element, then use this tool " +
        "with its ref to upload files directly.";

    private const string SharedFilesRule =
        "Only files the user has shared with this session (attachments, the session's outputs/uploads " +
        "folders, or folders the user has connected) can be uploaded; other paths will be rejected.";

    /// <summary>The reference's <c>W0t</c>.</summary>
    private const string SizeLimitRule =
        "The combined size of all files in a single call must stay under 10 MB.";

    /// <summary>
    /// The reference's schema. Its <c>coordinate</c> drop path belongs to
    /// <c>upload_image</c>, which this port does not carry, so the key stays
    /// readable by the body for a stored session but is no longer advertised.
    /// </summary>
    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "file_upload");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
    {
        var paths = Paths(arguments);
        var where = JarvisBrowserFormat.ParseRef(arguments["ref"]) is { } reference ? $" → {reference}" : "";
        return $"BrowserFileUpload({(paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files")}{where})";
    }

    /// <summary>The reference's refusal for a path the session may not read.</summary>
    internal static string OutOfScope(string path) =>
        $"Cannot upload \"{path}\": only files this session is allowed to read can be uploaded. " +
        "Ask the user to share the file with this session, or to add its folder with /add-dir.";

    private static List<string> Paths(JsonObject arguments)
        => (arguments["paths"] as JsonArray ?? [])
            .Select(static p => p?.GetValue<string>())
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => p!)
            .ToList();

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var paths = Paths(arguments);
        if (paths.Count == 0)
        {
            return ToolResult.Error(
                "file_upload requires a non-empty `paths` array of files the user has shared with this session.");
        }

        // A page the model is driving is somewhere the user's files leave this
        // machine, so an upload may only read what the session may read. The
        // reference runs the same check before handing the paths to Chrome.
        var scope = JarvisCode.Core.Security.WorkspacePathScope.FromRoots(
            new[] { context.WorkingDirectory }.Concat(context.AdditionalDirectories));

        var resolved = new List<FileInfo>(paths.Count);
        long total = 0;
        foreach (var path in paths)
        {
            if (context.EnforceWorkspaceFileScope && !scope.ValidatePath(path, context.WorkingDirectory).Allowed)
            {
                return ToolResult.Error(OutOfScope(path));
            }

            var file = new FileInfo(Path.GetFullPath(path, context.WorkingDirectory));
            if (!file.Exists)
            {
                return ToolResult.Error($"No such file: {file.FullName}");
            }

            total += file.Length;
            resolved.Add(file);
        }

        if (total > MaxTotalBytes)
        {
            return ToolResult.Error(
                $"The files total {total / 1024 / 1024}MB; one upload must stay under " +
                $"{MaxTotalBytes / 1024 / 1024}MB.");
        }

        var reference = JarvisBrowserFormat.ParseRef(arguments["ref"]);
        var coordinate = arguments["coordinate"] as JsonArray;
        if (reference is null && coordinate is not { Count: 2 })
        {
            return ToolResult.Error("Pass ref (the file input) or coordinate [x, y] (a drop target).");
        }

        var args = JarvisBrowserTools.TabArgs(arguments);
        if (reference is { } target)
        {
            target.ApplyTo(args);
            args["paths"] = new JsonArray([.. resolved.Select(static f => JsonValue.Create(f.FullName))]);
            return await JarvisBrowserTools.Run(bridge, "file_upload", args,
                data => $"Uploaded {data["uploaded"]} file(s) to {target}.",
                cancellationToken);
        }

        if (resolved.Count != 1)
        {
            return ToolResult.Error("A coordinate drop takes exactly one file; use a ref to upload several.");
        }

        var bytes = await File.ReadAllBytesAsync(resolved[0].FullName, cancellationToken);
        args["x"] = JarvisBrowserFormat.AsNumber(coordinate![0]);
        args["y"] = JarvisBrowserFormat.AsNumber(coordinate[1]);
        args["name"] = resolved[0].Name;
        args["mime"] = MimeType(resolved[0].Extension);
        args["data"] = Convert.ToBase64String(bytes);
        return await JarvisBrowserTools.Run(bridge, "drop_image", args,
            data => $"Dropped {resolved[0].Name} onto the <{data["target"]}> at {args["x"]},{args["y"]}.",
            cancellationToken);
    }

    internal static string MimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}

/// <summary>
/// Records the tab as a GIF: the extension captures screencast frames and logs
/// the input events driving them, and export composes both into the file.
/// </summary>
public sealed class BrowserGifTool(BrowserBridge bridge) : ITool
{
    public string Name => "gif_creator";
    public string Description =>
        "Manage GIF recording and export for browser automation sessions. Control when to start/stop " +
        "recording browser actions (clicks, scrolls, navigation), then export as an animated GIF with " +
        "visual overlays (click indicators, action labels, progress bar, watermark). All operations are " +
        "scoped to the tab's group. When starting recording, take a screenshot immediately after to capture " +
        "the initial state as the first frame. When stopping recording, take a screenshot immediately " +
        "before to capture the final state as the last frame. For export, either provide 'coordinate' to " +
        "drag/drop upload to a page element, or set 'download: true' to download the GIF.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "gif_creator");

    public bool IsReadOnly => false;
    public string DescribeCall(JsonObject arguments) => $"BrowserGif({JsonArgs.GetString(arguments, "action")})";

    internal static GifOptions ReadOptions(JsonObject arguments)
    {
        var options = arguments["options"] as JsonObject ?? [];

        // The reference spells these camelCase; the snake_case names this port
        // used are still read so a stored session replays.
        bool Flag(string name, string legacy, bool fallback) =>
            options[name] is JsonValue value && value.TryGetValue<bool>(out var set) ? set
            : options[legacy] is JsonValue old && old.TryGetValue<bool>(out var wasSet) ? wasSet
            : fallback;

        return new GifOptions
        {
            ShowClickIndicators = Flag("showClickIndicators", "show_click_indicators", true),
            ShowActionLabels = Flag("showActionLabels", "show_action_labels", true),
            ShowProgressBar = Flag("showProgressBar", "show_progress_bar", true),
            ShowWatermark = Flag("showWatermark", "show_watermark", true),
            ShowDragPaths = Flag("showDragPaths", "show_drag_paths", true),
            Quality = (int)Math.Clamp(JarvisBrowserFormat.AsNumber(options["quality"]) ?? 7, 1, 10),
        };
    }

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var action = JsonArgs.GetString(arguments, "action");
        var args = JarvisBrowserTools.TabArgs(arguments);

        switch (action)
        {
            case "start_recording":
                args["op"] = "start";
                return await JarvisBrowserTools.Run(bridge, "gif", args,
                    _ => "Recording. Take a screenshot now to capture the opening frame, then act on the page.",
                    cancellationToken);

            case "stop_recording":
                args["op"] = "stop";
                return await JarvisBrowserTools.Run(bridge, "gif", args,
                    data => $"Stopped with {data["frames"]} frame(s) held. Export to write the GIF.",
                    cancellationToken);

            case "clear":
                args["op"] = "clear";
                return await JarvisBrowserTools.Run(bridge, "gif", args, _ => "Discarded the recording.", cancellationToken);

            case "export":
                return await ExportAsync(arguments, args, context, cancellationToken);

            default:
                return ToolResult.Error("action must be start_recording, stop_recording, export or clear.");
        }
    }

    private async Task<ToolResult> ExportAsync(
        JsonObject arguments, JsonObject args, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        args["op"] = "fetch";
        JsonObject data;
        try
        {
            data = await bridge.RequestAsync("gif", args, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return ToolResult.Error(ex.Message);
        }

        var frames = (data["frames"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(static f => new GifFrame(
                Convert.FromBase64String(f["data"]?.GetValue<string>() ?? ""),
                JarvisBrowserFormat.AsNumber(f["ts"]) ?? 0))
            .Where(static f => f.Jpeg.Length > 0)
            .ToList();
        if (frames.Count == 0)
        {
            return ToolResult.Error("No frames were captured — start_recording, act on the page, then export.");
        }

        var events = (data["events"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(static e => new GifEvent(
                e["action"]?.GetValue<string>() ?? "action",
                JarvisBrowserFormat.AsNumber(e["ts"]) ?? 0,
                JarvisBrowserFormat.AsNumber(e["x"]),
                JarvisBrowserFormat.AsNumber(e["y"]),
                e["label"]?.GetValue<string>()))
            .ToList();

        byte[] gif;
        try
        {
            gif = GifComposer.Compose(frames, events, ReadOptions(arguments));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            return ToolResult.Error($"The GIF could not be composed: {ex.Message}");
        }

        // The reference names the export target `filename`; `path` is still read
        // so a stored session replays.
        var named = JsonArgs.GetString(arguments, "filename")
                    ?? JsonArgs.GetString(arguments, "path");
        var path = Path.GetFullPath(
            named is { Length: > 0 } given
                ? given
                : $"jarvis-recording-{DateTime.Now:yyyyMMdd-HHmmss}.gif",
            context.WorkingDirectory);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, gif, cancellationToken);
        var summary = $"Wrote {path} — {frames.Count} frame(s), {gif.Length / 1024}kB.";
        if (data["dropped"] is not null && JarvisBrowserFormat.AsNumber(data["dropped"]) > 0)
        {
            summary += $" ({data["dropped"]} early frame(s) fell out of the capture buffer.)";
        }

        if (arguments["coordinate"] is not JsonArray { Count: 2 } coordinate)
        {
            return ToolResult.Success(summary);
        }

        var drop = JarvisBrowserTools.TabArgs(arguments);
        drop["x"] = JarvisBrowserFormat.AsNumber(coordinate[0]);
        drop["y"] = JarvisBrowserFormat.AsNumber(coordinate[1]);
        drop["name"] = Path.GetFileName(path);
        drop["mime"] = "image/gif";
        drop["data"] = Convert.ToBase64String(gif);
        return await JarvisBrowserTools.Run(bridge, "drop_image", drop,
            dropped => $"{summary} Dropped it onto the <{dropped["target"]}> at {drop["x"]},{drop["y"]}.",
            cancellationToken);
    }
}

/// <summary>
/// upload_image — the reference's companion to file_upload: it uploads an image
/// this session already captured rather than a file on disk, by the id the
/// screenshot's own result printed.
///
/// The transport is file_upload's: a <c>ref</c> hands the path to the page's file
/// input, a <c>coordinate</c> synthesizes a drop. Only the source differs.
/// </summary>
public sealed class BrowserUploadImageTool(BrowserBridge bridge) : ITool
{
    public string Name => "upload_image";

    public string Description =>
        "Upload a previously captured screenshot or user-uploaded image to a file input or drag & drop " +
        "target. Supports two approaches: (1) ref - for targeting specific elements, especially hidden file " +
        "inputs, (2) coordinate - for drag & drop to visible locations like Google Docs. Provide either ref " +
        "or coordinate, not both.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ClaudeInChrome, "upload_image");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments) =>
        $"BrowserUploadImage({JsonArgs.GetString(arguments, "imageId") ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (JsonArgs.GetString(arguments, "imageId")?.Trim() is not { Length: > 0 } imageId)
        {
            return ToolResult.Error("imageId is required — pass the id a screenshot result reported.");
        }

        if (CapturedImages.Find(imageId) is not { } data)
        {
            return ToolResult.Error(CapturedImages.NotFound(imageId));
        }

        var reference = JarvisBrowserFormat.ParseRef(arguments["ref"]);
        var coordinate = arguments["coordinate"] as JsonArray;
        if (reference is null && coordinate is not { Count: 2 })
        {
            return ToolResult.Error("Pass ref (the file input) or coordinate [x, y] (a drop target).");
        }

        var name = JsonArgs.GetString(arguments, "filename")?.Trim() is { Length: > 0 } given
            ? given
            : "image.png";

        var args = JarvisBrowserTools.TabArgs(arguments);
        if (reference is { } target)
        {
            // A file input is handed a path, so this capture is materialised —
            // the only time one reaches disk without save_to_disk asking.
            if (CapturedImages.WriteToDisk(imageId, name) is not { } path)
            {
                return ToolResult.Error($"Could not write {imageId} to a temporary file for the upload.");
            }

            target.ApplyTo(args);
            args["paths"] = new JsonArray(JsonValue.Create(path));
            return await JarvisBrowserTools.Run(bridge, "file_upload", args,
                data => $"Uploaded {data["uploaded"]} file(s) to {target}.",
                cancellationToken);
        }

        args["x"] = JarvisBrowserFormat.AsNumber(coordinate![0]);
        args["y"] = JarvisBrowserFormat.AsNumber(coordinate[1]);
        args["name"] = name;
        args["mime"] = BrowserFileUploadTool.MimeType(Path.GetExtension(name));
        args["data"] = data;
        return await JarvisBrowserTools.Run(bridge, "drop_image", args,
            data => $"Dropped {name} onto the <{data["target"]}> at {args["x"]},{args["y"]}.",
            cancellationToken);
    }
}
