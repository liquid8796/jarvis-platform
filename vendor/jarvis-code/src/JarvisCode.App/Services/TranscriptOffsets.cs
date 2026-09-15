namespace JarvisCode.App.Services;

/// <summary>
/// Where every row of a transcript starts, whether or not it has ever been built.
/// A row that has been measured contributes its real height and one that has not
/// contributes <see cref="TranscriptRowEstimates"/>'s guess, which is what lets a
/// scrollbar describe a thousand rows while a dozen exist.
///
/// Ported from the reference desktop's virtualizer (1.44121.4.0, ion-dist chunk
/// <c>ccb6edd6b-BJ2sSjUO.js</c>): the prefix sums are rebuilt lazily behind a dirty
/// flag rather than on every frame, which is its own answer to a streaming turn
/// dirtying the list several times a second.
/// </summary>
/// <typeparam name="T">The row. Only the two delegates ever look inside it.</typeparam>
public sealed class TranscriptOffsets<T>
{
    private readonly Func<T, int, string> _key;
    private readonly Func<T, int, double> _estimate;
    private readonly Dictionary<string, double> _sizes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _indexByKey = new(StringComparer.Ordinal);

    private IReadOnlyList<T> _rows = [];
    private double[] _offsets = [0];
    private bool _dirty = true;

    public TranscriptOffsets(Func<T, int, string> key, Func<T, int, double> estimate)
    {
        _key = key;
        _estimate = estimate;
    }

    /// <summary>Rows whose measured height differed from the estimate, for the counters.</summary>
    public int MeasuredCount => _sizes.Count;

    /// <summary>The rows this describes. Setting it invalidates the sums.</summary>
    public void SetRows(IReadOnlyList<T> rows)
    {
        _rows = rows;
        _dirty = true;
    }

    /// <summary>
    /// Forget the measurements of rows that are no longer in the list. The
    /// reference prunes only when the list has shrunk, because its keys are server
    /// uuids that are never re-issued — the only way one of its keys goes stale is
    /// the row leaving. This port's keys are derived from a row's position in the
    /// stored session, so rebuilding a transcript re-issues them all and a list
    /// that did not shrink can still be carrying orphans; pruning on every change
    /// is what keeps them from accumulating for the life of the window.
    /// </summary>
    public void PruneToRows()
    {
        if (_sizes.Count == 0)
        {
            return;
        }

        var live = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _rows.Count; i++)
        {
            live.Add(_key(_rows[i], i));
        }

        foreach (var key in _sizes.Keys.Where(key => !live.Contains(key)).ToList())
        {
            _sizes.Remove(key);
        }
    }

    /// <summary>The prefix sums, rebuilt if anything has moved since the last read.</summary>
    public IReadOnlyList<double> Offsets
    {
        get
        {
            if (!_dirty)
            {
                return _offsets;
            }

            int count = _rows.Count;
            if (_offsets.Length != count + 1)
            {
                _offsets = new double[count + 1];
            }

            _indexByKey.Clear();
            _offsets[0] = 0;
            for (int i = 0; i < count; i++)
            {
                var key = _key(_rows[i], i);
                _offsets[i + 1] = _offsets[i] +
                    (_sizes.TryGetValue(key, out var measured) ? measured : _estimate(_rows[i], i));
                _indexByKey[key] = i;
            }

            _dirty = false;
            return _offsets;
        }
    }

    /// <summary>The content height every row together takes.</summary>
    public double TotalSize => Offsets[^1];

    /// <summary>Where row <paramref name="index"/> starts.</summary>
    public double StartOf(int index) => Offsets[Math.Clamp(index, 0, _rows.Count)];

    /// <summary>The height row <paramref name="index"/> is being laid out at.</summary>
    public double SizeOf(int index)
    {
        var offsets = Offsets;
        return index < 0 || index >= _rows.Count ? 0 : offsets[index + 1] - offsets[index];
    }

    /// <summary>Where a row went, or -1 once it is gone.</summary>
    public int IndexOfKey(string key)
    {
        _ = Offsets;
        return _indexByKey.TryGetValue(key, out var index) ? index : -1;
    }

    /// <summary>A row's measured height, or null while it is still an estimate.</summary>
    public double? MeasuredOf(string key) =>
        _sizes.TryGetValue(key, out var size) ? size : null;

    /// <summary>Every measurement taken so far, for the session snapshot.</summary>
    public IReadOnlyDictionary<string, double> Sizes => _sizes;

    /// <summary>
    /// Take a measurement. Answers whether it moved anything, which is the signal
    /// the panel compensates the scroll position on.
    /// </summary>
    public bool Measure(string key, double height)
    {
        if (double.IsNaN(height) || double.IsInfinity(height) || height < 0)
        {
            return false;
        }

        if (_sizes.TryGetValue(key, out var previous) &&
            Math.Abs(height - previous) < TranscriptWindow.SizeTolerance)
        {
            return false;
        }

        _sizes[key] = height;
        _dirty = true;
        return true;
    }

    /// <summary>Seed the measurements a stored snapshot carries.</summary>
    public void Restore(IReadOnlyDictionary<string, double> sizes)
    {
        foreach (var (key, size) in sizes)
        {
            if (!double.IsNaN(size) && !double.IsInfinity(size) && size >= 0)
            {
                _sizes[key] = size;
            }
        }

        _dirty = true;
    }

    /// <summary>
    /// Throw every measurement away. The width, the transcript text size and the
    /// code font each invalidate the lot: a height measured at another width says
    /// nothing about this one.
    /// </summary>
    public void Reset()
    {
        _sizes.Clear();
        _dirty = true;
    }

    /// <summary>Mark the sums stale without touching what has been measured.</summary>
    public void Invalidate() => _dirty = true;
}
