using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Controls;

/// <summary>Which ruler a panel measures its rows with before it has built them.</summary>
public enum TranscriptRowMetric
{
    /// <summary>Transcript rows, estimated per kind by <see cref="TranscriptRowEstimates"/>.</summary>
    Transcript,

    /// <summary>A list whose rows are alike — diff lines, argument rows, a run's calls.</summary>
    Uniform,
}

/// <summary>
/// The transcript's rows, built only where somebody can see them. Ported from the
/// reference desktop's own virtualizer (1.44121.4.0, ion-dist chunk
/// <c>ccb6edd6b-BJ2sSjUO.js</c>, driven by its Code transcript
/// <c>cd5a31703-Bbz821zb.js</c> and by its Chat transcript
/// <c>ca2ef848d-BDGEfEJa.js</c> — one virtualizer serves both surfaces there and
/// one serves both here).
///
/// It is a plain <see cref="VirtualizingPanel"/> rather than an
/// <c>IScrollInfo</c> implementation on purpose: the panel reports the estimated
/// height of the whole transcript and arranges the rows it built at their real
/// offsets, so the surrounding <see cref="ScrollViewer"/> keeps its own pixel
/// scrolling and every affordance already hung off it — the wheel step, the
/// scroll-to-bottom pill, the disclosure anchor, drag-select autoscroll — goes on
/// working untouched.
///
/// What the reference does that this deliberately does not is its momentum-scroll
/// trick: while a touch gesture is in flight it folds the compensation into a CSS
/// <c>translateY</c> rather than writing <c>scrollTop</c>, because a browser's
/// inertial scroll fights a write. WPF's wheel scrolling has no such inertia to
/// fight, so the compensation is always the write, which is the branch its own
/// code takes off a gesture.
/// </summary>
public sealed class VirtualizingTranscriptPanel : VirtualizingPanel
{
    /// <summary>
    /// The panel that is laying an items control out, so a caller holding the list
    /// can reach the virtualizer under it.
    /// </summary>
    public static readonly DependencyProperty PanelProperty =
        DependencyProperty.RegisterAttached(
            "Panel", typeof(VirtualizingTranscriptPanel), typeof(VirtualizingTranscriptPanel),
            new PropertyMetadata(null));

    public static VirtualizingTranscriptPanel? GetPanel(DependencyObject element) =>
        (VirtualizingTranscriptPanel?)element.GetValue(PanelProperty);

