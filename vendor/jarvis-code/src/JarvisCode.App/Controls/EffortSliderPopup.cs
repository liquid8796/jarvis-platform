using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// The reference app's effort selector: a 220px popover above the composer's
/// effort chip holding an "Effort" header with the current level (morphing as
/// the slider moves) and a hover "?" help card, a Faster/Smarter footnote row,
/// and a stepped slider — drag or click commits a level and keeps the popover
/// open, digit keys 1-9 commit and close, Esc closes. Ctrl+Shift+E toggles it
/// from anywhere, including from inside the popover itself.
/// </summary>
public sealed class EffortSliderPopup
{
    private readonly Popup _popup;
    private readonly Func<string> _currentName;
    private readonly Func<IReadOnlyList<string>> _levelNames;
    private readonly Action<string> _commit;
    private readonly Action? _onClosed;
    private readonly bool _usesStandardLevels;
    private DateTime _closedAt = DateTime.MinValue;
    private MorphLabel? _label;
    private EffortSlider? _slider;
    private IReadOnlyList<string> _activeLevels = EffortLevels.Names;

    public EffortSliderPopup(UIElement placementTarget, Func<string> currentName, Action<string> commit,
        Action? onClosed = null)
        : this(placementTarget, currentName, () => EffortLevels.Names, commit, onClosed,
            usesStandardLevels: true)
    {
    }

    /// <summary>
    /// Builds the same selector over a provider-defined ladder. The source is read each time the
    /// popup opens, so a caller can replace a cached live snapshot without recreating the popup.
    /// Labels are also the values passed to <paramref name="commit"/>; opaque provider keys stay
    /// with the caller, which owns the mapping between what the page shows and what it sends.
    /// </summary>
    public EffortSliderPopup(
        UIElement placementTarget,
        Func<string> currentName,
        IReadOnlyList<string> levelNames,
        Action<string> commit,
        Action? onClosed = null)
        : this(placementTarget, currentName, () => levelNames, commit, onClosed,
            usesStandardLevels: false)
    {
    }

    public EffortSliderPopup(
        UIElement placementTarget,
        Func<string> currentName,
        Func<IReadOnlyList<string>> levelNames,
        Action<string> commit,
        Action? onClosed = null)
        : this(placementTarget, currentName, levelNames, commit, onClosed, usesStandardLevels: false)
    {
    }

