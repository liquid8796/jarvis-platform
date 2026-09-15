using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>Where a drop would put the dragged tile, and the rectangle that says so.</summary>
public sealed record TileDropFeedback(TileDropTarget Target, Rect Rect, bool IsSplit);

/// <summary>
/// The pane tile host: one panel that arranges every open pane at the rectangle the tile
/// tree gives it. Ported from the desktop app's own tile layout (ion-dist chunk
/// cde8ce059-Bx5SPiHJ.js — its w() walks the tree resolving each child's flex and pixel
/// size, and its x() shares the pixels out with the minimums pinned).
///
/// Every tile the workspace has ever opened stays a child of this panel; a closed one is
/// Collapsed rather than removed, so a pane holding an engine view keeps its window
/// across an open/close cycle exactly as the single-panel host did.
/// </summary>
public sealed class TileHostPanel : Panel
{
    private TileLayout _layout = TileLayoutOps.Single(TileLayout.ChatTileId);
    private readonly Dictionary<string, Rect> _tileRects = new(StringComparer.Ordinal);
    private readonly List<(TileStack Stack, int Index, Rect Rect)> _boundaries = [];

    /// <summary>The tile a child element stands for.</summary>
    public static readonly DependencyProperty TileIdProperty =
        DependencyProperty.RegisterAttached(
            "TileId", typeof(string), typeof(TileHostPanel), new PropertyMetadata(null));

    public static string? GetTileId(DependencyObject element) => (string?)element.GetValue(TileIdProperty);

    public static void SetTileId(DependencyObject element, string? value) => element.SetValue(TileIdProperty, value);

    /// <summary>Children that float over the tiles (the drop indicator) rather than being one.</summary>
    public static readonly DependencyProperty IsOverlayProperty =
        DependencyProperty.RegisterAttached(
            "IsOverlay", typeof(bool), typeof(TileHostPanel), new PropertyMetadata(false));

    public static bool GetIsOverlay(DependencyObject element) => (bool)element.GetValue(IsOverlayProperty);

    public static void SetIsOverlay(DependencyObject element, bool value) => element.SetValue(IsOverlayProperty, value);

    /// <summary>The tile tree this panel draws.</summary>
    public TileLayout Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Tiles the layout still names but that are not drawn — the reference's hiddenTileIds.</summary>
    public IReadOnlyCollection<string> HiddenTileIds { get; set; } = [];

    /// <summary>The rectangle the drop indicator should cover, or null when nothing is being dragged.</summary>
    public Rect? DropIndicator { get; set; }

    /// <summary>The rectangles the last arrange gave each visible tile.</summary>
    public IReadOnlyDictionary<string, Rect> TileRects => _tileRects;

    private TileNode EffectiveRoot =>
        HiddenTileIds.Count == 0
            ? _layout.Root
            : TileLayoutOps.Filter(_layout.Root, id => !HiddenTileIds.Contains(id)) ?? _layout.Root;

    private static double? OverflowMin(string tileId) =>
        tileId == TileLayout.ChatTileId ? TileLayout.ChatOverflowMinWidth : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        Resolve(new Rect(TileLayout.Padding, TileLayout.Padding,
            Math.Max(0, width - 2 * TileLayout.Padding),
            Math.Max(0, height - 2 * TileLayout.Padding)));

