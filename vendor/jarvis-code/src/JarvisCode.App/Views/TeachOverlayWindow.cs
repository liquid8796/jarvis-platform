using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace JarvisCode.App.Views;

/// <summary>Which way the tooltip's arrow points at the anchor.</summary>
internal enum ArrowSide
{
    None,
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// Teach mode's on-screen tooltip: a full-screen click-through overlay carrying a
/// single card that explains the step and waits for the user. Geometry and states
/// are ported from the reference overlay (18px logo row, 280–420 wide card, 10px
/// arrow 16px off the anchor, 20px edge margin, below → above → right → left
/// placement, step ⇄ working swap); the colours come from the app's own theme.
/// </summary>
public sealed class TeachOverlayWindow : Window
{
    private const double ArrowSize = 10;
    private const double ArrowGap = 16;
    private const double EdgeMargin = 20;

    private readonly Canvas _canvas = new();
    private readonly Border _card = new();
    private readonly Polygon _arrow = new();
    private readonly TextBlock _explanation = new();
    private readonly TextBlock _nextPreview = new();
    private readonly Border _previewRow = new();
    private readonly StackPanel _step = new();
    private readonly StackPanel _working = new();
    private readonly Button _next = new();
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };

    private bool _interactive;

    public TeachOverlayWindow(bool reduceMotion)
    {
        ReduceMotion = reduceMotion;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Title = "Teaching";
        Content = _canvas;

        BuildCard();
        _canvas.Children.Add(_arrow);
        _canvas.Children.Add(_card);

        SourceInitialized += (_, _) =>
        {
            MakeClickThrough(true);
            _hoverTimer.Start();
        };
        Closed += (_, _) => _hoverTimer.Stop();
        _hoverTimer.Tick += (_, _) => SyncHover();
    }

    /// <summary>Skips the enter animation when the user asked for less motion.</summary>
    public bool ReduceMotion { get; }

    public event Action? NextRequested;

    public event Action? ExitRequested;

