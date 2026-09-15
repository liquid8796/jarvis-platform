namespace JarvisCode.App.Services;

/// <summary>
/// What the transcript's virtualizer is actually doing. Ported from the reference
/// desktop's own instrumentation (1.44121.4.0, ion-dist chunk
/// <c>ccb6edd6b-BJ2sSjUO.js</c>, whose event is <c>chat.transcript_settle_deferred</c>):
/// it reports the same five figures — how many rows exist, how many are built, what
/// the content adds up to, how many measurements were deferred, and how many rows
/// measured as a zero box.
///
/// A zero box is the one worth watching: a row whose element reports no size is a
/// row the offsets will keep guessing at, and the reference counts them separately
/// rather than folding them into "not measured yet" precisely because the two have
/// different causes.
/// </summary>
public sealed class TranscriptPerfCounters
{
    /// <summary>Rows the list holds.</summary>
    public int ItemCount { get; private set; }

    /// <summary>Rows built for the current window.</summary>
    public int RowsWindowed { get; private set; }

    /// <summary>The most rows that have been built at once since the last reset.</summary>
    public int PeakRowsWindowed { get; private set; }

    /// <summary>What every row together comes to, measured and estimated.</summary>
    public double TotalSizePx { get; private set; }

    /// <summary>Measurements that could not be taken because the row had no box yet.</summary>
    public int BoxlessSkips { get; private set; }

    /// <summary>Layout passes that ended with the offsets still dirty.</summary>
    public int SettleDeferrals { get; private set; }

    /// <summary>Rows whose real height has replaced their estimate.</summary>
    public int Measured { get; private set; }

    /// <summary>Scroll corrections applied because a row above the reader resized.</summary>
    public int Compensations { get; private set; }

    /// <summary>Total pixels those corrections moved, which should stay near zero over a session.</summary>
    public double CompensationPx { get; private set; }

    public void Observe(int itemCount, int rowsWindowed, double totalSizePx, int measured)
    {
        ItemCount = itemCount;
        RowsWindowed = rowsWindowed;
        PeakRowsWindowed = Math.Max(PeakRowsWindowed, rowsWindowed);
        TotalSizePx = totalSizePx;
        Measured = measured;
    }

    public void CountBoxless() => BoxlessSkips++;

    public void CountSettleDeferral() => SettleDeferrals++;

    public void CountCompensation(double delta)
    {
        Compensations++;
        CompensationPx += delta;
    }

    public void Reset()
    {
        ItemCount = 0;
        RowsWindowed = 0;
        PeakRowsWindowed = 0;
        TotalSizePx = 0;
        BoxlessSkips = 0;
        SettleDeferrals = 0;
        Measured = 0;
        Compensations = 0;
        CompensationPx = 0;
    }

    /// <summary>One line, in the order the reference reports them.</summary>
    public override string ToString() =>
        $"items={ItemCount} windowed={RowsWindowed} peak={PeakRowsWindowed} " +
        $"total_size_px={Math.Round(TotalSizePx)} measured={Measured} " +
        $"settle_deferrals={SettleDeferrals} boxless_skips={BoxlessSkips} " +
        $"compensations={Compensations} compensation_px={Math.Round(CompensationPx)}";
}
