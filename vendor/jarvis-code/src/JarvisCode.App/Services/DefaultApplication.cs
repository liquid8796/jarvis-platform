using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// The app Windows would open a file with. The reference labels its diff and file rows
/// "Open in {appName}" when it knows the handler and plain "Open" when it does not, so
/// this answers the same question — the friendly name of the registered handler.
/// </summary>
public static class DefaultApplication
{
    private const int AssocStrFriendlyAppName = 4;
    private const uint AssocFNoneOfTheAbove = 0x0000_1000;

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int AssocQueryStringW(
        uint flags, int str, string assoc, string? extra, StringBuilder? output, ref uint outputLength);

    /// <summary>The row label: "Open in {appName}", or "Open" when nothing is registered.</summary>
    public static string OpenLabel(string path)
    {
        var name = FriendlyName(path);
        return name is { Length: > 0 } ? $"Open in {name}" : "Open";
    }

    /// <summary>The friendly name of the handler for a file's extension, or null.</summary>
    public static string? FriendlyName(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            return null;
        }

        try
        {
            uint length = 0;
            if (AssocQueryStringW(AssocFNoneOfTheAbove, AssocStrFriendlyAppName, extension, null, null, ref length) != 1
                || length == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)length);
            if (AssocQueryStringW(AssocFNoneOfTheAbove, AssocStrFriendlyAppName, extension, null, buffer, ref length) != 0)
            {
                return null;
            }

            var name = buffer.ToString();
            return name.Length > 0 ? name : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }
}
