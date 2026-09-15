using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Controls;

/// <summary>
/// The Chat surface's thinking cell, ported from Claude Code Desktop's conversation
/// renderer: the disclosure header (chunk <c>c3e2391e3</c>, its status pill), the
/// thinking cell and its clamp (the same chunk, findable by <c>"Show more"</c> /
/// <c>aWpBzjCXKS</c>), and the timeline row they sit in (chunk <c>c7e6c37f6</c>).
///
/// The reference's shape, which this keeps:
/// <list type="bullet">
/// <item>a 20px gutter with a 1px rail and the thinking icon, 8px spacers above
/// and below;</item>
/// <item>the body rendered as markdown, one heading level down, in the muted body
/// colour;</item>
/// <item>while the turn streams, the body is visible and never clamped;</item>
/// <item>once it settles, the body collapses behind a header reading
/// "Thought for …", closed to begin with;</item>
/// <item>inside the cell, content over 200px tall is clipped under a 40px fade
/// with a Show more / Show less button that appears on hover.</item>
/// </list>
/// The one thing not reproduced is the reference's icon: it is a glyph of
/// Anthropic's own icon font (Anthropicons, <c>ExtendedThinking</c> at U+E068),
/// which this app cannot ship, so the gutter carries lucide's sparkles at the same
/// size, weight and colour.
/// </summary>
public sealed class ThinkingCell : ContentControl
{
    // Geometry, all from the reference's classes (Tailwind spacing = 0.25rem).
    private const double GutterWidth = 20;      // w-[20px]
    private const double SpacerHeight = 8;      // h-[8px] above and below the row
    private const double IconSize = 16;         // Icon size="md"
    private const double ContentTopPad = 4;     // pt-1 on the cell body
    private const double RailTopGap = 4;        // mt-1 under the icon
    private const double BodyInset = 10;        // px-2.5 on the timeline text
    private const double ClampHeight = 200;     // max-height:200px when collapsed
    private const double FadeHeight = 40;       // h-10 gradient over the clip
    private const double PillFontSize = 14;     // text-sm
    private const double ShowMoreFontSize = 12; // text-xs
    private const double CollapsedShowMoreOpacity = 0;
    private const double ShowMoreOpacity = 0.8; // text-text-500/80

    /// <summary>max-height transition: <c>duration-300 ease-out</c>.</summary>
    private static readonly Duration ClampDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>The reference's ease-out, <c>cubic-bezier(0, 0, .2, 1)</c>.</summary>
    private static readonly CubicBezierEase ClampEase = new(0, 0, 0.2, 1);

    /// <summary>Caret rotation and reveal: <c>transition-all duration-200</c>.</summary>
    private static readonly Duration CaretDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>The Show more button's bare <c>transition</c>: the 150ms default.</summary>
    private static readonly Duration RevealDuration = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The timeline's own disclosure: the reference animates height and opacity over
    /// its <c>COLLAPSE_EXPAND</c> (0.2s) with <c>SNAPPY_OUT</c>.
    /// </summary>
    private static readonly Duration DisclosureDuration = TimeSpan.FromMilliseconds(200);

    private static readonly CubicBezierEase SnappyOut = new(0.19, 1, 0.22, 1);

    private static readonly Geometry CaretGlyph =
        Geometry.Parse("M6,9 L12,15 L18,9");

    // lucide "sparkles" on the same 24×24 grid as the transcript's other icons.
    private static readonly Geometry ThinkingGlyph = Geometry.Parse(
        "M11.5,2.4 L13.1,8.5 A2,2 0 0 0 14.5,9.9 L20.6,11.5 L14.5,13.1 " +
        "A2,2 0 0 0 13.1,14.5 L11.5,20.6 L9.9,14.5 A2,2 0 0 0 8.5,13.1 " +
        "L2.4,11.5 L8.5,9.9 A2,2 0 0 0 9.9,8.5 Z M20,3 V7 M22,5 H18 M4,17 V19 M5,18 H3");

    private readonly Button _header = new();
    private readonly TextBlock _headerLabel = new();
    private readonly Path _caret = new();
    private readonly RotateTransform _caretRotation = new(-90);
    private readonly ClampHost _disclosure = new();
    private readonly Grid _timeline = new();
    private readonly ClampHost _clamp = new();
    private readonly MarkdownView _body = new();
    private readonly Border _fade = new();
    private readonly Button _showMore = new();
    private readonly TextBlock _showMoreLabel = new();

