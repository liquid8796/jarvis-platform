using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>KaTeX's full macro/layout coverage inside a native markdown line or block.</summary>
internal sealed class KatexBlock : ContentControl
{
    private readonly string _latex;
    private readonly bool _display;
    private readonly double _size;
    private int _generation;

    public KatexBlock(string latex, bool display, double size, UIElement? fallback = null)
    {
        _latex = latex; _display = display; _size = size;
        Focusable = false;
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        Content = fallback ?? new TextBlock { Text = latex, FontSize = size };
        SetResourceReference(ForegroundProperty, "Text100Brush");
        TextSelectionScope.SetCopyText(this, latex);
        System.Windows.Automation.AutomationProperties.SetName(this, latex);
        Loaded += (_, _) => _ = RenderAsync();
        Unloaded += (_, _) => _generation++;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty && IsLoaded) _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        var generation = ++_generation;
        var foreground = Foreground is SolidColorBrush brush ? brush.Color : Colors.Gray;
        try
        {
            var result = await RichTextRenderer.Shared.MathAsync(_latex, _display, _size,
                $"#{foreground.R:x2}{foreground.G:x2}{foreground.B:x2}", Math.Max(2, VisualTreeHelper.GetDpi(this).DpiScaleX));
            if (generation != _generation || !IsLoaded) return;
            Content = new Image { Source = result.Image, Width = result.Width, Height = result.Height, Stretch = Stretch.Uniform };
        }
        catch (Exception error)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write("math: bundled renderer unavailable: " + error.GetType().Name);
        }
    }
}
