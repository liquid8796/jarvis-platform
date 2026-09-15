namespace JarvisCode.App.Services;

/// <summary>
/// The Desktop settings' "Denied apps" list, the reference's
/// <c>chicagoUserDeniedBundleIds</c>: any access request for one of these apps is
/// refused outright, and its windows are filtered from what computer use may
/// target (<c>getCaptureContext</c> in the reference's main process drops them
/// from <c>allowedApps</c>). Kept as a plain store over <see cref="UiSettings"/> so
/// the computer-use tools can read it without the settings page.
/// </summary>
public static class ComputerUseAppPolicy
{
    /// <summary>Whether an app is on the deny list, matched the way grants are (process name, case-insensitive, extension dropped).</summary>
    public static bool IsDenied(UiSettings settings, string appName)
    {
        var name = ComputerUseGrants.Normalize(appName);
        return settings.ComputerUseDeniedApps.Any(denied =>
            string.Equals(ComputerUseGrants.Normalize(denied), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds an app to the deny list; a duplicate is ignored.</summary>
    public static bool Deny(UiSettings settings, string appName)
    {
        if (IsDenied(settings, appName))
        {
            return false;
        }

        settings.ComputerUseDeniedApps.Add(appName);
        return true;
    }

    /// <summary>Removes an app from the deny list.</summary>
    public static bool Allow(UiSettings settings, string appName)
    {
        var name = ComputerUseGrants.Normalize(appName);
        return settings.ComputerUseDeniedApps.RemoveAll(denied =>
            string.Equals(ComputerUseGrants.Normalize(denied), name, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>
    /// The refusal request_access answers for a denied app — the settings page's
    /// own description of what the list does.
    /// </summary>
    public static string Refusal(string appName) =>
        $"Access to {appName} was denied: the user has put it on the denied-apps list in Settings › Desktop app › Computer use. Any request to access it is automatically rejected.";

    /// <summary>
    /// The candidates the "Add app" menu offers: every installed application not
    /// already denied, sorted by display name (the reference sorts its
    /// <c>listInstalledApps</c> result the same way).
    /// </summary>
    public static IReadOnlyList<string> Candidates(UiSettings settings, IEnumerable<string> installed) =>
        [.. installed
            .Where(app => !IsDenied(settings, app))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(app => app, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// What the "Add app" menu offers. The reference asks the OS for every
    /// installed bundle (<c>listInstalledApps</c>, a macOS bundle enumeration);
    /// Windows has no equivalent list of "apps", so this build offers the
    /// processes that currently own a window plus everything already granted —
    /// the same set the grant flow itself names an app from.
    /// </summary>
    public static IReadOnlyList<string> InstalledApps()
    {
        var names = new List<string>();
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                using (process)
                {
                    if (process.MainWindowHandle != IntPtr.Zero && process.ProcessName.Length > 0)
                    {
                        names.Add(process.ProcessName);
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // A process exiting mid-enumeration is not a reason to fail the menu.
        }

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)];
    }
}
