using Microsoft.Win32;

namespace JarvisCode.App.Services;

/// <summary>
/// Registers this profile as the handler for <c>jarvis://</c>, the way the
/// reference registers <c>claude://</c> with <c>setAsDefaultProtocolClient</c>.
/// Written under HKCU only, so it needs no elevation, and removed completely when
/// switched off — the same shape the Explorer verb already uses.
/// </summary>
public static class ProtocolRegistration
{
    private static string Key => $@"Software\Classes\{DeepLinks.Scheme}";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("URL Protocol") is not null;
    }

    /// <summary>
    /// Claims or releases the protocol. <paramref name="profile"/> rides the command
    /// so a link opened while a named profile owns the registration lands in that
    /// profile rather than the default one.
    /// </summary>
    public static void SetRegistered(bool registered, string? profile)
    {
        if (!registered)
        {
            Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var profileArg = profile is { Length: > 0 } ? $" --profile={profile}" : "";
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("", "URL:Jarvis Protocol");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue("", $"\"{exe}\",0");
        }

        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exe}\"{profileArg} \"%1\"");
    }
}

/// <summary>
/// Writes the model <see cref="JumpList"/> builds into the Windows jump list.
///
/// One piece of the reference is deliberately missing and declared in the parity
/// suite's surface manifest: it reads back the rows the user removed by hand
/// (<c>getJumpListSettings().removedItems</c>) and never re-adds them. WPF's
/// JumpList exposes only <c>RejectedItems</c> — what the shell refused on the last
/// Apply — and no wrapper for the shell's removed-destinations list, so this port
/// re-offers a removed row. <see cref="JumpList.Categories"/> still takes the set,
/// which is what keeps that rule ported and tested.
/// </summary>
public static class WindowsJumpList
{
    /// <summary>
    /// Applies the categories. Windows refuses custom categories outright when the
    /// user has turned off recent-items tracking, and the reference answers that by
    /// re-applying the Tasks section alone rather than giving up, so this does too.
    /// </summary>
    public static void Apply(IReadOnlyList<JumpListCategory> categories)
    {
        var list = Compose(categories);
        try
        {
            list.Apply();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            try
            {
                Compose([.. categories.Where(static c => c.Name is null)]).Apply();
            }
            catch (Exception retry) when (retry is InvalidOperationException
                or System.Runtime.InteropServices.COMException)
            {
                // The shell refused the list entirely; the app is otherwise fine.
            }
        }
    }

    private static System.Windows.Shell.JumpList Compose(IReadOnlyList<JumpListCategory> categories)
    {
        var exe = Environment.ProcessPath;
        var list = new System.Windows.Shell.JumpList
        {
            ShowRecentCategory = false,
            ShowFrequentCategory = false,
        };

        foreach (var category in categories)
        {
            foreach (var item in category.Items)
            {
                list.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = item.Label,
                    ApplicationPath = exe,
                    Arguments = item.Url,
                    IconResourcePath = exe,
                    IconResourceIndex = 0,
                    // A null CustomCategory is the OS's own Tasks section, which is
                    // where the reference pushes its two standing entries.
                    CustomCategory = category.Name,
                });
            }
        }

        return list;
    }
}
