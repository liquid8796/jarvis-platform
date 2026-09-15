using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using JarvisCode.App.Controls;

namespace JarvisCode.App.Views;

/// <summary>
/// The rim of light around the screen while a session is driving the desktop.
///
/// The reference draws this as a transparent, frameless, click-through window
/// pinned at the screen-saver level over one display's whole bounds, loading a
/// generated <c>cu-glow.html</c> — measured in desktop 1.44121.2.0's main
/// process (<c>index.chunk-BHbE7U4N.js</c>, its <c>Uwn</c> page and the
/// <c>Qwn</c>/<c>$wn</c>/<c>eTn</c> create/show/hide trio, raised from its
/// <c>cuLockChanged</c>). Every value here is that page's: the 2s pulse, the
/// 0.3s fade, the 320ms hide delay (its <c>Vwn</c>), and the badge that starts
/// at the centre of the screen and settles into the corner over 6s.
///
/// It is drawn natively rather than in a browser window, which is this port's
/// one deliberate difference here: the indicator says a machine is being
/// driven, and making it depend on the browser engine having finished
/// downloading would leave the honest case — a fresh install — unmarked.
/// </summary>
internal sealed class ComputerUseGlowWindow : Window
{
    /// <summary>The reference's own hide delay, <c>Vwn = 320</c>.</summary>
    internal static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(320);

    /// <summary>The rim's colour, the page's own: <c>rgb(217, 119, 87)</c>.</summary>
    internal static readonly Color Accent = Color.FromRgb(217, 119, 87);

    /// <summary>The rim's reach: the widest of the page's three inset shadows.</summary>
    private const double Reach = 150;

    /// <summary>
    /// The rim at its brightest, so the pulse is one opacity animation rather
    /// than three animated shadows. Composited, the page's three shadows read
    /// 0.685 at rest and 0.880 at the peak of the pulse, which is this ratio.
    /// </summary>
    private const double RestOpacity = 0.778;

    private readonly Grid _overlay;
    private readonly Border _badge;
    private readonly TranslateTransform _badgeOffset = new();
    private readonly ScaleTransform _badgeScale = new(1.4, 1.4);

    internal ComputerUseGlowWindow(string label)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Left = 0;
        Top = 0;
        Width = 1;
        Height = 1;

        var glow = new Grid { Opacity = RestOpacity };
        foreach (var side in new[] { Dock.Top, Dock.Bottom, Dock.Left, Dock.Right })
        {
            glow.Children.Add(Edge(side));
        }

        glow.BeginAnimation(OpacityProperty, Pulse());

