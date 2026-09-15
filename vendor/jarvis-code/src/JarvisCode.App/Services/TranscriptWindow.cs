namespace JarvisCode.App.Services;

/// <summary>
/// Which rows of a transcript are worth building. Ported from the reference
/// desktop's own transcript virtualizer (1.44121.4.0, ion-dist chunk
/// <c>ccb6edd6b-BJ2sSjUO.js</c>, the range function its Code transcript
/// <c>cd5a31703-Bbz821zb.js</c> drives at overscan 600) rather than approximated:
/// the overscan is counted in <em>pixels of row</em> rather than in a row count,
/// and the two end-snaps are what make the ends of a long transcript feel solid
/// instead of paging in.
/// </summary>
public static class TranscriptWindow
{
    /// <summary>
    /// The reference's <c>c0</c>: how far past each edge of the viewport rows are
    /// still built, in pixels. Its Code transcript passes the same value for the
    /// top and the bottom.
    /// </summary>
    public const double Overscan = 600;

    /// <summary>
    /// The reference's <c>initialWindow.release.afterPresentedFrames</c>: how many
    /// frames the first paint holds its narrow window before the full overscan is
    /// released. Opening a session paints downward only, which is the difference
    /// between one screen of work and one screen plus everything above it.
    /// </summary>
    public const int MountReleaseFrames = 2;

    /// <summary>
    /// How many rows at the tail the reference keeps mounted whatever the window
    /// says - its <c>Zg = 3</c>, read off the Chat transcript's keep set on
    /// desktop 1.46388.2.0. The newest rows are the ones a streaming answer and a
    /// reply are written into, so they are the ones worth not rebuilding.
    /// </summary>
    public const int KeepMountedTail = 3;

    /// <summary>
    /// A measured height replaces a stored one only past this much difference. The
    /// reference's own tolerance; without it a sub-pixel layout wobble re-dirties
    /// the offsets on every frame.
    /// </summary>
    public const double SizeTolerance = 0.5;

    /// <summary>The rows to build, inclusive. <c>Last</c> is -1 for an empty list.</summary>
    public readonly record struct Range(int First, int Last)
    {
        public int Count => Last < First ? 0 : Last - First + 1;

        public bool Contains(int index) => index >= First && index <= Last;
    }

    /// <summary>What the window is computed from. Every field is the reference's own.</summary>
    public readonly record struct Inputs
    {
        /// <summary>
        /// Prefix sums of row heights, length <c>rows + 1</c>: <c>Offsets[i]</c> is
        /// where row <c>i</c> starts and <c>Offsets[^1]</c> is the content height.
        /// </summary>
        public required IReadOnlyList<double> Offsets { get; init; }

        public required double ViewportHeight { get; init; }

        /// <summary>How far the scroller has moved, in the panel's own coordinates.</summary>
        public required double ScrollTop { get; init; }

        /// <summary>How far the scroller could still move — WPF's <c>ScrollableHeight</c>.</summary>
        public double ScrollableHeight { get; init; }

        public double PaddingTop { get; init; }

        public double PaddingBottom { get; init; }

        public double OverscanTop { get; init; }

        public double OverscanBottom { get; init; }

        /// <summary>
        /// The reader is pinned to the tail. The window is then computed at the tail
        /// rather than at the scroller's own offset, so a row that arrives while a
        /// turn streams is built before the scroller has caught up with it.
        /// </summary>
        public bool Following { get; init; }

        /// <summary>
        /// The first-paint window is still in force: the overscan is whatever it
        /// carries and the two end-snaps stand down, which is what keeps opening a
        /// long session from building everything above the anchor as well.
        /// </summary>
        public bool MountPhase { get; init; }

        public double BottomInset { get; init; }
    }

    /// <summary>
    /// The row whose box holds <paramref name="offset"/>. The reference's <c>mt</c>:
    /// a lower bound over the prefix sums, clamped to the last row so a pixel past
    /// the end still names a row.
    /// </summary>
    public static int IndexAt(IReadOnlyList<double> offsets, double offset)
    {
        int low = 0;
        int high = offsets.Count - 1;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            if (offsets[mid + 1] <= offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return Math.Min(low, Math.Max(0, offsets.Count - 2));
    }

    /// <summary>The reference's range function, ported step for step.</summary>
    public static Range Compute(in Inputs inputs)
    {
        var offsets = inputs.Offsets;
        int rows = offsets.Count - 1;
        if (rows <= 0)
        {
            return new Range(0, -1);
        }

        double contentSize = offsets[rows];
        double viewport = Math.Max(0, inputs.ViewportHeight);

        // Where the viewport sits once it is at the tail. Everything below is
        // measured against this rather than against the scroller, because the
        // scroller lags a row that has only just been added.
        double tailTop = Math.Max(
            0, contentSize + inputs.PaddingBottom + inputs.BottomInset - viewport);

        double scrolled = Math.Max(0, inputs.ScrollTop - inputs.PaddingTop);
        double viewTop;
        if (!inputs.Following)
        {
            viewTop = scrolled;
        }
        else
        {
            double reachable = Math.Max(
                tailTop, inputs.ScrollableHeight - inputs.PaddingTop);
            viewTop = Math.Max(tailTop, Math.Min(scrolled, reachable));
        }

        double viewBottom = viewTop + viewport;
        int first = IndexAt(offsets, viewTop);
        int last = IndexAt(offsets, Math.Max(viewTop, viewBottom - 1));

        // The overscan is a pixel budget spent on whole rows, not a row count: one
        // 900px answer above the fold is the whole budget, and forty short tool
        // rows are also the whole budget.
        for (double spent = 0; first > 0 && spent < inputs.OverscanTop; first--)
        {
            spent += offsets[first] - offsets[first - 1];
        }

        for (double spent = 0; last < rows - 1 && spent < inputs.OverscanBottom; last++)
        {
            spent += offsets[last + 2] - offsets[last + 1];
        }

        if (!inputs.MountPhase)
        {
            // Within two viewports of either end, build all the way to it. A reader
            // who has arrived near the top or the bottom is about to reach it, and
            // an estimate that is wrong there is the one that shows.
            double reach = 2 * viewport;
            if (contentSize - offsets[last + 1] <= reach)
            {
                last = rows - 1;
            }

            if (offsets[first] <= reach)
            {
                first = 0;
            }
        }

        return new Range(first, last);
    }
}
