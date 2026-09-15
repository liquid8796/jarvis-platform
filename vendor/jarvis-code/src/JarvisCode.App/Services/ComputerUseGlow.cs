using System;
using System.Drawing;
using System.Linq;
using System.Windows.Threading;
using JarvisCode.App.Views;

namespace JarvisCode.App.Services;

/// <summary>
/// Raises and lowers the on-screen indicator with the desktop lock.
///
/// The reference wires exactly this (its <c>nTn</c> in desktop 1.44121.2.0's
/// <c>index.chunk-BHbE7U4N.js</c>): it subscribes to <c>cuLockChanged</c>, and
/// when the lock has a holder it resolves that holder's display and shows the
/// glow, and when it does not it hides it. <see cref="DesktopLock"/> is this
/// app's own lock of the same kind, so the indicator is up for exactly as long
/// as a session is entitled to drive the desktop — not per action, which would
/// flicker, and not per turn, which would outlast the driving.
/// </summary>
internal static class ComputerUseGlow
{
    /// <summary>
    /// The pill's own sentence. The reference says "Claude is using your
    /// computer"; in front of the user this assistant is Jarvis.
    /// </summary>
    internal const string Label = "Jarvis is using your computer";

    /// <summary>
    /// Whether the indicator may appear in a screen capture. It is excluded from
    /// one in every ordinary run — the rim is for the person at the keyboard,
    /// and a screenshot carrying it would show the model its own marking — so
    /// the only way to photograph the pose is for the pose to say so.
    /// </summary>
    internal static bool Capturable { get; set; }

    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static ComputerUseGlowWindow? _window;
    private static DispatcherTimer? _hide;
    private static bool _installed;

    /// <summary>
    /// The handle of the live indicator, so the screenshot mask can skip it.
    /// Zero while nothing is shown.
    /// </summary>
    internal static IntPtr WindowHandle { get; private set; }

    /// <summary>Starts listening on the calling thread's dispatcher.</summary>
    internal static void Install(Dispatcher dispatcher)
    {
        lock (Gate)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            _dispatcher = dispatcher;
        }

        DesktopLock.HeldChanged += held => dispatcher.BeginInvoke(() => Apply(held));
    }

    /// <summary>Takes the indicator down and forgets it — for shutdown and tests.</summary>
    internal static void Reset()
    {
        _hide?.Stop();
        _hide = null;
        _window?.Close();
        _window = null;
        WindowHandle = IntPtr.Zero;
    }

    /// <summary>
    /// Which display the indicator covers: the one the last capture framed, so
    /// it marks the screen the session is actually driving. The reference
    /// resolves the holder's display the same way and falls back to the display
    /// its own window is on; here the fallback is the primary, which is what an
    /// unframed session captures.
    /// </summary>
    internal static Rectangle Display()
    {
        var frame = ComputerUseService.LastFrame;
        var displays = ComputerUseService.Displays();
        if (frame.IsEmpty)
        {
            return displays.FirstOrDefault(static d => d.IsPrimary)?.Bounds
                ?? System.Windows.Forms.SystemInformation.VirtualScreen;
        }

        // A capture of the whole virtual desktop is not one display; the
        // indicator then marks the desktop it covers.
        var centre = new Point(frame.X + (frame.Width / 2), frame.Y + (frame.Height / 2));
        return displays.FirstOrDefault(d => d.Bounds.Contains(centre))?.Bounds ?? frame;
    }

    private static void Apply(bool held)
    {
        if (held)
        {
            Show();
        }
        else
        {
            Hide();
        }
    }

    private static void Show()
    {
        _hide?.Stop();
        _hide = null;

        var first = _window is null;
        if (_window is null)
        {
            _window = new ComputerUseGlowWindow(Label);
            _window.Closed += (_, _) =>
            {
                _window = null;
                WindowHandle = IntPtr.Zero;
            };
            _window.Show();
            WindowHandle = _window.Handle;
        }

        _window.Cover(Display());
        _window.Show();

        // The badge flashes when the indicator comes up, not on every re-show
        // of a window that is already standing.
        _window.Reveal(first);
    }

    private static void Hide()
    {
        if (_window is null || _hide is not null)
        {
            return;
        }

        _window.Conceal();
        _hide = new DispatcherTimer(
            ComputerUseGlowWindow.HideDelay,
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _hide?.Stop();
                _hide = null;
                _window?.Hide();
            },
            _dispatcher ?? Dispatcher.CurrentDispatcher);
        _hide.Start();
    }
}
