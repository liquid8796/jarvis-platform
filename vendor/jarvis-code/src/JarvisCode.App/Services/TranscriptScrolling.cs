namespace JarvisCode.App.Services;

/// <summary>
/// How far one wheel notch moves the transcript. The transcript used to answer the
/// wheel two ways — a code fence or a tool result scrolled it by <c>delta ×
/// speed</c>, 120px a notch, while the prose beside them was left to WPF's own
/// step of about 48px — so passing the pointer over a fence scrolled two and a
/// half times faster than passing it over the paragraph above. One step now serves
/// the whole transcript, and <c>1.0×</c> is defined as exactly what WPF does, so
/// the default feel is unchanged and <c>/scroll-speed</c> is the multiplier its own
/// description promises: "the wheel multiplier on the transcript".
/// </summary>
public static class TranscriptScrolling
{
    /// <summary>
    /// The wheel delta one notch reports. A high-resolution wheel reports fractions
    /// of it, and the step scales with whatever arrives.
    /// </summary>
    public const double DeltaForOneNotch = System.Windows.Input.Mouse.MouseWheelDeltaForOneLine;

    /// <summary>
    /// The pixels one scroll line moves, which is WPF's own
    /// <c>ScrollContentPresenter</c> line delta. Measured against the transcript on
    /// this platform: three lines to a notch at the Windows default is the 48px
    /// step the prose already scrolled by.
    /// </summary>
    public const double LineHeight = 16.0;

    /// <summary>
    /// How close to the bottom still counts as being at the bottom. The transcript
    /// pins itself to the tail inside this band, and the scroll-to-bottom pill
    /// appears outside it.
    /// </summary>
    public const double TailSlack = 60;

    /// <summary>
    /// Whether the reader is at the tail — the reference's <c>isPinned</c>, which
    /// decides both whether an arriving row scrolls the view and whether the
    /// virtualizer computes its window at the tail rather than at the scroller's
    /// own offset. One rule, because a transcript that pinned by one definition and
    /// windowed by another would build the wrong rows for exactly the turn that is
    /// streaming into it.
    /// </summary>
    public static bool IsAtTail(double verticalOffset, double scrollableHeight) =>
        scrollableHeight <= 0 || verticalOffset >= scrollableHeight - TailSlack;

    /// <summary>
    /// How much to add to the scroll viewer's vertical offset for one wheel event.
    /// Positive delta (wheel up) moves the offset back toward the top.
    /// </summary>
    /// <param name="wheelDelta">The event's delta, ±<see cref="DeltaForOneNotch"/> per notch.</param>
    /// <param name="speed">The /scroll-speed multiplier, already clamped.</param>
    /// <param name="wheelScrollLines">
    /// The user's Windows setting (<c>SystemParameters.WheelScrollLines</c>): lines
    /// per notch, or a negative value for the "one screen at a time" option, or 0
    /// where the user has turned wheel scrolling off.
    /// </param>
    /// <param name="viewportHeight">The scroll viewport, which is what a screen means.</param>
    public static double VerticalOffsetDelta(
        int wheelDelta, double speed, int wheelScrollLines, double viewportHeight)
    {
        if (wheelDelta == 0 || wheelScrollLines == 0 || speed <= 0)
        {
            return 0;
        }

        // Windows offers "One screen at a time" as a negative line count; WPF pages
        // for it and so does this.
        var perNotch = wheelScrollLines < 0
            ? Math.Max(0, viewportHeight)
            : wheelScrollLines * LineHeight;

        return -(wheelDelta / DeltaForOneNotch) * perNotch * speed;
    }
}