    /// <summary>
    /// The size the chat cell's prose inherits. The reference takes it from the
    /// timeline row rather than from the chat renderer's own 16px body.
    /// </summary>
    private const double InheritedBodySize = 14;

    private ThinkingItem? _item;
    private bool _hovered;
    private bool _headerHovered;
    private bool? _lastClamped;
    private bool? _lastOpen;

    public ThinkingCell()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = BuildVisualTree();

        DataContextChanged += OnDataContextChanged;
        _clamp.ContentHeightChanged += (_, _) => UpdateShowMore();
    }

    private UIElement BuildVisualTree()
    {
        var root = new StackPanel();
        root.Children.Add(BuildHeader());
        _disclosure.Child = BuildTimeline();
        _disclosure.ClipToBounds = true;
        root.Children.Add(_disclosure);
        return root;
    }

    private UIElement BuildHeader()
    {
        _headerLabel.FontSize = PillFontSize;
        _headerLabel.VerticalAlignment = VerticalAlignment.Center;
        _headerLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _headerLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        _caret.Data = CaretGlyph;
        _caret.Width = 12;
        _caret.Height = 12;
        _caret.Stretch = Stretch.Uniform;
        _caret.StrokeThickness = 2.2;
        _caret.StrokeStartLineCap = PenLineCap.Round;
        _caret.StrokeEndLineCap = PenLineCap.Round;
        _caret.StrokeLineJoin = PenLineJoin.Round;
        _caret.VerticalAlignment = VerticalAlignment.Center;
        _caret.Opacity = 0;
        _caret.RenderTransformOrigin = new Point(0.5, 0.5);
        _caret.RenderTransform = _caretRotation;
        _caret.SetResourceReference(Shape.StrokeProperty, "Text500Brush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_headerLabel);
        row.Children.Add(new FrameworkElement { Width = 8 });   // gap-2
        row.Children.Add(_caret);

        _header.Content = row;
        _header.Padding = new Thickness(0, 4, 0, 4);             // py-1
        _header.HorizontalAlignment = HorizontalAlignment.Left;
        _header.HorizontalContentAlignment = HorizontalAlignment.Left;
        _header.Background = Brushes.Transparent;
        _header.BorderThickness = new Thickness(0);
        _header.Cursor = Cursors.Hand;
        _header.Template = TransparentButtonTemplate();
        _header.Click += (_, _) => ToggleExpanded();
        _header.MouseEnter += (_, _) => { _headerHovered = true; UpdateHeaderChrome(); };
        _header.MouseLeave += (_, _) => { _headerHovered = false; UpdateHeaderChrome(); };
        _header.GotKeyboardFocus += (_, _) => UpdateHeaderChrome();
        _header.LostKeyboardFocus += (_, _) => UpdateHeaderChrome();
        return _header;
    }

    private UIElement BuildTimeline()
    {
        _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _timeline.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SpacerHeight) });
        _timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _timeline.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SpacerHeight) });

        // The gutter: the icon, then the 1px rail filling the rest of the row.
        // A standalone cell is both the first and the last item of its group, so
        // the reference draws no rail in the spacers above and below it.
        var icon = new Path
        {
            Data = ThinkingGlyph,
            Width = IconSize,
            Height = IconSize,
            Stretch = Stretch.Uniform,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        icon.SetResourceReference(Shape.StrokeProperty, "Text500Brush");

        var rail = new Border
        {
            Width = 1,
            Margin = new Thickness(0, RailTopGap, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        rail.SetResourceReference(Border.BackgroundProperty, "Border300Brush");

        var gutter = new Grid();
        gutter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        gutter.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        gutter.Children.Add(icon);
        Grid.SetRow(rail, 1);
        gutter.Children.Add(rail);
        Grid.SetRow(gutter, 1);
        _timeline.Children.Add(gutter);

        // The body: markdown under the clamp, the fade over its bottom edge, and
        // the Show more button below both.
        _body.BodyBrushKey = "Text300Brush";
        _body.HeadingLevelOffset = 1;

        _clamp.Child = _body;
        _clamp.ClipToBounds = true;

        _fade.Height = FadeHeight;
        _fade.VerticalAlignment = VerticalAlignment.Bottom;
        _fade.IsHitTestVisible = false;
        _fade.Visibility = Visibility.Collapsed;
        _fade.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
        _fade.OpacityMask = new LinearGradientBrush(
            Colors.Transparent, Color.FromArgb(255, 0, 0, 0), new Point(0, 0), new Point(0, 1));

        var clipped = new Grid();
        clipped.Children.Add(_clamp);
        clipped.Children.Add(_fade);

        _showMoreLabel.FontSize = ShowMoreFontSize;
        _showMoreLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        _showMore.Content = _showMoreLabel;
        _showMore.Template = TransparentButtonTemplate();
        _showMore.Background = Brushes.Transparent;
        _showMore.BorderThickness = new Thickness(0);
        _showMore.Padding = new Thickness(0, 2, 0, 2);
        _showMore.HorizontalAlignment = HorizontalAlignment.Left;
        _showMore.Cursor = Cursors.Hand;
        _showMore.Opacity = CollapsedShowMoreOpacity;
        _showMore.Visibility = Visibility.Collapsed;
        _showMore.Click += (_, _) => ToggleShowMore();
        _showMore.GotKeyboardFocus += (_, _) => UpdateShowMore();
        _showMore.LostKeyboardFocus += (_, _) => UpdateShowMore();
        _showMore.MouseEnter += (_, _) => _showMoreLabel.SetResourceReference(
            TextBlock.ForegroundProperty, "Text100Brush");
        _showMore.MouseLeave += (_, _) => _showMoreLabel.SetResourceReference(
            TextBlock.ForegroundProperty, "Text500Brush");

        // The reference's hover group is the body, not the whole cell: hovering the
        // header alone does not reveal the Show more button.
        var body = new StackPanel
        {
            Margin = new Thickness(BodyInset, ContentTopPad, BodyInset, 0),
            Background = Brushes.Transparent,
        };
        body.MouseEnter += (_, _) => { _hovered = true; UpdateShowMore(); };
        body.MouseLeave += (_, _) => { _hovered = false; UpdateShowMore(); };
        body.Children.Add(clipped);
        body.Children.Add(_showMore);
        Grid.SetRow(body, 1);
        Grid.SetColumn(body, 1);
        _timeline.Children.Add(body);

        return _timeline;
    }

    /// <summary>A button that is nothing but its content — no chrome, no hover fill.</summary>
    private static ControlTemplate TransparentButtonTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(0));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetBinding(Border.PaddingProperty,
            new System.Windows.Data.Binding(nameof(Control.Padding))
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        _item = DataContext as ThinkingItem;
        if (_item is not null)
        {
            _item.PropertyChanged += OnItemPropertyChanged;
        }

        ApplyItem();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ThinkingItem.Text):
            case nameof(ThinkingItem.HasText):
            case nameof(ThinkingItem.IsStreaming):
            case nameof(ThinkingItem.IsExpanded):
            case nameof(ThinkingItem.ShowsFullText):
            case nameof(ThinkingItem.Label):
                ApplyItem();
                break;
        }
    }

    private void ApplyItem()
    {
        if (_item is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        // The reference renders nothing at all for a thinking block with no text.
        Visibility = _item.HasText ? Visibility.Visible : Visibility.Collapsed;

        // The reference's chat thinking cell renders with StandardMarkdown at
        // typography:"inherit" — the chat renderer's rules at the cell's own
        // size, not the transcript renderer. The Code surface keeps its own.
        _body.Profile = _item.IsChatCell ? MarkdownProfile.Chat : MarkdownProfile.Code;
        _body.BodySizeOverride = _item.IsChatCell ? InheritedBodySize : double.NaN;
        _body.Markdown = _item.Text;
        _headerLabel.Text = _item.Label;

        // While the turn streams there is no header — the transcript's status line
        // is carrying the live label — and the body is shown whole. Once it
        // settles the header takes over and the body hides behind it.
        var settled = !_item.IsStreaming;
        _header.Visibility = settled ? Visibility.Visible : Visibility.Collapsed;
        UpdateDisclosure(open: !settled || _item.IsExpanded);

        UpdateHeaderChrome();
        UpdateClamp();
        UpdateShowMore();
    }

    /// <summary>
    /// Shows or hides the timeline the way the reference does: height and opacity
    /// over 200ms, but only when the state actually changes — a cell that renders
    /// already closed must not play an opening it never had.
    /// </summary>
    private void UpdateDisclosure(bool open)
    {
        if (_lastOpen == open)
        {
            return;
        }

        var first = _lastOpen is null;
        _lastOpen = open;

        if (first)
        {
            _disclosure.BeginAnimation(ClampHost.ClampLimitProperty, null);
            _disclosure.ClampLimit = open ? double.PositiveInfinity : 0;
            _disclosure.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _disclosure.Opacity = open ? 1 : 0;
            return;
        }

        if (open)
        {
            // A collapsed element is never measured, so its height is only known
            // after one layout pass with it visible: show it clamped to nothing,
            // then animate to the height that pass reported.
            _disclosure.BeginAnimation(ClampHost.ClampLimitProperty, null);
            _disclosure.ClampLimit = 0;
            _disclosure.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_lastOpen != true)
                    {
                        return;
                    }

                    var grow = new DoubleAnimation(0, _disclosure.ContentHeight, DisclosureDuration)
                    {
                        EasingFunction = SnappyOut,
                    };
                    grow.Completed += (_, _) =>
                    {
                        if (_lastOpen != true)
                        {
                            return;
                        }

                        _disclosure.BeginAnimation(ClampHost.ClampLimitProperty, null);
                        _disclosure.ClampLimit = double.PositiveInfinity;
                    };
                    _disclosure.BeginAnimation(ClampHost.ClampLimitProperty, grow);
                }),
                DispatcherPriority.Loaded);
            _disclosure.BeginAnimation(OpacityProperty, new DoubleAnimation(1, DisclosureDuration));
            return;
        }

        var shrink = new DoubleAnimation(_disclosure.ContentHeight, 0, DisclosureDuration)
        {
            EasingFunction = SnappyOut,
        };
        shrink.Completed += (_, _) =>
        {
            if (_lastOpen == false)
            {
                _disclosure.Visibility = Visibility.Collapsed;
            }
        };
        _disclosure.BeginAnimation(ClampHost.ClampLimitProperty, shrink);
        _disclosure.BeginAnimation(OpacityProperty, new DoubleAnimation(0, DisclosureDuration));
    }

    private void UpdateHeaderChrome()
    {
        if (_item is null)
        {
            return;
        }

        var open = _item.IsExpanded;
        _caretRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(open ? 0 : -90, CaretDuration));

        // The caret is invisible until the header is hovered or focused.
        var reveal = _headerHovered || _header.IsKeyboardFocusWithin;
        _caret.BeginAnimation(OpacityProperty, new DoubleAnimation(reveal ? 1 : 0, CaretDuration));
        _headerLabel.SetResourceReference(TextBlock.ForegroundProperty,
            _headerHovered ? "Text300Brush" : "Text500Brush");
        _header.SetValue(AutomationProperties.NameProperty, _item.Label);
    }

    private void UpdateClamp()
    {
        if (_item is null)
        {
            return;
        }

        // The clamp only applies to settled thinking; a streaming block grows freely.
        var clamped = !_item.IsStreaming && !_item.ShowsFullText;

        // Only the transition between the two states animates. A cell that opens
        // already clamped must be clamped from its first frame — animating there
        // would play a collapse the reference never shows.
        if (_lastClamped != clamped)
        {
            _lastClamped = clamped;
        }
        else
        {
            _clamp.BeginAnimation(ClampHost.ClampLimitProperty, null);
            _clamp.ClampLimit = clamped ? ClampHeight : double.PositiveInfinity;
            return;
        }

        var target = clamped ? ClampHeight : double.PositiveInfinity;
        if (double.IsPositiveInfinity(target))
        {
            // Animate up to the measured height, then release the limit so later
            // growth is not capped by a stale number.
            var full = _clamp.ContentHeight;
            if (_clamp.ClampLimit < full)
            {
                var grow = new DoubleAnimation(_clamp.ClampLimit, full, ClampDuration)
                {
                    EasingFunction = ClampEase,
                };
                grow.Completed += (_, _) =>
                {
                    _clamp.BeginAnimation(ClampHost.ClampLimitProperty, null);
                    _clamp.ClampLimit = double.PositiveInfinity;
                };
                _clamp.BeginAnimation(ClampHost.ClampLimitProperty, grow);
            }
            else
            {
                _clamp.BeginAnimation(ClampHost.ClampLimitProperty, null);
                _clamp.ClampLimit = double.PositiveInfinity;
            }
        }
        else
        {
            var from = double.IsPositiveInfinity(_clamp.ClampLimit) ? _clamp.ContentHeight : _clamp.ClampLimit;
            var shrink = new DoubleAnimation(from, ClampHeight, ClampDuration)
            {
                EasingFunction = ClampEase,
            };
            _clamp.BeginAnimation(ClampHost.ClampLimitProperty, shrink);
        }
    }

    private void UpdateShowMore()
    {
        if (_item is null)
        {
            return;
        }

        var clampable = !_item.IsStreaming && _clamp.ContentHeight > ClampHeight + 0.5;
        _showMore.Visibility = clampable ? Visibility.Visible : Visibility.Collapsed;
        _fade.Visibility = clampable && !_item.ShowsFullText ? Visibility.Visible : Visibility.Collapsed;
        _showMoreLabel.Text = _item.ShowsFullText ? ShowLess : ShowMore;

        // Collapsed, the button is revealed by hovering the body (or focusing it);
        // expanded, the reference leaves it visible. Its `transition` is the
        // framework default, 150ms.
        var reveal = _item.ShowsFullText || _hovered || _showMore.IsKeyboardFocusWithin;
        _showMore.BeginAnimation(OpacityProperty, new DoubleAnimation(
            reveal ? ShowMoreOpacity : CollapsedShowMoreOpacity, RevealDuration));
        _showMore.IsHitTestVisible = clampable;
    }

    private void ToggleExpanded()
    {
        if (_item is null)
        {
            return;
        }

        _item.IsExpanded = !_item.IsExpanded;
    }

    private void ToggleShowMore()
    {
        if (_item is null)
        {
            return;
        }

        _item.ShowsFullText = !_item.ShowsFullText;
    }

    /// <summary>Reference strings, by their catalogue ids.</summary>
    internal const string ShowMore = "Show more";   // aWpBzjCXKS

    internal const string ShowLess = "Show less";   // qyJtWyZ0yt

    /// <summary>True while this cell is showing a clipped body — what --ui-selftest measures.</summary>
    internal bool IsClamped =>
        _item is { IsStreaming: false, ShowsFullText: false } && _clamp.ContentHeight > ClampHeight;

    /// <summary>
    /// The laid-out numbers <c>--ui-selftest</c> checks, so a style or container
    /// change that moves the cell fails the same way a wrong literal would.
    /// </summary>
    internal IReadOnlyDictionary<string, double> MeasuredGeometry() =>
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["thinking gutter width"] = _timeline.ColumnDefinitions[0].ActualWidth,
            ["thinking spacer height"] = _timeline.RowDefinitions[0].ActualHeight,
            ["thinking clamp height"] = _clamp.ActualHeight,
            ["thinking fade height"] = _fade.ActualHeight,
        };

    /// <summary>
    /// Hosts the cell body at its natural height while showing only
    /// <see cref="ClampLimit"/> of it — the reference clamps with a max-height
    /// transition, which measures the content unconstrained the whole time.
    /// </summary>
    internal sealed class ClampHost : Decorator
    {
        public static readonly DependencyProperty ClampLimitProperty = DependencyProperty.Register(
            nameof(ClampLimit), typeof(double), typeof(ClampHost),
            new FrameworkPropertyMetadata(double.PositiveInfinity,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

        private double _contentHeight;

        /// <summary>Raised after a layout pass changed the unclamped content height.</summary>
        public event EventHandler? ContentHeightChanged;

        public double ClampLimit
        {
            get => (double)GetValue(ClampLimitProperty);
            set => SetValue(ClampLimitProperty, value);
        }

        /// <summary>The height the content wants, whatever is currently shown.</summary>
        public double ContentHeight => _contentHeight;

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is not { } child)
            {
                return new Size(0, 0);
            }

            child.Measure(new Size(constraint.Width, double.PositiveInfinity));
            var full = child.DesiredSize.Height;
            if (Math.Abs(full - _contentHeight) > 0.5)
            {
                _contentHeight = full;
                // Raising this during measure would re-enter layout, so hand it to
                // the dispatcher and let the current pass finish first.
                Dispatcher.BeginInvoke(
                    new Action(() => ContentHeightChanged?.Invoke(this, EventArgs.Empty)),
                    DispatcherPriority.Loaded);
            }

            var shown = double.IsPositiveInfinity(ClampLimit) ? full : Math.Min(full, ClampLimit);
            return new Size(child.DesiredSize.Width, shown);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            // Arranged at its full height and clipped by the host, so clamping never
            // reflows the text.
            Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(arrangeSize.Height, _contentHeight)));
            return arrangeSize;
        }
    }
}
