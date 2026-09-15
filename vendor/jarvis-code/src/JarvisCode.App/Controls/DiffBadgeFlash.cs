using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JarvisCode.App.Controls;

/// <summary>
/// The settling blink the reference gives a +adds/−dels badge that arrived while
/// the reader was watching — its <c>epitaxy-diff-badge-add-flash</c>, measured in
/// desktop 1.44121.2.0's stylesheet as <c>.2s ease-out 1.2s backwards</c> from the
/// git colour to the muted one. It fills backwards and not forwards, so the badge
/// holds its own colour for the delay, fades to muted over 200ms, and is back in
/// its colour the moment the animation ends.
///
/// It only ever runs on a badge whose number appeared during this session: the
/// reference captures "was this row running when it mounted" once, so reopening a
/// finished session does not blink every badge in it.
///
/// The animation targets a colour, and a theme brush is frozen and shared, so the
/// badge is given a brush of its own for the duration and handed back to the
/// theme's afterwards.
/// </summary>
public static class DiffBadgeFlash
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1200);

    public static readonly DependencyProperty FlashProperty =
        DependencyProperty.RegisterAttached(
            "Flash", typeof(bool), typeof(DiffBadgeFlash), new PropertyMetadata(false, OnFlashChanged));

    /// <summary>The theme brush the badge belongs to, restored once the blink ends.</summary>
    public static readonly DependencyProperty RestingBrushProperty =
        DependencyProperty.RegisterAttached(
            "RestingBrush", typeof(string), typeof(DiffBadgeFlash), new PropertyMetadata(null));

    public static void SetFlash(DependencyObject element, bool value) =>
        element.SetValue(FlashProperty, value);

    public static bool GetFlash(DependencyObject element) => (bool)element.GetValue(FlashProperty);

    public static void SetRestingBrush(DependencyObject element, string? value) =>
        element.SetValue(RestingBrushProperty, value);

    public static string? GetRestingBrush(DependencyObject element) =>
        (string?)element.GetValue(RestingBrushProperty);

    private static void OnFlashChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block || e.NewValue is not true ||
            GetRestingBrush(block) is not { Length: > 0 } resting)
        {
            return;
        }

        if (block.Foreground is not SolidColorBrush from ||
            block.TryFindResource("Text500Brush") is not SolidColorBrush muted)
        {
            return;
        }

        var own = new SolidColorBrush(from.Color);
        block.Foreground = own;

        var fade = new ColorAnimation(muted.Color, FadeDuration)
        {
            BeginTime = Delay,
            EasingFunction = new CubicBezierEase(0, 0, 0.58, 1),
        };

        fade.Completed += (_, _) =>
        {
            own.BeginAnimation(SolidColorBrush.ColorProperty, null);
            block.SetResourceReference(TextBlock.ForegroundProperty, resting);
        };

        own.BeginAnimation(SolidColorBrush.ColorProperty, fade);
    }
}