    private EffortSliderPopup(
        UIElement placementTarget,
        Func<string> currentName,
        Func<IReadOnlyList<string>> levelNames,
        Action<string> commit,
        Action? onClosed,
        bool usesStandardLevels)
    {
        _currentName = currentName;
        _levelNames = levelNames;
        _commit = commit;
        _onClosed = onClosed;
        _usesStandardLevels = usesStandardLevels;
        _popup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Custom,
            StaysOpen = false,
            AllowsTransparency = true,
            // Above the chip, right edges aligned, 8px gap — the reference's
            // side "top" / align "end" / sideOffset 8.
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                new CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 8),
                    PopupPrimaryAxis.Vertical),
            ],
        };
        _popup.Closed += (_, _) =>
        {
            _closedAt = DateTime.UtcNow;
            _onClosed?.Invoke();
        };
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>
    /// Opens, or closes when open. A click on the chip while the popover is up
    /// first closes it through StaysOpen=false, so a reopen racing in within
    /// the same click is swallowed instead of bouncing the popover back open.
    /// </summary>
    public void Toggle()
    {
        if (_popup.IsOpen)
        {
            Close();
            return;
        }

        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250)
        {
            return;
        }

        _popup.Child = Build();
        _popup.IsOpen = true;
        _popup.Dispatcher.BeginInvoke(() => (_popup.Child as FrameworkElement)?.Focus());
    }

    public void Close() => _popup.IsOpen = false;

    private FrameworkElement Build()
    {
        var supplied = _levelNames();
        _activeLevels = supplied is { Count: > 0 }
            ? [.. supplied.Where(static level => !string.IsNullOrWhiteSpace(level))
                .Distinct(StringComparer.OrdinalIgnoreCase)]
            : EffortLevels.Names;
        if (_activeLevels.Count == 0)
        {
            _activeLevels = EffortLevels.Names;
        }

        var selected = IndexOf(_currentName());

        var card = new Border
        {
            Width = 220,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Focusable = true,
            FocusVisualStyle = null,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        System.Windows.Automation.AutomationProperties.SetName(card, EffortLevels.Header);

        var stack = new StackPanel();
        card.Child = stack;

        // Header: "Effort" · current level (morphs while scrubbing) · "?" help.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var caption = Text(EffortLevels.Header, 12.5, "Text400Brush");
        caption.Margin = new Thickness(0, 0, 4, 0);
        title.Children.Add(caption);
        _label = new MorphLabel(_activeLevels, _activeLevels[selected]);
        title.Children.Add(_label);
        header.Children.Add(title);
        var help = BuildHelpButton();
        Grid.SetColumn(help, 1);
        header.Children.Add(help);
        stack.Children.Add(header);

        // Faster … Smarter over the stepped slider.
        var ends = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        ends.ColumnDefinitions.Add(new ColumnDefinition());
        ends.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ends.Children.Add(Text(EffortLevels.Faster, 11, "Text400Brush"));
        var smarter = Text(EffortLevels.Smarter, 11, "Text400Brush");
        Grid.SetColumn(smarter, 1);
        ends.Children.Add(smarter);
        stack.Children.Add(ends);

        _slider = new EffortSlider(_activeLevels, selected);
        _slider.Preview += index => _label!.SetText(_activeLevels[index]);
        _slider.Committed += index => _commit(_activeLevels[index]);
        _slider.Cancelled += () => _slider!.ResetTo(IndexOf(_currentName()));
        stack.Children.Add(_slider);

        card.PreviewKeyDown += (_, e) => HandleKey(e);
        return card;
    }

    /// <summary>
    /// The popover's keys. Also called from the main window's key handler:
    /// after a mouse interaction the popup's hwnd can lose Win32 focus to the
    /// window, and the reference intercepts these at the window level while a
    /// composer menu is open.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!_popup.IsOpen || _slider is null)
        {
            return false;
        }

        var action = EffortKeys.Decide(e.Key, Keyboard.Modifiers, _activeLevels.Count);
        switch (action.Kind)
        {
            case EffortKeys.Kind.Close:
                Close();
                break;
            case EffortKeys.Kind.Select:
                _slider.MoveTo(action.Index);
                Close();
                break;
            case EffortKeys.Kind.MoveTo:
                _slider.MoveTo(action.Index);
                break;
            case EffortKeys.Kind.Step:
                _slider.MoveTo(_slider.Index + action.Index);
                break;
            case EffortKeys.Kind.Swallow:
                break;
            default:
                return false;
        }

        e.Handled = true;
        return true;
    }

    private int IndexOf(string? name)
    {
        if (_usesStandardLevels)
        {
            return EffortLevels.IndexOf(name);
        }

        for (var i = 0; i < _activeLevels.Count; i++)
        {
            if (string.Equals(_activeLevels[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static FrameworkElement BuildHelpButton()
    {
        var glyph = Text("?", 11, "Text400Brush");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var help = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            Child = glyph,
        };
        help.MouseEnter += (_, _) => glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        help.MouseLeave += (_, _) => glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");

        var body = new StackPanel { Width = 200 };
        var tipTitle = Text(EffortLevels.HelpTitle, 11.5, "Text100Brush", semibold: true);
        tipTitle.Margin = new Thickness(0, 0, 0, 4);
        body.Children.Add(tipTitle);
        var tipBody = Text(EffortLevels.HelpBody, 12, "Text400Brush");
        tipBody.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(tipBody);

        var tip = new ToolTip
        {
            Content = body,
            Placement = PlacementMode.Top,
            Padding = new Thickness(10),
        };
        tip.SetResourceReference(Control.BackgroundProperty, "Bg000Brush");
        tip.SetResourceReference(Control.BorderBrushProperty, "BorderMidBrush");
        help.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(help, 200);
        System.Windows.Automation.AutomationProperties.SetName(help, "About effort");
        return help;
    }

    private static TextBlock Text(string text, double size, string brushKey, bool semibold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>
    /// The reference's level label morph: the outgoing text slides 0.55em away
    /// and fades while the incoming one slides in from the opposite side, the
    /// direction following whether the level went up or down the ladder.
    /// </summary>
    private sealed class MorphLabel : Grid
    {
        private readonly IReadOnlyList<string> _levels;
        private TextBlock _current;
        private string _text;
        private int _index;

        public MorphLabel(IReadOnlyList<string> levels, string text)
        {
            _levels = levels;
            _text = text;
            _index = IndexOf(text);
            _current = Make(text);
            Children.Add(_current);
        }

        public void SetText(string text)
        {
            if (text == _text)
            {
                return;
            }

            var index = IndexOf(text);
            var direction = index >= _index ? 1 : -1;
            _text = text;
            _index = index;

            var old = _current;
            Fly(old, toY: -7 * direction, toOpacity: 0, onDone: () => Children.Remove(old));

            _current = Make(text);
            ((TranslateTransform)_current.RenderTransform).Y = 7 * direction;
            _current.Opacity = 0;
            Children.Add(_current);
            Fly(_current, toY: 0, toOpacity: 1, onDone: null);
        }

        private int IndexOf(string text)
        {
            for (var i = 0; i < _levels.Count; i++)
            {
                if (string.Equals(_levels[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 0;
        }

        private static TextBlock Make(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                RenderTransform = new TranslateTransform(),
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            return block;
        }

        private static void Fly(TextBlock block, double toY, double toOpacity, Action? onDone)
        {
            var duration = TimeSpan.FromMilliseconds(200);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var slide = new DoubleAnimation(toY, duration) { EasingFunction = ease };
            var fade = new DoubleAnimation(toOpacity, duration) { EasingFunction = ease };
            if (onDone is not null)
            {
                fade.Completed += (_, _) => onDone();
            }

            block.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slide);
            block.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}

/// <summary>
/// The stepped slider itself: a rounded track with an accent fill up to the
/// handle, one 3px dot per level (hover names it), and a white handle that
/// snaps between stops, growing slightly while dragged. Click or drag anywhere
/// on the track; the value commits on release.
/// </summary>
public sealed class EffortSlider : Canvas
{
    private const double TrackHeight = 24;
    private const double ThumbWidth = 20;

    private static readonly SolidColorBrush FillBrush = Frozen(Color.FromRgb(0x2A, 0x78, 0xD6));
    private static readonly SolidColorBrush DotBrush = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush ThumbRing = Frozen(Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

    private readonly IReadOnlyList<string> _levels;
    private readonly Border _track;
    private readonly Rectangle _fill;
    private readonly Border _thumb;
    private readonly ScaleTransform _thumbScale = new(1, 1);
    private readonly List<Ellipse> _dots = [];
    private readonly List<Rectangle> _hits = [];
    private bool _dragging;

    public event Action<int>? Preview;
    public event Action<int>? Committed;

    /// <summary>A drag that lost mouse capture without a release — the owner reverts the preview.</summary>
    public event Action? Cancelled;

    public int Index { get; private set; }

    public EffortSlider(IReadOnlyList<string> levels, int index)
    {
        _levels = levels;
        Index = Math.Clamp(index, 0, levels.Count - 1);
        Height = 28;
        Background = Brushes.Transparent;

        _track = new Border { Height = TrackHeight, CornerRadius = new CornerRadius(4) };
        _track.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");
        SetTop(_track, 2);
        Children.Add(_track);

        _fill = new Rectangle { Height = TrackHeight, RadiusX = 4, RadiusY = 4, Fill = FillBrush };
        SetTop(_fill, 2);
        Children.Add(_fill);

        foreach (var level in levels)
        {
            var hit = new Rectangle { Width = 16, Height = 28, Fill = Brushes.Transparent, ToolTip = level };
            SetTop(hit, 0);
            _hits.Add(hit);
            Children.Add(hit);
            var dot = new Ellipse { Width = 3, Height = 3, Fill = DotBrush, IsHitTestVisible = false };
            SetTop(dot, 12.5);
            _dots.Add(dot);
            Children.Add(dot);
        }

        _thumb = new Border
        {
            Width = ThumbWidth,
            Height = TrackHeight,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.White,
            BorderThickness = new Thickness(1),
            BorderBrush = ThumbRing,
            RenderTransform = _thumbScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.15 },
        };
        SetTop(_thumb, 2);
        Children.Add(_thumb);

        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => EndDrag(commit: false);
    }

    /// <summary>Moves to a level (clamped), raising Preview and committing — the keyboard path.</summary>
    public void MoveTo(int index)
    {
        SetIndex(Math.Clamp(index, 0, _levels.Count - 1));
        Committed?.Invoke(Index);
    }

    private bool SetIndex(int index)
    {
        if (index == Index)
        {
            return false;
        }

        Index = index;
        Preview?.Invoke(index);
        Reposition(animate: true);
        return true;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        SetIndex(EffortSliderMath.NearestStop(e.GetPosition(this).X, ActualWidth, _levels.Count));
        ScaleThumb(1.12);
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            SetIndex(EffortSliderMath.NearestStop(e.GetPosition(this).X, ActualWidth, _levels.Count));
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            // Commit before releasing capture: releasing raises LostMouseCapture,
            // whose EndDrag would otherwise swallow this one.
            EndDrag(commit: true);
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ScaleThumb(1.0);
        if (commit)
        {
            Committed?.Invoke(Index);
        }
        else
        {
            Cancelled?.Invoke();
        }
    }

    /// <summary>Puts the handle back on a level without committing — the cancel path.</summary>
    public void ResetTo(int index) => SetIndex(Math.Clamp(index, 0, _levels.Count - 1));

    private void ScaleThumb(double scale)
    {
        var duration = TimeSpan.FromMilliseconds(150);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        _thumbScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = ease });
        _thumbScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = ease });
    }

    private void Layout()
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _track.Width = width;
        for (int i = 0; i < _dots.Count; i++)
        {
            var center = EffortSliderMath.StopCenter(i, width, _dots.Count);
            SetLeft(_dots[i], center - 1.5);
            SetLeft(_hits[i], center - 8);
        }

        Reposition(animate: false);
    }

    private void Reposition(bool animate)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var center = EffortSliderMath.ThumbCenter(Index, width, _levels.Count);
        if (!animate)
        {
            _thumb.BeginAnimation(LeftProperty, null);
            _fill.BeginAnimation(WidthProperty, null);
            SetLeft(_thumb, center - ThumbWidth / 2);
            _fill.Width = Math.Max(1, center);
            return;
        }

        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        _thumb.BeginAnimation(LeftProperty,
            new DoubleAnimation(center - ThumbWidth / 2, duration) { EasingFunction = ease });
        _fill.BeginAnimation(WidthProperty,
            new DoubleAnimation(Math.Max(1, center), duration) { EasingFunction = ease });
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// What a key does to the open popover, kept pure for tests: Esc and the
/// Ctrl+Shift+E chord close, a bare digit selects that level and closes,
/// arrows step and commit, Home/End jump — the reference's composer-menu key
/// handling. Everything else falls through to the window.
/// </summary>
internal static class EffortKeys
{
    public enum Kind
    {
        None,
        Close,
        Select,
        MoveTo,
        Step,
        Swallow,
    }

    public readonly record struct Action(Kind Kind, int Index = 0);

    public static Action Decide(Key key, ModifierKeys modifiers, int count)
    {
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (key == Key.Escape || (ctrl && shift && key == Key.E))
        {
            return new(Kind.Close);
        }

        if (modifiers != ModifierKeys.None)
        {
            return new(Kind.None);
        }

        if (DigitOf(key) is { } digit)
        {
            // A digit past the ladder is still the popover's — swallowed, not typed.
            return digit <= count ? new(Kind.Select, digit - 1) : new(Kind.Swallow);
        }

        return key switch
        {
            Key.Left or Key.Down => new(Kind.Step, -1),
            Key.Right or Key.Up => new(Kind.Step, 1),
            Key.Home => new(Kind.MoveTo, 0),
            Key.End => new(Kind.MoveTo, count - 1),
            _ => new(Kind.None),
        };
    }

    private static int? DigitOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };
}

/// <summary>
/// Geometry of the stepped track, kept pure for tests: the handle travels
/// inset by half its width, the stop dots sit inset a hair further, exactly
/// as the reference lays them out.
/// </summary>
internal static class EffortSliderMath
{
    public const double ThumbInset = 10;
    public const double StopInset = 12;

    public static double ThumbCenter(int index, double width, int count)
        => Center(index, width, count, ThumbInset);

    public static double StopCenter(int index, double width, int count)
        => Center(index, width, count, StopInset);

    public static int NearestStop(double x, double width, int count)
    {
        var span = width - 2 * ThumbInset;
        if (count <= 1 || span <= 0)
        {
            return 0;
        }

        var fraction = Math.Clamp((x - ThumbInset) / span, 0, 1);
        return (int)Math.Round(fraction * (count - 1));
    }

    private static double Center(int index, double width, int count, double inset)
    {
        var span = width - 2 * inset;
        if (count <= 1 || span <= 0)
        {
            return inset;
        }

        return inset + Math.Clamp(index, 0, count - 1) * span / (count - 1);
    }
}
