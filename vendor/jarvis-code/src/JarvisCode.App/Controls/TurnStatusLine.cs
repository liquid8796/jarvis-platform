using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JarvisCode.App.ViewModels;
using JarvisCode.App.Views;

namespace JarvisCode.App.Controls;

/// <summary>
/// The turn status line at the transcript tail — Claude Code Desktop's progress
/// indicator: spinner glyph, elapsed timer (after 2s), token counter (animated
/// count-up), interpunct separators, and the phase label with its shimmer and
/// the elapsed-bucketed thinking ladder. Ported from the reference app's
/// component: thresholds, formats, dwell and animation timings all match.
/// </summary>
public sealed class TurnStatusLine : ContentControl
{
    // Strings from the reference app's en-US resources. The ids are the
    // catalogue keys they carry there, which is what the parity tests look up.
    // The first names the assistant, so it carries this app's name rather than
    // the reference's; the parity check compares it through UiBrand.
    internal const string WaitingForJarvis = "Waiting for Jarvis…";       // QhT5HdB9mD
    internal const string RunningTools = "Running tools…";                // /BJXX9tCZt
    internal const string StoppingLabel = "Stopping…";                    // NACfNicI6z
    internal const string CompactingSession = "Compacting session…";      // FIEdqIul1I

    /// <summary>
    /// Minimum time a phase label stays up before the next swap (ms). The
    /// reference holds a label for 650ms and, when a newer one arrives sooner,
    /// schedules the remainder rather than dropping it.
    /// </summary>
    internal const int LabelDwellMs = 650;

    /// <summary>
    /// Label morph duration (ms). The reference animates the label's width with
    /// <c>{duration:180, easing:cubic-bezier(.2,0,0,1)}</c>.
    /// </summary>
    internal const int LabelMorphMs = 180;

    /// <summary>Cluster show/hide fade (ms), the reference's 150ms swap timeout.</summary>
    internal const int ClusterFadeMs = 150;

    /// <summary>
    /// Thinking shimmer, from the reference stylesheet:
    /// <c>animation: 2s ease-in-out 3s infinite</c> over
    /// <c>@keyframes{0%,to{opacity:1}50%{opacity:.75}}</c>.
    /// </summary>
    internal const int ShimmerPeriodMs = 2000;

    internal const int ShimmerDelayMs = 3000;

    internal const double ShimmerMinOpacity = 0.75;

    /// <summary>Token counter count-up duration (ms).</summary>
    internal const int TokenCountUpMs = 400;

