using System.Windows;
using System.Windows.Media;
using JarvisCode.App.Theming;

namespace JarvisCode.App.Views;

/// <summary>
/// Draws the active theme's loading spinner (or the default starburst) and
/// animates it per the spec's animation kind: spin, bounce, pulse, or the
/// 2-frame flip. Static (not spinning) it doubles as the app glyph.
/// </summary>
public sealed class SpinnerGlyph : FrameworkElement
{
    public static readonly DependencyProperty IsSpinningProperty = DependencyProperty.Register(
        nameof(IsSpinning), typeof(bool), typeof(SpinnerGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsSpinningChanged));

    private SpinnerSpec _spec = ThemeService.DefaultSpinner;
    private IReadOnlyList<(Geometry Geometry, Color? Fill)> _frame1 = [];
    private IReadOnlyList<(Geometry Geometry, Color? Fill)> _frame2 = [];
    private Rect _viewBox = new(0, 0, 100, 100);
    private long _startTicks;
    private bool _renderHooked;

    public SpinnerGlyph()
    {
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
        };
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

    public bool IsSpinning
    {
        get => (bool)GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    private static ThemeService? ThemeServiceOrNull()
        => Application.Current is App { } app && app.TryGetServices(out var services) ? services.Theme : null;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (sender is ThemeService theme)
        {
            LoadSpec(theme.ActiveSpinner);
        }
    }

    private void LoadSpec(SpinnerSpec spec)
    {
        _spec = spec;
        _viewBox = ParseViewBox(spec.ViewBox);
        _frame1 = BuildFrame(spec.Paths);
        _frame2 = BuildFrame(spec.Paths2);
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

    private static IReadOnlyList<(Geometry, Color?)> BuildFrame(IReadOnlyList<SpinnerPath> paths)
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

    private static void OnIsSpinningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var glyph = (SpinnerGlyph)d;
        if ((bool)e.NewValue)
        {
            glyph._startTicks = Environment.TickCount64;
            glyph.HookRender();
        }
        else
        {
            glyph.UnhookRender();
            glyph.InvalidateVisual();
        }
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

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _frame1.Count == 0)
        {
            return;
        }

        var t = (Environment.TickCount64 - _startTicks) / 1000.0;
        var frame = _frame1;
        var opacity = 1.0;
        double rotation = 0, bounceY = 0;

        if (IsSpinning)
        {
            switch (_spec.Animation)
            {
                case "spin":
                    rotation = t % 1.0 * 360.0;
                    break;
                case "bounce":
                    bounceY = -0.12 * ActualHeight * Math.Sin(Math.PI * (t % 0.8 / 0.8));
                    break;
                case "pulse":
                    opacity = 1.0 - 0.55 * Math.Sin(Math.PI * (t % 1.2 / 1.2));
                    break;
                case "flip":
                    if (_frame2.Count > 0 && t % 1.0 >= 0.5)
                    {
                        frame = _frame2;
                    }

                    break;
                default:
                    rotation = t % 1.0 * 360.0;
                    break;
            }
        }

        var scale = Math.Min(ActualWidth / _viewBox.Width, ActualHeight / _viewBox.Height);
        var offsetX = (ActualWidth - _viewBox.Width * scale) / 2 - _viewBox.X * scale;
        var offsetY = (ActualHeight - _viewBox.Height * scale) / 2 - _viewBox.Y * scale;

        if (opacity < 1.0)
        {
            dc.PushOpacity(opacity);
        }

        if (rotation != 0)
        {
            dc.PushTransform(new RotateTransform(rotation, ActualWidth / 2, ActualHeight / 2));
        }

        if (bounceY != 0)
        {
            dc.PushTransform(new TranslateTransform(0, bounceY));
        }

        dc.PushTransform(new TranslateTransform(offsetX, offsetY));
        dc.PushTransform(new ScaleTransform(scale, scale));

        var accent = AccentBrush();
        foreach (var (geometry, fill) in frame)
        {
            Brush brush = fill is { } color ? new SolidColorBrush(color) : accent;
            dc.DrawGeometry(brush, null, geometry);
        }

        dc.Pop();
        dc.Pop();
        if (bounceY != 0)
        {
            dc.Pop();
        }

        if (rotation != 0)
        {
            dc.Pop();
        }

        if (opacity < 1.0)
        {
            dc.Pop();
        }
    }

    private static Brush AccentBrush()
        => Application.Current?.Resources["AccentBrandBrush"] as Brush ?? Brushes.Coral;
}
