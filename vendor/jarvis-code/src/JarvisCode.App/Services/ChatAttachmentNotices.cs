namespace JarvisCode.App.Services;

/// <summary>
/// What the composer says when an attachment cannot be taken, in the reference
/// desktop's own words. It names the four image formats it accepts, and counts the
/// images it dropped rather than refusing the whole drop.
/// </summary>
public static class ChatAttachmentNotices
{
    /// <summary>A dropped file whose type is not one of the four accepted images.</summary>
    public const string UnsupportedImage =
        "Only PNG, JPEG, GIF, and WebP images are supported.";                   // 2jkIk4kqLa

    /// <summary>Images past the per-message cap: what was dropped, and what the cap is.</summary>
    public static string RemovedOverCap(int removed, int max) =>
        // E+pUI9A4ya
        removed == 1
            ? $"Removed 1 image — this session allows up to {max}."
            : $"Removed {removed} images — this session allows up to {max}.";
}