    public static readonly DependencyProperty MetricProperty =
        DependencyProperty.Register(
            nameof(Metric), typeof(TranscriptRowMetric), typeof(VirtualizingTranscriptPanel),
            new FrameworkPropertyMetadata(
                TranscriptRowMetric.Transcript, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>What a row of a <see cref="TranscriptRowMetric.Uniform"/> list is worth.</summary>
    public static readonly DependencyProperty EstimatedRowHeightProperty =
        DependencyProperty.Register(
            nameof(EstimatedRowHeight), typeof(double), typeof(VirtualizingTranscriptPanel),
            new FrameworkPropertyMetadata(
                TranscriptRowEstimates.ToolRow, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public TranscriptRowMetric Metric
    {
        get => (TranscriptRowMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public double EstimatedRowHeight
    {
        get => (double)GetValue(EstimatedRowHeightProperty);
        set => SetValue(EstimatedRowHeightProperty, value);
    }

    private readonly TranscriptOffsets<object?> _offsets;
    private readonly TranscriptPerfCounters _counters = new();

    private ItemsView _rows = ItemsView.Empty;
    private ScrollViewer? _scroller;
    private UIElement? _scrollContent;
    private double _panelTop;
    private double _columnWidth = TranscriptEntrySplit.MeasurementWidth;
    private double _textScale = 1;
    private TranscriptWindow.Range _window = new(0, -1);
    private double _pendingCompensation;
    private bool _settlePending;
    private bool _rowsDirty = true;
    private bool _holdRealized;
    private bool _realizeAll;
    private int _mountFrames = TranscriptWindow.MountReleaseFrames;
    private bool _mountWatching;

    public VirtualizingTranscriptPanel()
    {
        _offsets = new TranscriptOffsets<object?>(KeyOf, Estimate);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // The reference tracks the focused row with a focusin/focusout pair on
        // the container; these are the two events that bubble the same way.
        AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnRowFocusChanged), true);
        AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnRowFocusChanged), true);
    }

    private void OnRowFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        TrackFocusedRow(Keyboard.FocusedElement as DependencyObject);

    /// <summary>What the virtualizer is doing, for <c>--transcript-selftest</c>.</summary>
    public TranscriptPerfCounters Counters => _counters;

    /// <summary>The rows currently built, inclusive.</summary>
    public TranscriptWindow.Range Window => _window;

    /// <summary>Every height measured so far, for the session snapshot.</summary>
    public IReadOnlyDictionary<string, double> Sizes => _offsets.Sizes;

    /// <summary>
    /// A correction is already in flight from somewhere else — the disclosure
    /// anchor, which measures the clicked row itself. Two corrections for one
    /// resize would move the view twice.
    /// </summary>
    public bool SuspendCompensation { get; set; }

    /// <summary>
    /// Stop evicting rows. A drag selection reaches across rows that scroll out
    /// behind it, and a selection whose anchor was virtualized away is a selection
    /// that cannot be copied — this is the reference's own <c>keepMountedRef</c>.
    /// </summary>
    public void HoldRealized() => _holdRealized = true;

    /// <summary>Let go of the held rows and of a full realization.</summary>
    public void ReleaseHold()
    {
        if (!_holdRealized && !_realizeAll)
        {
            return;
        }

        _holdRealized = false;
        _realizeAll = false;
        InvalidateMeasure();
    }

    /// <summary>
    /// Build every row, now. Only Select all asks for this: it is the one action
    /// whose answer is the whole transcript, and paying for the whole transcript
    /// when the reader asks for the whole transcript is the honest trade.
    /// </summary>
    public void RealizeAll()
    {
        _realizeAll = true;
        _holdRealized = true;
        InvalidateMeasure();
        UpdateLayout();
    }

    /// <summary>Seed the heights a stored snapshot carries, before the first pass.</summary>
    public void RestoreSizes(IReadOnlyDictionary<string, double> sizes)
    {
        _offsets.Restore(sizes);
        InvalidateMeasure();
    }

    /// <summary>
    /// The row the reader is looking at and how far into it they are, which is what
    /// a snapshot has to store: a scroll offset alone is meaningless once the rows
    /// above it have been re-estimated.
    /// </summary>
    public (string Key, double Offset)? AnchorAt(double verticalOffset)
    {
        if (_rows.Count == 0)
        {
            return null;
        }

        double scrollTop = Math.Max(0, verticalOffset - _panelTop);
        var offsets = _offsets.Offsets;
        int index = TranscriptWindow.IndexAt(offsets, scrollTop);
        return (KeyOf(_rows[index], index), scrollTop - offsets[index]);
    }

    /// <summary>Put a stored anchor back, or answer false when its row is gone.</summary>
    public bool ScrollToKey(string key, double offsetIntoRow)
    {
        int index = _offsets.IndexOfKey(key);
        if (index < 0 || _scroller is null)
        {
            return false;
        }

        _scroller.ScrollToVerticalOffset(_panelTop + _offsets.StartOf(index) + offsetIntoRow);
        return true;
    }

    /// <summary>Bring a row into view by index — what a prompt jump needs.</summary>
    public bool ScrollToIndex(int index)
    {
        if (_scroller is null || index < 0 || index >= _rows.Count)
        {
            return false;
        }

        _scroller.ScrollToVerticalOffset(_panelTop + _offsets.StartOf(index));
        return true;
    }

    /// <summary>Where a row sits in the scrolled content, whether or not it is built.</summary>
    public double OffsetOfRow(int index) => _panelTop + _offsets.StartOf(index);

    /// <summary>
    /// Forget every measurement. The column width, the transcript text size and the
    /// code font each invalidate the lot, because a height taken at one of them
    /// describes none of the others.
    /// </summary>
    public void ResetMeasurements()
    {
        _offsets.Reset();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Touching the children is what brings the item container generator to
        // life; reading it first hands back null.
        var children = InternalChildren;
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null)
        {
            return default;
        }

        if (!ReferenceEquals(GetPanel(owner), this))
        {
            owner.SetValue(PanelProperty, this);
        }

        double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? _columnWidth
            : availableSize.Width;

        double scale = MarkdownView.TranscriptTextSize / TranscriptTextSizes.Medium;
        if (Math.Abs(width - _columnWidth) > 0.5 || Math.Abs(scale - _textScale) > 0.001)
        {
            _columnWidth = width;
            _textScale = scale;
            _offsets.Reset();
        }

        if (_rowsDirty || _rows.Count != owner.Items.Count)
        {
            _rows = new ItemsView(owner.Items);
            _offsets.SetRows(_rows);
            _offsets.PruneToRows();
            _rowsDirty = false;
        }

        var range = ComputeWindow();
        _window = range;

        // Every generator member this needs is an explicit interface implementation.
        IItemContainerGenerator generator = ItemContainerGenerator;
        if (range.Last >= range.First)
        {
            var start = generator.GeneratorPositionFromIndex(range.First);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = range.First; i <= range.Last; i++, childIndex++)
                {
                    if (generator.GenerateNext(out bool isNew) is not UIElement child)
                    {
                        break;
                    }

                    if (isNew)
                    {
                        if (childIndex >= children.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }

                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(width, double.PositiveInfinity));
                }
            }
        }

