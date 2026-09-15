using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// A paragraph that arrives a word at a time, ported from the reference desktop's
/// reveal component (<c>c4b7ce3b5-DNSas4Nx.js</c> and its stylesheet
/// <c>c4b7ce3b5-B43ptWJb.css</c>, desktop 1.44121.2.0).
///
/// It renders one element per word — as the reference renders one <c>span</c> per
/// word inside its <c>p</c> — and fades each from 0 to 1 over
/// <see cref="FadeInWords.WordFadeMs"/> at the delay
/// <see cref="FadeInWords.Schedule"/> gives it, then raises <see cref="Completed"/>
/// once the tail has passed. The schedule and the tokenizer (including its
/// <c>**bold**</c> toggle) live in <see cref="FadeInWords"/>, where they are tested
/// without a window.
/// </summary>
public sealed class FadeInText : WrapPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FadeInText),
        new FrameworkPropertyMetadata(string.Empty, OnRebuild));

    public static readonly DependencyProperty InstantProperty = DependencyProperty.Register(
        nameof(Instant), typeof(bool), typeof(FadeInText),
        new FrameworkPropertyMetadata(false, OnRebuild));

    public static readonly DependencyProperty SpeedMultiplierProperty = DependencyProperty.Register(
        nameof(SpeedMultiplier), typeof(double), typeof(FadeInText),
        new FrameworkPropertyMetadata(1.0, OnRebuild));

    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight), typeof(double), typeof(FadeInText),
        new FrameworkPropertyMetadata(double.NaN, OnRebuild));

    private DispatcherTimerToken? _pending;

    /// <summary>Raised once every word has started and the reference's tail has passed.</summary>
    public event EventHandler? Completed;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Skips the reveal, which is what the reference does for an account it already
    /// knows: every delay is zero and completion is reported at once.
    /// </summary>
    public bool Instant
    {
        get => (bool)GetValue(InstantProperty);
        set => SetValue(InstantProperty, value);
    }

    public double SpeedMultiplier
    {
        get => (double)GetValue(SpeedMultiplierProperty);
        set => SetValue(SpeedMultiplierProperty, value);
    }

    /// <summary>Applied to each word, since WPF does not inherit line height.</summary>
    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    /// <summary>
    /// Windows' answer to <c>prefers-reduced-motion</c>: a reader who has asked for
    /// less of it gets the paragraph whole rather than a slower reveal.
    /// </summary>
    private bool RevealsInstantly => Instant || !SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Runs the reveal again from the top, even when the text has not changed. A
    /// chain that shows the same paragraph twice would otherwise leave it settled at
    /// full opacity, because a dependency property does not report an unchanged value.
    /// </summary>
    public void Restart() => Rebuild();

    private static void OnRebuild(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FadeInText)d).Rebuild();

    private void Rebuild()
    {
        _pending?.Cancel();
        _pending = null;
        Children.Clear();

        var words = FadeInWords.Split(Text ?? string.Empty);
        var instant = RevealsInstantly;
        var schedule = FadeInWords.Schedule(words.Count, instant, SpeedMultiplier);

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var block = new TextBlock
            {
                Text = word.Text + word.TrailingSpace,
                Opacity = instant ? 1 : 0,
            };

            // Only a bold word states a weight: setting one on the rest would override
            // the weight inherited from the paragraph, which the greeting sets to Light.
            if (word.IsBold)
            {
                block.FontWeight = FontWeights.SemiBold;
            }

            if (!double.IsNaN(LineHeight))
            {
                block.LineHeight = LineHeight;
                block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            }

            Children.Add(block);

            if (instant)
            {
                continue;
            }

            block.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                BeginTime = TimeSpan.FromMilliseconds(schedule[i]),
                Duration = TimeSpan.FromMilliseconds(FadeInWords.WordFadeMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            });
        }

        var after = FadeInWords.CompleteAfterMs(words.Count, instant, SpeedMultiplier);
        if (after <= 0)
        {
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _pending = DispatcherTimerToken.After(TimeSpan.FromMilliseconds(after), () =>
        {
            _pending = null;
            Completed?.Invoke(this, EventArgs.Empty);
        });
    }
}

/// <summary>
/// A one-shot dispatcher callback that can be cancelled. Switching session mid-reveal
/// must not let a stale paragraph report completion and advance the next one.
/// </summary>
internal sealed class DispatcherTimerToken
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    private DispatcherTimerToken(System.Windows.Threading.DispatcherTimer timer) => _timer = timer;

    public static DispatcherTimerToken After(TimeSpan delay, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        var token = new DispatcherTimerToken(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return token;
    }

    public void Cancel() => _timer.Stop();
}
