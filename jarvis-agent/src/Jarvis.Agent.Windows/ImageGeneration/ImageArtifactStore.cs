using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jarvis.Agent.Core.ImageGeneration;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.ImageGeneration;

/// <summary>Original bytes are immutable. Only separate, bounded PNG previews are resized.</summary>
public sealed class ImageArtifactStore : IImageArtifactStore
{
    public const long MaxInputBytes = 10 * 1024 * 1024;
    public const long MaxResultBytes = 24 * 1024 * 1024;
    public const int MaxPreviewBytes = 384 * 1024;
    public async Task<IReadOnlyList<ImageArtifact>> StageInputsAsync(IReadOnlyList<string> paths, IReadOnlyList<string> parents, string directory, CancellationToken ct)
    {
        if (paths.Count > 5) throw new ArgumentException("At most five reference images are supported.");
        var result = new List<ImageArtifact>();
        long total = 0;
        foreach (var path in paths)
        {
            var bytes = await ReadBoundedAsync(path, MaxInputBytes, ct).ConfigureAwait(false);
            total += bytes.Length;
            if (total > MaxInputBytes) throw new ImageGenerationException("INPUT_TOO_LARGE", "Reference images exceed the combined 10 MiB upload limit.");
            var decoded = Decode(bytes);
            var hash = Hash(bytes);
            var dest = Path.Combine(directory, "inputs", $"source-{result.Count}-{hash[..12]}{decoded.Extension}");
            await WriteOriginalAsync(dest, bytes, ct).ConfigureAwait(false);
            var parent = parents.Count > result.Count ? new[] { parents[result.Count] } : Array.Empty<string>();
            result.Add(new("ia_" + Guid.NewGuid().ToString("N"), dest, decoded.Mime, decoded.Image.PixelWidth, decoded.Image.PixelHeight,
                bytes.Length, hash, null, parent));
        }
        return result;
    }
    public async Task<IReadOnlyList<ImageArtifact>> ImportResultsAsync(JsonObject browserResult, IReadOnlyList<ImageArtifact> parents, string directory, string jobId, CancellationToken ct)
    {
        if (browserResult["downloads"] is not JsonArray { Count: > 0 and <= 5 } files)
            throw new ImageGenerationException("RESULT_UNVERIFIED", "The browser did not report verified image downloads.");
        var output = new List<ImageArtifact>();
        long total = 0;
        foreach (var node in files)
        {
            if (node is not JsonObject file || file["filename"]?.GetValue<string>() is not { Length: > 0 } path ||
                file["state"]?.GetValue<string>() != "complete" || file["downloadId"]?.GetValue<int>() is not >= 0)
                throw new ImageGenerationException("RESULT_UNVERIFIED", "A browser download is incomplete or unverified.");
            ValidateDownloadPath(path, jobId);
            var bytes = await ReadBoundedAsync(path, MaxResultBytes, ct).ConfigureAwait(false);
            total += bytes.Length;
            if (total > 64 * 1024 * 1024) throw new ImageGenerationException("RESULT_TOO_LARGE", "Generated images exceed the bounded import size.");
            if (file["fileSize"]?.GetValue<long>() is { } size && size >= 0 && size != bytes.Length)
                throw new ImageGenerationException("RESULT_CHANGED", "A downloaded image changed before import.");
            var decoded = Decode(bytes);
            var hash = Hash(bytes);
            var id = "ia_" + ImageGenerationSafety.Digest(jobId + ":" + output.Count + ":" + hash)[..32];
            var dest = Path.Combine(directory, "outputs", id + decoded.Extension);
            await WriteOriginalAsync(dest, bytes, ct).ConfigureAwait(false);
            var preview = Path.Combine(directory, "previews", id + ".png");
            await WriteOriginalAsync(preview, EncodePreview(decoded.Image), ct).ConfigureAwait(false);
            output.Add(new(id, dest, decoded.Mime, decoded.Image.PixelWidth, decoded.Image.PixelHeight, bytes.Length, hash, preview,
                parents.Select(p => p.ArtifactId).ToArray()));
        }
        return output;
    }
    public async Task<IReadOnlyList<WireImage>> ReadPreviewsAsync(IReadOnlyList<ImageArtifact> artifacts, CancellationToken ct)
    {
        var result = new List<WireImage>();
        foreach (var image in artifacts.Take(5))
        {
            if (image.PreviewPath is not { } path) continue;
            var bytes = await ReadBoundedAsync(path, MaxPreviewBytes, ct).ConfigureAwait(false);
            if (Decode(bytes).Mime != "image/png") throw new IOException("Image preview is invalid.");
            result.Add(new WireImage("image/png", Convert.ToBase64String(bytes)));
        }
        return result;
    }
    public static void ValidateDownloadPath(string path, string jobId)
    {
        if (!ImageGenerationSafety.IsJobId(jobId) || !Path.IsPathFullyQualified(path)) throw new IOException("Invalid image download path.");
        ImageGenerationSafety.NoLinks(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Path.GetFileName(dir).Equals(jobId, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), "JarvisImageGen", StringComparison.Ordinal) ||
            !Path.GetFileName(path).StartsWith("result-", StringComparison.Ordinal))
            throw new IOException("Download is outside this ImageGen job's browser destination.");
    }
    public static async Task<byte[]> ReadBoundedAsync(string path, long maxBytes, CancellationToken ct)
    {
        ImageGenerationSafety.NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length <= 0 || stream.Length > maxBytes) throw new ImageGenerationException("IMAGE_SIZE", "An image is empty or exceeds the supported size.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw new IOException("Image size changed while reading.");
        return bytes;
    }
    private static async Task WriteOriginalAsync(string path, byte[] bytes, CancellationToken ct)
    {
        ImageGenerationSafety.NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = await ReadBoundedAsync(path, MaxResultBytes, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes))) throw new IOException("Refusing to overwrite a different image artifact.");
            return;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await output.WriteAsync(bytes, ct).ConfigureAwait(false); await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true); }
            ImageGenerationSafety.NoLinks(path);
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private sealed record Decoded(BitmapSource Image, string Mime, string Extension);
    private static Decoded Decode(byte[] bytes)
    {
        var (mime, extension) = bytes.AsSpan() switch
        {
            var s when s.Length >= 8 && s[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) => ("image/png", ".png"),
            var s when s.Length >= 3 && s[0] == 255 && s[1] == 216 && s[2] == 255 => ("image/jpeg", ".jpg"),
            var s when s.Length >= 6 && (s[..6].SequenceEqual("GIF89a"u8) || s[..6].SequenceEqual("GIF87a"u8)) => ("image/gif", ".gif"),
            var s when s.Length >= 12 && s[..4].SequenceEqual("RIFF"u8) && s[8..12].SequenceEqual("WEBP"u8) => ("image/webp", ".webp"),
            _ => throw new ImageGenerationException("IMAGE_FORMAT", "Use a valid PNG, JPEG, GIF or supported WebP image. SVG/HTML and non-image downloads are rejected.")
        };
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth is <= 0 or > 8192 || frame.PixelHeight is <= 0 or > 8192 || (long)frame.PixelWidth * frame.PixelHeight > 32_000_000)
                throw new ImageGenerationException("IMAGE_DIMENSIONS", "Image dimensions exceed the safe decode limit.");
            // Check dimensions before decoding pixels to bound decompression memory.
            using var full = new MemoryStream(bytes, false);
            var loaded = BitmapFrame.Create(full, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            loaded.Freeze();
            return new(loaded, mime, extension);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        { throw new ImageGenerationException("IMAGE_FORMAT", "The image could not be decoded. WebP requires a compatible Windows codec; use PNG when unavailable."); }
    }
    private static byte[] EncodePreview(BitmapSource image)
    {
        for (var limit = 768; limit >= 96; limit /= 2)
        {
            var ratio = Math.Min(1d, (double)limit / Math.Max(image.PixelWidth, image.PixelHeight));
            BitmapSource preview = ratio < 1 ? new TransformedBitmap(image, new ScaleTransform(ratio, ratio)) : image;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
            using var output = new MemoryStream(); encoder.Save(output);
            if (output.Length <= MaxPreviewBytes) return output.ToArray();
        }
        throw new IOException("Could not create a bounded PNG preview.");
    }
}
