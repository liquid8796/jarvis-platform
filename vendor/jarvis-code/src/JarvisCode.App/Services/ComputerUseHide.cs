using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// Gets the ungranted windows out of the way before the session acts, and puts
/// them back when the turn ends.
///
/// The reference's win32 <c>prepareForAction</c> (desktop 1.44121.2.0,
/// <c>index.chunk-BHbE7U4N.js</c>): with the <c>hideBeforeAction</c> sub-gate on
/// — which its own defaults table <c>Rln</c> ships true — it lists the running
/// applications, hides every one that is neither granted, nor the Windows shell,
/// nor a system process, then re-reads the frontmost application up to five
/// times and hides that too until an allowed one is in front, and finally blurs
/// its own focused window. What it hid is remembered process-wide, reported to
/// the model on the next screenshot, and put back at turn end when
/// <c>chicagoAutoUnhide</c> is on (its <c>onResult</c>: "auto-unhide at turn
/// end"). It runs once before a batch and once before a screenshot, never per
/// action — the batch turns the flag off for the actions inside it.
///
/// One thing differs and is declared: the reference's native helper hides a
/// window outright, and this port minimizes it instead. A hidden window is
/// recoverable only by the process that hid it, so a crash mid-turn would leave
/// the user's windows gone with nothing on screen to click; a minimized one is
/// already on the taskbar.
/// </summary>
internal static class ComputerUseHide
{
    /// <summary>
    /// The reference's own never-hide set: a Windows system process, matched by
    /// executable name and only where it really lives under the Windows
    /// directory (its <c>BX</c> over <c>Htr</c>).
    /// </summary>
    internal static readonly IReadOnlySet<string> SystemProcesses = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "winlogon", "csrss", "smss", "wininit", "services", "lsass", "svchost", "spoolsv",
        "taskhost", "taskhostw", "conhost", "audiodg", "fontdrvhost", "sihost", "runtimebroker",
        "searchui", "searchapp", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "applicationframehost",
    };

    /// <summary>The shell itself, which the reference never hides either (its <c>VX</c>).</summary>
    internal const string Shell = "explorer";

    /// <summary>The reference's own frontmost re-read budget.</summary>
    private const int FrontmostPasses = 5;

    private static readonly object Gate = new();
    private static readonly List<IntPtr> Hidden = [];
    private static readonly List<string> HiddenApps = [];
    private static readonly List<string> Pending = [];

    /// <summary>
    /// Whether a name may be hidden at all: not granted to this session, not the
    /// shell, not a Windows system process, and not this application.
    /// </summary>
    internal static bool MayHide(UiSettings settings, string? sessionId, string app, string self)
    {
        var name = ComputerUseGrants.Normalize(app);
        return !name.Equals(self, StringComparison.OrdinalIgnoreCase)
            && !name.Equals(Shell, StringComparison.OrdinalIgnoreCase)
            && !SystemProcesses.Contains(name)
            && ComputerUseGrants.GrantedTier(settings, sessionId, name) is null;
    }

    /// <summary>
    /// What a hide would take off the screen, without taking it — the
    /// reference's <c>previewHideSet</c>, which its grant dialog lists.
    /// </summary>
    internal static IReadOnlyList<string> PreviewHideSet(UiSettings settings, string? sessionId)
    {
        if (!settings.RequireComputerUseGrants)
        {
            return [];
        }

        var self = SelfName();
        return
        [
            .. WindowsByApp()
                .Select(static entry => entry.Key)
                .Where(app => MayHide(settings, sessionId, app, self))
                .OrderBy(static app => app, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Hides what this session may not drive and remembers it for the turn.
    /// Answers the applications it hid, which the next screenshot reports.
    /// </summary>
    internal static IReadOnlyList<string> HideUngranted(UiSettings settings, string? sessionId)
    {
        if (!settings.RequireComputerUseGrants || !settings.ComputerUseHideBeforeAction)
        {
            return [];
        }

        var self = SelfName();
        var hidden = new List<string>();
        foreach (var (app, windows) in WindowsByApp())
        {
            if (!MayHide(settings, sessionId, app, self))
            {
                continue;
            }

            if (Minimize(windows))
            {
                hidden.Add(app);
            }
        }

        // The reference then re-reads the frontmost application and hides that
        // too, up to five times, because hiding a window promotes whatever was
        // behind it.
        for (var pass = 0; pass < FrontmostPasses; pass++)
        {
            if (ComputerUseExtras.ForegroundProcessName() is not { } front
                || !MayHide(settings, sessionId, front, self))
            {
                break;
            }

            if (!Minimize(WindowsOf(front)))
            {
                break;
            }

            if (!hidden.Contains(front, StringComparer.OrdinalIgnoreCase))
            {
                hidden.Add(front);
            }
        }

        if (hidden.Count > 0)
        {
            lock (Gate)
            {
                foreach (var app in hidden.Where(app =>
                    !HiddenApps.Contains(app, StringComparer.OrdinalIgnoreCase)))
                {
                    HiddenApps.Add(app);
                }

                foreach (var app in hidden.Where(app =>
                    !Pending.Contains(app, StringComparer.OrdinalIgnoreCase)))
                {
                    Pending.Add(app);
                }
            }
        }

        return hidden;
    }

    /// <summary>
    /// The applications hidden since the last screenshot, and forgets them —
    /// the reference's <c>getHiddenPendingNote</c> / <c>drainHiddenPendingNote</c>.
    /// </summary>
    internal static IReadOnlyList<string> DrainPending()
    {
        lock (Gate)
        {
            var pending = Pending.ToList();
            Pending.Clear();
            return pending;
        }
    }

    /// <summary>
    /// Puts back everything hidden this turn. The reference does this at turn
    /// end behind <c>chicagoAutoUnhide</c>, and clears what it hid either way —
    /// a window it decided not to restore is the user's to restore.
    /// </summary>
    internal static void UnhideAll(bool restore)
    {
        List<IntPtr> windows;
        lock (Gate)
        {
            windows = Hidden.ToList();
            Hidden.Clear();
            HiddenApps.Clear();
            Pending.Clear();
        }

        if (!restore)
        {
            return;
        }

        foreach (var window in windows)
        {
            if (IsWindow(window))
            {
                _ = ShowWindow(window, SwRestore);
            }
        }
    }

    /// <summary>
    /// The note the reference puts above the next screenshot, in its two
    /// branches: the applications it could name, and the processes it could not.
    /// </summary>
    internal static string? HiddenNote(IReadOnlyList<string> hidden, IReadOnlyList<string> installed)
    {
        if (hidden.Count == 0)
        {
            return null;
        }

        var known = hidden
            .Where(app => installed.Any(name =>
                ComputerUseGrants.Normalize(name).Equals(app, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var unknown = hidden.Except(known, StringComparer.OrdinalIgnoreCase).ToList();

        var parts = new List<string>();
        if (known.Count > 0)
        {
            var names = string.Join(", ", known.Select(static app => $"\"{app}\""));
            var one = known.Count == 1;
            parts.Add($"{names} {(one ? "was" : "were")} open and got hidden before this screenshot (not in " +
                $"the session allowlist). If a previous action was meant to open {(one ? "it" : "one of them")}, " +
                $"that's why you don't see it — call request_access to add {(one ? "it" : "them")}.");
        }

        if (unknown.Count > 0)
        {
            var names = string.Join(", ", unknown.Select(static app => $"\"{app}\""));
            var one = unknown.Count == 1;
            var also = known.Count > 0 ? "also " : "";
            parts.Add($"{names} {(one ? "was" : "were")} {also}hidden. " +
                $"{(one ? "This process owns" : "These processes own")} the visible " +
                $"{(one ? "window" : "windows")} but {(one ? "isn't" : "aren't")} in the installed-apps list — " +
                "likely a worker process spawned by a launcher you already granted (e.g. LibreOffice's " +
                "simpress.exe launches soffice.bin, which owns the actual window). Pass the exact " +
                $"{(one ? "basename" : "basenames")} above to request_access.");
        }

        return string.Join(" ", parts);
    }

    private static string SelfName()
    {
        using var self = Process.GetCurrentProcess();
        return self.ProcessName;
    }

    private static bool Minimize(IReadOnlyList<IntPtr> windows)
    {
        var any = false;
        foreach (var window in windows)
        {
            if (IsIconic(window) || !IsWindowVisible(window))
            {
                continue;
            }

            if (ShowWindow(window, SwMinimize))
            {
                lock (Gate)
                {
                    Hidden.Add(window);
                }

                any = true;
            }
        }

        return any;
    }

    private static IReadOnlyList<IntPtr> WindowsOf(string app) =>
        WindowsByApp().TryGetValue(ComputerUseGrants.Normalize(app), out var windows) ? windows : [];

    /// <summary>Top-level, visible, un-cloaked windows, grouped by process name.</summary>
    private static Dictionary<string, List<IntPtr>> WindowsByApp()
    {
        var byApp = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            if (DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if (!GetWindowRect(handle, out var rect) || rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
            {
                return true;
            }

            // A window with no title is a helper, not something the user sees as
            // an application — the reference lists applications, not windows.
            if (GetWindowTextLength(handle) == 0)
            {
                return true;
            }

            if (ProcessNameOf(handle) is not { } app)
            {
                return true;
            }

            if (!byApp.TryGetValue(app, out var windows))
            {
                byApp[app] = windows = [];
            }

            windows.Add(handle);
            return true;
        }, IntPtr.Zero);
        return byApp;
    }

    private static string? ProcessNameOf(IntPtr window)
    {
        try
        {
            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint GwOwner = 4;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}
