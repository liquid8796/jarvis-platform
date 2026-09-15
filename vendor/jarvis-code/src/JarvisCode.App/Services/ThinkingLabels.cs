namespace JarvisCode.App.Services;

/// <summary>
/// The header a finished chat message shows above its thinking, ported from Claude
/// Code Desktop's shared conversation library (chunk <c>c9671e974</c>, findable by
/// the message id <c>Jj0DasG7Mx</c>).
///
/// The reference sums <c>stop_timestamp - start_timestamp</c> across the message's
/// thinking blocks — not a wall clock over the whole turn, so time spent between
/// blocks does not count — rounds the total to whole seconds, and picks a rung.
/// A message whose blocks carry no usable timestamps reads "Thought process";
/// that is also what a session restored from disk shows here, because the label is
/// only as good as the durations stored with it.
/// </summary>
public static class ThinkingLabels
{
    /// <summary>Shown when nothing timed the thinking, or it rounded to zero seconds.</summary>
    public const string NoDuration = "Thought process";               // zl6fNbo7RW

    private const int SecondsPerMinute = 60;
    private const int MinutesPerHour = 60;

    /// <summary>
    /// JavaScript's <c>Math.round</c>: halves go up. C#'s <c>Math.Round</c> is
    /// banker's rounding and disagrees on every exact .5 with an even quotient,
    /// which is a visible off-by-one in both this label and the copy header.
    /// </summary>
    public static double RoundHalfUp(double value) => Math.Floor(value + 0.5);

    /// <summary>
    /// The reference ladder for a summed thinking duration. <paramref name="total"/>
    /// is null when no block was timed.
    /// </summary>
    public static string ForTotal(TimeSpan? total)
    {
        if (total is not { } measured)
        {
            return NoDuration;
        }

        var seconds = (int)RoundHalfUp(measured.TotalSeconds);
        if (seconds <= 0)
        {
            return NoDuration;
        }

        if (seconds < SecondsPerMinute)
        {
            return $"Thought for {seconds}s";                          // Jj0DasG7Mx
        }

        var minutes = seconds / SecondsPerMinute;
        var restSeconds = seconds % SecondsPerMinute;
        if (minutes < MinutesPerHour)
        {
            return restSeconds > 0
                ? $"Thought for {minutes}m {restSeconds}s"             // 4LM6AdGWNg
                : $"Thought for {minutes}m";                           // vFyvc213kN
        }

        var hours = minutes / MinutesPerHour;
        var restMinutes = minutes % MinutesPerHour;
        return restMinutes > 0
            ? $"Thought for {hours}h {restMinutes}m"                   // voTuOb/ijW
            : $"Thought for {hours}h";                                 // MFx4UWu2HS
    }
}
