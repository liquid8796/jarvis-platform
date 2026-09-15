using System.Windows;
using System.Windows.Media;
using JarvisCode.App.Services;
using JarvisCode.App.Theming;

namespace JarvisCode.App.Controls;

/// <summary>
/// The mascot the empty chat screen carries, ported from the reference desktop's
/// CDS Spark (<c>pu</c> in <c>shared-frame-BADe7YPD.js</c>, desktop 1.44121.2.0).
///
/// The reference steps a vertical strip of pre-rendered frames; that strip is its
/// artwork and is not copied, so this steps the active theme's own mark through the
/// same schedule — the measured frame counts and speeds in
/// <see cref="SparkStates"/> — which keeps all 97 palettes looking like themselves.
/// Everything else is the reference's: <see cref="SparkState.Idle"/> and a reader
/// who has asked for reduced motion get the static mark, a one-shot state holds its
/// last frame and then raises <see cref="Ended"/>, and the default width is its own
/// <c>size</c> prop of 32.
/// </summary>
public sealed class SparkGlyph : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(SparkState), typeof(SparkGlyph),
        new FrameworkPropertyMetadata(SparkState.Idle,
            FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    private IReadOnlyList<(Geometry Geometry, Color? Fill)> _mark = [];
    private Rect _viewBox = new(0, 0, 100, 100);
    private long _startTicks;
    private bool _renderHooked;
    private bool _ended;

    public SparkGlyph()
    {
        Width = SparkStates.DefaultSize;
        Height = SparkStates.DefaultSize;
        IsHitTestVisible = false;

        Loaded += (_, _) =>
        {
            var theme = ThemeServiceOrNull();
            if (theme is not null)
            {
                theme.ThemeChanged += OnThemeChanged;
                LoadSpec(theme.ActiveSpinner);
            }
            else
            {
                LoadSpec(ThemeService.DefaultSpinner);
            }

            Restart();
        };

        // CompositionTarget.Rendering is a static event, so a mascot on a collapsed
        // screen would otherwise keep invalidating itself sixty times a second for as
        // long as the surface lives.
        IsVisibleChanged += (_, _) => Restart();

        Unloaded += (_, _) =>
        {
            var theme = ThemeServiceOrNull();
            if (theme is not null)
            {
                theme.ThemeChanged -= OnThemeChanged;
            }

            UnhookRender();
        };
    }

    /// <summary>Raised once a one-shot state has held its last frame, the reference's <c>onEnd</c>.</summary>
    public event EventHandler? Ended;

    public SparkState State
    {
        get => (SparkState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>
    /// Windows' answer to <c>prefers-reduced-motion</c>. The reference falls back to
    /// the static mark for such a reader rather than slowing the strip down.
    /// </summary>
    private static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    private static ThemeService? ThemeServiceOrNull()
        => Application.Current is App app && app.TryGetServices(out var services) ? services.Theme : null;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SparkGlyph)d).Restart();

    /// <summary>Starts this state's pass from frame zero, which is what a state change does there.</summary>
    private void Restart()
    {
        _startTicks = Environment.TickCount64;
        _ended = false;

        if (SparkStates.IsAnimated(State) && !ReducedMotion && IsVisible)
        {
            HookRender();
        }
        else
        {
            UnhookRender();

            // A one-shot state a reduced-motion reader cannot see still has to report,
            // or the caller waits forever for a callback that never arrives — which is
            // the reference's own `o && m && c()` arm for a sprite it could not load.
            if (SparkStates.IsOneShot(State))
            {
                RaiseEnded();
            }
        }

        InvalidateVisual();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (sender is ThemeService theme)
        {
            LoadSpec(theme.ActiveSpinner);
        }
    }

    private void LoadSpec(SpinnerSpec spec)
    {
        _viewBox = ParseViewBox(spec.ViewBox);
        _mark = BuildMark(spec.Paths);
        InvalidateVisual();
    }

    private static Rect ParseViewBox(string viewBox)
    {
        var parts = viewBox.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4 &&
            double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x) &&
            double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y) &&
            double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var w) &&
            double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var h) &&
            w > 0 && h > 0)
        {
            return new Rect(x, y, w, h);
        }

        return new Rect(0, 0, 100, 100);
    }

    private static IReadOnlyList<(Geometry, Color?)> BuildMark(IReadOnlyList<SpinnerPath> paths)
    {
        var list = new List<(Geometry, Color?)>(paths.Count);
        foreach (var path in paths)
        {
            Geometry geometry;
            try
            {
                geometry = Geometry.Parse(path.D);
            }
            catch (FormatException)
            {
                continue;
            }

            geometry.Freeze();
            Color? fill = path.Fill is not null && CssColor.TryParse(path.Fill, out var color) ? color : null;
            list.Add((geometry, fill));
        }

        return list;
    }

    private void HookRender()
    {
        if (!_renderHooked)
        {
            _renderHooked = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void UnhookRender()
    {
        if (_renderHooked)
        {
            _renderHooked = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        InvalidateVisual();

        if (SparkStates.HasEnded(State, Environment.TickCount64 - _startTicks))
        {
            // The last frame stays on screen — the reference's `fill: "forwards"` — so
            // the pass simply stops repainting rather than the element going blank.
            UnhookRender();
            RaiseEnded();
        }
    }

    private void RaiseEnded()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _mark.Count == 0)
        {
            return;
        }

        var elapsed = ReducedMotion ? 0 : Environment.TickCount64 - _startTicks;
        var frame = SparkStates.FrameAt(State, elapsed);
        var (frameScale, rotation, opacity) = ReducedMotion
            ? (1.0, 0.0, 1.0)
            : SparkStates.Frame(State, frame);

        if (frameScale <= 0 || opacity <= 0)
        {
            return;
        }

        var fit = Math.Min(ActualWidth / _viewBox.Width, ActualHeight / _viewBox.Height);
        var offsetX = ((ActualWidth - (_viewBox.Width * fit)) / 2) - (_viewBox.X * fit);
        var offsetY = ((ActualHeight - (_viewBox.Height * fit)) / 2) - (_viewBox.Y * fit);
        var centreX = ActualWidth / 2;
        var centreY = ActualHeight / 2;

        var pushed = 0;

        if (opacity < 1.0)
        {
            dc.PushOpacity(opacity);
            pushed++;
        }

        if (rotation != 0)
        {
            dc.PushTransform(new RotateTransform(rotation, centreX, centreY));
            pushed++;
        }

        if (frameScale != 1.0)
        {
            dc.PushTransform(new ScaleTransform(frameScale, frameScale, centreX, centreY));
            pushed++;
        }

        dc.PushTransform(new TranslateTransform(offsetX, offsetY));
        dc.PushTransform(new ScaleTransform(fit, fit));
        pushed += 2;

        var accent = AccentBrush();
        foreach (var (geometry, fill) in _mark)
        {
            Brush brush = fill is { } color ? new SolidColorBrush(color) : accent;
            dc.DrawGeometry(brush, null, geometry);
        }

        for (var i = 0; i < pushed; i++)
        {
            dc.Pop();
        }
    }

    private static Brush AccentBrush()
        => Application.Current?.Resources["AccentBrandBrush"] as Brush ?? Brushes.Coral;
}
