namespace JarvisCode.App.Services;

/// <summary>
/// Where a session's transcript was left, and how tall its rows turned out to be.
/// Ported from the reference desktop's own snapshot store (1.44121.4.0, ion-dist
/// chunk <c>cd5a31703-Bbz821zb.js</c>, its <c>open</c>/<c>retain</c>/<c>observe</c>/
/// <c>save</c>/<c>release</c> around the transcript), which saves exactly this
/// record and hands it back to the virtualizer as <c>initialSizes</c>,
/// <c>initialViewport</c> and the anchor to restore.
///
/// It lives for the run rather than on disk, which is where the reference keeps it
/// too: the point is that stepping to another session and back does not re-measure
/// a thousand rows, not that a height survives a restart — and a height is only
/// true for the window width and text size it was taken at, neither of which a
/// stored file can promise.
/// </summary>
public sealed class TranscriptViewportStore
{
    /// <summary>
    /// How many sessions are remembered. The same cap the rest of this app's
    /// per-session UI state uses, evicted oldest-first.
    /// </summary>
    public const int Capacity = 100;

    /// <summary>What was saved for one session.</summary>
    public sealed record Snapshot
    {
        /// <summary>The reader was at the tail, so there is no anchor to put back.</summary>
        public bool IsPinned { get; init; } = true;

        /// <summary>The row the viewport's top edge was in.</summary>
        public string? AnchorKey { get; init; }

        /// <summary>How far into that row the top edge sat.</summary>
        public double AnchorOffsetPx { get; init; }

        /// <summary>Every height measured while the session was open.</summary>
        public IReadOnlyDictionary<string, double> Sizes { get; init; } =
            new Dictionary<string, double>();

        /// <summary>The column those heights were measured at; another one voids them.</summary>
        public double ColumnWidth { get; init; }

        /// <summary>The transcript text size they were measured at, for the same reason.</summary>
        public double TextSize { get; init; }
    }

    private readonly Dictionary<string, Snapshot> _bySession = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public static TranscriptViewportStore Current { get; } = new();

    public void Save(string sessionId, Snapshot snapshot)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        if (!_bySession.ContainsKey(sessionId))
        {
            _order.Add(sessionId);
            while (_order.Count > Capacity)
            {
                _bySession.Remove(_order[0]);
                _order.RemoveAt(0);
            }
        }

        _bySession[sessionId] = snapshot;
    }

    /// <summary>
    /// What was saved for this session, if the heights in it still describe this
    /// window. A snapshot taken at another column or another text size is dropped
    /// rather than restored: putting a stale height back is worse than measuring,
    /// because the offsets under it are then wrong and nobody re-measures a row
    /// that was never built.
    /// </summary>
    public Snapshot? Load(string sessionId, double columnWidth, double textSize)
    {
        if (string.IsNullOrEmpty(sessionId) || !_bySession.TryGetValue(sessionId, out var snapshot))
        {
            return null;
        }

        if (Math.Abs(snapshot.ColumnWidth - columnWidth) > 0.5 ||
            Math.Abs(snapshot.TextSize - textSize) > 0.01)
        {
            return snapshot with { Sizes = new Dictionary<string, double>() };
        }

        return snapshot;
    }

    public void Forget(string sessionId)
    {
        if (_bySession.Remove(sessionId))
        {
            _order.Remove(sessionId);
        }
    }

    public void Clear()
    {
        _bySession.Clear();
        _order.Clear();
    }
}
