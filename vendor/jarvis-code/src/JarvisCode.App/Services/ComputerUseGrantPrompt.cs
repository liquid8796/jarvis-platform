using System;
using System.Collections.Generic;
using System.Linq;

namespace JarvisCode.App.Services;

/// <summary>One application the card is asking about.</summary>
/// <param name="App">The process name the grant will be filed under.</param>
/// <param name="Installed">Whether anything resolved the name; the reference dims an unresolved row.</param>
/// <param name="AlreadyGranted">Whether this session already holds it.</param>
internal sealed record GrantRow(string App, bool Installed, bool AlreadyGranted, AppTier Tier);

/// <summary>What request_access is asking the user for.</summary>
internal sealed record GrantRequest(
    string Reason,
    IReadOnlyList<GrantRow> Apps,
    bool ClipboardRead,
    bool ClipboardWrite,
    bool SystemKeyCombos,
    IReadOnlyList<string> WillHide,
    bool AutoUnhide)
{
    public bool HasFlags => ClipboardRead || ClipboardWrite || SystemKeyCombos;
}

/// <summary>A title in the reference's three pieces: it bolds the verb.</summary>
internal sealed record GrantTitle(string Before, string Bold, string After);

/// <summary>
/// The wording and the classification behind the computer-use grant card, as the
/// Code surface's own card draws it — measured in desktop 1.44121.2.0's
/// <c>cd5a31703-DPCARDPv.js</c> (its title builder <c>NO</c>, row <c>PO</c>,
/// tier labels <c>OO</c>, sentinel labels <c>DO</c>) over the sentinel
/// classification in <c>c0b9fcb09-DqtRhoSg.js</c> (its <c>ue</c>).
///
/// The chat surface draws a different card from the same request; this is the
/// one a Code session raises.
/// </summary>
internal static class ComputerUseGrantPrompt
{
    /// <summary>The reference's win32 terminal-and-IDE set, which it warns about by name.</summary>
    private static readonly IReadOnlySet<string> ShellExecutables = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wt.exe", "windowsterminal.exe", "code.exe",
        "cursor.exe", "vscodium.exe", "windsurf.exe", "zed.exe", "alacritty.exe", "wezterm-gui.exe",
        "warp.exe", "hyper.exe", "tabby.exe", "idea64.exe", "pycharm64.exe", "conemu.exe", "conemu64.exe",
    };

    private static readonly string[] ShellPackages =
        ["Microsoft.WindowsTerminal_", "Microsoft.WindowsTerminalPreview_", "Microsoft.PowerShell_"];

    private static readonly string[] SettingsPackages = ["windows.immersivecontrolpanel_"];

    /// <summary>
    /// The warning a row carries, or null. The reference names three kinds and
    /// draws each in its warning colour under the application's name.
    /// </summary>
    internal static string? SentinelWarning(string app)
    {
        var name = ComputerUseGrants.Normalize(app);
        var executable = name + ".exe";
        if (ShellExecutables.Contains(executable)
            || ShellPackages.Any(prefix => app.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return CanRunCommands;
        }

        if (executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return CanAccessFiles;
        }

        if (executable.Equals("systemsettings.exe", StringComparison.OrdinalIgnoreCase)
            || SettingsPackages.Any(prefix => app.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return CanChangeSettings;
        }

        return null;
    }

    internal const string CanRunCommands = "Can run commands on your computer";
    internal const string CanAccessFiles = "Can access all your files";
    internal const string CanChangeSettings = "Can change system settings";

    internal const string ViewOnly = "View only";
    internal const string ClickOnly = "Click only";
    internal const string All = "All";
    internal const string NotInstalled = "(not installed)";

    internal const string ReadClipboard = "Read your clipboard";
    internal const string WriteClipboard = "Write to your clipboard";
    internal const string SystemShortcuts = "Use system shortcuts (Alt+F4, Alt+Tab, and similar)";

    internal const string ReasonLabel = "Reason";
    internal const string Deny = "Deny";
    internal const string AllowForThisSession = "Allow for this session";

    /// <summary>The two sentences the reference picks between when it will hide windows.</summary>
    internal const string HiddenThenRestored =
        "Your other windows will be hidden, then restored when Jarvis is done.";

    internal const string HiddenWhileWorking = "Your other windows will be hidden while Jarvis works.";

    internal static string TierLabel(AppTier tier) => tier switch
    {
        AppTier.Read => ViewOnly,
        AppTier.Click => ClickOnly,
        _ => All,
    };

    /// <summary>
    /// The reference's four titles, in its order: one app, several apps, apps
    /// with flags (or flags alone), and nothing at all.
    /// </summary>
    internal static GrantTitle Title(GrantRequest request)
    {
        if (request.Apps.Count == 1 && !request.HasFlags)
        {
            return new GrantTitle("Allow Jarvis to ", "use", $" {request.Apps[0].App}?");
        }

        if (request.Apps.Count > 1 && !request.HasFlags)
        {
            return new GrantTitle("Allow Jarvis to ", "use", $" {request.Apps.Count} apps?");
        }

        return request.Apps.Count > 0 || request.HasFlags
            ? new GrantTitle("Allow Jarvis to ", "use", " these capabilities?")
            : new GrantTitle("Allow Jarvis to ", "use your computer", "?");
    }

    /// <summary>The sentence under the list, or null when nothing will be hidden.</summary>
    internal static string? HideNotice(GrantRequest request) => request.WillHide.Count == 0
        ? null
        : request.AutoUnhide ? HiddenThenRestored : HiddenWhileWorking;

    /// <summary>The flag rows, in the reference's own order.</summary>
    internal static IReadOnlyList<string> FlagRows(GrantRequest request)
    {
        var rows = new List<string>();
        if (request.ClipboardRead)
        {
            rows.Add(ReadClipboard);
        }

        if (request.ClipboardWrite)
        {
            rows.Add(WriteClipboard);
        }

        if (request.SystemKeyCombos)
        {
            rows.Add(SystemShortcuts);
        }

        return rows;
    }
}
