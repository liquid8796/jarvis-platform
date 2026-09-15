using System.IO;
using System.Windows.Media.Imaging;

namespace JarvisCode.App.Services;

/// <summary>
/// Prepares an attached image for sending the way the reference pipeline does:
/// small files of an accepted type pass through untouched, anything else is
/// clamped to 2000px on its longest edge and re-encoded as JPEG (quality 85).
/// </summary>
public static class ImageAttachments
{
    /// <summary>Reference pre-processing cap: images over 30 MB are refused at attach.</summary>
    public const long MaxSourceBytes = 30 * 1024 * 1024;

    /// <summary>Files at or under this size in an accepted format are sent unmodified.</summary>
    public const int PassThroughBytes = 512_000;

    /// <summary>Longest-edge clamp applied when re-encoding.</summary>
    public const int MaxDimension = 2000;

    private static readonly string[] AcceptedExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public static bool IsAcceptedExtension(string path) =>
        AcceptedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    public static (string MediaType, byte[] Data) PrepareForSend(byte[] bytes, string path)
    {
        if (bytes.Length <= PassThroughBytes && IsAcceptedExtension(path))
        {
            return (MediaTypeFor(path), bytes);
        }

        using var source = new MemoryStream(bytes);
        var frame = BitmapFrame.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        System.Windows.Media.Imaging.BitmapSource image = frame;
        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longest > MaxDimension)
        {
            var scale = (double)MaxDimension / longest;
            image = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return ("image/jpeg", output.ToArray());
    }
}