        _badge = Badge(label);
        _overlay = new Grid { Opacity = 0 };
        _overlay.Children.Add(glow);
        _overlay.Children.Add(_badge);
        Content = _overlay;
    }

    /// <summary>Windows' own answer to the compositor exclusion macOS has.</summary>
    private const uint WdaExcludeFromCapture = 0x11;

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x8000000;
    private const int WsExToolWindow = 0x80;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// The handle, once the window has one — the mask in
    /// <see cref="Services.ComputerUseGrants"/> paints by window bounds rather
    /// than by pixels, so a full-screen indicator it did not know about would
    /// grey out every screenshot taken while it was up.
    /// </summary>
    internal IntPtr Handle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var style = GetWindowLong(Handle, GwlExStyle);
        _ = SetWindowLong(
            Handle, GwlExStyle, style | WsExTransparent | WsExLayered | WsExNoActivate | WsExToolWindow);

        // Kept out of the screenshots this app takes of the desktop it is
        // marking: the rim is for the person at the keyboard, not for the model.
        if (!Services.ComputerUseGlow.Capturable)
        {
            _ = SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
        }
    }

    /// <summary>
    /// Covers one display, in physical pixels. WPF places a window in
    /// device-independent units against its own scale, which puts a full-screen
    /// overlay in the wrong place the moment two monitors differ in DPI; a
    /// display's bounds are already pixels, so they are set through the window
    /// manager instead.
    /// </summary>
    internal void Cover(System.Drawing.Rectangle bounds)
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            Handle, HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    /// <summary>Fades the rim in and flashes the badge, as the page's show does.</summary>
    internal void Reveal(bool showBadge)
    {
        _overlay.BeginAnimation(OpacityProperty, Fade(1));
        _badge.BeginAnimation(OpacityProperty, null);
        _badge.Opacity = 0;
        if (showBadge)
        {
            // Where the badge lands is measured off the window and off the pill,
            // so the flash waits for the layout pass the new bounds provoke.
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Flash);
        }
    }

    /// <summary>Fades the rim out; the caller hides the window after <see cref="HideDelay"/>.</summary>
    internal void Conceal() => _overlay.BeginAnimation(OpacityProperty, Fade(0));

    private static DoubleAnimation Fade(double to) => new(to, new Duration(TimeSpan.FromSeconds(0.3)))
    {
        // CSS ease-in-out.
        EasingFunction = new CubicBezierEase(0.42, 0, 0.58, 1),
        FillBehavior = FillBehavior.HoldEnd,
    };

    private static DoubleAnimation Pulse() =>
        new(RestOpacity, 1, new Duration(TimeSpan.FromSeconds(1)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicBezierEase(0.42, 0, 0.58, 1),
        };

    /// <summary>
    /// One side of the rim. The page stacks three inset shadows at 50, 100 and
    /// 150px; composited they are a ramp from the edge inwards, which is what
    /// these stops are.
    /// </summary>
    private static UIElement Edge(Dock side)
    {
        var horizontal = side is Dock.Left or Dock.Right;
        var start = side switch
        {
            Dock.Top => new Point(0, 0),
            Dock.Bottom => new Point(0, 1),
            Dock.Left => new Point(0, 0),
            _ => new Point(1, 0),
        };
        var end = side switch
        {
            Dock.Top => new Point(0, 1),
            Dock.Bottom => new Point(0, 0),
            Dock.Left => new Point(1, 0),
            _ => new Point(0, 0),
        };

        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(Alpha(0.88), 0));
        brush.GradientStops.Add(new GradientStop(Alpha(0.42), 50.0 / Reach));
        brush.GradientStops.Add(new GradientStop(Alpha(0.14), 100.0 / Reach));
        brush.GradientStops.Add(new GradientStop(Alpha(0), 1));
        brush.Freeze();

        return new System.Windows.Shapes.Rectangle
        {
            Fill = brush,
            Width = horizontal ? Reach : double.NaN,
            Height = horizontal ? double.NaN : Reach,
            HorizontalAlignment = side switch
            {
                Dock.Left => HorizontalAlignment.Left,
                Dock.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Stretch,
            },
            VerticalAlignment = side switch
            {
                Dock.Top => VerticalAlignment.Top,
                Dock.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Stretch,
            },
        };
    }

    private static Color Alpha(double alpha) =>
        Color.FromArgb((byte)Math.Round(alpha * 255), Accent.R, Accent.G, Accent.B);

    /// <summary>
    /// The page's pill: a near-white warm base, because it sits over arbitrary
    /// screen content rather than over the app's own panel.
    /// </summary>
    private Border Badge(string label)
    {
        var dotScale = new ScaleTransform(1, 1);
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Accent),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = dotScale,
        };
        dot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0.5, new Duration(TimeSpan.FromSeconds(0.9)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicBezierEase(0.42, 0, 0.58, 1),
            });
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            dotScale.BeginAnimation(
                property,
                new DoubleAnimation(1, 0.7, new Duration(TimeSpan.FromSeconds(0.9)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new CubicBezierEase(0.42, 0, 0.58, 1),
                });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Accent),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var pill = new Border
        {
            Child = row,
            // The page's own base under the rim: rgba(255, 247, 242, 0.95).
            Background = new SolidColorBrush(Color.FromArgb(242, 255, 247, 242)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, Accent.R, Accent.G, Accent.B)),
            BorderThickness = new Thickness(1),
            // CSS clamps `border-radius: 999px` to half the box; WPF does not,
            // and an unclamped radius draws a lens instead of a pill.
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _badgeScale, _badgeOffset } },
        };
        pill.SizeChanged += (_, e) => pill.CornerRadius = new CornerRadius(e.NewSize.Height / 2);
        return pill;
    }

    /// <summary>
    /// The page's <c>badge-center-to-corner</c>: six seconds from the middle of
    /// the screen to the top-right corner, at its own keyframe times.
    /// </summary>
    private void Flash()
    {
        var duration = new Duration(TimeSpan.FromSeconds(6));
        var ease = new CubicBezierEase(0.4, 0, 0.2, 1);

        var opacity = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            FillBehavior = FillBehavior.HoldEnd,
        };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.05)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.88)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        _badge.BeginAnimation(OpacityProperty, opacity);

        // The page moves it from the centre to `top: 40px; left: calc(100% - 280px)`.
        // From a pill centred in the window that is this offset — upwards, and
        // to the right — once the layout has given both a size.
        var toX = (ActualWidth / 2) - 280 + (_badge.ActualWidth / 2);
        var toY = 40 + (_badge.ActualHeight / 2) - (ActualHeight / 2);

        _badgeOffset.BeginAnimation(TranslateTransform.XProperty, Slide(0, toX, duration, ease));
        _badgeOffset.BeginAnimation(TranslateTransform.YProperty, Slide(0, toY, duration, ease));
        _badgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, Slide(1.4, 0.8, duration, ease));
        _badgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, Slide(1.4, 0.8, duration, ease));
    }

    private static DoubleAnimationUsingKeyFrames Slide(
        double from, double to, Duration duration, IEasingFunction ease)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0.88)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromPercent(1), ease));
        return animation;
    }
}
