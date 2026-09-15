using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Controls;

/// <summary>
/// The Chat surface's waiting line: one sentence while the answer has not started
/// arriving, and the compaction indicator while history is being summarized.
///
/// Ported from the reference desktop's chat page (<c>ca2ef848d-D8BWZk64.js</c>, its
/// <c>jw</c> text row and <c>Ow</c> compaction row). It deliberately shares nothing
/// with <see cref="TurnStatusLine"/>, which is the Code surface's own component:
/// there is no spinner, no elapsed clock and no token counter here, and the label
/// set and its timings are different.
/// </summary>
public sealed class ChatStatusLine : ContentControl
{
    /// <summary>The reference's <c>animate-fade-in-fast</c>: 100ms, ease-out.</summary>
    internal const int EntranceFadeMs = 100;

    /// <summary>The bar's width transition: <c>duration-300 ease-[cubic-bezier(0,0,0.58,1)]</c>.</summary>
    internal const int ProgressTransitionMs = 300;

    /// <summary>The bar's own sweep: <c>animate-[shimmer_1.5s_infinite]</c>.</summary>
    internal const int ShimmerMs = 1500;

    /// <summary>The reference bar is <c>w-48 h-1</c> — 192 by 4 device-independent pixels.</summary>
    internal const double BarWidth = 192;
    internal const double BarHeight = 4;

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(ChatTurnStatus), typeof(ChatStatusLine),
        new PropertyMetadata(null, OnStatusChanged));

    private readonly TextBlock _label = new()
    {
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly TextBlock _compactingLabel = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _percent = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _barFill = new() { CornerRadius = new CornerRadius(BarHeight / 2), Width = 0, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _barTrack;
    private readonly Rectangle _shimmer = new() { Width = BarWidth, Height = BarHeight, IsHitTestVisible = false };
    private readonly TranslateTransform _shimmerOffset = new();
    private readonly StackPanel _compacting;
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Normal);

    private ChatStatusView _shown;
    private bool _entranceDone;

    public ChatStatusLine()
    {
        Focusable = false;
        Visibility = Visibility.Collapsed;
        // ml-2 pb-1.5 on the reference's own container.
        Margin = new Thickness(8, 0, 0, 6);
        HorizontalAlignment = HorizontalAlignment.Left;

        _label.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        _compactingLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        _percent.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        Typography.SetNumeralAlignment(_percent, FontNumeralAlignment.Tabular);
        _barFill.SetResourceReference(Border.BackgroundProperty, "Text300Brush");

        _shimmer.Fill = new LinearGradientBrush(
            [
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
            ],
            new Point(0, 0.5), new Point(1, 0.5))
        { Transform = _shimmerOffset };

        _barTrack = new Border
        {
            Width = BarWidth,
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarHeight / 2),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid { Children = { _barFill, _shimmer } },
        };
        _barTrack.SetResourceReference(Border.BackgroundProperty, "FieldFillBrush");

        var barRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { _barTrack, Spacer(), _percent },
        };
        _compacting = new StackPanel { Children = { _compactingLabel, barRow } };

        _tick.Tick += (_, _) => Refresh();
    }

    /// <summary>The turn state this line narrates.</summary>
    public ChatTurnStatus? Status
    {
        get => (ChatTurnStatus?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>The clock, so a test can drive the line without waiting.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.Now;

    /// <summary>Recomputes what is shown. Public so a pose can drive it directly.</summary>
    public void Refresh()
    {
        var view = Status?.Describe(Clock()) ?? ChatStatusView.Hidden;
        Apply(view);

        // The ladder's own thresholds need no better than half a second; the
        // compaction bar is the reference's 100ms ticker.
        var wanted = TimeSpan.FromMilliseconds(view.Progress is not null ? 100 : 500);
        if (view.Visible || Status is { Running: true })
        {
            if (_tick.Interval != wanted)
            {
                _tick.Interval = wanted;
            }

            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }
    }

    private void Apply(ChatStatusView view)
    {
        if (!view.Visible)
        {
            Visibility = Visibility.Collapsed;
            _entranceDone = false;
            _shown = view;
            StopShimmer();
            return;
        }

        if (view.Progress is { } progress)
        {
            if (Content != _compacting)
            {
                Content = _compacting;
            }

            _compactingLabel.Text = view.Label;
            if (_shown.Progress != progress)
            {
                _percent.Text = $"{progress}%";                       // LI4B/nslxi
                AnimateBar(progress);
            }

            StartShimmer();
        }
        else
        {
            if (Content != _label)
            {
                Content = _label;
                StopShimmer();
            }

            _label.Text = view.Label;
        }

        _shown = view;
        Visibility = Visibility.Visible;
        if (!_entranceDone)
        {
            _entranceDone = true;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(EntranceFadeMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            BeginAnimation(OpacityProperty, fade);
        }
    }

    private void AnimateBar(int progress)
    {
        var target = BarWidth * Math.Clamp(progress, 0, 100) / 100.0;
        var grow = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(ProgressTransitionMs)))
        {
            // ease-[cubic-bezier(0,0,0.58,1)] — CSS's own "ease-out" curve.
            EasingFunction = new CubicBezierEase(0, 0, 0.58, 1),
        };
        _barFill.BeginAnimation(WidthProperty, grow);
    }

    private void StartShimmer()
    {
        if (_shimmerRunning)
        {
            return;
        }

        _shimmerRunning = true;
        var sweep = new DoubleAnimation(-BarWidth, BarWidth, new Duration(TimeSpan.FromMilliseconds(ShimmerMs)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _shimmerOffset.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private void StopShimmer()
    {
        if (!_shimmerRunning)
        {
            return;
        }

        _shimmerRunning = false;
        _shimmerOffset.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private bool _shimmerRunning;

    private static FrameworkElement Spacer() => new Border { Width = 8 };

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var line = (ChatStatusLine)d;
        if (e.OldValue is ChatTurnStatus old)
        {
            old.PropertyChanged -= line.OnStatusPropertyChanged;
        }

        if (e.NewValue is ChatTurnStatus status)
        {
            status.PropertyChanged += line.OnStatusPropertyChanged;
        }

        line._entranceDone = false;
        line.Refresh();
    }

    private void OnStatusPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
}
