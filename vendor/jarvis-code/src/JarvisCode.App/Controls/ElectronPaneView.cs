using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;

namespace JarvisCode.App.Controls;

/// <summary>
/// The WPF element the Browser pane's Electron window lives inside.
///
/// <see cref="HwndHost"/> has to hand WPF a window handle synchronously, and
/// the engine's window arrives over a pipe some time later, so this hosts a
/// plain Win32 container and reparents the engine's window into it once it
/// exists (SetParent plus WS_CHILD, which is what makes an Electron window a
/// child of a WPF one at all). The container is also what keeps resizing
/// cheap: WPF sizes the container, and the child is stretched to fill it
/// without the engine being asked anything.
/// </summary>
public sealed class ElectronPaneView : HwndHost
{
    private const int WsChild = 0x4000_0000;
    private const int WsVisible = 0x1000_0000;
    private const int WsClipChildren = 0x0200_0000;
    private const int WsPopup = unchecked((int)0x8000_0000);
    private const int WsCaption = 0x00C0_0000;
    private const int WsThickFrame = 0x0004_0000;
    private const int GwlStyle = -16;
    private const int SwShowNoActivate = 4;

    /// <summary>The widest border this correction will believe, in device pixels.</summary>
    private const int MaxFrameInset = 32;

    private IntPtr _container;
    private IntPtr _child;
    private System.Windows.Rect? _lastClip;

    public ElectronPaneView()
    {
        // OnRenderSizeChanged alone is not enough: the pane's host starts
        // collapsed and is only shown once a tab has an address, and a
        // visibility change lays the element out without changing its render
        // size. A window left at 0x0 is not merely invisible - Chromium stops
        // producing frames for it, so Page.captureScreenshot never answers and
        // synthetic clicks land nowhere.
        SizeChanged += (_, _) => Resize();
        IsVisibleChanged += (_, _) => Resize();
        Loaded += (_, _) => Resize();
        // HwndHost is an airspace island: WPF's ScrollViewer clip does not
        // reach its native child automatically. Apply the ancestor viewport to
        // the container HWND on layout/scroll instead of letting inline web
        // content paint over the composer or adjacent transcript rows.
        LayoutUpdated += (_, _) => UpdateViewportClip();
    }

    /// <summary>The engine window currently reparented here, or zero.</summary>
    public IntPtr HostedWindow => _child;
    internal IntPtr ContainerWindow => _container;

