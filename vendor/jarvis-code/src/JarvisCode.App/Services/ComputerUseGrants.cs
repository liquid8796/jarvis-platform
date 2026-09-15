using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>How much of the desktop a granted application exposes to the model.</summary>
public enum AppTier
{
    /// <summary>Visible in screenshots; no input at all.</summary>
    Read,

    /// <summary>Plain left-clicks only — no typing, keys, right-click, modifier-clicks or drag.</summary>
    Click,

    /// <summary>Everything.</summary>
    Full,
}

/// <summary>What an action needs from the app it lands in.</summary>
public enum InputNeed
{
    /// <summary>Pointer movement and position reads — allowed wherever a grant exists.</summary>
    Pointer,

    /// <summary>Scroll, plain left click, press/release — the "click" tier.</summary>
    Click,

    /// <summary>Right/middle/modified clicks and drags — full tier only.</summary>
    FullMouse,

    /// <summary>Typing and key chords — full tier only.</summary>
    Keyboard,
}

/// <summary>
/// The reference's per-application grant tiers, ported for Windows: browsers are
/// granted read-only, terminals/IDEs and the Windows shell get plain left-clicks
/// only, everything else is unrestricted. Enforced only while "Require app
/// grants" is on, which is also what turns screenshot masking on.
/// </summary>
public static class ComputerUseGrants
{
    // The reference's own win32 name sets (app.asar index.chunk-C5__TEgr.js).
    private static readonly HashSet<string> BrowserExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "arc",
        "duckduckgo", "zen", "librewolf", "waterfox", "mullvadbrowser", "floorp", "comet",
    };

    private static readonly HashSet<string> TerminalExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wt", "conemu", "conemu64", "cmder", "alacritty",
        "wezterm-gui", "warp", "hyper", "zed", "tabby", "termius", "code", "code - insiders",
        "cursor", "vscodium", "windsurf", "sublime_text", "devenv", "gitkraken", "idea64",
        "pycharm64", "webstorm64", "goland64", "clion64", "rider64", "rgui", "rterm", "r",
    };

    private static readonly HashSet<string> ShellExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "searchhost", "searchapp", "searchui", "startmenuexperiencehost",
        "shellexperiencehost", "taskmgr",
    };

    private static readonly HashSet<string> TradingExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "webull", "tradingview", "tws", "thinkorswim", "binance", "ledger live", "trezor suite",
    };

    /// <summary>
    /// Windows shortcuts the OS owns; the reference gates them behind the
    /// systemKeyCombos grant flag rather than letting a stray chord log the user out.
    /// </summary>
    private static readonly HashSet<string> SystemShortcuts = new(StringComparer.Ordinal)
    {
        "ctrl+alt+delete", "alt+f4", "alt+tab", "alt+shift+tab", "ctrl+alt+tab",
        "ctrl+alt+shift+tab", "meta+l", "meta+d", "meta+r", "meta+e", "meta+s", "meta+q",
        "ctrl+escape", "meta+i", "meta+u", "meta+x",
    };

    private static readonly Dictionary<string, string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["meta"] = "meta", ["super"] = "meta", ["command"] = "meta", ["cmd"] = "meta",
        ["windows"] = "meta", ["win"] = "meta",
        ["ctrl"] = "ctrl", ["control"] = "ctrl", ["lctrl"] = "ctrl", ["lcontrol"] = "ctrl",
        ["rctrl"] = "ctrl", ["rcontrol"] = "ctrl",
        ["shift"] = "shift", ["lshift"] = "shift", ["rshift"] = "shift",
        ["alt"] = "alt", ["option"] = "alt",
    };

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "escape", ["del"] = "delete", [" "] = "space", ["\t"] = "tab",
    };

    private static readonly string[] ModifierOrder = ["ctrl", "alt", "shift", "meta"];

    /// <summary>"browser", "terminal", "shell", "trading", or null for an ordinary app.</summary>
    public static string? Category(string processName)
    {
        var name = Normalize(processName);
        if (BrowserExes.Contains(name))
            return "browser";
        if (TerminalExes.Contains(name))
            return "terminal";
        if (TradingExes.Contains(name))
            return "trading";
        return ShellExes.Contains(name) ? "shell" : null;
    }

    /// <summary>The tier request_access offers for an app, before the user decides.</summary>
    public static AppTier ProposedTier(string processName) => Category(processName) switch
    {
        "browser" => AppTier.Read,
        // The reference has a trading category but does not spell out its tier; read-only
        // is the half of that question that cannot place an order by accident.
        "trading" => AppTier.Read,
        "terminal" or "shell" => AppTier.Click,
        _ => AppTier.Full,
    };

    /// <summary>The reference's explanation shown when a request lands on restricted apps.</summary>
    public static string? RestrictedTierNote(IEnumerable<string> processNames)
    {
        var categories = processNames.Select(Category).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (categories.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (categories.Contains("terminal"))
        {
            parts.Add("Terminals and IDEs can only be granted in 'click' mode — you can see and " +
                "left-click, but cannot type, press keys, or paste. To run shell commands use the PowerShell tool instead.");
        }

        if (categories.Contains("shell"))
        {
            parts.Add("The Windows shell (File Explorer, Start, Search, Task Manager) can only be granted " +
                "in 'click' mode — you can left-click to navigate, but cannot type into the address, " +
                "search, or Run boxes.");
        }

        if (categories.Contains("browser"))
        {
            parts.Add("Browsers can only be granted in 'read' mode — you can see what is on screen but " +
                "cannot interact. For navigation, clicking, or typing on the web use the browser tools instead.");
        }

        if (categories.Contains("trading"))
        {
            parts.Add("Trading and wallet applications can only be granted in 'read' mode — you can see " +
                "what is on screen but cannot interact. Ask the user to place any order themselves.");
        }

        if (parts.Count == 0)
        {
            return null;
        }

        parts.Add("If you still need this restricted access, proceed with request_access — the user approves once.");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The tier an app was granted at in this session, or null when it has none.
    ///
    /// Two sources are read, in the reference's own order of authority: the
    /// session's own grants (its <c>cuAllowedApps</c>, which is where
    /// request_access writes) and the standing allowlist the Settings page
    /// edits, which the reference has no equivalent of and which is therefore
    /// checked second.
    /// </summary>
    public static AppTier? GrantedTier(UiSettings settings, string? sessionId, string processName)
    {
        var name = Normalize(processName);
        if (ComputerUseSessionGrants.Peek(settings, sessionId) is { } session &&
            session.Apps.Any(g => Normalize(g).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return TierOf(session.Tiers, name);
        }

        if (!settings.ComputerUseGrantedApps.Any(g => Normalize(g).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return TierOf(settings.ComputerUseGrantedTiers, name);
    }

    private static AppTier TierOf(Dictionary<string, string> tiers, string name)
    {
        foreach (var (app, tier) in tiers)
        {
            if (Normalize(app).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return ParseTier(tier);
            }
        }

        // A grant stored before tiers existed is a full grant — the behaviour it was given under.
        return AppTier.Full;
    }

    public static AppTier ParseTier(string? tier) => tier?.ToLowerInvariant() switch
    {
        "read" => AppTier.Read,
        "click" => AppTier.Click,
        _ => AppTier.Full,
    };

    public static string TierName(AppTier tier) => tier switch
    {
        AppTier.Read => "read",
        AppTier.Click => "click",
        _ => "full",
    };

    /// <summary>Null when the action may run; otherwise the refusal the model reads.</summary>
    public static string? CheckForeground(UiSettings settings, string? sessionId, InputNeed need)
    {
        var app = ComputerUseExtras.ForegroundProcessName();
        if (app is null)
        {
            // An unreadable foreground window should not brick input entirely.
            return null;
        }

        return RefusalFor(settings, sessionId, app, need, "The frontmost application");
    }

    /// <summary>
    /// The reference resolves the window under the pointer before clicking: the
    /// frontmost app is not necessarily the one a coordinate lands in.
    /// </summary>
    public static string? CheckClickTarget(UiSettings settings, string? sessionId, Point screen, InputNeed need)
    {
        var app = ProcessNameAt(screen);
        if (app is null)
        {
            return "These coordinates are outside the visible screen, so the target cannot be verified. " +
                "Take a new screenshot and use coordinates inside it.";
        }

        if (ComputerUseAppPolicy.IsDenied(settings, app)) return ComputerUseAppPolicy.Refusal(app);
        if (!settings.RequireComputerUseGrants) return null;
        var tier = GrantedTier(settings, sessionId, app);
        if (tier is null)
        {
            return $"Click at these coordinates would land on \"{app}\", which is not in the allowed " +
                "applications. Take a fresh screenshot to see the current window layout.";
        }

        if (Allows(tier.Value, need))
        {
            return null;
        }

        return tier.Value == AppTier.Read
            ? $"Click at these coordinates would land on \"{app}\", which is granted at tier \"read\" " +
              "(screenshots only, no interaction)."
            : $"Click at these coordinates would land on \"{app}\", which is granted at tier \"click\" — " +
              "right-click, middle-click, and clicks with modifier keys require tier \"full\" (they can " +
              "Paste via the context menu or fire modifier-chord keystrokes). Plain left_click is allowed " +
              "here. Do not attempt to work around this restriction — never use shell commands, automation " +
              "scripts, or any other method to send clicks or keystrokes to this app.";
    }

    /// <summary>The tier check itself: null when <paramref name="app"/> may take this input.</summary>
    public static string? RefusalFor(
        UiSettings settings, string? sessionId, string app, InputNeed need, string subject = "The application")
    {
        if (ComputerUseAppPolicy.IsDenied(settings, app)) return ComputerUseAppPolicy.Refusal(app);
        if (!settings.RequireComputerUseGrants) return null;
        var tier = GrantedTier(settings, sessionId, app);
        if (tier is null)
        {
            return $"{subject} '{app}' has no input grant. Ask for it with request_access " +
                $"(apps: [\"{app}\"]), or the user can turn off \"Require app grants\" in Settings.";
        }

        if (Allows(tier.Value, need))
        {
            return null;
        }

        if (tier.Value == AppTier.Read)
        {
            return $"{subject} '{app}' is granted at tier \"read\" (visible in screenshots only; no clicks " +
                "or typing). Call request_access to ask the user to raise the tier, or take a screenshot " +
                "for read-only inspection.";
        }

        return Category(app) == "shell"
            ? $"{subject} '{app}' is the Windows desktop shell — granted at tier \"click\" (visible + plain " +
              "left-click only; NO typing, key presses, right-click, modifier-clicks, or drag-drop). You can " +
              "click to open folders and items, but typing is blocked: the address bar, Search box, and Run " +
              "dialog hand typed text to ShellExecute. For shell commands, use the PowerShell tool."
            : $"{subject} '{app}' is granted at tier \"click\" — plain left-clicks only; typing, key presses, " +
              "right-click, modifier-clicks and drag-drop require tier \"full\". Call request_access to ask " +
              "the user to raise the tier. For shell commands, use the PowerShell tool.";
    }

    private static bool Allows(AppTier tier, InputNeed need) => need switch
    {
        InputNeed.Pointer => true,
        InputNeed.Click => tier is AppTier.Click or AppTier.Full,
        _ => tier == AppTier.Full,
    };

    /// <summary>A chord the OS owns, in the reference's canonical form (ctrl+alt+shift+meta order).</summary>
    public static bool IsSystemShortcut(string chord)
    {
        var modifiers = new List<string>();
        var keys = new List<string>();
        foreach (var raw in chord.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                // "ctrl++" means the plus key itself, which is not a modifier.
                if (raw.Length == 1)
                {
                    keys.Add(raw);
                }

                continue;
            }

            if (Modifiers.TryGetValue(part, out var modifier))
            {
                modifiers.Add(modifier);
            }
            else
            {
                keys.Add(KeyAliases.TryGetValue(part, out var alias) ? alias : part.ToLowerInvariant());
            }
        }

        var ordered = modifiers.Distinct().OrderBy(m => Array.IndexOf(ModifierOrder, m));
        return SystemShortcuts.Contains(string.Join('+', ordered.Concat(keys)));
    }

    /// <summary>Null when the clipboard tool may run; otherwise the missing-flag refusal.</summary>
    public static string? ClipboardRefusal(UiSettings settings, string? sessionId, bool read)
    {
        if (!settings.RequireComputerUseGrants)
        {
            return null;
        }

        var session = ComputerUseSessionGrants.Peek(settings, sessionId);
        var granted = read
            ? settings.ComputerUseClipboardRead || session?.ClipboardRead == true
            : settings.ComputerUseClipboardWrite || session?.ClipboardWrite == true;
        if (granted)
        {
            return null;
        }

        var flag = read ? "clipboardRead" : "clipboardWrite";
        return $"The `{flag}` grant is required for this. Request it via request_access " +
            $"({flag}: true), or the user can turn off \"Require app grants\" in Settings.";
    }

    /// <summary>
    /// The apps this session may drive, with their tiers, in the order they were
    /// granted: the session's own first, then the standing allowlist.
    /// </summary>
    public static IReadOnlyList<(string App, AppTier Tier)> Granted(UiSettings settings, string? sessionId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(string App, AppTier Tier)>();
        var session = ComputerUseSessionGrants.Peek(settings, sessionId);
        foreach (var app in (session?.Apps ?? []).Concat(settings.ComputerUseGrantedApps))
        {
            if (seen.Add(Normalize(app)))
            {
                rows.Add((app, GrantedTier(settings, sessionId, app) ?? AppTier.Full));
            }
        }

        return rows;
    }

    // ---- screenshot masking ----

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private const uint GaRoot = 2;
    private const int DwmwaCloaked = 14;

    /// <summary>The process owning the top-level window under a screen point.</summary>
    public static string? ProcessNameAt(Point screen)
    {
        var handle = WindowFromPoint(screen);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var root = GetAncestor(handle, GaRoot);
        return ProcessNameOf(root == IntPtr.Zero ? handle : root);
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

    /// <summary>
    /// Windows has no compositor-level exclusion, so the reference masks instead:
    /// every visible window belonging to an ungranted application is painted over
    /// with a solid rectangle, and granted windows above it are restored from the
    /// untouched capture so the z-order still reads correctly.
    /// </summary>
    public static void MaskUngranted(Bitmap capture, Rectangle region, UiSettings settings, string? sessionId)
    {
        if (!settings.RequireComputerUseGrants && settings.ComputerUseDeniedApps.Count == 0) return;

        var windows = TopLevelWindows();
        if (windows.Count == 0)
        {
            return;
        }

        using var original = (Bitmap)capture.Clone();
        using var graphics = Graphics.FromImage(capture);
        // Mid-grey rather than black: the reference wants the rectangle's position
        // to stay readable, and black disappears into a dark-themed desktop.
        using var mask = new SolidBrush(Color.FromArgb(60, 60, 66));

        // Bottom of the z-order first: a granted window drawn later re-covers the
        // part of a mask it actually sits on top of.
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var (handle, bounds) = windows[i];
            bounds.Intersect(region);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            var target = new Rectangle(bounds.X - region.X, bounds.Y - region.Y, bounds.Width, bounds.Height);
            var app = ProcessNameOf(handle);
            if (app is not null && (ComputerUseAppPolicy.IsDenied(settings, app) ||
                (settings.RequireComputerUseGrants && GrantedTier(settings, sessionId, app) is null)))
            {
                graphics.FillRectangle(mask, target);
            }
            else
            {
                graphics.DrawImage(original, target, target, GraphicsUnit.Pixel);
            }
        }
    }

    /// <summary>
    /// The granted apps that are actually running — the set the reference resolves
    /// its capture display against, rather than every app the user ever approved.
    /// </summary>
    public static IReadOnlyCollection<string> RunningGrantedApps(UiSettings settings, string? sessionId)
    {
        // The reference resolves from the apps a session was granted, and a
        // session always has grants there. Here they are only in force behind the
        // switch, so a list left over from when it was on steers nothing.
        if (!settings.RequireComputerUseGrants)
        {
            return [];
        }

        var granted = Granted(settings, sessionId).Select(g => Normalize(g.App)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (granted.Count == 0)
        {
            return [];
        }

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (granted.Contains(process.ProcessName))
                {
                    running.Add(process.ProcessName);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the listing and the read; it is not running.
            }
            finally
            {
                process.Dispose();
            }
        }

        return running;
    }

    /// <summary>
    /// The display the granted apps are on, or null when none of them has a
    /// window anywhere. With windows across several displays the one holding the
    /// most of them wins, and the primary breaks a tie — the reference hands this
    /// to a native resolver, so the tie-break is ours rather than measured.
    /// </summary>
    public static int? DisplayForApps(IReadOnlyCollection<string> processNames)
    {
        if (processNames.Count == 0)
        {
            return null;
        }

        var wanted = new HashSet<string>(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var displays = ComputerUseService.Displays();
        var counts = new Dictionary<int, int>();
        foreach (var (handle, bounds) in TopLevelWindows())
        {
            if (ProcessNameOf(handle) is not { } name || !wanted.Contains(Normalize(name)))
            {
                continue;
            }

            // A window is on the display its centre falls in, which is what makes
            // one straddling two of them count once.
            var centre = new System.Drawing.Point(
                bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
            if (displays.FirstOrDefault(d => d.Bounds.Contains(centre)) is { } display)
            {
                counts[display.Number] = counts.GetValueOrDefault(display.Number) + 1;
            }
        }

        return PickDisplay(counts, displays.FirstOrDefault(d => d.IsPrimary)?.Number);
    }

    /// <summary>
    /// The display with the most granted windows; the primary wins a tie, and
    /// failing that the lowest-numbered display, so the choice is never arbitrary.
    /// </summary>
    public static int? PickDisplay(IReadOnlyDictionary<int, int> windowsPerDisplay, int? primary)
    {
        if (windowsPerDisplay.Count == 0)
        {
            return null;
        }

        var most = windowsPerDisplay.Values.Max();
        var tied = windowsPerDisplay.Where(e => e.Value == most).Select(e => e.Key).ToList();
        return tied.Count == 1 ? tied[0]
            : primary is { } p && tied.Contains(p) ? p
            : tied.Min();
    }

    /// <summary>Visible, uncloaked top-level windows, topmost first.</summary>
    private static List<(IntPtr Handle, Rectangle Bounds)> TopLevelWindows()
    {
        var windows = new List<(IntPtr, Rectangle)>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle))
            {
                return true;
            }

            // UWP keeps invisible "cloaked" host windows that cover the whole screen;
            // masking those would black out the entire capture.
            if (DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if (!GetWindowRect(handle, out var rect))
            {
                return true;
            }

            var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (bounds.Width <= 0 || bounds.Height <= 0 || IsDesktopShell(handle))
            {
                return true;
            }

            // The on-screen indicator covers a whole display and belongs to this
            // app, which is never granted: masked by bounds it would grey out
            // every screenshot taken while it was up. Its pixels are excluded
            // from capture as well, but the mask paints by rectangle.
            if (handle == ComputerUseGlow.WindowHandle)
            {
                return true;
            }

            windows.Add((handle, bounds));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>
    /// The wallpaper host and the shell's full-screen defview cover everything;
    /// treating them as ordinary windows would mask the whole desktop.
    /// </summary>
    private static bool IsDesktopShell(IntPtr handle)
    {
        var name = new StringBuilder(64);
        if (GetClassName(handle, name, name.Capacity) == 0)
        {
            return false;
        }

        var className = name.ToString();
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow";
    }

    /// <summary>Grants are keyed by process name; the model may send "chrome.exe" or a path.</summary>
    public static string Normalize(string name)
    {
        var trimmed = name.Trim().Trim('"');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            trimmed = trimmed[(slash + 1)..];
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
