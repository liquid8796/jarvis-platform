using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

/// <summary>Codex-compatible local image inspection with original and bounded high-detail payloads.</summary>
public sealed class CodexViewImageTool(string privateRoot) : IAgentTool
{
    private const long MaxBytes = 50L * 1024 * 1024;
    private const long MaxPixels = 100_000_000;

    public ToolDescriptor Descriptor { get; } = new(
        "image.view_image",
        "view_image",
        "image",
        "View a local image file for visual inspection. Use detail='high' for a bounded high-detail preview or detail='original' for the original encoded image.",
        WireJson.Element(new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", minLength = 1, description = "Absolute path or path relative to the selected workspace." },
                detail = new { type = "string", @enum = new[] { "high", "original" }, @default = "high" }
            },
            required = new[] { "path" },
            additionalProperties = false
        }),
        ReadOnly: true,
        Sensitive: false);

    public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var raw = arguments.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("path is required.");
            var path = WorkspaceDirectories.ResolvePath(raw, context.Workspace);
            RejectPrivate(path);
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("Image file was not found.", path);
            if (file.Length > MaxBytes) throw new InvalidDataException("Image exceeds the 50 MiB local inspection limit.");
            var detail = arguments.TryGetProperty("detail", out var node) ? node.GetString() ?? "high" : "high";
            if (detail is not ("high" or "original")) throw new ArgumentException("detail must be 'high' or 'original'.");

            var (mime, bytes, width, height) = detail == "original" ? Original(path) : High(path);
            var base64 = Convert.ToBase64String(bytes);
            var text = JsonSerializer.Serialize(new
            {
                detail,
                path,
                width,
                height,
                image_url = $"data:{mime};base64,{base64}"
            }, WireJson.Options);
            return Task.FromResult(new ToolReply(text, Images: [new WireImage(mime, base64)]));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            return Task.FromResult(ToolReply.Error(ex.Message));
        }
    }

    private (string Mime, byte[] Bytes, int Width, int Height) Original(string path)
    {
        using var input = File.OpenRead(path);
        using var image = System.Drawing.Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        ValidateDimensions(image.Width, image.Height);
        return (Mime(path, image.RawFormat), File.ReadAllBytes(path), image.Width, image.Height);
    }

    private static (string Mime, byte[] Bytes, int Width, int Height) High(string path)
    {
        using var input = File.OpenRead(path);
        using var source = System.Drawing.Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        ValidateDimensions(source.Width, source.Height);
        const int maximum = 2048;
        var ratio = Math.Min(1d, maximum / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        using var output = new System.Drawing.Bitmap(width, height, PixelFormat.Format32bppArgb);
        output.SetResolution(Math.Max(1, source.HorizontalResolution), Math.Max(1, source.VerticalResolution));
        using (var graphics = System.Drawing.Graphics.FromImage(output))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, width, height));
        }
        using var encoded = new MemoryStream();
        output.Save(encoded, ImageFormat.Png);
        return ("image/png", encoded.ToArray(), width, height);
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException("Image dimensions are invalid or exceed the 100 megapixel limit.");
    }

    private static string Mime(string path, ImageFormat format)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            _ when format.Guid == ImageFormat.Png.Guid => "image/png",
            _ when format.Guid == ImageFormat.Jpeg.Guid => "image/jpeg",
            _ when format.Guid == ImageFormat.Gif.Guid => "image/gif",
            _ when format.Guid == ImageFormat.Bmp.Guid => "image/bmp",
            _ when format.Guid == ImageFormat.Tiff.Guid => "image/tiff",
            _ => "application/octet-stream"
        };
    }

    private void RejectPrivate(string path)
    {
        var root = ToolExecutionResources.CanonicalPath(privateRoot);
        var target = ToolExecutionResources.CanonicalPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Equals(root, comparison) || target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("The agent's private profile cannot be accessed by view_image.");
    }
}