    internal System.Windows.Rect ViewportClip()
    {
        var visible = new System.Windows.Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        DependencyObject? ancestor = VisualTreeHelper.GetParent(this);
        while (ancestor is not null)
        {
            if (ancestor is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter or ScrollViewer))
            {
                var bounds = element.TransformToDescendant(this).TransformBounds(new System.Windows.Rect(element.RenderSize));
                visible.Intersect(bounds);
                if (visible.IsEmpty) return new System.Windows.Rect(0, 0, 0, 0);
            }
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }
        return visible;
    }

    private void UpdateViewportClip()
    {
        if (_container == IntPtr.Zero) return;
        System.Windows.Rect visible;
        try { visible = ViewportClip(); }
        catch (InvalidOperationException) { return; } // the view is moving between trees
        var scale = VisualTreeHelper.GetDpi(this);
        var pixels = new System.Windows.Rect(
            Math.Floor(visible.X * scale.DpiScaleX), Math.Floor(visible.Y * scale.DpiScaleY),
            Math.Ceiling(visible.Right * scale.DpiScaleX) - Math.Floor(visible.X * scale.DpiScaleX),
            Math.Ceiling(visible.Bottom * scale.DpiScaleY) - Math.Floor(visible.Y * scale.DpiScaleY));
        if (_lastClip == pixels) return;
        var region = CreateRectRgn((int)pixels.Left, (int)pixels.Top, (int)pixels.Right, (int)pixels.Bottom);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(_container, region, true) == 0) DeleteObject(region);
        else _lastClip = pixels; // Windows owns the region after a successful call.
    }

    /// <summary>
    /// Takes the engine's window as a child. Safe to call again with the same
    /// handle — the pane re-attaches whenever it is rebuilt after being hidden.
    /// </summary>
    public void Attach(IntPtr engineWindow)
    {
        if (engineWindow == IntPtr.Zero)
        {
            return;
        }

        _child = engineWindow;
        if (_container == IntPtr.Zero)
        {
            // The container is not built yet; BuildWindowCore attaches instead.
            return;
        }

        Reparent();
    }

    private void Reparent()
    {
        // An Electron BrowserWindow is a top-level popup; it has to stop being
        // one before it can be a child, or SetParent leaves it floating above
        // the app with its own frame.
        var style = GetWindowLong(_child, GwlStyle);
        SetWindowLong(_child, GwlStyle, (style & ~(WsPopup | WsCaption | WsThickFrame)) | WsChild | WsVisible);
        SetParent(_child, _container);
        ShowWindow(_child, SwShowNoActivate);
        Resize();
    }

    /// <summary>
    /// Stretches the engine window over this element.
    ///
    /// The size is taken from the WPF element rather than from the container's
    /// client rect, because HwndHost resizes that container *after* the layout
    /// event that reports the new size - reading it here returns the previous
    /// pass's rectangle, which left the engine window a pass behind and, on the
    /// first layout, at nothing at all. Going through the element also puts the
    /// DPI transform in one place, which is what keeps the page the right size
    /// on a scaled display.
    /// </summary>
    private void Resize()
    {
        if (_container == IntPtr.Zero || _child == IntPtr.Zero)
        {
            return;
        }

        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        var width = (int)Math.Round(ActualWidth * scale);
        var height = (int)Math.Round(ActualHeight * scaleY);
        if (width <= 0 || height <= 0)
        {
            // Mid-layout the element is momentarily empty. Moving the engine
            // window to nothing would stop it rendering, and the next real
            // layout is what restores it, so this pass is skipped instead.
            return;
        }

        MoveWindow(_container, 0, 0, width, height, repaint: false);

        // A frameless Electron window still carries the invisible resize border
        // DWM gives every window: its window rect is larger than what it paints
        // by a few pixels at the left, right and bottom (never the top). Sizing
        // the window to the container therefore leaves that border uncovered,
        // and the container shows through it as a band around the page. The
        // window is grown by the border instead, so what it paints is the
        // container exactly.
        var (horizontal, vertical) = FrameInsets(_child);
        MoveWindow(
            _child, -(horizontal / 2), 0, width + horizontal, height + vertical, repaint: true);
        UpdateViewportClip();
    }

    /// <summary>
    /// How much wider and taller the window's rect is than the area it paints —
    /// its client rect. A frameless Electron window keeps a resize border in its
    /// window rect on Windows 11, and reparenting does not take it away, so
    /// sizing the window to the container leaves that border over the container
    /// rather than over the page. Answers zero for anything wider than a border
    /// could be, since a wrong answer would move the page rather than cancel it.
    /// </summary>
    private static (int Horizontal, int Vertical) FrameInsets(IntPtr window)
    {
        if (!GetClientRect(window, out var client) || !GetWindowRect(window, out var outer))
        {
            return (0, 0);
        }

        var horizontal = outer.Right - outer.Left - (client.Right - client.Left);
        var vertical = outer.Bottom - outer.Top - (client.Bottom - client.Top);
        return horizontal is >= 0 and <= MaxFrameInset && vertical is >= 0 and <= MaxFrameInset
            ? (horizontal, vertical)
            : (0, 0);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = CreateWindowEx(
            0, "static", null, WsChild | WsVisible | WsClipChildren,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_child != IntPtr.Zero)
        {
            Reparent();
        }
        UpdateViewportClip();

        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // The engine's window belongs to the engine process: it is released
        // from this container rather than destroyed, so the pane can be rebuilt
        // and re-attach the same window with its pages still loaded.
        if (_child != IntPtr.Zero)
        {
            SetParent(_child, IntPtr.Zero);
            _child = IntPtr.Zero;
        }

        if (_container != IntPtr.Zero)
        {
            DestroyWindow(_container);
            _container = IntPtr.Zero;
            _lastClip = null;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Resize();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);


    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
}