    private void BuildCard()
    {
        _explanation.TextWrapping = TextWrapping.Wrap;
        _explanation.FontSize = 14;
        _explanation.LineHeight = 21;
        _explanation.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        _nextPreview.TextWrapping = TextWrapping.Wrap;
        _nextPreview.FontSize = 12;
        _nextPreview.LineHeight = 17;
        _nextPreview.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");

        _previewRow.Child = _nextPreview;
        _previewRow.BorderThickness = new Thickness(0, 0.5, 0, 0);
        _previewRow.Padding = new Thickness(0, 8, 0, 0);
        _previewRow.Margin = new Thickness(0, 2, 0, 0);
        _previewRow.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");

        var logo = new SpinnerGlyph
        {
            Width = 18,
            Height = 18,
            IsSpinning = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var exit = SecondaryButton("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _next.Content = "Next";
        _next.Padding = new Thickness(16, 0, 16, 0);
        _next.Height = 32;
        _next.MinWidth = 72;
        _next.FontSize = 13;
        _next.FontWeight = FontWeights.Medium;
        _next.Cursor = System.Windows.Input.Cursors.Hand;
        _next.SetResourceReference(Control.BackgroundProperty, "AccentBrandBrush");
        _next.SetResourceReference(Control.ForegroundProperty, "Oncolor100Brush");
        _next.BorderThickness = new Thickness(0);
        _next.Click += (_, _) => NextRequested?.Invoke();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        exit.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(exit);
        buttons.Children.Add(_next);

        _step.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { logo },
            Margin = new Thickness(0, 0, 0, 10),
        });
        _step.Children.Add(_explanation);
        _step.Children.Add(_previewRow);
        _step.Children.Add(buttons);

        var workingExit = SecondaryButton("Exit");
        workingExit.Click += (_, _) => ExitRequested?.Invoke();
        var workingLabel = new TextBlock
        {
            Text = "Working…",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
        };
        workingLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        _working.Orientation = Orientation.Horizontal;
        _working.Visibility = Visibility.Collapsed;
        _working.Children.Add(new SpinnerGlyph { Width = 18, Height = 18, IsSpinning = true });
        _working.Children.Add(workingLabel);
        _working.Children.Add(workingExit);

        _card.MinWidth = 280;
        _card.MaxWidth = 420;
        _card.CornerRadius = new CornerRadius(16);
        _card.Padding = new Thickness(20, 18, 20, 18);
        _card.BorderThickness = new Thickness(0.5);
        _card.SetResourceReference(Border.BackgroundProperty, "ClaudeBackgroundColorBrush");
        _card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        _card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 40,
            ShadowDepth = 10,
            Direction = 270,
            Opacity = 0.25,
            Color = Colors.Black,
        };
        _card.Child = new Grid { Children = { _step, _working } };
        _card.RenderTransform = new ScaleTransform(1, 1);
        _card.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        // No stroke: the card's own border would cut across the arrow's base and
        // leave a seam. The reference gives the triangle a drop shadow instead, so
        // its tip stays legible against a same-coloured window underneath.
        _arrow.Points = [];
        _arrow.SetResourceReference(Shape.FillProperty, "ClaudeBackgroundColorBrush");
        _arrow.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 4,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 0.2,
            Color = Colors.Black,
        };
    }

    private static Button SecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Height = 32,
            MinWidth = 64,
            Padding = new Thickness(16, 0, 16, 0),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            BorderThickness = new Thickness(0.5),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.SetResourceReference(Control.ForegroundProperty, "Text100Brush");
        button.SetResourceReference(Control.BorderBrushProperty, "BorderMidBrush");
        return button;
    }

    /// <summary>Covers one display, in that display's own device pixels.</summary>
    public void CoverDisplay(System.Drawing.Rectangle bounds)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = bounds.X / dpi.DpiScaleX;
        Top = bounds.Y / dpi.DpiScaleY;
        Width = bounds.Width / dpi.DpiScaleX;
        Height = bounds.Height / dpi.DpiScaleY;
    }

    /// <summary>Shows one step; <paramref name="anchor"/> is in screen pixels.</summary>
    public void ShowStep(string explanation, string? nextPreview, System.Drawing.Point? anchor)
    {
        _step.Visibility = Visibility.Visible;
        _working.Visibility = Visibility.Collapsed;
        _next.IsEnabled = true;
        _explanation.Text = explanation;
        _nextPreview.Text = nextPreview ?? "";
        _previewRow.Visibility = string.IsNullOrWhiteSpace(nextPreview) ? Visibility.Collapsed : Visibility.Visible;

        _card.UpdateLayout();
        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(anchor);
        Animate();
    }

    /// <summary>The step's actions are running; only Exit stays live.</summary>
    public void ShowWorking()
    {
        _step.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Visible;
    }

    private void Animate()
    {
        if (ReduceMotion)
        {
            _card.Opacity = 1;
            return;
        }

        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var duration = TimeSpan.FromMilliseconds(300);
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        var scale = (ScaleTransform)_card.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
    }

    /// <summary>
    /// The reference's auto-placement: prefer below the anchor, then above, right,
    /// left; clamp into the display and slide the arrow so its tip still lands on
    /// the anchor after clamping.
    /// </summary>
    private void Place(System.Drawing.Point? anchor)
    {
        var size = _card.DesiredSize;
        var card = new Size(Math.Max(size.Width, _card.MinWidth), size.Height);
        var viewport = new Size(ActualWidth, ActualHeight);

        if (anchor is null)
        {
            var (_, centre) = Resolve(null, card, viewport);
            Canvas.SetLeft(_card, centre.X);
            Canvas.SetTop(_card, centre.Y);
            _arrow.Points = [];
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var point = new System.Windows.Point(
            anchor.Value.X / dpi.DpiScaleX - Left,
            anchor.Value.Y / dpi.DpiScaleY - Top);
        var (side, origin) = Resolve(point, card, viewport);

        Canvas.SetLeft(_card, origin.X);
        Canvas.SetTop(_card, origin.Y);
        DrawArrow(
            side,
            new Rect(origin.X, origin.Y, card.Width, card.Height),
            Math.Clamp(point.X, EdgeMargin, Math.Max(EdgeMargin, viewport.Width - EdgeMargin)),
            Math.Clamp(point.Y, EdgeMargin, Math.Max(EdgeMargin, viewport.Height - EdgeMargin)));
    }

    /// <summary>
    /// The reference's auto-placement, as a pure function: prefer below the anchor,
    /// then above, right, left, and clamp the card into the display. An anchorless
    /// step is centred with no arrow.
    /// </summary>
    internal static (ArrowSide Side, System.Windows.Point Origin) Resolve(
        System.Windows.Point? anchor,
        Size card,
        Size viewport)
    {
        if (anchor is null)
        {
            return (ArrowSide.None, new System.Windows.Point(
                Math.Round((viewport.Width - card.Width) / 2),
                Math.Round((viewport.Height - card.Height) / 2)));
        }

        var ax = Math.Clamp(anchor.Value.X, EdgeMargin, Math.Max(EdgeMargin, viewport.Width - EdgeMargin));
        var ay = Math.Clamp(anchor.Value.Y, EdgeMargin, Math.Max(EdgeMargin, viewport.Height - EdgeMargin));

        var side = ay + ArrowGap + card.Height + EdgeMargin <= viewport.Height ? ArrowSide.Up
            : ay - ArrowGap - card.Height - EdgeMargin >= 0 ? ArrowSide.Down
            : ax + ArrowGap + card.Width + EdgeMargin <= viewport.Width ? ArrowSide.Left
            : ax - ArrowGap - card.Width - EdgeMargin >= 0 ? ArrowSide.Right
            : ArrowSide.Up;

        double left, top;
        if (side is ArrowSide.Up or ArrowSide.Down)
        {
            left = Math.Clamp(
                ax - card.Width / 2,
                EdgeMargin,
                Math.Max(EdgeMargin, viewport.Width - card.Width - EdgeMargin));
            top = side == ArrowSide.Up ? ay + ArrowGap : ay - ArrowGap - card.Height;
        }
        else
        {
            top = Math.Clamp(
                ay - card.Height / 2,
                EdgeMargin,
                Math.Max(EdgeMargin, viewport.Height - card.Height - EdgeMargin));
            left = side == ArrowSide.Left ? ax + ArrowGap : ax - ArrowGap - card.Width;
        }

        return (side, new System.Windows.Point(Math.Round(left), Math.Round(top)));
    }

    private void DrawArrow(ArrowSide side, Rect card, double ax, double ay)
    {
        // The tip sits on the anchor; the base spans the card edge it grows from.
        var points = side switch
        {
            ArrowSide.Up =>
            [
                new System.Windows.Point(Clamp(ax, card.Left, card.Right) - ArrowSize, card.Top),
                new System.Windows.Point(Clamp(ax, card.Left, card.Right) + ArrowSize, card.Top),
                new System.Windows.Point(ax, card.Top - ArrowSize),
            ],
            ArrowSide.Down =>
            [
                new System.Windows.Point(Clamp(ax, card.Left, card.Right) - ArrowSize, card.Bottom),
                new System.Windows.Point(Clamp(ax, card.Left, card.Right) + ArrowSize, card.Bottom),
                new System.Windows.Point(ax, card.Bottom + ArrowSize),
            ],
            ArrowSide.Left =>
            [
                new System.Windows.Point(card.Left, Clamp(ay, card.Top, card.Bottom) - ArrowSize),
                new System.Windows.Point(card.Left, Clamp(ay, card.Top, card.Bottom) + ArrowSize),
                new System.Windows.Point(card.Left - ArrowSize, ay),
            ],
            ArrowSide.Right =>
            [
                new System.Windows.Point(card.Right, Clamp(ay, card.Top, card.Bottom) - ArrowSize),
                new System.Windows.Point(card.Right, Clamp(ay, card.Top, card.Bottom) + ArrowSize),
                new System.Windows.Point(card.Right + ArrowSize, ay),
            ],
            _ => new List<System.Windows.Point>(),
        };
        _arrow.Points = [.. points];
    }

    private static double Clamp(double value, double low, double high) =>
        Math.Clamp(value, low + ArrowSize + 6, Math.Max(low + ArrowSize + 6, high - ArrowSize - 6));

    // ---- click-through ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x08000000;

    /// <summary>
    /// The overlay covers the whole display, so it is click-through by default and
    /// only becomes solid while the pointer is over the card — the reference's
    /// mouseEnter/mouseLeave dance, done with the Windows extended style.
    /// </summary>
    private void MakeClickThrough(bool transparent)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle) | WsExLayered | WsExNoActivate;
        style = transparent ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(handle, GwlExStyle, style);
        _interactive = !transparent;
    }

    /// <summary>
    /// Polls the pointer instead of relying on mouse events: while the window is
    /// click-through it receives none, and the card can also appear under a
    /// stationary cursor when a step changes.
    /// </summary>
    private void SyncHover()
    {
        if (!IsVisible)
        {
            return;
        }

        var cursor = System.Windows.Forms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = cursor.X / dpi.DpiScaleX - Left;
        var y = cursor.Y / dpi.DpiScaleY - Top;
        var left = Canvas.GetLeft(_card);
        var top = Canvas.GetTop(_card);
        var over = !double.IsNaN(left) && !double.IsNaN(top)
            && x >= left && x <= left + _card.ActualWidth
            && y >= top && y <= top + _card.ActualHeight;

        if (over != _interactive)
        {
            MakeClickThrough(!over);
        }
    }
}
