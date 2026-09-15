using System.Text;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisCode.Core.Markdown;

namespace JarvisCode.App.Controls;

/// <summary>
/// Renders markdown (via JarvisCode.Core's parser) as native WPF elements, in
/// whichever of the reference's two renderer shapes <see cref="Profile"/> names.
/// Re-renders are throttled so token streaming stays cheap.
/// </summary>
public sealed class MarkdownView : ContentControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new FrameworkPropertyMetadata("", OnMarkdownChanged));

    /// <summary>
    /// True while the text is still arriving. The reference holds back a
    /// half-typed construct and fades in what has landed; it draws no caret.
    /// </summary>
    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming), typeof(bool), typeof(MarkdownView),
        new FrameworkPropertyMetadata(false, OnMarkdownChanged));

    public static readonly DependencyProperty BodyBrushKeyProperty = DependencyProperty.Register(
        nameof(BodyBrushKey), typeof(string), typeof(MarkdownView),
        new FrameworkPropertyMetadata("Text100Brush", OnMarkdownChanged));

    public static readonly DependencyProperty HeadingLevelOffsetProperty = DependencyProperty.Register(
        nameof(HeadingLevelOffset), typeof(int), typeof(MarkdownView),
        new FrameworkPropertyMetadata(0, OnMarkdownChanged));

    /// <summary>
    /// Body size when the surrounding cell sets it — the reference's
    /// <c>typography:"inherit"</c>, which keeps a renderer's rules while taking
    /// its size from the parent. NaN leaves the renderer's own size in place.
    /// </summary>
    public static readonly DependencyProperty BodySizeOverrideProperty = DependencyProperty.Register(
        nameof(BodySizeOverride), typeof(double), typeof(MarkdownView),
        new FrameworkPropertyMetadata(double.NaN, OnMarkdownChanged));

    /// <summary>Which reference renderer this view reproduces.</summary>
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(MarkdownProfile), typeof(MarkdownView),
        new FrameworkPropertyMetadata(MarkdownProfile.Code, OnMarkdownChanged));

    /// <summary>
    /// The directory a relative link in this markdown is resolved against —
    /// the session's working directory. Empty leaves relative links inert.
    /// </summary>
    public static readonly DependencyProperty WorkingDirectoryProperty = DependencyProperty.Register(
        nameof(WorkingDirectory), typeof(string), typeof(MarkdownView),
        new FrameworkPropertyMetadata(""));

    /// <summary>
    /// Runs a shell command the user clicked Run on. Host-level rather than
    /// per-instance: the transcript builds these from DataTemplates, and the
    /// terminal a command should land in belongs to the window, not to one
    /// rendered block. Null hides the button, which is what keeps the harness
    /// prompt's Run-button sentence true only when there is one.
    /// </summary>
    public static Action<string>? ShellCommandRequested { get; set; }

    /// <summary>
    /// Shows a command in the terminal without running it — the reference's
    /// "Open in terminal", beside its Run. Null hides that button.
    /// </summary>
    public static Action<string>? TerminalOpenRequested { get; set; }

    /// <summary>
    /// Opens a file a link pointed at, with an optional 1-based line. Null
    /// leaves relative links inert.
    /// </summary>
    public static Action<string, int?>? FileActivationRequested { get; set; }

    private static double _transcriptTextSize = TranscriptTextSizes.Medium;

    /// <summary>
    /// The reader's transcript text size. Only the Code renderer has one — the
    /// reference exposes it as "Transcript text size" and derives every other
    /// value in that renderer from it. Setting it repaints the open transcripts.
    /// </summary>
    public static double TranscriptTextSize
    {
        get => _transcriptTextSize;
        set
        {
            if (Math.Abs(_transcriptTextSize - value) < 0.01)
            {
                return;
            }

            _transcriptTextSize = value;
            TranscriptTextSizeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Raised when the reader changes the transcript text size. The virtualizer
    /// listens too: every height it has measured was taken at the old size and
    /// describes nothing at the new one.
    /// </summary>
    internal static event EventHandler? TranscriptTextSizeChanged;

    /// <summary>
    /// Asks the user whether to load a remote image. Null keeps every image
    /// behind its placeholder, which is the safe direction.
    /// </summary>
    public static Func<string, bool>? ImageLoadRequested { get; set; }

    public string WorkingDirectory
    {
        get => (string)GetValue(WorkingDirectoryProperty);
        set => SetValue(WorkingDirectoryProperty, value);
    }

    private readonly StackPanel _panel = new();
    private readonly DispatcherTimer _throttle;
    private bool _renderQueued;
    private MarkdownMetrics _metrics = MarkdownMetrics.Code;
    private sealed record RenderedBlock(string Key, FrameworkElement Element, Thickness BaseMargin);
    private readonly List<RenderedBlock> _renderedBlocks = [];
    private string? _renderStyle;

    public MarkdownView()
    {
        Content = _panel;
        Focusable = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _throttle = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _throttle.Tick += (_, _) =>
        {
            _throttle.Stop();
            if (_renderQueued)
            {
                _renderQueued = false;
                Render();
            }
        };

        // The size is global, so a view repaints only while it is on screen.
        Loaded += (_, _) =>
        {
            TranscriptTextSizeChanged -= OnTranscriptTextSizeChanged;
            TranscriptTextSizeChanged += OnTranscriptTextSizeChanged;
        };
        Unloaded += (_, _) => TranscriptTextSizeChanged -= OnTranscriptTextSizeChanged;
    }

    private void OnTranscriptTextSizeChanged(object? sender, EventArgs e) => Render();


    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    /// <summary>
    /// Theme resource key for prose text. Defaults to the message body's colour;
    /// the chat thinking cell renders its markdown muted, as the reference does.
    /// </summary>
    public string BodyBrushKey
    {
        get => (string)GetValue(BodyBrushKeyProperty);
        set => SetValue(BodyBrushKeyProperty, value);
    }

    /// <summary>
    /// Demotes headings by this many levels, the reference's headingLevelOffset:
    /// a heading inside a thinking cell must not outrank the message around it.
    /// </summary>
    public int HeadingLevelOffset
    {
        get => (int)GetValue(HeadingLevelOffsetProperty);
        set => SetValue(HeadingLevelOffsetProperty, value);
    }

    public MarkdownProfile Profile
    {
        get => (MarkdownProfile)GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public double BodySizeOverride
    {
        get => (double)GetValue(BodySizeOverrideProperty);
        set => SetValue(BodySizeOverrideProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)d;
        if (view._throttle.IsEnabled)
        {
            view._renderQueued = true;
        }
        else
        {
            view.Render();
            view._throttle.Start();
        }
    }

    private void Render()
    {
        _metrics = MarkdownMetrics.For(Profile, TranscriptTextSize);
        if (!double.IsNaN(BodySizeOverride))
        {
            // Inherit the size but keep the renderer's leading ratio, so a cell
            // that sets its own size still reads as this renderer's prose.
            double ratio = _metrics.BodyLineHeight / _metrics.BodySize;
            _metrics = _metrics with
            {
                BodySize = BodySizeOverride,
                BodyLineHeight = BodySizeOverride * ratio,
            };
        }

        _tableIndex = 0;
        var style = $"{Profile}|{TranscriptTextSize}|{BodySizeOverride}|{BodyBrushKey}|{HeadingLevelOffset}|{IsStreaming}";
        if (_renderStyle != style)
        {
            _panel.Children.Clear();
            _renderedBlocks.Clear();
            _renderStyle = style;
        }

        var markdown = Markdown ?? "";
        if (IsStreaming)
        {
            // The reference holds back a construct that is still being typed so
            // its delimiters never flash as literal text.
            markdown = StreamingMarkdown.HoldBack(markdown, Profile == MarkdownProfile.Chat);
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            _panel.Children.Clear();
            _renderedBlocks.Clear();
            _lastLength = 0;
            _lastBlockCount = 0;
            return;
        }

        // Both reference renderers drop remark-gfm while a turn streams and add it
        // back once the message settles, so a table arrives whole rather than as
        // a run of half-formed rows.
        var options = IsStreaming
            ? _metrics.ParseOptions with { GithubFlavored = Profile == MarkdownProfile.Code && _metrics.ParseOptions.GithubFlavored, Streaming = true }
            : _metrics.ParseOptions;
        var blocks = MarkdownParser.Parse(markdown, options);
        var nextBlocks = new List<RenderedBlock>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var key = MarkdownBlockIdentity.Key(blocks[i]);
            RenderedBlock rendered;
            if (i < _renderedBlocks.Count && _renderedBlocks[i].Key == key)
            {
                rendered = _renderedBlocks[i];
                _tableIndex += MarkdownBlockIdentity.TableCount(blocks[i]);
            }
            else
            {
                if (RenderBlock(blocks[i]) is not FrameworkElement element) continue;
                rendered = new RenderedBlock(key, element, element.Margin);
            }
            nextBlocks.Add(rendered);

            // The reference lays blocks out with a flex/grid gap rather than
            // per-block margins, so every block but the last carries the gap.
            if (i < blocks.Count - 1)
            {
                rendered.Element.Margin = WithBottom(rendered.BaseMargin, rendered.BaseMargin.Bottom + _metrics.BlockGap);
            }
            else rendered.Element.Margin = rendered.BaseMargin;

            var index = nextBlocks.Count - 1;
            if (index >= _panel.Children.Count) _panel.Children.Add(rendered.Element);
            else if (!ReferenceEquals(_panel.Children[index], rendered.Element))
            {
                _panel.Children.RemoveAt(index);
                _panel.Children.Insert(index, rendered.Element);
            }
        }
        while (_panel.Children.Count > nextBlocks.Count) _panel.Children.RemoveAt(_panel.Children.Count - 1);
        _renderedBlocks.Clear();
        _renderedBlocks.AddRange(nextBlocks);

        if (IsStreaming)
        {
            FadeInStreamedTail(markdown);
        }
        else
        {
            _lastLength = 0;
            _lastBlockCount = 0;
            _tableColumns.Clear();
        }
    }

    private int _lastLength;
    private int _lastBlockCount;

    /// <summary>
    /// Column widths already measured for each table in this message. The
    /// reference pins a streaming table's columns to what they were so a row
    /// arriving mid-turn cannot make every column jump; columns only grow.
    /// </summary>
    private readonly List<double[]> _tableColumns = [];
    private int _tableIndex;

    /// <summary>
    /// The reference fades freshly streamed words in. Blocks that did not exist
    /// last render fade whole; in the final paragraph only the characters that
    /// arrived in this delta do, so settled text never re-animates.
    /// </summary>
    private void FadeInStreamedTail(string markdown)
    {
        int delta = markdown.Length - _lastLength;
        int previousBlocks = _lastBlockCount;
        _lastLength = markdown.Length;
        _lastBlockCount = _panel.Children.Count;
        if (delta <= 0 || delta >= markdown.Length || _panel.Children.Count == 0)
        {
            return; // a fresh message or a rewrite; nothing to single out
        }

        for (int b = Math.Max(0, previousBlocks); b < _panel.Children.Count - 1; b++)
        {
            AnimateElementIn((UIElement)_panel.Children[b]);
        }

        if (previousBlocks > _panel.Children.Count || _panel.Children[^1] is not TextBlock lastText)
        {
            AnimateElementIn((UIElement)_panel.Children[^1]);
            return;
        }

        int remaining = delta;
        foreach (var inline in lastText.Inlines.OfType<Inline>().Reverse().ToList())
        {
            if (remaining <= 0)
            {
                break;
            }

            switch (inline)
            {
                case Run run:
                    // Split the run at the delta boundary so only the new words fade.
                    if (run.Text.Length > remaining)
                    {
                        var tail = new Run(run.Text[^remaining..]);
                        run.Text = run.Text[..^remaining];
                        lastText.Inlines.InsertAfter(run, tail);
                        AnimateRunIn(tail, lastText);
                        remaining = 0;
                    }
                    else
                    {
                        remaining -= run.Text.Length;
                        AnimateRunIn(run, lastText);
                    }

                    break;
                case InlineUIContainer container:
                    remaining -= 1;
                    AnimateElementIn(container.Child);
                    break;
                case Hyperlink link:
                    remaining -= link.Inlines.OfType<Run>().Sum(r => r.Text.Length);
                    foreach (var child in link.Inlines.OfType<Run>().ToList())
                    {
                        AnimateRunIn(child, lastText);
                    }

                    break;
            }
        }
    }

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(350));

    private static void AnimateElementIn(UIElement element)
        => element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    private static void AnimateRunIn(Run run, TextBlock host)
    {
        // Runs inherit the host's brush; a per-run clone lets its opacity animate.
        var brush = (host.Foreground as SolidColorBrush)?.Clone() ?? new SolidColorBrush(Colors.Gray);
        run.Foreground = brush;
        brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, FadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static Thickness WithBottom(Thickness t, double bottom) => new(t.Left, t.Top, t.Right, bottom);

    private UIElement? RenderBlock(MarkdownBlock block) => block switch
    {
        HeadingBlock heading => RenderHeading(heading),
        MathBlock math => RenderMath(math),
        ParagraphBlock paragraph => RenderParagraph(paragraph),
        CodeFenceBlock code => RenderCodeFence(code),
        QuoteBlock quote => RenderQuote(quote),
        ListBlock list => RenderList(list),
        TableBlock table => RenderTable(table),
        HorizontalRuleBlock => RenderRule(),
        FootnoteDefinitionBlock footnote => RenderFootnoteDefinition(footnote),
        _ => null,
    };

    /// <summary>
    /// A footnote definition: the label in brackets, then its text, both at the
    /// renderer's footnote size in the secondary colour.
    /// </summary>
    private UIElement RenderFootnoteDefinition(FootnoteDefinitionBlock footnote)
    {
        var text = NewTextBlock();
        text.FontSize = _metrics.FootnoteSize;
        text.LineHeight = double.NaN;
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        text.Inlines.Add(new Run($"[{footnote.Label}] "));
        AddInlines(text.Inlines, footnote.Inlines, text.FontSize);
        ApplyDirection(text, footnote.Inlines);
        return text;
    }

    private UIElement RenderRule()
    {
        var rule = new Border
        {
            Height = _metrics.RuleThickness,
            Margin = _metrics.RuleMargin,
            // A half-pixel rule is what both renderers draw. WPF's layout
            // rounding would collapse it to nothing, so this one opts out and
            // is drawn antialiased instead of disappearing.
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
        };
        rule.SetResourceReference(Border.BackgroundProperty, _metrics.RuleKey);
        return rule;
    }

    private TextBlock RenderHeading(HeadingBlock heading)
    {
        var text = NewTextBlock();
        var level = Math.Clamp(heading.Level + HeadingLevelOffset, 1, 6);
        text.FontSize = _metrics.HeadingSize(level);
        text.LineHeight = double.NaN;
        text.FontWeight = _metrics.HeadingWeightFor(level);
        text.Margin = new Thickness(0, _metrics.HeadingSpace(level), 0, _metrics.HeadingSpaceBelow);
        AddInlines(text.Inlines, heading.Inlines, text.FontSize);
        ApplyDirection(text, heading.Inlines);
        return text;
    }

    private TextBlock RenderParagraph(ParagraphBlock paragraph)
    {
        var text = NewTextBlock();
        AddInlines(text.Inlines, paragraph.Inlines, text.FontSize);
        ApplyDirection(text, paragraph.Inlines);
        return text;
    }

    private UIElement RenderCodeFence(CodeFenceBlock code)
        // The chat renderer's CodeBlock returns the diagram component before it
        // builds any of the code chrome, which is why a rendered mermaid fence
        // has no copy button in the reference either.
        => _metrics.RendersMermaid && Services.MermaidDiagrams.IsDiagramFence(code.Language)
            ? new MermaidBlock(code.Code.TrimEnd(), _metrics, IsStreaming)
            : new CodeBlockCard(code, _metrics, ShellCommandRequested, TerminalOpenRequested);

    /// <summary>Display math via pinned KaTeX, with native geometry while the engine loads.</summary>
    private UIElement RenderMath(MathBlock math)
    {
        var path = TryRenderLatex(math.Latex, WpfMath.Rendering.WpfTeXEnvironment.Create(
                XamlMath.TexStyle.Display, scale: 20.0), 20.0);

        var border = new Border
        {
            Child = new KatexBlock(math.Latex, true, _metrics.BodySize, path),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 2, 8, 2),
        };
        TextSelectionScope.SetCopyText(border, math.Latex);
        return border;
    }

    private Path? TryRenderLatex(string latex, XamlMath.TexEnvironment environment, double scale)
    {
        try
        {
            var formula = WpfMath.Parsers.WpfTeXFormulaParser.Instance.Parse(latex);
            var geometry = WpfMath.Rendering.WpfTeXFormulaExtensions.RenderToGeometry(formula, environment, scale);
            var path = new Path
            {
                Data = geometry,
                Stretch = Stretch.None,
                SnapsToDevicePixels = false,
            };
            path.SetResourceReference(Shape.FillProperty, "Text100Brush");
            return path;
        }
        catch (Exception)
        {
            // WpfMath throws its own parser/render exception types for bad input;
            // any failure just falls back to showing the source.
            return null;
        }
    }

    private UIElement RenderQuote(QuoteBlock quote)
    {
        var inner = new StackPanel();
        var blocks = quote.Blocks ?? [new ParagraphBlock(quote.Inlines)];
        for (int i = 0; i < blocks.Count; i++)
        {
            if (RenderBlock(blocks[i]) is not FrameworkElement element)
            {
                continue;
            }

            if (i < blocks.Count - 1)
            {
                element.Margin = WithBottom(element.Margin, element.Margin.Bottom + _metrics.BlockGap);
            }

            // A quote paints its own prose muted, however its blocks are built.
            if (element is TextBlock prose)
            {
                prose.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
            }

            inner.Children.Add(element);
        }

        var border = new Border
        {
            Child = inner,
            BorderThickness = new Thickness(_metrics.QuoteRuleThickness, 0, 0, 0),
            Padding = new Thickness(_metrics.QuoteTextInset, 0, 0, 0),
            Margin = new Thickness(_metrics.QuoteOuterInset, 0, 0, 0),
        };
        border.SetResourceReference(Border.BorderBrushProperty, _metrics.QuoteRuleKey);
        return border;
    }

    private UIElement RenderList(ListBlock list)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, _metrics.ListSpaceBelow) };
        int ordinal = list.Start;

        for (int index = 0; index < list.Items.Count; index++)
        {
            var item = list.Items[index];
            var row = new Grid
            {
                Margin = new Thickness(_metrics.ListInset, index == 0 ? 0 : _metrics.ListItemGap, 0, 0),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            row.Children.Add(BuildMarker(list, item, ref ordinal));

            var content = new StackPanel();
            Grid.SetColumn(content, 1);

            if (item.Inlines.Count > 0 || item.ChildBlocks is null or { Count: 0 })
            {
                var text = NewTextBlock();
                AddInlines(text.Inlines, item.Inlines, text.FontSize);
                ApplyDirection(text, item.Inlines);
                content.Children.Add(text);
            }

            foreach (var child in item.ChildBlocks ?? [])
            {
                if (RenderBlock(child) is not FrameworkElement nested)
                {
                    continue;
                }

                nested.Margin = new Thickness(
                    nested.Margin.Left,
                    nested.Margin.Top + _metrics.ListItemGap,
                    nested.Margin.Right,
                    nested.Margin.Bottom);
                content.Children.Add(nested);
            }

            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private UIElement BuildMarker(ListBlock list, Core.Markdown.ListItem item, ref int ordinal)
    {
        if (item.IsTask)
        {
            return BuildCheckbox(item.IsChecked);
        }

        var marker = new TextBlock
        {
            Text = list.Ordered ? $"{ordinal++}." : "•",
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = list.Ordered ? 18 : 12,
            TextAlignment = list.Ordered ? TextAlignment.Right : TextAlignment.Left,
            FontSize = _metrics.BodySize,
            LineHeight = _metrics.BodyLineHeight,
        };
        marker.SetResourceReference(TextBlock.FontFamilyProperty, _metrics.FontFamilyKey);
        marker.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        return marker;
    }

    /// <summary>
    /// A task item's checkbox. The reference renders a real disabled checkbox
    /// rather than a glyph, so this draws the same two states as a box and a tick.
    /// </summary>
    private UIElement BuildCheckbox(bool isChecked)
    {
        var box = new Border
        {
            Width = 13,
            Height = 13,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, (_metrics.BodyLineHeight - 13) / 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        box.SetResourceReference(Border.BorderBrushProperty, isChecked ? "AccentBrandBrush" : "BorderStrongBrush");
        if (isChecked)
        {
            box.SetResourceReference(Border.BackgroundProperty, "AccentBrandBrush");
            var tick = new Path
            {
                Data = Geometry.Parse("M20,6 L9,17 L4,12"),
                StrokeThickness = 3,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(2),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            tick.SetResourceReference(Shape.StrokeProperty, "Oncolor100Brush");
            box.Child = tick;
        }

        AutomationProperties.SetName(box, isChecked ? "Checked" : "Unchecked");
        return box;
    }

    private UIElement RenderTable(TableBlock table)
    {
        var grid = new Grid();
        int columnCount = Math.Max(
            table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(static r => r.Count));
        if (columnCount == 0)
        {
            return new Grid();
        }

        bool tiles = _metrics.TableStyle == TableStyle.Tiles;
        for (int c = 0; c < columnCount; c++)
        {
            // Both renderers give the table the full column width (min-w-full,
            // and width:100%), so the last column takes the slack. Putting it
            // there rather than in a spacer keeps the row rules running the
            // whole way across, which is where the reference's own td borders end.
            grid.ColumnDefinitions.Add(c == columnCount - 1
                ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 }
                : new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 420 });
        }

        // The reference hides a header whose every cell is empty.
        bool hasHeader = table.Header.Any(cell => cell.Count > 0 && cell.Any(r => r.Text.Trim().Length > 0));
        int row = 0;
        if (hasHeader)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            AddTableRow(grid, table, row++, table.Header, isHeader: true, tiles);
        }

        foreach (var cells in table.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            AddTableRow(grid, table, row++, cells, isHeader: false, tiles);
        }

        StabiliseColumns(grid, _tableIndex++);

        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, _metrics.TableSpaceBelow),
        };
    }

    /// <summary>
    /// Holds a streaming table's columns at the widths they have already had.
    /// Only applies while the turn is streaming: once it settles the table lays
    /// out from its content, which is where the reference releases the pin too.
    /// </summary>
    private void StabiliseColumns(Grid grid, int index)
    {
        if (!IsStreaming)
        {
            return;
        }

        while (_tableColumns.Count <= index)
        {
            _tableColumns.Add([]);
        }

        var stored = _tableColumns[index];
        if (stored.Length != grid.ColumnDefinitions.Count)
        {
            stored = new double[grid.ColumnDefinitions.Count];
            _tableColumns[index] = stored;
        }

        for (int c = 0; c < grid.ColumnDefinitions.Count; c++)
        {
            if (stored[c] > 0)
            {
                grid.ColumnDefinitions[c].MinWidth = stored[c];
            }
        }

        // Read the widths back once layout has run. A LayoutUpdated handler would
        // fire for every layout pass in the window and keep this grid alive after
        // the next render replaced it, which during streaming is one leak a frame.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            for (int c = 0; c < grid.ColumnDefinitions.Count && c < stored.Length; c++)
            {
                stored[c] = Math.Max(stored[c], grid.ColumnDefinitions[c].ActualWidth);
            }
        });
    }

    private void AddTableRow(
        Grid grid,
        TableBlock table,
        int row,
        IReadOnlyList<IReadOnlyList<InlineRun>> cells,
        bool isHeader,
        bool tiles)
    {
        double pad = _metrics.TableCellPadding;
        for (int c = 0; c < cells.Count && c < grid.ColumnDefinitions.Count; c++)
        {
            var text = NewTextBlock();
            text.FontSize = _metrics.TableFontSize;
            text.LineHeight = _metrics.TableLineHeight;
            text.TextAlignment = (table.Alignments is { } a && c < a.Count ? a[c] : ColumnAlignment.None) switch
            {
                ColumnAlignment.Center => TextAlignment.Center,
                ColumnAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            if (isHeader)
            {
                text.FontWeight = tiles ? FontWeights.Medium : FontWeights.Bold;
            }

            AddInlines(text.Inlines, cells[c], text.FontSize);

            var cell = new Border
            {
                Child = text,
                Padding = tiles
                    ? new Thickness(pad)
                    : new Thickness(0, pad, pad * 2, pad),
                VerticalAlignment = VerticalAlignment.Top,
            };

            if (tiles)
            {
                // Rounded tiles with a 2px gutter, which is border-spacing.
                cell.CornerRadius = new CornerRadius(_metrics.TableTileRadius);
                cell.Margin = new Thickness(_metrics.TableTileGap / 2);
                cell.SetResourceReference(Border.BackgroundProperty, isHeader ? "Tint2Brush" : "Tint1Brush");
            }
            else
            {
                // A single bottom rule, heavier under the header row. Half a pixel
                // survives only without layout rounding, as with the horizontal rule.
                cell.BorderThickness = new Thickness(0, 0, 0, 0.5);
                cell.UseLayoutRounding = false;
                cell.SnapsToDevicePixels = false;
                cell.SetResourceReference(
                    Border.BorderBrushProperty, isHeader ? "ChatTableHeadRuleBrush" : "ChatTableRuleBrush");
            }

            if (isHeader)
            {
                AutomationProperties.SetName(cell, "Column header");
            }

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
    }

    private TextBlock NewTextBlock()
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = _metrics.BodySize,
            LineHeight = _metrics.BodyLineHeight,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, _metrics.FontFamilyKey);
        text.SetResourceReference(TextBlock.ForegroundProperty, BodyBrushKey);
        return text;
    }

    /// <summary>
    /// The reference reads a block's writing direction from its leading text and
    /// stamps <c>dir</c> on the element, so an Arabic or Hebrew paragraph lays
    /// out right to left inside an otherwise left-to-right transcript.
    /// </summary>
    private static void ApplyDirection(TextBlock text, IReadOnlyList<InlineRun> runs)
    {
        if (TextDirection.IsRightToLeft(runs))
        {
            text.FlowDirection = FlowDirection.RightToLeft;
        }
    }

    private void AddInlines(InlineCollection target, IReadOnlyList<InlineRun> runs, double fontSize)
    {
        foreach (var run in runs)
        {
            if (run.HardBreak)
            {
                target.Add(new LineBreak());
                continue;
            }

            if (run.FootnoteLabel is { Length: > 0 } label)
            {
                // The reference prints a reference as a bracketed superscript in
                // the secondary colour, not as a link.
                var marker = new Run($"[{label}]")
                {
                    BaselineAlignment = BaselineAlignment.Superscript,
                    FontSize = fontSize * 0.75,
                };
                marker.SetResourceReference(TextElement.ForegroundProperty, "Text300Brush");
                target.Add(marker);
                continue;
            }

            Inline inline;
            if (run.Kbd)
            {
                target.Add(new InlineUIContainer(BuildKbd(run.Text, fontSize))
                {
                    BaselineAlignment = BaselineAlignment.Center,
                });
                continue;
            }

            if (run.ImageUrl is { Length: > 0 } imageUrl)
            {
                target.Add(new InlineUIContainer(BuildImagePlaceholder(run.Text, imageUrl))
                {
                    BaselineAlignment = BaselineAlignment.Center,
                });
                continue;
            }

            if (run.Math)
            {
                var mathPath = TryRenderLatex(run.Text, WpfMath.Rendering.WpfTeXEnvironment.Create(
                    XamlMath.TexStyle.Text, scale: fontSize + 2), fontSize + 2);
                target.Add(new InlineUIContainer(new KatexBlock(run.Text, false, fontSize, mathPath))
                    { BaselineAlignment = BaselineAlignment.Center });

                continue;
            }

            if (run.Style.HasFlag(InlineStyle.Code))
            {
                inline = new InlineUIContainer(BuildCodeChip(run.Text, fontSize))
                {
                    BaselineAlignment = BaselineAlignment.Center,
                };
            }
            else
            {
                var r = new Run(run.Text);
                if (run.Style.HasFlag(InlineStyle.Bold))
                {
                    r.FontWeight = _metrics.StrongWeight;
                }

                if (run.Style.HasFlag(InlineStyle.Italic))
                {
                    r.FontStyle = FontStyles.Italic;
                }

                if (run.Style.HasFlag(InlineStyle.Strikethrough))
                {
                    r.TextDecorations = TextDecorations.Strikethrough;
                }

                inline = r;
            }

            if (run.LinkUrl is { } url)
            {
                target.Add(BuildLink(inline, url));
            }
            else
            {
                target.Add(inline);
            }
        }
    }

    private Hyperlink BuildLink(Inline content, string url)
    {
        var link = new Hyperlink(content)
        {
            NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null,
            Cursor = Cursors.Hand,
        };
        link.SetResourceReference(TextElement.ForegroundProperty, BodyBrushKey);

        // "decoration-current/40 hover:decoration-current": the underline sits at
        // 40% of the text colour and comes to full strength under the pointer.
        var underline = new TextDecoration
        {
            Location = TextDecorationLocation.Underline,
            PenOffset = 2,
            Pen = new Pen(TryFindResource("LinkUnderlineBrush") as Brush ?? Brushes.Gray, 1),
        };
        link.TextDecorations = [underline];
        link.MouseEnter += (_, _) => underline.Pen =
            new Pen(TryFindResource(BodyBrushKey) as Brush ?? Brushes.Gray, 1);
        link.MouseLeave += (_, _) => underline.Pen =
            new Pen(TryFindResource("LinkUnderlineBrush") as Brush ?? Brushes.Gray, 1);

        if (link.NavigateUri is { IsAbsoluteUri: true })
        {
            link.RequestNavigate += OnLinkNavigate;
        }
        else
        {
            link.RequestNavigate += OnLocalLinkNavigate;
        }

        return link;
    }

    /// <summary>
    /// Inline code. A run that is exactly a hex colour also gets the reference's
    /// swatch in front of it, which is how a palette reads at a glance.
    /// </summary>
    private UIElement BuildCodeChip(string text, double fontSize)
    {
        var codeText = new TextBlock
        {
            Text = text,
            FontSize = _metrics.CodeChipSize * (fontSize / _metrics.BodySize),
            VerticalAlignment = VerticalAlignment.Center,
        };
        codeText.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");
        codeText.SetResourceReference(TextBlock.ForegroundProperty, _metrics.CodeChipForegroundKey);

        UIElement content = codeText;
        if (HexColor.TryParse(text, out var swatchColor))
        {
            var swatch = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(swatchColor),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Border200Brush");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(swatch);
            row.Children.Add(codeText);
            content = row;
        }

        var chip = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(_metrics.CodeChipRadius),
            Padding = _metrics.CodeChipPadding,
            BorderThickness = new Thickness(_metrics.CodeChipBorderThickness),
        };
        chip.SetResourceReference(Border.BackgroundProperty, _metrics.CodeChipFillKey);
        if (_metrics.CodeChipBorderKey is { } key)
        {
            chip.SetResourceReference(Border.BorderBrushProperty, key);
        }

        TextSelectionScope.SetCopyText(chip, text);
        TextSelectionScope.SetCopyKind(chip, TextSelectionScope.CopyKind.InlineCode);
        return chip;
    }

    /// <summary>
    /// A keyboard key. The reference draws it as a monospace chip two pixels
    /// under body size with a heavier bottom border, which is what makes it read
    /// as a key rather than as code.
    /// </summary>
    private UIElement BuildKbd(string keys, double fontSize)
    {
        var text = new TextBlock
        {
            Text = keys,
            FontSize = fontSize - 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        var chip = new Border
        {
            Child = text,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2, 4, 2),
            BorderThickness = new Thickness(1, 1, 1, 2),
        };
        chip.SetResourceReference(Border.BackgroundProperty, _metrics.KbdFillKey ?? "AssistantCodeFillBrush");
        chip.SetResourceReference(Border.BorderBrushProperty, "Border300Brush");
        TextSelectionScope.SetCopyText(chip, keys);
        return chip;
    }

    /// <summary>
    /// An image the model linked to. The reference does not fetch one unasked —
    /// it shows a button that names the request and loads only on a click.
    /// </summary>
    private UIElement BuildImagePlaceholder(string alt, string url)
    {
        var host = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand,
        };
        host.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");
        host.SetResourceReference(Border.BorderBrushProperty, "Border300Brush");

        // The reference's placeholder says only this; the alt text rides the
        // accessible name, where a screen reader can still reach it.
        var label = new TextBlock
        {
            Text = "Show Image",
            FontSize = _metrics.BodySize - 2,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "ChatFontFamily");
        host.Child = label;

        AutomationProperties.SetName(host, alt.Length > 0 ? $"Show image: {alt}" : "Show image");
        host.MouseLeftButtonUp += (_, _) =>
        {
            if (ImageLoadRequested?.Invoke(url) == true)
            {
                ShowImage(host, url, alt);
            }
        };

        TextSelectionScope.SetCopyText(host, alt.Length > 0 ? $"![{alt}]({url})" : url);
        return host;
    }

    private static void ShowImage(Border host, string url, string alt)
    {
        // A remote image downloads in the background, so failure arrives as an
        // event rather than an exception. Both paths land on the same message:
        // a button that silently does nothing is the one outcome to avoid.
        void Failed()
        {
            host.Child = new TextBlock
            {
                Text = "Image could not be loaded",
                FontSize = 13,
            };
            AutomationProperties.SetName(host, "Image could not be loaded");
        }

        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url, UriKind.Absolute);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.DownloadFailed += (_, _) => Failed();
            bitmap.DecodeFailed += (_, _) => Failed();
            host.Child = new Image
            {
                Source = bitmap,
                MaxWidth = 480,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
            };
            host.Padding = new Thickness(0);
            host.Cursor = Cursors.Arrow;
            AutomationProperties.SetName(host, alt.Length > 0 ? alt : "Image");
        }
        catch (Exception ex) when (ex is UriFormatException or NotSupportedException or
                                       System.IO.IOException or InvalidOperationException)
        {
            Failed();
        }
    }

    /// <summary>
    /// The fence tags the Run button appears on. `bash` is the one the harness
    /// prompt asks for; the rest are what people actually type.
    /// </summary>
    internal static bool IsShellLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "bash" or "sh" or "shell" or "zsh" or "console" or "powershell" or "pwsh" or "ps1"
            or "cmd" or "bat" or "batch" => true,
        _ => false,
    };

    /// <summary>
    /// Splits a link target into a path and an optional 1-based line, so
    /// `src/Foo.cs:42` opens at the line. A Windows drive letter is not a line
    /// suffix, so only a trailing all-digit segment counts.
    /// </summary>
    internal static (string Path, int? Line) SplitFileTarget(string target)
    {
        var colon = target.LastIndexOf(':');
        if (colon > 1 && colon < target.Length - 1 &&
            int.TryParse(target[(colon + 1)..], out var line) && line > 0)
        {
            return (target[..colon], line);
        }

        return (target, null);
    }

    private void OnLocalLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (FileActivationRequested is null || WorkingDirectory is not { Length: > 0 } root)
        {
            return;
        }

        var (path, line) = SplitFileTarget(Uri.UnescapeDataString(e.Uri.OriginalString));
        if (path.Length == 0)
        {
            return;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
            FileActivationRequested(full, line);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.PathTooLongException
                                       or NotSupportedException)
        {
            // A link the model wrote is not a path this filesystem accepts;
            // there is nothing to open and nothing to report.
        }
    }

    private static void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is { IsAbsoluteUri: true } uri && uri.Scheme is "http" or "https")
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No browser association; nothing sensible to do.
            }
        }

        e.Handled = true;
    }

    internal static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard briefly locked by another process; ignore.
        }
    }

    /// <summary>
    /// Puts a message on the clipboard in both flavours the reference offers
    /// (its copy action asks for "plain_html"): the plain text a terminal or a
    /// code editor will take, and the HTML a rich editor will, so a pasted
    /// message keeps its headings, lists, links and code.
    /// </summary>
    internal static void TrySetClipboard(string text, string markdown)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Text, text);
            if (!string.IsNullOrWhiteSpace(markdown))
            {
                data.SetData(DataFormats.Html, CfHtml(Core.Markdown.MarkdownHtml.RenderMarkdown(markdown)));
            }

            Clipboard.SetDataObject(data, copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard briefly locked by another process; ignore.
        }
    }

    /// <summary>
    /// An HTML fragment wrapped in the CF_HTML envelope Windows requires. Its
    /// four offsets are counted in UTF-8 bytes, not characters, which is what
    /// makes a fragment holding anything above ASCII paste as itself.
    /// </summary>
    private static string CfHtml(string fragment)
    {
        const string header =
            "Version:0.9\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\n" +
            "StartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        const string open = "<html><body><!--StartFragment-->";
        const string close = "<!--EndFragment--></body></html>";

        // The header is fixed-width once formatted, so its length is known
        // before the offsets it carries are.
        var headerLength = Encoding.UTF8.GetByteCount(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(open);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(close);

        return string.Format(
                   System.Globalization.CultureInfo.InvariantCulture,
                   header, startHtml, endHtml, startFragment, endFragment)
               + open + fragment + close;
    }
}

/// <summary>A <c>#rrggbb</c> literal, which the reference draws a swatch for.</summary>
internal static class HexColor
{
    internal static bool TryParse(string text, out Color color)
    {
        color = default;
        var trimmed = text.Trim();
        if (trimmed.Length != 7 || trimmed[0] != '#')
        {
            return false;
        }

        for (int i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
            {
                return false;
            }
        }

        color = Color.FromRgb(
            byte.Parse(trimmed.AsSpan(1, 2), NumberStyles.HexNumber),
            byte.Parse(trimmed.AsSpan(3, 2), NumberStyles.HexNumber),
            byte.Parse(trimmed.AsSpan(5, 2), NumberStyles.HexNumber));
        return true;
    }
}
