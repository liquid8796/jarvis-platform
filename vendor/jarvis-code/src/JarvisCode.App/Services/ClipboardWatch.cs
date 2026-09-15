using System.Runtime.InteropServices;

namespace JarvisCode.App.Services;

/// <summary>
/// Watches the OS clipboard around the Browser pane's synthetic input, so a page that
/// fires a copy handler from a driven click cannot change what the user pastes without
/// either of them being told. The reference desktop runs the same guard (its
/// ClipboardGuard); Windows hands us a sequence number that changes on any write, which
/// answers "did it change" without opening the clipboard and risking clobbering it.
/// </summary>
public static class ClipboardWatch
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>
    /// A token for the clipboard's current contents, or null when the OS will not say.
    /// Only ever compared with another token; the value itself means nothing.
    /// </summary>
    public static uint? Token()
    {
        try
        {
            var sequence = GetClipboardSequenceNumber();
            return sequence == 0 ? null : sequence;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>True when the clipboard was written between the two tokens.</summary>
    public static bool Changed(uint? before, uint? after) =>
        before is { } b && after is { } a && b != a;
}