        foreach (UIElement child in InternalChildren)
        {
            if (GetIsOverlay(child))
            {
                child.Measure(availableSize);
                continue;
            }

            var id = GetTileId(child);
            if (id is not null && _tileRects.TryGetValue(id, out var rect))
            {
                child.Measure(rect.Size);
            }
            else
            {
                child.Measure(new Size(0, 0));
            }
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Resolve(new Rect(TileLayout.Padding, TileLayout.Padding,
            Math.Max(0, finalSize.Width - 2 * TileLayout.Padding),
            Math.Max(0, finalSize.Height - 2 * TileLayout.Padding)));

        foreach (UIElement child in InternalChildren)
        {
            if (GetIsOverlay(child))
            {
                child.Arrange(DropIndicator ?? new Rect(0, 0, 0, 0));
                continue;
            }

            var id = GetTileId(child);
            if (id is not null && _tileRects.TryGetValue(id, out var rect))
            {
                child.Visibility = Visibility.Visible;
                child.Arrange(rect);
            }
            else
            {
                child.Visibility = Visibility.Collapsed;
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return finalSize;
    }

    /// <summary>Walks the tree, filling the tile rectangles and the resize boundaries.</summary>
    private void Resolve(Rect bounds)
    {
        _tileRects.Clear();
        _boundaries.Clear();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Place(EffectiveRoot, bounds);
    }

    private void Place(TileNode node, Rect rect)
    {
        if (node is TileLeaf leaf)
        {
            _tileRects[leaf.TileId] = rect;
            return;
        }

        var stack = (TileStack)node;
        var count = stack.Children.Count;
        if (count == 0)
        {
            return;
        }

        var horizontal = stack.Direction == TileDirection.Row;
        var extent = horizontal ? rect.Width : rect.Height;
        var available = Math.Max(0, extent - TileLayout.Gap * (count - 1));
        var minimums = stack.Children
            .Select(child => TileLayoutOps.MinSize(child, stack.Direction, OverflowMin))
            .ToList();
        var flexes = stack.Children.Select(child => child.Flex).ToList();
        var resolved = TileLayoutOps.ResolveFlex(flexes, minimums, available);
        var total = resolved.Sum();
        if (total <= 0)
        {
            total = count;
        }

        // The reference rounds each running edge rather than each size, so the last tile
        // lands exactly on the container edge however the flexes divide.
        double running = 0;
        double consumed = 0;
        var sizes = new double[count];
        for (int i = 0; i < count; i++)
        {
            consumed += resolved[i];
            var edge = Math.Round(consumed / total * available);
            sizes[i] = edge - running;
            running = edge;
        }

        var offset = horizontal ? rect.Left : rect.Top;
        for (int i = 0; i < count; i++)
        {
            var childRect = horizontal
                ? new Rect(offset, rect.Top, sizes[i], rect.Height)
                : new Rect(rect.Left, offset, rect.Width, sizes[i]);
            Place(stack.Children[i], childRect);
            offset += sizes[i];
            if (i < count - 1)
            {
                var gapRect = horizontal
                    ? new Rect(offset, rect.Top, TileLayout.Gap, rect.Height)
                    : new Rect(rect.Left, offset, rect.Width, TileLayout.Gap);
                _boundaries.Add((stack, i, gapRect));
                offset += TileLayout.Gap;
            }
        }
    }

    /// <summary>The resize boundary under a point, if any.</summary>
    public (TileStack Stack, int Index, Rect Rect)? BoundaryAt(Point point)
    {
        foreach (var boundary in _boundaries)
        {
            if (boundary.Rect.Contains(point))
            {
                return boundary;
            }
        }

        return null;
    }

    /// <summary>Every boundary the last arrange produced, for the resize cursor.</summary>
    public IReadOnlyList<(TileStack Stack, int Index, Rect Rect)> Boundaries => _boundaries;

    /// <summary>
    /// Where a tile dragged to this point would land. The rules are the reference's own
    /// drop rects: a gap-wide strip along each edge of the root splits it, the leading or
    /// trailing 24px band of any other tile wraps that tile in a perpendicular stack, and
    /// anywhere else inside the dragged tile's own stack reorders it.
    /// </summary>
    public TileDropFeedback? DropTargetAt(Point point, string draggedTileId)
    {
        var root = EffectiveRoot as TileStack;
        if (root is null || _tileRects.Count == 0)
        {
            return null;
        }

        var bounds = new Rect(
            TileLayout.Padding, TileLayout.Padding,
            Math.Max(0, ActualWidth - 2 * TileLayout.Padding),
            Math.Max(0, ActualHeight - 2 * TileLayout.Padding));
        var horizontalRoot = root.Direction == TileDirection.Row;

        // Root edges first, the way the reference pushes them before every other rect.
        if (point.Y <= bounds.Top + TileLayout.Gap)
        {
            return Split(root, 0, TileDirection.Column,
                new Rect(bounds.Left, bounds.Top, bounds.Width, TileLayout.Gap));
        }

        if (point.Y >= bounds.Bottom - TileLayout.Gap)
        {
            return Split(root, horizontalRoot ? 1 : root.Children.Count, TileDirection.Column,
                new Rect(bounds.Left, bounds.Bottom - TileLayout.Gap, bounds.Width, TileLayout.Gap));
        }

        if (point.X <= bounds.Left + TileLayout.Gap)
        {
            return Split(root, 0, TileDirection.Row,
                new Rect(bounds.Left, bounds.Top, TileLayout.Gap, bounds.Height));
        }

        if (point.X >= bounds.Right - TileLayout.Gap)
        {
            return Split(root, horizontalRoot ? root.Children.Count : 1, TileDirection.Row,
                new Rect(bounds.Right - TileLayout.Gap, bounds.Top, TileLayout.Gap, bounds.Height));
        }

        foreach (var (tileId, rect) in _tileRects)
        {
            if (tileId == draggedTileId || !rect.Contains(point))
            {
                continue;
            }

            var path = TileLayoutOps.Path(EffectiveRoot, tileId);
            var parent = path is null ? null : TileLayoutOps.StackAt(EffectiveRoot, path);
            if (parent is null)
            {
                continue;
            }

            // The wrap band runs across the parent's cross axis, which is the direction
            // the new stack takes.
            var crossHorizontal = parent.Direction != TileDirection.Row;
            var leading = crossHorizontal
                ? new Rect(rect.Left, rect.Top, TileLayout.EdgeBandPx, rect.Height)
                : new Rect(rect.Left, rect.Top, rect.Width, TileLayout.EdgeBandPx);
            var trailing = crossHorizontal
                ? new Rect(rect.Right - TileLayout.EdgeBandPx, rect.Top, TileLayout.EdgeBandPx, rect.Height)
                : new Rect(rect.Left, rect.Bottom - TileLayout.EdgeBandPx, rect.Width, TileLayout.EdgeBandPx);
            var direction = crossHorizontal ? TileDirection.Row : TileDirection.Column;
            if (leading.Contains(point))
            {
                return new TileDropFeedback(new TileWrapTarget(tileId, direction, 0), leading, IsSplit: true);
            }

            if (trailing.Contains(point))
            {
                return new TileDropFeedback(new TileWrapTarget(tileId, direction, 1), trailing, IsSplit: true);
            }

            // Otherwise the drop reorders: the dragged tile takes this tile's slot in the
            // stack it belongs to.
            var index = path![^1];
            return new TileDropFeedback(new TileInsertTarget(parent.Id, index), rect, IsSplit: false);
        }

        return null;
    }

    private static TileDropFeedback Split(TileStack stack, int index, TileDirection direction, Rect rect) =>
        new(new TileSplitTarget(stack.Id, index, direction), rect, IsSplit: true);
}
