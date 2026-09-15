namespace JarvisCode.Core.Utilities;

/// <summary>
/// The reference refuses network paths — UNC shares and <c>/net/&lt;host&gt;</c>
/// automounts — as working directories and attachments before touching them
/// (CLI 2.1.257). These are its two sentences and the test it applies.
/// </summary>
public static class NetworkPaths
{
    /// <summary>True for <c>\\server\share</c>, <c>//server/share</c> and <c>/net/host/…</c>.</summary>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            // Long-path syntax (\\?\C:\…) is local; \\?\UNC\ and everything else is not.
            if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                !trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        return trimmed.StartsWith("/net/", StringComparison.Ordinal);
    }

    /// <summary>The refusal for --add-dir, /add-dir and additionalDirectories, verbatim.</summary>
    public static string AddDirectoryRefusal(string path) =>
        $"{path} is a network path, which cannot be added as a working directory. On Windows, map the share to a " +
        "drive letter and pass it at launch with --add-dir (a drive letter added mid-session does not yet carry " +
        "remote-read trust)";

    /// <summary>The refusal for a file attachment, verbatim.</summary>
    public static string AttachmentRefusal(string path) =>
        $"Attachment \"{path}\" is a network path (UNC or /net autofs), which is not supported.";
}
