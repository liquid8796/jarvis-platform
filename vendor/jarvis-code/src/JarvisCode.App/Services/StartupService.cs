using System.IO;
using Microsoft.Win32;

namespace JarvisCode.App.Services;

/// <summary>"Start with Windows" via HKCU Run — per profile, no admin needed.</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ValueName(ProfilePaths paths) => paths.InstanceKey;

    public static bool IsEnabled(ProfilePaths paths)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName(paths)) is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    public static void SetEnabled(ProfilePaths paths, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is null)
                {
                    return;
                }

                var arguments = "--tray" + (paths.ProfileName is { } profile ? $" --profile={profile}" : "");
                key.SetValue(ValueName(paths), $"\"{exe}\" {arguments}");
            }
            else
            {
                key.DeleteValue(ValueName(paths), throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Losing the toggle is preferable to crashing settings.
        }
    }
}
