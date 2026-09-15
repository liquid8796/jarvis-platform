using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace JarvisCode.App.Controls;

/// <summary>
/// Selectable, syntax-highlighted code display: a read-only RichTextBox whose runs
/// take their colours from theme resources, so every palette keeps its own look and
/// a live theme switch restyles the code in place. Token colours map onto semantic
/// theme tokens because the palettes carry no syntax-specific ones.
/// </summary>
public sealed class CodeText : ContentControl
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(CodeText),
        new FrameworkPropertyMetadata("", static (d, _) => ((CodeText)d).Render()));

    public static readonly DependencyProperty LanguageProperty = DependencyProperty.Register(
        nameof(Language), typeof(string), typeof(CodeText),
        new FrameworkPropertyMetadata(null, static (d, _) => ((CodeText)d).Render()));

    public static readonly DependencyProperty WrapProperty = DependencyProperty.Register(
        nameof(Wrap), typeof(bool), typeof(CodeText),
        new FrameworkPropertyMetadata(true, static (d, _) => ((CodeText)d).Render()));

    /// <summary>
    /// Leading for the code lines. NaN keeps the font's own, which is what every
    /// caller but the markdown renderers wants; those two carry a measured value.
    /// </summary>
    public static readonly DependencyProperty CodeLineHeightProperty = DependencyProperty.Register(
        nameof(CodeLineHeight), typeof(double), typeof(CodeText),
        new FrameworkPropertyMetadata(double.NaN, static (d, _) => ((CodeText)d).Render()));

    private static readonly Dictionary<SyntaxKind, string> KindBrushes = new()
    {
        [SyntaxKind.Comment] = "Text500Brush",
        [SyntaxKind.String] = "Success000Brush",
        [SyntaxKind.Keyword] = "Accent000Brush",
        [SyntaxKind.Number] = "Warning000Brush",
        [SyntaxKind.Variable] = "AccentPro000Brush",
    };

    /// <summary>The theme brush a token kind falls back to when the active code theme names no colour for it.</summary>
    public static string BrushKeyFor(SyntaxKind kind) =>
        KindBrushes.TryGetValue(kind, out var key) ? key : "Text100Brush";

    private readonly RichTextBox _box;
    private int _highlightVersion;

    public CodeText()
    {
        // The Code appearance setting swaps the palette every code view paints with.
        Services.CodeThemes.ActiveChanged += OnCodeThemeChanged;
        Unloaded += (_, _) => Services.CodeThemes.ActiveChanged -= OnCodeThemeChanged;
        Loaded += (_, _) =>
        {
            Services.CodeThemes.ActiveChanged -= OnCodeThemeChanged;
            Services.CodeThemes.ActiveChanged += OnCodeThemeChanged;
        };
        Focusable = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _box = new RichTextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsUndoEnabled = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _box.SetResourceReference(FontFamilyProperty, "MonoFontFamily");
        _box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "SelectionBrush");
        _box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Text100Brush");
        // A RichTextBox takes the system theme's foreground, not its parent's, so the
        // control's own Foreground has to be handed down or Default runs render black.
        _box.SetBinding(ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        Content = _box;
        Loaded += (_, _) => Render();
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public string? Language
    {
        get => (string?)GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    /// <summary>Wrapped for prose-width spots; unwrapped code scrolls sideways instead.</summary>
    public bool Wrap
    {
        get => (bool)GetValue(WrapProperty);
        set => SetValue(WrapProperty, value);
    }

    public double CodeLineHeight
    {
        get => (double)GetValue(CodeLineHeightProperty);
        set => SetValue(CodeLineHeightProperty, value);
    }

    private void OnCodeThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        var version = ++_highlightVersion;
        RenderDocument();
        if (IsLoaded && Code.Length > 0 && !string.IsNullOrWhiteSpace(Language))
            _ = HighlightAsync(version, Code.TrimEnd('\n'), Language, Services.CodeThemes.Active);
    }

    private async Task HighlightAsync(int version, string code, string language, Services.CodeTheme theme)
    {
        try
        {
            var spans = await Services.RichTextRenderer.Shared.HighlightAsync(code, language, theme);
            // Replacing runs changes WPF's symbol offsets. Do not disturb a
            // selection the reader made while the grammar was loading.
            if (version == _highlightVersion && IsLoaded && _box.Selection.IsEmpty) RenderDocument(spans);
        }
        catch (Exception error)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write("highlight: bundled renderer unavailable: " + error.GetType().Name);
        }
    }

    private void RenderDocument(IReadOnlyList<Services.RichSyntaxSpan>? highlighted = null)
    {
        var code = Code.TrimEnd('\n');
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        if (!double.IsNaN(CodeLineHeight))
        {
            paragraph.LineHeight = CodeLineHeight;
            paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }

        var theme = Services.CodeThemes.Active;
        var spans = highlighted is null
            ? SyntaxHighlighter.Highlight(Language, code).Select(span => (span.Text, span.Kind, Color: (string?)null, FontStyle: 0))
            : highlighted.Select(span => (span.Text, Kind: SyntaxKind.Default, span.Color, span.FontStyle));
        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            var hex = span.Color ?? (span.Kind switch
            {
                SyntaxKind.Comment => theme.Comment,
                SyntaxKind.String => theme.String,
                SyntaxKind.Keyword => theme.Keyword,
                SyntaxKind.Number => theme.Number,
                SyntaxKind.Variable => theme.Variable,
                _ => theme.Background is null ? null : theme.Foreground,
            });
            if ((span.FontStyle & 1) != 0) run.FontStyle = FontStyles.Italic;
            if ((span.FontStyle & 2) != 0) run.FontWeight = FontWeights.Bold;
            if ((span.FontStyle & 4) != 0) run.TextDecorations = TextDecorations.Underline;
            if ((span.FontStyle & 8) != 0) run.TextDecorations = TextDecorations.Strikethrough;
            // A theme colour paints the run outright; a kind the theme leaves
            // unnamed keeps the app's semantic brush, which is what the two Claude
            // themes do for every kind they omit.
            if (theme.Color(hex) is { } color)
            {
                run.Foreground = new SolidColorBrush(color);
            }
            else if (KindBrushes.TryGetValue(span.Kind, out var brushKey))
            {
                run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
            }

            paragraph.Inlines.Add(run);
        }

        var document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
        };
        _box.HorizontalScrollBarVisibility = Wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        if (!Wrap)
        {
            // A FlowDocument wraps at the viewport unless the page is widened to the
            // longest line; only then does the horizontal scrollbar have work to do.
            document.PageWidth = MeasureWidestLine(code) + Padding.Left + Padding.Right + 8;
        }

        _box.Padding = Padding;
        _box.Document = document;
    }

    private double MeasureWidestLine(string code)
    {
        var typeface = new Typeface(_box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double pixelsPerDip = IsLoaded ? VisualTreeHelper.GetDpi(this).PixelsPerDip : 1.0;
        double widest = 0;
        foreach (var line in code.Split('\n'))
        {
            var formatted = new FormattedText(
                line.TrimEnd('\r'), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, FontSize, Brushes.Black, pixelsPerDip);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }

        return widest;
    }
}
