namespace JarvisCode.App.Services;

/// <summary>The mascot's animation states, the reference's own set.</summary>
public enum SparkState
{
    /// <summary>Not animated at all: the reference renders the static mark here.</summary>
    Idle,
    Waiting,
    Thinking,
    Writing,
    Entrance,
    Exit,
    Tickle,
}

/// <summary>One state's frame strip: how many frames, and how long each is held.</summary>
/// <param name="FrameCount">Frames in the strip.</param>
/// <param name="Speed">Milliseconds per frame.</param>
public readonly record struct SparkSpec(int FrameCount, double Speed)
{
    /// <summary>How long one pass through the strip takes.</summary>
    public double DurationMs => FrameCount * Speed;
}

/// <summary>
/// The mascot's timing, ported from the reference desktop's CDS Spark
/// (<c>pu</c> in <c>shared-frame-BADe7YPD.js</c> over the sprite table in
/// <c>cf2613ee5-Btwr9m9F.js</c>, desktop 1.44121.2.0).
///
/// A sprite there is a vertical SVG strip of <c>frameCount</c> 100×100 frames,
/// stepped by a Web Animation of <c>frameCount</c> <c>translateY</c> keyframes at
/// <c>easing: steps(frameCount, jump-none)</c> over <c>speed × frameCount</c> ms,
/// looping forever except for the one-shot set, which runs once and holds its last
/// frame (<c>fill: "forwards"</c>) before calling back. <c>idle</c> has no strip at
/// all — the reference's wrapper renders the static mark for it, and for a sprite
/// that has not loaded or a reader who asked for reduced motion.
///
/// What is ported here is that mechanism and its measured constants. The frames
/// themselves are the reference's artwork and are not copied: this build steps the
/// active theme's own mark through the same schedule.
/// </summary>
public static class SparkStates
{
    /// <summary>The reference's default <c>size</c> prop, in pixels of width.</summary>
    public const double DefaultSize = 32;

    /// <summary>The states that run once and hold, rather than looping (its <c>fu</c>).</summary>
    public static bool IsOneShot(SparkState state) =>
        state is SparkState.Entrance or SparkState.Exit or SparkState.Tickle;

    /// <summary>Whether this state has a strip at all; <see cref="SparkState.Idle"/> has none.</summary>
    public static bool IsAnimated(SparkState state) => state != SparkState.Idle;

    /// <summary>The measured strip for a state, or null for the static one.</summary>
    public static SparkSpec? Spec(SparkState state) => state switch
    {
        SparkState.Waiting => new SparkSpec(16, 600),
        SparkState.Thinking => new SparkSpec(9, 90),
        SparkState.Writing => new SparkSpec(8, 90),
        SparkState.Entrance => new SparkSpec(6, 70),
        SparkState.Exit => new SparkSpec(6, 70),
        SparkState.Tickle => new SparkSpec(7, 40),
        _ => null,
    };

    /// <summary>
    /// Which frame is showing after <paramref name="elapsedMs"/>.
    /// <c>steps(n, jump-none)</c> divides the pass into <c>n</c> equal holds, so the
    /// frame is <c>floor(progress × n)</c>; a looping strip wraps, and a one-shot one
    /// holds its last frame once the pass is over, which is what <c>fill: forwards</c>
    /// leaves on screen.
    /// </summary>
    public static int FrameAt(SparkState state, double elapsedMs)
    {
        if (Spec(state) is not { } spec || spec.FrameCount <= 0)
        {
            return 0;
        }

        if (elapsedMs <= 0)
        {
            return 0;
        }

        var passes = elapsedMs / spec.DurationMs;
        if (IsOneShot(state) && passes >= 1)
        {
            return spec.FrameCount - 1;
        }

        var progress = passes - Math.Floor(passes);
        var frame = (int)Math.Floor(progress * spec.FrameCount);
        return Math.Clamp(frame, 0, spec.FrameCount - 1);
    }

    /// <summary>Whether a one-shot state has finished, which is when the reference calls back.</summary>
    public static bool HasEnded(SparkState state, double elapsedMs) =>
        IsOneShot(state) && Spec(state) is { } spec && elapsedMs >= spec.DurationMs;

    /// <summary>
    /// How the mark is drawn for one frame. The reference's frames are artwork; this
    /// build derives a transform per frame instead, so every theme's own mark animates
    /// on the reference's schedule rather than a copied strip being shown.
    /// </summary>
    /// <returns>A uniform scale, a rotation in degrees, and an opacity.</returns>
    public static (double Scale, double Rotation, double Opacity) Frame(SparkState state, int frame)
    {
        if (Spec(state) is not { } spec || spec.FrameCount <= 1)
        {
            return (1, 0, 1);
        }

        var index = Math.Clamp(frame, 0, spec.FrameCount - 1);
        var phase = (double)index / spec.FrameCount;

        return state switch
        {
            // A slow breathe over 9.6s — the calm state the empty chat screen sits in.
            SparkState.Waiting => (1 + (0.08 * (1 - Math.Cos(phase * 2 * Math.PI)) / 2), 0,
                0.85 + (0.15 * (1 - Math.Cos(phase * 2 * Math.PI)) / 2)),

            // Both working states turn one whole revolution per pass.
            SparkState.Thinking or SparkState.Writing => (1, phase * 360, 1),

            // Entrance grows in past its size; exit is its mirror.
            SparkState.Entrance => (EaseOvershoot((index + 1.0) / spec.FrameCount), 0, 1),
            SparkState.Exit => (1 - ((index + 1.0) / spec.FrameCount), 0,
                1 - ((index + 1.0) / spec.FrameCount)),

            // A damped wobble, which is what being poked looks like.
            SparkState.Tickle => (1, 14 * Math.Sin(phase * 3 * Math.PI) * (1 - phase), 1),

            _ => (1, 0, 1),
        };
    }

    /// <summary>Grows to 1 with a small overshoot, so the entrance lands rather than stops.</summary>
    private static double EaseOvershoot(double t) =>
        t >= 1 ? 1 : 1 - (Math.Pow(1 - t, 2) * Math.Cos(t * Math.PI * 0.5)) + (0.12 * Math.Sin(t * Math.PI));
}