        bool moved = TakeMeasurements(generator);
        Evict(generator, range);

        double total = _offsets.TotalSize;
        _counters.Observe(_rows.Count, children.Count, total, _offsets.MeasuredCount);

        if (moved)
        {
            ApplyCompensation();
            ScheduleSettle();
        }

        WatchMountFrames();
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;

        LocatePanelTop();
        var children = InternalChildren;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not UIElement child)
            {
                continue;
            }

            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0 || index >= _rows.Count)
            {
                continue;
            }

            double top = _offsets.StartOf(index);
            double height = child.DesiredSize.Height;
            child.Arrange(new Rect(0, top, finalSize.Width, height));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                // Another session's transcript is another first paint.
                _mountFrames = TranscriptWindow.MountReleaseFrames;
                break;
        }

        _rowsDirty = true;
        _offsets.Invalidate();
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    // ---- window ----

    private TranscriptWindow.Range ComputeWindow()
    {
        LocatePanelTop();

        if (_rows.Count == 0)
        {
            return new TranscriptWindow.Range(0, -1);
        }

        if (_realizeAll)
        {
            return new TranscriptWindow.Range(0, _rows.Count - 1);
        }

        var offsets = _offsets.Offsets;

        // The reference splits an entry into windowed rows only when it is heavy;
        // a light one is rendered whole. Carried here as the same decision about a
        // whole list: a short one costs less built than windowed, and an estimate
        // cannot be wrong about a row that was built.
        if (!TranscriptEntrySplit.ShouldSplit(_rows.Count, offsets[^1]))
        {
            return new TranscriptWindow.Range(0, _rows.Count - 1);
        }

        if (_scroller is null)
        {
            // Nothing to scroll yet: the reference's mount window builds downward
            // from the top and nothing above it.
            return TranscriptWindow.Compute(new TranscriptWindow.Inputs
            {
                Offsets = offsets,
                ViewportHeight = 0,
                ScrollTop = 0,
                OverscanTop = 0,
                OverscanBottom = TranscriptWindow.Overscan,
                MountPhase = true,
            });
        }

        // The reference hands its first-paint window to the transcript and to
        // nothing else. A nested list must not take it: its window is computed
        // against the transcript's own scroller, and until the first arrange has
        // said where the list sits that reading is meaningless — a narrow window
        // over a meaningless reading is how a two-row card renders one row.
        bool mount = _mountFrames > 0 && Metric == TranscriptRowMetric.Transcript;
        bool following = TranscriptScrolling.IsAtTail(
            _scroller.VerticalOffset, _scroller.ScrollableHeight);

        return TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = offsets,
            ViewportHeight = _scroller.ViewportHeight,
            ScrollTop = Math.Max(0, _scroller.VerticalOffset - _panelTop),
            ScrollableHeight = Math.Max(0, _scroller.ScrollableHeight - _panelTop),
            OverscanTop = mount ? 0 : TranscriptWindow.Overscan,
            OverscanBottom = TranscriptWindow.Overscan,
            Following = following,
            MountPhase = mount,
        });
    }

    /// <summary>
    /// Read every built row's height back into the offsets, and answer whether any
    /// of them moved. A row whose box is empty is skipped and counted rather than
    /// stored as zero: an element that has not been given a box yet would otherwise
    /// collapse the offsets under everything below it.
    /// </summary>
    private bool TakeMeasurements(IItemContainerGenerator? generator)
    {
        if (generator is null)
        {
            return false;
        }

        var children = InternalChildren;
        double anchorStart = 0;
        string? anchorKey = null;
        if (_scroller is not null && !TranscriptScrolling.IsAtTail(
                _scroller.VerticalOffset, _scroller.ScrollableHeight))
        {
            double scrollTop = Math.Max(0, _scroller.VerticalOffset - _panelTop);
            var before = _offsets.Offsets;
            int index = TranscriptWindow.IndexAt(before, scrollTop);
            anchorKey = KeyOf(_rows[index], index);
            anchorStart = before[index];
        }

        bool moved = false;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not UIElement child)
            {
                continue;
            }

            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0 || index >= _rows.Count)
            {
                continue;
            }

            var size = child.DesiredSize;
            if (size.Height <= 0 && size.Width <= 0)
            {
                _counters.CountBoxless();
                continue;
            }

            moved |= _offsets.Measure(KeyOf(_rows[index], index), size.Height);
        }

        if (moved && anchorKey is not null)
        {
            int now = _offsets.IndexOfKey(anchorKey);
            if (now >= 0)
            {
                _pendingCompensation += _offsets.StartOf(now) - anchorStart;
            }
        }

        return moved;
    }

    private void Evict(IItemContainerGenerator? generator, TranscriptWindow.Range range)
    {
        if (generator is null || _holdRealized)
        {
            return;
        }

        var children = InternalChildren;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if (index >= 0 && (range.Contains(index) || IsKeptMounted(index)))
            {
                continue;
            }

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    /// <summary>
    /// The reference's <c>keepMountedRef</c>: a set of indices unioned into the
    /// rendered range, so a listed row stays mounted after it leaves the window.
    /// Measured on desktop 1.46388.2.0 - its Chat transcript keeps the last
    /// <c>Zg = 3</c> rows and the row that holds focus, among other inputs.
    ///
    /// <para>
    /// The reference unions the set into the range it renders; this generator
    /// builds one contiguous run, so the same invariant is kept from the other
    /// end - a kept row that is already realized is not evicted. What that
    /// cannot do is create a row nobody has scrolled to, which is a difference
    /// in machinery rather than in what the reader sees: the point of the set is
    /// that a row survives leaving the window, and one that was never in it has
    /// nothing to survive.
    /// </para>
    /// </summary>
    private bool IsKeptMounted(int index)
    {
        if (_rows.Count == 0)
        {
            return false;
        }

        if (index >= Math.Max(0, _rows.Count - TranscriptWindow.KeepMountedTail))
        {
            return true;
        }

        return index == _focusedRow;
    }

    /// <summary>
    /// The row that holds keyboard focus, which the reference tracks with a
    /// focusin/focusout pair over its <c>data-index</c> attribute and keeps
    /// mounted for the same reason: a focused row evicted from under the reader
    /// takes the focus with it.
    /// </summary>
    private int _focusedRow = -1;

    private void TrackFocusedRow(DependencyObject? source)
    {
        _focusedRow = -1;
        source = HostVisual(source);
        if (source is null)
        {
            return;
        }

        IItemContainerGenerator generator = ItemContainerGenerator;
        var children = InternalChildren;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is UIElement child && child.IsAncestorOf(source))
            {
                _focusedRow = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                return;
            }
        }
    }

    /// <summary>
    /// The visual a focus target lives in. Keyboard focus does not only land on visuals: a
    /// <see cref="System.Windows.Documents.Hyperlink"/> is a content element, and a rendered answer
    /// is full of them — a file reference, a URL, the dev-server address a preview row prints. Asking
    /// a row whether it is an ancestor of one throws ("'System.Windows.Documents.Hyperlink' is not a
    /// Visual or Visual3D"), and from a focus handler that reaches the window as a crash dialog over
    /// a turn that was working. A content element is walked up to whatever hosts it, which is the
    /// thing a row can actually be asked about.
    /// </summary>
    internal static DependencyObject? HostVisual(DependencyObject? source)
    {
        while (source is not null and not Visual and not System.Windows.Media.Media3D.Visual3D)
        {
            source = source switch
            {
                FrameworkContentElement framework => framework.Parent ?? ContentOperations.GetParent(framework),
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(source),
            };
        }

        return source;
    }

    private void ApplyCompensation()
    {
        if (SuspendCompensation || _scroller is null ||
            Math.Abs(_pendingCompensation) < TranscriptWindow.SizeTolerance)
        {
            return;
        }

        double delta = _pendingCompensation;
        _pendingCompensation = 0;
        _counters.CountCompensation(delta);
        double target = Math.Max(0, _scroller.VerticalOffset + delta);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Render, () => _scroller?.ScrollToVerticalOffset(target));
    }

    private void ScheduleSettle()
    {
        if (_settlePending)
        {
            return;
        }

        _settlePending = true;
        _counters.CountSettleDeferral();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _settlePending = false;
            InvalidateMeasure();
        });
    }

    /// <summary>
    /// The reference releases its first-paint window after two presented frames, so
    /// this counts real frames rather than layout passes: a session opens paying for
    /// one screen, and the rows above the anchor arrive once that screen is up.
    /// </summary>
    private void WatchMountFrames()
    {
        if (_mountFrames <= 0 || _mountWatching || _rows.Count == 0)
        {
            return;
        }

        _mountWatching = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (--_mountFrames > 0)
        {
            return;
        }

        CompositionTarget.Rendering -= OnFrame;
        _mountWatching = false;
        InvalidateMeasure();
    }

    // ---- plumbing ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scroller = FindScroller();
        _scrollContent = _scroller?.Content as UIElement;
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
            _scroller.ScrollChanged += OnScrollChanged;
        }

        MarkdownView.TranscriptTextSizeChanged -= OnTextSizeChanged;
        MarkdownView.TranscriptTextSizeChanged += OnTextSizeChanged;
        InvalidateMeasure();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
        }

        MarkdownView.TranscriptTextSizeChanged -= OnTextSizeChanged;
        if (_mountWatching)
        {
            CompositionTarget.Rendering -= OnFrame;
            _mountWatching = false;
        }
    }

    private void OnTextSizeChanged(object? sender, EventArgs e) => ResetMeasurements();

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
        {
            InvalidateMeasure();
        }
    }

    private ScrollViewer? FindScroller()
    {
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer)
            {
                return viewer;
            }
        }

        return null;
    }

    /// <summary>
    /// Where this panel starts inside the scrolled content. The transcript's own
    /// list is the first thing in it, so this is normally zero — but the panel also
    /// lays out the lists inside an expanded body, and those sit wherever their card
    /// put them.
    /// </summary>
    private void LocatePanelTop()
    {
        if (_scrollContent is null || !IsLoaded)
        {
            return;
        }

        try
        {
            double top = TransformToAncestor(_scrollContent).Transform(default).Y;
            if (Math.Abs(top - _panelTop) > TranscriptWindow.SizeTolerance)
            {
                _panelTop = top;
                // The window was computed against the old reading, so the rows it
                // chose describe somewhere else. Take the pass again.
                Dispatcher.BeginInvoke(DispatcherPriority.Render, InvalidateMeasure);
            }
        }
        catch (InvalidOperationException)
        {
            // Not laid out under that content yet; the previous value still holds.
        }
    }

    private string KeyOf(object? item, int index) =>
        item is TranscriptItem row
            ? row.RowKey
            : index.ToString(CultureInfo.InvariantCulture);

    private double Estimate(object? item, int index)
    {
        _ = index;
        return Metric == TranscriptRowMetric.Uniform || item is not TranscriptItem row
            ? EstimatedRowHeight
            : TranscriptRowEstimates.For(row, _columnWidth, _textScale);
    }

    /// <summary>An items control's collection as an indexable list, without copying it.</summary>
    private sealed class ItemsView(IList items) : IReadOnlyList<object?>
    {
        public static readonly ItemsView Empty = new(Array.Empty<object?>());

        public object? this[int index] => index >= 0 && index < items.Count ? items[index] : null;

        public int Count => items.Count;

        public IEnumerator<object?> GetEnumerator()
        {
            for (int i = 0; i < items.Count; i++)
            {
                yield return items[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
