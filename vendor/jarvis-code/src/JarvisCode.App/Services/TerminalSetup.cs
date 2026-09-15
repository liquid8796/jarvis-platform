using System.IO;
using Microsoft.Win32;

namespace JarvisCode.App.Services;

/// <summary>
/// /terminal-setup, the desktop analog: makes the app launchable from any
/// terminal by dropping a `jarvis-code.cmd` shim (which opens a Code session
/// in the terminal's current directory via --code-dir) into a profile bin
/// folder and putting that folder on the user PATH. Also turns on the
/// Explorer context-menu entry, so both entry points are covered at once.
/// </summary>
public static class TerminalSetup
{
    public const string ShimName = "jarvis-code.cmd";

    /// <summary>
    /// Writes the launcher shim and returns its path. Split out from
    /// <see cref="Run"/> so it can be tested without touching the registry.
    /// </summary>
    internal static string WriteShim(string binDirectory, string exePath, string? profileName)
    {
        Directory.CreateDirectory(binDirectory);
        var shim = Path.Combine(binDirectory, ShimName);
        var profileArg = profileName is { Length: > 0 } ? $" --profile={profileName}" : "";
        File.WriteAllText(shim,
            "@echo off\r\n" +
            $"start \"\" \"{exePath}\"{profileArg} \"--code-dir=%CD%\"\r\n");
        return shim;
    }

    /// <summary>Runs the setup; returns a human summary of what changed.</summary>
    public static string Run(string profileRoot, string? profileName)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return "Could not determine the app's executable path.";

        var binDirectory = Path.Combine(profileRoot, "bin");
        var shim = WriteShim(binDirectory, exe, profileName);
        bool pathAdded = AddToUserPath(binDirectory);
        ExplorerContextMenu.SetEnabled(true, profileName);

        return
            $"Terminal setup complete:\n" +
            $"  ✓ `jarvis-code` shim written to {shim}\n" +
            $"  {(pathAdded ? "✓ Added" : "✓ Already on")} the user PATH — new terminals can run `jarvis-code` " +
            "in any directory to open a Code session there\n" +
            "  ✓ Explorer context menu \"New Jarvis Code Session Here\" enabled\n" +
            "Open a new terminal window for the PATH change to take effect.";
    }

    /// <summary>Appends the directory to HKCU PATH; false when it is already there.</summary>
    internal static bool AddToUserPath(string directory)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        if (key is null)
            return false;
        var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        if (current.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => entry.Equals(directory, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var kind = key.GetValueKind("Path") == RegistryValueKind.String
            ? RegistryValueKind.String
            : RegistryValueKind.ExpandString;
        key.SetValue("Path", current.Length == 0 ? directory : current.TrimEnd(';') + ";" + directory, kind);
        return true;
    }
}
