using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// A <c>```mermaid</c> fence in the chat renderer, ported from the reference's
/// own <c>Ql</c> (ion-dist chunk <c>shared-12-4ZL7iq1e.js</c>, desktop
/// 1.40609.1.0) — the component its CodeBlock returns instead of a code block
/// once <c>claude_ai_markdown_mermaid_render</c> is on.
///
/// Its three states are the reference's. While the answer is still streaming, and
/// again whenever the render fails, the fence is a plain <c>&lt;pre&gt;</c> in the
/// code card's chrome at its 14px padding — unhighlighted, unwrapped, and with no
/// copy button, because the reference returns this component before it builds one.
/// In between it is the same card at 16px padding holding the diagram, centred and
/// never wider than the column, announcing itself as "Mermaid diagram"; and while
/// mermaid is working it is that card empty at a 96px floor, pulsing.
/// </summary>
internal sealed class MermaidBlock : ContentControl
{
    /// <summary>The reference's <c>min-h-24</c> placeholder, and its <c>p-4</c> card padding.</summary>
    private const double LoadingMinHeight = 96;

    private const double DiagramPadding = 16;

    /// <summary>Tailwind's <c>animate-pulse</c>: opacity 1 → .5 → 1 over 2s on its own curve.</summary>
    private static readonly TimeSpan PulsePeriod = TimeSpan.FromSeconds(2);

    private readonly string _code;
    private readonly MarkdownMetrics _metrics;
    private readonly Border _card;
    private readonly bool _streaming;
    private bool _rendered;
    private double _renderedAtWidth;
    private int _renderGeneration;
    private JarvisCode.App.Theming.ThemeService? _theme;

    internal MermaidBlock(string code, MarkdownMetrics metrics, bool streaming)
    {
        _code = code;
        _metrics = metrics;
        _streaming = streaming;
        Focusable = false;
        IsTabStop = false;

        _card = new Border
        {
            CornerRadius = new CornerRadius(metrics.CodeRadius),
            BorderThickness = new Thickness(metrics.CodeCardBorderThickness),
        };
        _card.SetResourceReference(Border.BackgroundProperty, metrics.CodeCardFillKey);
        if (metrics.CodeCardBorderKey is { } borderKey)
        {
            _card.SetResourceReference(Border.BorderBrushProperty, borderKey);
        }

        Content = _card;
        ShowSource();

        // A fence still being typed is the reference's plain pre; only a settled
        // one is handed to mermaid, which is what keeps a half-written diagram
        // from being parsed and refused a character at a time.
        if (!streaming)
        {
            Loaded += OnLoaded;
            SizeChanged += (_, change) =>
            {
                if (_rendered && Math.Abs(change.NewSize.Width - _renderedAtWidth) > MermaidRenderer.WidthTolerance)
                    _ = RenderAsync();
            };
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_rendered)
        {
            return;
        }

        _rendered = true;

        // The reference keys its cache on the resolved light/dark mode and on the
        // window width within 120px, and re-renders when either moves; the
        // palette is passed into mermaid, so a theme flip is a different diagram.
        if (Application.Current is App app && app.TryGetServices(out var services))
        {
            _theme = services.Theme;
            _theme.ThemeChanged += OnThemeChanged;
            Unloaded += (_, _) => _theme.ThemeChanged -= OnThemeChanged;
        }

        _ = RenderAsync();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _ = RenderAsync();

    private async Task RenderAsync()
    {
        if (MermaidRenderer.Current is not { } renderer || _streaming)
        {
            return;
        }

        var dark = Application.Current is App app && app.TryGetServices(out var services)
            && services.Theme.IsDark;
        var font = Application.Current?.TryFindResource("UiFontFamily") is FontFamily family
            ? family.Source
            : "Segoe UI Variable Text, Segoe UI, Arial";
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var viewport = ActualWidth > 0 ? ActualWidth : Application.Current?.MainWindow?.ActualWidth ?? 768;
        var generation = ++_renderGeneration;

        ShowLoading();
        _renderedAtWidth = viewport;
        var diagram = await renderer.RenderAsync(
            _code, dark, font, Math.Max(2, scale), viewport).ConfigureAwait(true);
        if (generation != _renderGeneration) return;
        if (diagram.Image is null)
        {
            ShowSource();
            return;
        }

        ShowDiagram(diagram);
    }

    /// <summary>The reference's fallback and streaming state: the source, plain.</summary>
    private void ShowSource()
    {
        var text = new TextBlock
        {
            Text = _code,
            FontSize = _metrics.CodeSize,
            LineHeight = _metrics.CodeLineHeight,
            TextWrapping = TextWrapping.NoWrap,
            Padding = _metrics.CodePadding,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        _card.BeginAnimation(OpacityProperty, null);
        _card.Opacity = 1;
        _card.MinHeight = 0;
        _card.Child = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        AutomationProperties.SetName(this, "");
    }

    private void ShowLoading()
    {
        // The reference keeps the height the pre already had, so the answer does
        // not jump while mermaid works; a fence that never had one gets its floor.
        var measured = _card.ActualHeight;
        _card.Child = null;
        _card.MinHeight = measured > 0 ? measured : LoadingMinHeight;

        if (Application.Current is App app && app.TryGetServices(out var services)
            && services.UiSettings.Current.ReduceMotion)
        {
            return;
        }

        _card.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            Duration = PulsePeriod,
            RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames =
            {
                new SplineDoubleKeyFrame(1, KeyTime.FromPercent(0)),
                new SplineDoubleKeyFrame(0.5, KeyTime.FromPercent(0.5), new KeySpline(0.4, 0, 0.6, 1)),
                new SplineDoubleKeyFrame(1, KeyTime.FromPercent(1), new KeySpline(0.4, 0, 0.6, 1)),
            },
        });
    }

    private void ShowDiagram(MermaidDiagram diagram)
    {
        _card.BeginAnimation(OpacityProperty, null);
        _card.Opacity = 1;
        _card.MinHeight = 0;
        _card.Child = diagram.Svg is not null ? new InlineMermaidView(diagram)
        {
            Margin = new Thickness(DiagramPadding),
            Background = _card.Background,
        } : new Image
        {
            Source = diagram.Image,
            Stretch = Stretch.Uniform,
            MaxWidth = diagram.Width,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(DiagramPadding),
        };
        AutomationProperties.SetName(this, MermaidDiagrams.DiagramLabel);
    }
}
