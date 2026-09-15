using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Services;

/// <summary>
/// Whether a row holds too much to be built in one piece. Ported from the
/// reference desktop's <c>shouldSplitEntry</c> (1.44121.4.0, ion-dist chunk
/// <c>cd5a31703-Bbz821zb.js</c>: its <c>n0</c> over the pair <c>t0</c>), which is
/// what stops one enormous message from becoming one enormous row and defeating
/// the virtualizer around it.
///
/// The reference applies it to an assistant <em>entry</em>, whose items it then
/// lays out as rows of their own. This port already emits one transcript row per
/// block, so the split it still needs is one level in: a tool run's call list and
/// the line lists inside an expanded body are the places where this port packs an
/// unbounded number of things into a single row.
/// </summary>
public static class TranscriptEntrySplit
{
    /// <summary>The reference's <c>t0.items</c>.</summary>
    public const int ItemThreshold = 16;

    /// <summary>The reference's <c>t0.px</c>.</summary>
    public const double PixelThreshold = 2000;

    /// <summary>
    /// The reference measures at its medium column (960px) and default text size
    /// whatever the reader is actually at, so the same conversation splits the same
    /// way on every window.
    /// </summary>
    public const double MeasurementWidth = 960;

    /// <summary>The reference's predicate over a count and an estimated height.</summary>
    public static bool ShouldSplit(int itemCount, double estimatedHeight) =>
        itemCount >= ItemThreshold || estimatedHeight >= PixelThreshold;

    /// <summary>Whether one transcript row holds enough to be worth splitting.</summary>
    public static bool ShouldSplit(TranscriptItem item) =>
        item switch
        {
            ToolGroupItem group => ShouldSplit(
                group.Calls.Count,
                TranscriptRowEstimates.For(group, MeasurementWidth, 1)),
            _ => ShouldSplit(1, TranscriptRowEstimates.For(item, MeasurementWidth, 1)),
        };
}