    /// <summary>Elapsed seconds before the timer and token counter appear (reference: <c>f&gt;=2</c>).</summary>
    internal const int StatsVisibleAfterSeconds = 2;

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(TurnStatus), typeof(TurnStatusLine),
        new PropertyMetadata(null, OnStatusChanged));

    public static readonly DependencyProperty RunningTaskCountProperty = DependencyProperty.Register(
        nameof(RunningTaskCount), typeof(int), typeof(TurnStatusLine),
        new PropertyMetadata(0, static (d, _) => ((TurnStatusLine)d).Update()));

    public static readonly DependencyProperty TasksPaneOpenProperty = DependencyProperty.Register(
        nameof(TasksPaneOpen), typeof(bool), typeof(TurnStatusLine),
        new PropertyMetadata(false, static (d, _) => ((TurnStatusLine)d).UpdateTasksChipBrush()));

    public static readonly DependencyProperty ThinkingToggleEnabledProperty = DependencyProperty.Register(
        nameof(ThinkingToggleEnabled), typeof(bool), typeof(TurnStatusLine),
        new PropertyMetadata(true, static (d, _) => ((TurnStatusLine)d).Update()));

    // The reference eases every entrance/exit with cubic-bezier(0.2, 0, 0, 1) over 180ms
    // and fades the whole cluster with the stock 150ms transition curve.
    private static readonly KeySpline MorphSpline = Frozen(new KeySpline(0.2, 0, 0, 1));
    private static readonly KeySpline FadeSpline = Frozen(new KeySpline(0.4, 0, 0.2, 1));
    private static readonly KeySpline EaseInOutSpline = Frozen(new KeySpline(0.42, 0, 0.58, 1));
    private static readonly Duration MorphDuration = new(TimeSpan.FromMilliseconds(LabelMorphMs));

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _dwell = new() { IsEnabled = false };

    private readonly SpinnerGlyph _spinner = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _cluster = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _elapsedText;
    private readonly TextBlock _elapsedLeadSeparator;
    private readonly TextBlock _tokensText;
    private readonly TextBlock _statusSeparator;
    private readonly StackPanel _elapsedSegment;
    private readonly StackPanel _tokensSegment;
    private readonly StackPanel _statusSegment;
    private readonly StackPanel _tasksSegment;
    private readonly TextBlock _tasksSeparator;
    private readonly TextBlock _tasksChip;
    private readonly Grid _statusHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusCurrent;
    private readonly TextBlock _statusPrevious;

    private string _variant = "";
    private bool _statusClickable;
    private string? _displayedLabel;
    private long _lastSwapTicks;
    private bool _clusterShown;
    private bool _shimmerRunning;
    private long _shownTokens;
    private long _pendingTokens;
    private long _animatedFrom;
    private DateTimeOffset _tokenAnimStart;
    private bool _tokenAnimHooked;

    public TurnStatusLine()
    {
        Focusable = false;
        _elapsedText = FootnoteText();
        _elapsedLeadSeparator = Separator();
        _tokensText = FootnoteText();
        _statusSeparator = Separator();
        _statusCurrent = FootnoteText();
        _statusPrevious = FootnoteText();
        _statusPrevious.Visibility = Visibility.Collapsed;

        _statusHost.Children.Add(_statusPrevious);
        _statusHost.Children.Add(_statusCurrent);

        _elapsedSegment = Segment(_elapsedLeadSeparator, _elapsedText);
        _elapsedLeadSeparator.Visibility = Visibility.Collapsed;
        _tokensSegment = Segment(Separator(), _tokensText);
        _statusSegment = Segment(_statusSeparator, _statusHost);

        // The reference's running-tasks chip: footnote text, accent on hover
        // and while the tasks pane is open; clicking toggles the pane.
        _tasksSeparator = Separator();
        _tasksChip = FootnoteText();
        _tasksChip.Cursor = System.Windows.Input.Cursors.Hand;
        _tasksChip.MouseLeftButtonUp += (_, _) => TasksChipClicked?.Invoke(this, EventArgs.Empty);
        _tasksChip.MouseEnter += (_, _) => UpdateTasksChipBrush(hover: true);
        _tasksChip.MouseLeave += (_, _) => UpdateTasksChipBrush();
        _tasksSegment = Segment(_tasksSeparator, _tasksChip);

        // Clicking the phase label toggles the thinking transcript view. Like
        // the reference, the label is a button only while thinking is active.
        _statusHost.MouseLeftButtonUp += (_, _) =>
        {
            if (_statusClickable)
            {
                StatusLabelClicked?.Invoke(this, EventArgs.Empty);
            }
        };
        _statusHost.Background = System.Windows.Media.Brushes.Transparent;

        // Row: spinner · 16px gap · text cluster, 20px tall (the reference's h-h3).
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 20 };
        row.Children.Add(_spinner);
        row.Children.Add(_cluster);
        Content = row;
        Visibility = Visibility.Collapsed;

        ApplyVariant("stats");

        _tick.Tick += (_, _) => Update();
        _dwell.Tick += (_, _) => { _dwell.Stop(); Update(); };
        Unloaded += (_, _) => { _tick.Stop(); _dwell.Stop(); UnhookTokenAnimation(); };
        Loaded += (_, _) => Update();
    }

    public TurnStatus? Status
    {
        get => (TurnStatus?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Background tasks and workers alive right now; shows the chip when &gt; 0.</summary>
    public int RunningTaskCount
    {
        get => (int)GetValue(RunningTaskCountProperty);
        set => SetValue(RunningTaskCountProperty, value);
    }

    public bool TasksPaneOpen
    {
        get => (bool)GetValue(TasksPaneOpenProperty);
        set => SetValue(TasksPaneOpenProperty, value);
    }

    /// <summary>
    /// False while the transcript view is Verbose, which already shows thinking —
    /// the surface mirrors the reference's transcriptModeShowsThinking gate here.
    /// </summary>
    public bool ThinkingToggleEnabled
    {
        get => (bool)GetValue(ThinkingToggleEnabledProperty);
        set => SetValue(ThinkingToggleEnabledProperty, value);
    }

    /// <summary>The phase label was clicked (the reference toggles thinking view).</summary>
    public event EventHandler? StatusLabelClicked;

    /// <summary>The running-tasks chip was clicked (the reference toggles the tasks pane).</summary>
    public event EventHandler? TasksChipClicked;

    private void SetStatusClickable(bool clickable)
    {
        if (clickable == _statusClickable)
        {
            return;
        }

        _statusClickable = clickable;
        _statusHost.Cursor = clickable ? System.Windows.Input.Cursors.Hand : null;
    }

    private void UpdateTasksChipBrush(bool hover = false)
    {
        if (TasksPaneOpen || hover)
        {
            _tasksChip.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrandBrush");
        }
        else
        {
            _tasksChip.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        }
    }

    // ---- reference formatters (ported verbatim) ----

    /// <summary>"55s", "14m 6s", "1h 2m 5s" — the reference's elapsed format.</summary>
    public static string FormatElapsed(int totalSeconds)
    {
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}h {minutes}m {seconds}s"
            : minutes > 0 ? $"{minutes}m {seconds}s"
            : $"{seconds}s";
    }

    /// <summary>
    /// "441", "9.5k", "1.2M", "3B" — one decimal, ".0" trimmed, and a value that
    /// rounds to 1000 of its unit promotes to the next one (999,999 → "1M").
    /// </summary>
    public static string FormatTokenCount(long count)
    {
        if (count >= 1_000_000_000)
        {
            return TrimZero(ToFixed1(count / 1e9)) + "B";
        }

        if (count >= 1_000_000)
        {
            var scaled = double.Parse(ToFixed1(count / 1e6), System.Globalization.CultureInfo.InvariantCulture);
            return scaled >= 1000 ? TrimZero(ToFixed1(count / 1e9)) + "B" : TrimZero(ToFixed1(scaled)) + "M";
        }

        if (count >= 1_000)
        {
            var scaled = double.Parse(ToFixed1(count / 1e3), System.Globalization.CultureInfo.InvariantCulture);
            return scaled >= 1000 ? TrimZero(ToFixed1(count / 1e6)) + "M" : TrimZero(ToFixed1(scaled)) + "k";
        }

        return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The thinking ladder, bucketed on seconds elapsed in the turn.</summary>
    public static string ThinkingLabel(int elapsedSeconds) => elapsedSeconds switch
    {
        >= 60 => "Almost done thinking…",
        >= 45 => "Thinking some more…",
        >= 30 => "Thinking more…",
        >= 15 => "Still thinking…",
        _ => "Thinking…",
    };

    /// <summary>
    /// The label that replaces the ladder once thinking ends. The reference
    /// status line renders the seconds form only, however long the block ran.
    /// </summary>
    public static string ThoughtForLabel(int seconds) => $"Thought for {seconds}s";

    private static string ToFixed1(double value) =>
        value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static string TrimZero(string value) =>
        value.EndsWith(".0", StringComparison.Ordinal) ? value[..^2] : value;

    // ---- state machine ----

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var line = (TurnStatusLine)d;
        if (e.OldValue is TurnStatus old)
        {
            old.PropertyChanged -= line.OnStatusPropertyChanged;
        }

        if (e.NewValue is TurnStatus status)
        {
            status.PropertyChanged += line.OnStatusPropertyChanged;
        }

        line.ResetVisualState();
        line.Update();
    }

    private void OnStatusPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TurnStatus.StartedAt) && Status is { StartedAt: not null })
        {
            // A new turn: the counter restarts from zero without a downward count-up.
            SnapTokens(0);
        }

        Update();
    }

    private void ResetVisualState()
    {
        _displayedLabel = null;
        _lastSwapTicks = 0;
        _clusterShown = false;
        _cluster.BeginAnimation(OpacityProperty, null);
        _cluster.Opacity = 0;
        SnapTokens(Status?.OutputTokens ?? 0);
        _statusCurrent.Text = "";
        _statusPrevious.Visibility = Visibility.Collapsed;
        SetStatusClickable(false);
        _elapsedSegment.Visibility = Visibility.Collapsed;
        _tokensSegment.Visibility = Visibility.Collapsed;
        _tasksSegment.Visibility = Visibility.Collapsed;
        _statusSegment.Visibility = Visibility.Collapsed;
        StopShimmer();
    }

    private void Update()
    {
        var status = Status;
        var idle = status is null || status.Phase == TurnPhase.Idle;
        if (idle && RunningTaskCount == 0)
        {
            _tick.Stop();
            _dwell.Stop();
            UnhookTokenAnimation();
            _spinner.IsSpinning = false;
            Visibility = Visibility.Collapsed;
            ResetVisualState();
            return;
        }

        Visibility = Visibility.Visible;
        UpdateTasksChipBrush();
        _tasksChip.Text = RunningTaskCount == 1 ? "1 running task" : $"{RunningTaskCount} running tasks";

        if (idle)
        {
            // The reference's tasks-idle state: static glyph and the chip alone.
            _tick.Stop();
            _dwell.Stop();
            UnhookTokenAnimation();
            _spinner.IsSpinning = false;
            ApplyVariant("stats");
            SetSegmentVisible(_elapsedSegment, false);
            SetSegmentVisible(_tokensSegment, false);
            SetSegmentVisible(_statusSegment, false);
            _tasksSeparator.Visibility = Visibility.Collapsed;
            SetSegmentVisible(_tasksSegment, true);
            StopShimmer();
            _displayedLabel = null;
            SetStatusClickable(false);
            if (!_clusterShown)
            {
                _clusterShown = true;
                _cluster.BeginAnimation(OpacityProperty, null);
                _cluster.Opacity = 1;
            }

            return;
        }

        _spinner.IsSpinning = true;
        if (!_tick.IsEnabled)
        {
            _tick.Start();
        }

        // Stopping shows the label alone; compacting reads label-first with the
        // timer trailing; a normal turn reads elapsed · tokens · label.
        ApplyVariant(status.Phase switch
        {
            TurnPhase.Stopping => "stopping",
            TurnPhase.Compacting => "compacting",
            _ => "stats",
        });

        var elapsed = status.StartedAt is { } started
            ? Math.Max(0, (int)(DateTimeOffset.Now - started).TotalSeconds)
            : 0;
        var timerVisible = elapsed >= StatsVisibleAfterSeconds;
        var tokens = status.OutputTokens;
        var tokensVisible = _variant == "stats" && timerVisible && tokens > 0;

        _elapsedText.Text = FormatElapsed(elapsed);
        AnimateTokensTo(tokens);

        ApplyLabel(DesiredLabel(status, elapsed, tokensVisible));
        SetStatusClickable(ThinkingToggleEnabled && status.Phase == TurnPhase.Thinking);

        var tasksShown = _variant == "stats" && RunningTaskCount > 0;
        SetSegmentVisible(_elapsedSegment, _variant != "stopping" && timerVisible);
        SetSegmentVisible(_tokensSegment, tokensVisible);
        SetSegmentVisible(_tasksSegment, tasksShown);
        _tasksSeparator.Visibility = tasksShown && (timerVisible || tokensVisible)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetSegmentVisible(_statusSegment, _displayedLabel is not null);
        _statusSeparator.Visibility = _variant == "stats" && (timerVisible || tasksShown)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var shimmer = _displayedLabel is not null
            && status.Phase is not (TurnPhase.ThoughtFor or TurnPhase.Compacting);
        if (shimmer && !_shimmerRunning)
        {
            StartShimmer();
        }
        else if (!shimmer && _shimmerRunning)
        {
            StopShimmer();
        }

        var lineVisible = timerVisible || _displayedLabel is not null || tasksShown;
        if (lineVisible != _clusterShown)
        {
            _clusterShown = lineVisible;
            var fade = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(ClusterFadeMs)) };
            fade.KeyFrames.Add(new SplineDoubleKeyFrame(
                lineVisible ? 1 : 0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ClusterFadeMs)), FadeSpline));
            _cluster.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>Reflows the cluster for the stats / compacting / stopping layouts.</summary>
    private void ApplyVariant(string variant)
    {
        if (variant == _variant)
        {
            return;
        }

        _variant = variant;
        _cluster.Children.Clear();
        if (variant == "stats")
        {
            _elapsedLeadSeparator.Visibility = Visibility.Collapsed;
            _cluster.Children.Add(_elapsedSegment);
            _cluster.Children.Add(_tokensSegment);
            _cluster.Children.Add(_tasksSegment);
            _cluster.Children.Add(_statusSegment);
        }
        else
        {
            // Label first; in compacting the timer trails behind a separator.
            _statusSeparator.Visibility = Visibility.Collapsed;
            _elapsedLeadSeparator.Visibility = Visibility.Visible;
            _cluster.Children.Add(_statusSegment);
            if (variant == "compacting")
            {
                _cluster.Children.Add(_elapsedSegment);
            }
        }
    }

    private string? DesiredLabel(TurnStatus status, int elapsed, bool tokensVisible) => status.Phase switch
    {
        TurnPhase.Stopping => StoppingLabel,
        TurnPhase.Compacting => CompactingSession,
        TurnPhase.Thinking => ThinkingLabel(elapsed),
        TurnPhase.ThoughtFor => ThoughtForLabel(status.ThoughtForSeconds),
        TurnPhase.WaitingForModel => WaitingForJarvis,
        TurnPhase.RunningTools => RunningTools,
        // Streaming has no label of its own; the last one holds until the
        // timer and counter are both up (the reference's waiting-label hold).
        TurnPhase.Streaming => tokensVisible ? null : _displayedLabel,
        _ => null,
    };

    /// <summary>Swaps the label with the reference's 650ms dwell and morph crossfade.</summary>
    private void ApplyLabel(string? desired)
    {
        if (desired == _displayedLabel)
        {
            return;
        }

        var sinceSwap = Environment.TickCount64 - _lastSwapTicks;
        if (_displayedLabel is not null && sinceSwap < LabelDwellMs)
        {
            if (!_dwell.IsEnabled)
            {
                _dwell.Interval = TimeSpan.FromMilliseconds(Math.Max(1, LabelDwellMs - sinceSwap));
                _dwell.Start();
            }

            return;
        }

        _lastSwapTicks = Environment.TickCount64;
        var previous = _displayedLabel;
        _displayedLabel = desired;
        if (desired is null)
        {
            return; // the whole segment fades out via SetSegmentVisible
        }

        if (previous is not null && _statusSegment.Visibility == Visibility.Visible)
        {
            _statusPrevious.Text = previous;
            _statusPrevious.Visibility = Visibility.Visible;
            Morph(_statusPrevious, from: 1, to: 0, fromY: 0, toY: -3,
                () => _statusPrevious.Visibility = Visibility.Collapsed);
            _statusCurrent.Text = desired;
            Morph(_statusCurrent, from: 0, to: 1, fromY: 3, toY: 0, completed: null);
        }
        else
        {
            _statusCurrent.Text = desired;
        }
    }

    private static void SetSegmentVisible(FrameworkElement segment, bool show)
    {
        if (show && segment.Visibility != Visibility.Visible)
        {
            segment.Visibility = Visibility.Visible;
            Morph(segment, from: 0, to: 1, fromY: 3, toY: 0, completed: null);
        }
        else if (!show && segment.Visibility == Visibility.Visible)
        {
            Morph(segment, from: 1, to: 0, fromY: 0, toY: -3, () =>
            {
                if (segment.Opacity == 0)
                {
                    segment.Visibility = Visibility.Collapsed;
                }
            });
        }
    }

    private static void Morph(FrameworkElement element, double from, double to, double fromY, double toY, Action? completed)
    {
        var opacity = new DoubleAnimationUsingKeyFrames { Duration = MorphDuration };
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(MorphDuration.TimeSpan), MorphSpline));
        if (completed is not null)
        {
            opacity.Completed += (_, _) => completed();
        }

        if (element.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            element.RenderTransform = translate;
        }

        var slide = new DoubleAnimationUsingKeyFrames { Duration = MorphDuration };
        slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(fromY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new SplineDoubleKeyFrame(toY, KeyTime.FromTimeSpan(MorphDuration.TimeSpan), MorphSpline));

        element.BeginAnimation(OpacityProperty, opacity);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // ---- shimmer: opacity 1 → 0.75 → 1 over 2s, 3s delay, forever ----

    private void StartShimmer()
    {
        _shimmerRunning = true;
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(ShimmerDelayMs),
            Duration = new Duration(TimeSpan.FromMilliseconds(ShimmerPeriodMs)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        pulse.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new SplineDoubleKeyFrame(
            ShimmerMinOpacity,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ShimmerPeriodMs / 2.0)),
            EaseInOutSpline));
        pulse.KeyFrames.Add(new SplineDoubleKeyFrame(
            1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ShimmerPeriodMs)), EaseInOutSpline));
        _statusHost.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopShimmer()
    {
        _shimmerRunning = false;
        _statusHost.BeginAnimation(OpacityProperty, null);
        _statusHost.Opacity = 1;
    }

    // ---- token count-up: 400ms cubic ease-out toward the reported total ----

    private void AnimateTokensTo(long target)
    {
        if (_tokenAnimHooked)
        {
            _pendingTokens = target;
            return;
        }

        if (target == _shownTokens)
        {
            return;
        }

        _pendingTokens = target;
        _animatedFrom = _shownTokens;
        _tokenAnimStart = DateTimeOffset.Now;
        _tokenAnimHooked = true;
        CompositionTarget.Rendering += OnTokenAnimationFrame;
    }

    private void OnTokenAnimationFrame(object? sender, EventArgs e)
    {
        var progress = Math.Min(1, (DateTimeOffset.Now - _tokenAnimStart).TotalMilliseconds / TokenCountUpMs);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var value = (long)Math.Round(_animatedFrom + (_pendingTokens - _animatedFrom) * eased);
        if (value != _shownTokens)
        {
            _shownTokens = value;
            _tokensText.Text = FormatTokenCount(value) + " tokens";
        }

        if (progress >= 1)
        {
            if (_shownTokens == _pendingTokens)
            {
                UnhookTokenAnimation();
            }
            else
            {
                // A new target arrived mid-flight; glide on from here.
                _animatedFrom = _shownTokens;
                _tokenAnimStart = DateTimeOffset.Now;
            }
        }
    }

    private void SnapTokens(long value)
    {
        UnhookTokenAnimation();
        _shownTokens = value;
        _pendingTokens = value;
        _tokensText.Text = FormatTokenCount(value) + " tokens";
    }

    private void UnhookTokenAnimation()
    {
        if (_tokenAnimHooked)
        {
            _tokenAnimHooked = false;
            CompositionTarget.Rendering -= OnTokenAnimationFrame;
        }
    }

    // ---- building blocks ----

    private static TextBlock FootnoteText()
    {
        var text = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    private static TextBlock Separator()
    {
        var separator = FootnoteText();
        separator.Text = "·";
        separator.Padding = new Thickness(4, 0, 4, 0);
        return separator;
    }

    private static StackPanel Segment(params UIElement[] children)
    {
        var segment = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        foreach (var child in children)
        {
            segment.Children.Add(child);
        }

        return segment;
    }

    private static KeySpline Frozen(KeySpline spline)
    {
        spline.Freeze();
        return spline;
    }
}
