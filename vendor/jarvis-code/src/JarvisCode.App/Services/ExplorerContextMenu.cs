using Microsoft.Win32;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's folder context-menu entry, as an opt-in Settings switch.
/// Written under HKCU only (no elevation), and fully removed when switched off.
/// The command routes through the single-instance pipe when the app is already
/// running. The verb reads "Open in Jarvis Code", which is the reference's own
/// wording (its <c>lGSZOWCVYC</c>), and carries the same MultiSelectModel=Single
/// value it writes so the shell offers it once for a multiple selection.
/// </summary>
public static class ExplorerContextMenu
{
    private const string FolderKey = @"Software\Classes\Directory\shell\JarvisCode";
    private const string BackgroundKey = @"Software\Classes\Directory\Background\shell\JarvisCode";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(FolderKey);
        return key is not null;
    }

    public static void SetEnabled(bool enabled, string? profile)
    {
        if (!enabled)
        {
            Registry.CurrentUser.DeleteSubKeyTree(FolderKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(BackgroundKey, throwOnMissingSubKey: false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var profileArg = profile is { Length: > 0 } ? $" --profile={profile}" : "";
        foreach (var (path, token) in new[] { (FolderKey, "%1"), (BackgroundKey, "%V") })
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("", "Open in Jarvis Code");
            key.SetValue("MultiSelectModel", "Single");
            key.SetValue("Icon", $"\"{exe}\",0");
            using var command = key.CreateSubKey("command");
            command.SetValue("", $"\"{exe}\"{profileArg} --code-dir=\"{token}\"");
        }
    }
}
