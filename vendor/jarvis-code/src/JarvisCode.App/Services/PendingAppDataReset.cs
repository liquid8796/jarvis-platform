using System.IO;

namespace JarvisCode.App.Services;

/// <summary>
/// Reset App Data, deferred by one launch. The reference deletes the profile from
/// the process that is quitting; this app cannot, because the settings file, the
/// session store and every engine profile inside it are still open in this
/// process. So the deletion is armed with a marker and carried out by the next
/// launch, before anything opens a file.
///
/// The marker sits beside the profile rather than inside it, which is what keeps it
/// from being deleted by the very sweep it asks for.
/// </summary>
public static class PendingAppDataReset
{
    private const string MarkerName = ".reset-pending";

    private static string MarkerPath(string profileRoot) =>
        Path.Combine(Path.GetDirectoryName(profileRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? profileRoot,
            Path.GetFileName(profileRoot.TrimEnd(Path.DirectorySeparatorChar)) + MarkerName);

    /// <summary>Asks the next launch of this profile to wipe it.</summary>
    public static void Arm(string profileRoot)
    {
        try
        {
            File.WriteAllText(MarkerPath(profileRoot), profileRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing is armed, so the next launch simply keeps the data.
        }
    }

    /// <summary>
    /// Runs an armed reset. Returns true when one was pending, so the caller can say
    /// so. Everything under the profile goes except the logs the reference also
    /// keeps, since a reset caused by a crash is exactly when they are wanted.
    /// </summary>
    public static bool RunIfArmed(string profileRoot)
    {
        var marker = MarkerPath(profileRoot);
        if (!File.Exists(marker))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(profileRoot))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(profileRoot))
                {
                    if (entry.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            Directory.Delete(entry, recursive: true);
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // One locked entry must not abandon the rest of the reset.
                    }
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(marker);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A marker that survives would reset again; nothing else can be done here.
            }
        }

        return true;
    }
}
