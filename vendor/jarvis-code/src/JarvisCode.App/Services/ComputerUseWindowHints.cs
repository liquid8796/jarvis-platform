using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>One open window the user pointed at from the composer.</summary>
/// <param name="App">The process name, which is what request_access takes.</param>
/// <param name="Title">The window's own title, as the user sees it in the switcher.</param>
/// <param name="WindowId">The window handle, which the hint quotes.</param>
internal sealed record WindowMention(string App, string Title, long WindowId);

/// <summary>
/// The <c>&lt;cu_window_hints&gt;</c> block: what the reference puts on a message
/// when the user pointed at a window rather than describing it.
///
/// Measured in desktop 1.44121.2.0 (<c>index.chunk-ArHpFRaV.js</c>: its <c>L</c>
/// builds the block, <c>noteCuWindowMentions</c> records the pick on the session
/// and <c>appendCuWindowHint</c> appends it to one message and clears it). It is
/// not a grant — the model still calls request_access — it is the answer to the
/// question that made the grant fail in the first place: which exact name to ask
/// for. Its own not-installed guidance points the user here, saying to "type @
/// followed by the app name in the prompt to target it directly — that path does
/// not depend on the index".
/// </summary>
internal static class ComputerUseWindowHints
{
    /// <summary>The reference truncates a window title at 120 characters.</summary>
    internal const int MaxTitle = 120;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<WindowMention>> Pointed = new(StringComparer.Ordinal);

    /// <summary>Remembers a window the user pointed at, for the next message.</summary>
    internal static void Point(string? sessionId, WindowMention window)
    {
        lock (Gate)
        {
            var key = sessionId ?? "";
            if (!Pointed.TryGetValue(key, out var list))
            {
                Pointed[key] = list = [];
            }

            if (!list.Any(existing => existing.WindowId == window.WindowId))
            {
                list.Add(window);
            }
        }
    }

    /// <summary>
    /// The block for what was pointed at, and forgets it. The reference appends
    /// it to one message and clears the list, so a second message does not carry
    /// a hint the user gave once.
    /// </summary>
    internal static string? Drain(string? sessionId)
    {
        List<WindowMention> windows;
        lock (Gate)
        {
            var key = sessionId ?? "";
            if (!Pointed.Remove(key, out var found) || found.Count == 0)
            {
                return null;
            }

            windows = found;
        }

        return Render(windows);
    }

    /// <summary>The reference's <c>L</c>, verbatim.</summary>
    internal static string? Render(IReadOnlyList<WindowMention> windows)
    {
        if (windows.Count == 0)
        {
            return null;
        }

        var listed = string.Join(", ", windows.Select(static window =>
        {
            var title = window.Title.Length > MaxTitle ? window.Title[..MaxTitle] : window.Title;
            if (title.Length == 0)
            {
                title = "(untitled)";
            }

            return $"window \"{title}\" (already open — pass \"{window.App}\" to request_access; if " +
                $"app_screenshot is available, call it with app: \"{window.App}\" and window_id: " +
                $"{window.WindowId} to see just this window in the background; otherwise take a display " +
                "screenshot)";
        }));

        return $"\n\n<cu_window_hints>The user is pointing at: {listed}. " +
            "Do not open_application for it.</cu_window_hints>";
    }

    /// <summary>
    /// The windows the user could point at: visible, titled, top-level, and not
    /// this application's own.
    /// </summary>
    internal static IReadOnlyList<WindowMention> Windows()
    {
        using var self = Process.GetCurrentProcess();
        var selfId = self.Id;
        var found = new List<WindowMention>();
        EnumWindows((handle, _unused) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            if (DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var length = GetWindowTextLength(handle);
            if (length == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0 || pid == (uint)selfId)
            {
                return true;
            }

            string app;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                app = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                return true;
            }

            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            found.Add(new WindowMention(app, title.ToString(), handle.ToInt64()));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private const uint GwOwner = 4;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}
