using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>Which way a stack lays its children out.</summary>
public enum TileDirection
{
    Row,
    Column,
}

/// <summary>A node of the pane tile tree: either one pane, or a stack of nodes.</summary>
public abstract record TileNode
{
    /// <summary>The share of its parent's main axis this node asks for.</summary>
    public double Flex { get; init; } = 1;
}

/// <summary>One pane in the tile tree, named by its pane key ("chat", "terminal", …).</summary>
public sealed record TileLeaf(string TileId) : TileNode;

/// <summary>A row or column of tiles.</summary>
public sealed record TileStack(string Id, TileDirection Direction, IReadOnlyList<TileNode> Children) : TileNode;

/// <summary>Where a dragged tile would land.</summary>
public abstract record TileDropTarget;

/// <summary>Reorder inside the stack the tile is already in.</summary>
public sealed record TileInsertTarget(string StackId, int Index) : TileDropTarget;

/// <summary>Split a stack — a new child at Index, laid out along Direction.</summary>
public sealed record TileSplitTarget(string StackId, int Index, TileDirection Direction) : TileDropTarget;

/// <summary>Wrap one tile in a new stack, the dragged tile before (0) or after (1) it.</summary>
public sealed record TileWrapTarget(string TileId, TileDirection Direction, int Index) : TileDropTarget;

/// <summary>
/// The pane tile layout: a mosaic tree whose root is always a stack.
/// Ported from the desktop app's own tile module (ion-dist chunk
/// cde8ce059-Bx5SPiHJ.js, whose M/b/v/R/P/S/T/D/y are Normalize/EnsureRoot/
/// FromSpec/AppendRight/AppendBelow/Solo/Remove/Rename/Move here), with its
/// config constants read from c360a9e1c-DUoNQd2W.js's Tq.
/// </summary>
public sealed record TileLayout(TileStack Root)
{
    /// <summary>The gap between two tiles, in device-independent pixels (the reference's Tq.gap).</summary>
    public const double Gap = 12;

    /// <summary>The padding around the whole tile host (Tq.padding).</summary>
    public const double Padding = 8;

    /// <summary>The smallest a tile may become on its cross axis (Tq.minTileBasePx).</summary>
    public const double MinTileBasePx = 100;

    /// <summary>The smallest a tile may become across a row (Tq.minTileWidthPx).</summary>
    public const double MinTileWidthPx = 280;

    /// <summary>How far a dragged tile lifts off the surface (Tq.dragLift).</summary>
    public const double DragLift = 24;

    /// <summary>The leading/trailing band of a tile that means "wrap", not "reorder" (the module's k).</summary>
    public const double EdgeBandPx = 24;

    /// <summary>The conversation tile's own key; it is a tile like any other.</summary>
    public const string ChatTileId = "chat";

    /// <summary>The conversation refuses to go below this width (the reference's overflowMin).</summary>
    public const double ChatOverflowMinWidth = 320;

    public IEnumerable<string> TileIds => TileLayoutOps.TileIds(Root);

    public bool Contains(string tileId) => TileLayoutOps.Path(Root, tileId) is not null;
}

/// <summary>The tile tree's operations, kept plain so they can be unit tested without a window.</summary>
public static class TileLayoutOps
{
    private static int _stackCounter;
    private static readonly string StackSalt = Guid.NewGuid().ToString("N")[..8];

    /// <summary>A fresh stack id, shaped like the reference's stack-{rand}-{n}.</summary>
    public static string NewStackId() => $"stack-{StackSalt}-{Interlocked.Increment(ref _stackCounter)}";

    /// <summary>A node's key: a stack answers with its id, a tile with its pane key.</summary>
    public static string NodeKey(TileNode node) =>
        node is TileStack stack ? stack.Id : ((TileLeaf)node).TileId;

    // ---- lookups ----

    /// <summary>Every pane key in the tree, in layout order.</summary>
    public static IEnumerable<string> TileIds(TileNode node)
    {
        if (node is TileLeaf leaf)
        {
            yield return leaf.TileId;
            yield break;
        }

        foreach (var child in ((TileStack)node).Children)
        {
            foreach (var id in TileIds(child))
            {
                yield return id;
            }
        }
    }

    /// <summary>The child indexes leading to a tile, or null when it is not in the tree.</summary>
    public static IReadOnlyList<int>? Path(TileNode node, string tileId, List<int>? prefix = null)
    {
        prefix ??= [];
        if (node is TileLeaf leaf)
        {
            return leaf.TileId == tileId ? prefix.ToArray() : null;
        }

        var children = ((TileStack)node).Children;
        for (int i = 0; i < children.Count; i++)
        {
            var found = Path(children[i], tileId, [.. prefix, i]);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The stack holding the node the path names.</summary>
    public static TileStack? StackAt(TileNode root, IReadOnlyList<int> path)
    {
        if (path.Count == 0)
        {
            return null;
        }

        var node = root;
        for (int i = 0; i < path.Count - 1; i++)
        {
            if (node is not TileStack stack || path[i] >= stack.Children.Count)
            {
                return null;
            }

            node = stack.Children[path[i]];
        }

        return node as TileStack;
    }

    /// <summary>The stack with this id, anywhere under the given node.</summary>
    public static TileStack? FindStack(TileNode node, string stackId)
    {
        if (node is not TileStack stack)
        {
            return null;
        }

        if (stack.Id == stackId)
        {
            return stack;
        }

        foreach (var child in stack.Children)
        {
            if (FindStack(child, stackId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Whether a tile sits under a column with more than one child — the reference's u.</summary>
    public static bool IsStacked(TileNode root, string tileId)
    {
        var path = Path(root, tileId);
        if (path is null)
        {
            return false;
        }

        var node = root;
        foreach (var index in path)
        {
            if (node is not TileStack stack)
            {
                return false;
            }

            if (stack.Direction == TileDirection.Column && stack.Children.Count > 1)
            {
                return true;
            }

            node = stack.Children[index];
        }

        return false;
    }

    // ---- shaping ----

    /// <summary>
    /// The reference's M: empty stacks go, a child stack of the same direction is
    /// flattened into its parent, and a stack left with one child collapses into it while
    /// keeping the parent's flex.
    /// </summary>
    public static TileNode Normalize(TileNode node)
    {
        if (node is TileLeaf)
        {
            return node;
        }

        var stack = (TileStack)node;
        var kept = new List<TileNode>();
        foreach (var child in stack.Children)
        {
            var normalized = Normalize(child);
            if (normalized is TileLeaf || ((TileStack)normalized).Children.Count > 0)
            {
                kept.Add(normalized);
            }
        }

        var flattened = new List<TileNode>();
        foreach (var child in kept)
        {
            if (child is TileStack inner && inner.Direction == stack.Direction)
            {
                flattened.AddRange(inner.Children);
            }
            else
            {
                flattened.Add(child);
            }
        }

        if (flattened.Count == 1)
        {
            return flattened[0] with { Flex = stack.Flex };
        }

        return stack with { Children = flattened };
    }

    /// <summary>The reference's b: a bare tile becomes a one-child row.</summary>
    public static TileStack EnsureRoot(TileNode node) =>
        node as TileStack ?? new TileStack(NewStackId(), TileDirection.Row, [node]) { Flex = 1 };

    private static TileLayout Rebuild(TileNode root) => new(EnsureRoot(Normalize(root)));

    /// <summary>A layout holding one pane.</summary>
    public static TileLayout Single(string tileId) => Rebuild(new TileLeaf(tileId));

    // ---- editing ----

    /// <summary>The reference's R: the pane joins the root row on the right.</summary>
    public static TileLayout AppendRight(TileLayout layout, string tileId)
    {
        var leaf = new TileLeaf(tileId);
        var root = layout.Root;
        TileNode next = root.Direction == TileDirection.Row
            ? root with { Children = [.. root.Children, leaf] }
            : new TileStack(NewStackId(), TileDirection.Row, [root with { Flex = 1 }, leaf]) { Flex = 1 };
        return Rebuild(next);
    }

    /// <summary>
    /// The reference's P: the pane joins the anchor's column, or the anchor is
    /// wrapped in a new column with the pane below it.
    /// </summary>
    public static TileLayout AppendBelow(TileLayout layout, string anchorTileId, string tileId)
    {
        var path = Path(layout.Root, anchorTileId);
        var parent = path is null ? null : StackAt(layout.Root, path);
        if (path is null || parent is null)
        {
            return AppendRight(layout, tileId);
        }

        var leaf = new TileLeaf(tileId);
        var index = path[^1];
        var root = Replace(layout.Root, parent.Id, stack =>
        {
            if (stack.Direction == TileDirection.Column)
            {
                return stack with { Children = [.. stack.Children, leaf] };
            }

            var anchor = stack.Children[index];
            var wrapped = new TileStack(NewStackId(), TileDirection.Column, [anchor with { Flex = 1 }, leaf])
            {
                Flex = anchor.Flex,
            };
            var children = stack.Children.ToList();
            children[index] = wrapped;
            return stack with { Children = children };
        });
        return Rebuild(root);
    }

    /// <summary>The reference's T: the pane leaves the tree.</summary>
    public static TileLayout Remove(TileLayout layout, string tileId)
    {
        var path = Path(layout.Root, tileId);
        var parent = path is null ? null : StackAt(layout.Root, path);
        if (path is null || parent is null)
        {
            return layout;
        }

        var index = path[^1];
        var root = Replace(layout.Root, parent.Id, stack =>
        {
            var children = stack.Children.ToList();
            children.RemoveAt(index);
            return stack with { Children = children };
        });
        return Rebuild(root);
    }

    /// <summary>The reference's D: one pane key becomes another, in place.</summary>
    public static TileLayout Rename(TileLayout layout, string tileId, string newTileId)
    {
        var path = Path(layout.Root, tileId);
        var parent = path is null ? null : StackAt(layout.Root, path);
        if (path is null || parent is null || parent.Children[path[^1]] is not TileLeaf leaf)
        {
            return layout;
        }

        var index = path[^1];
        var root = Replace(layout.Root, parent.Id, stack =>
        {
            var children = stack.Children.ToList();
            children[index] = new TileLeaf(newTileId) { Flex = leaf.Flex };
            return stack with { Children = children };
        });
        return new TileLayout((TileStack)root);
    }

    /// <summary>The reference's E: keep the tiles a predicate accepts.</summary>
    public static TileNode? Filter(TileNode node, Func<string, bool> keep)
    {
        if (node is TileLeaf leaf)
        {
            return keep(leaf.TileId) ? leaf : null;
        }

        var stack = (TileStack)node;
        var children = stack.Children.Select(child => Filter(child, keep)).OfType<TileNode>().ToList();
        return children.Count > 0 ? stack with { Children = children } : null;
    }

    /// <summary>
    /// The reference's S: the layout narrowed to one pane (and optionally the
    /// conversation beside it). Null when the pane the caller asked for is not there.
    /// </summary>
    public static TileLayout? Solo(TileLayout layout, string tileId, string? alsoKeep = null)
    {
        var filtered = Filter(layout.Root, id => id == tileId || (alsoKeep is not null && id == alsoKeep));
        if (filtered is null || Path(filtered, tileId) is null)
        {
            return null;
        }

        return new TileLayout(EnsureRoot(filtered));
    }

    /// <summary>
    /// The reference's y: the dragged pane moves to a drop target — reordered
    /// inside its own stack, wrapped around another tile, or splitting a stack.
    /// </summary>
    public static TileLayout Move(TileLayout layout, string tileId, TileDropTarget target)
    {
        var path = Path(layout.Root, tileId);
        var parent = path is null ? null : StackAt(layout.Root, path);
        if (path is null || parent is null)
        {
            return layout;
        }

        var index = path[^1];
        var moving = parent.Children[index];

        if (target is TileInsertTarget insert && insert.StackId == parent.Id)
        {
            var reordered = Replace(layout.Root, parent.Id, stack =>
            {
                var children = stack.Children.Where((_, i) => i != index).ToList();
                children.Insert(Math.Clamp(insert.Index, 0, children.Count), moving);
                return stack with { Children = children };
            });
            return Rebuild(reordered);
        }

        // Everything else takes the tile out first, then puts it where the target says.
        var lifted = Replace(layout.Root, parent.Id, stack =>
        {
            var children = stack.Children.ToList();
            children.RemoveAt(index);
            return stack with { Children = children };
        });

        if (target is TileWrapTarget wrap)
        {
            var wrapPath = Path(lifted, wrap.TileId);
            var wrapParent = wrapPath is null ? null : StackAt(lifted, wrapPath);
            if (wrapPath is null || wrapParent is null)
            {
                return layout;
            }

            var wrapIndex = wrapPath[^1];
            var anchor = wrapParent.Children[wrapIndex];
            var leaf = new TileLeaf(tileId) { Flex = 1 };
            var stackNode = new TileStack(
                NewStackId(),
                wrap.Direction,
                wrap.Index == 0 ? [leaf, anchor with { Flex = 1 }] : [anchor with { Flex = 1 }, leaf])
            {
                Flex = anchor.Flex,
            };
            var wrapped = Replace(lifted, wrapParent.Id, stack =>
            {
                var children = stack.Children.ToList();
                children[wrapIndex] = stackNode;
                return stack with { Children = children };
            });
            return Rebuild(wrapped);
        }

        var targetStackId = target switch
        {
            TileSplitTarget split => split.StackId,
            TileInsertTarget other => other.StackId,
            _ => null,
        };
        if (targetStackId is null || FindStack(lifted, targetStackId) is not { } targetStack)
        {
            return layout;
        }

        if (target is TileSplitTarget cross && cross.Direction != targetStack.Direction)
        {
            var leaf = new TileLeaf(tileId) { Flex = 1 };
            var replacement = new TileStack(
                NewStackId(),
                cross.Direction,
                cross.Index == 0 ? [leaf, targetStack with { Flex = 1 }] : [targetStack with { Flex = 1 }, leaf])
            {
                Flex = targetStack.Flex,
            };
            var reparented = ReplaceNode(lifted, targetStackId, replacement);
            return Rebuild(reparented ?? replacement);
        }

        var insertIndex = target switch
        {
            TileSplitTarget split => split.Index,
            TileInsertTarget other => other.Index,
            _ => 0,
        };
        if (targetStack.Id == parent.Id && insertIndex > index)
        {
            insertIndex--;
        }

        insertIndex = Math.Clamp(insertIndex, 0, targetStack.Children.Count);
        var flex = target is TileSplitTarget ? 1 : (moving as TileLeaf)?.Flex ?? 1;
        var placed = Replace(lifted, targetStackId, stack =>
        {
            var children = stack.Children.ToList();
            children.Insert(Math.Min(insertIndex, children.Count), new TileLeaf(tileId) { Flex = flex });
            return stack with { Children = children };
        });
        return Rebuild(placed);
    }

    /// <summary>Writes new flex shares onto one stack's children, leaving the shape alone.</summary>
    public static TileLayout SetFlexes(TileLayout layout, string stackId, IReadOnlyList<double> flexes)
    {
        var root = Replace(layout.Root, stackId, stack =>
        {
            if (flexes.Count != stack.Children.Count)
            {
                return stack;
            }

            return stack with
            {
                Children = [.. stack.Children.Select((child, i) => child with { Flex = flexes[i] })],
            };
        });
        return new TileLayout((TileStack)root);
    }

    /// <summary>Resizes the boundary between two children — the reference's p.</summary>
    public static IReadOnlyList<double> ResizeBoundary(
        IReadOnlyList<double> flexes, int boundaryIndex, double deltaPx, double totalPx,
        double minBefore, double minAfter)
    {
        var next = flexes.ToArray();
        var before = boundaryIndex;
        var after = boundaryIndex + 1;
        if (before < 0 || after >= next.Length || totalPx <= 0)
        {
            return next;
        }

        var delta = deltaPx / totalPx;
        var lowBefore = minBefore / totalPx;
        var lowAfter = minAfter / totalPx;
        var pair = next[before] + next[after];
        if (lowBefore + lowAfter > pair)
        {
            return next;
        }

        var value = Math.Clamp(next[before] + delta, lowBefore, pair - lowAfter);
        next[before] = value;
        next[after] = pair - value;
        return next;
    }

    /// <summary>
    /// The reference's x: flexes shared out over the available pixels, with any
    /// child that falls under its minimum pinned there and the rest re-shared. The result
    /// is renormalized so it sums to the child count, the way the reference stores flex.
    /// </summary>
    public static IReadOnlyList<double> ResolveFlex(
        IReadOnlyList<double> flexes, IReadOnlyList<double> minimums, double available)
    {
        var count = flexes.Count;
        if (count == 0 || available <= 0)
        {
            return flexes.ToArray();
        }

        var total = flexes.Sum();
        if (total <= 0)
        {
            total = count;
        }

        var pinned = new HashSet<int>();
        var sizes = flexes.Select(f => f / total * available).ToArray();
        while (true)
        {
            var under = -1;
            for (int i = 0; i < sizes.Length; i++)
            {
                if (!pinned.Contains(i) && sizes[i] < minimums[i] - 0.5)
                {
                    under = i;
                    break;
                }
            }

            if (under < 0)
            {
                break;
            }

            pinned.Add(under);
            var pinnedSize = pinned.Sum(i => minimums[i]);
            var freeFlex = flexes.Where((_, i) => !pinned.Contains(i)).Sum();
            var freeSize = available - pinnedSize;
            if (freeFlex <= 0 || freeSize <= 0)
            {
                var minTotal = minimums.Sum();
                if (minTotal <= 0)
                {
                    minTotal = 1;
                }

                sizes = minimums.Select(m => m / minTotal * available).ToArray();
                break;
            }

            sizes = flexes
                .Select((f, i) => pinned.Contains(i) ? minimums[i] : f / freeFlex * freeSize)
                .ToArray();
        }

        var sum = sizes.Sum();
        if (sum <= 0)
        {
            sum = count;
        }

        return sizes.Select(s => s * count / sum).ToArray();
    }

    /// <summary>
    /// The reference's I: how small a node may become along the given direction.
    /// A row asks for MinTileWidthPx, a column for the base minimum, and a tile with its
    /// own overflow minimum (the conversation) asks for that instead.
    /// </summary>
    public static double MinSize(
        TileNode node, TileDirection direction, Func<string, double?> overflowMin, bool overflowReachable = true)
    {
        var floor = direction == TileDirection.Row ? TileLayout.MinTileWidthPx : TileLayout.MinTileBasePx;
        if (node is TileLeaf leaf)
        {
            if (direction == TileDirection.Row && overflowMin(leaf.TileId) is { } own)
            {
                return overflowReachable
                    ? TileLayout.MinTileBasePx
                    : Math.Max(own, TileLayout.MinTileBasePx);
            }

            return floor;
        }

        var stack = (TileStack)node;
        var children = stack.Children.Select(c => MinSize(c, direction, overflowMin, overflowReachable)).ToList();
        return stack.Direction == direction
            ? children.Sum() + TileLayout.Gap * Math.Max(0, stack.Children.Count - 1)
            : Math.Max(floor, children.Count == 0 ? 0 : children.Max());
    }

    // ---- persistence ----

    /// <summary>The layout as JSON, for ui-settings.json.</summary>
    public static JsonNode ToJson(TileNode node) => node switch
    {
        TileLeaf leaf => new JsonObject
        {
            ["kind"] = "tile",
            ["tileId"] = leaf.TileId,
            ["flex"] = leaf.Flex,
        },
        TileStack stack => new JsonObject
        {
            ["kind"] = "stack",
            ["id"] = stack.Id,
            ["direction"] = stack.Direction == TileDirection.Row ? "row" : "column",
            ["flex"] = stack.Flex,
            ["children"] = new JsonArray([.. stack.Children.Select(ToJson)]),
        },
        _ => throw new InvalidOperationException("Unknown tile node"),
    };

    /// <summary>Reads a stored layout back; anything malformed answers null rather than throwing.</summary>
    public static TileLayout? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var node = ParseNode(JsonNode.Parse(json));
            return node is null ? null : new TileLayout(EnsureRoot(Normalize(node)));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static TileNode? ParseNode(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        var flex = obj["flex"] is { } value && value.AsValue().TryGetValue(out double parsed) ? parsed : 1;
        if ((string?)obj["kind"] == "tile")
        {
            var id = (string?)obj["tileId"];
            return string.IsNullOrEmpty(id) ? null : new TileLeaf(id) { Flex = flex };
        }

        if (obj["children"] is not JsonArray array)
        {
            return null;
        }

        var children = array.Select(ParseNode).OfType<TileNode>().ToList();
        if (children.Count == 0)
        {
            return null;
        }

        var direction = (string?)obj["direction"] == "column" ? TileDirection.Column : TileDirection.Row;
        return new TileStack((string?)obj["id"] ?? NewStackId(), direction, children) { Flex = flex };
    }

    // ---- helpers ----

    private static TileNode Replace(TileNode node, string stackId, Func<TileStack, TileStack> edit)
    {
        if (node is not TileStack stack)
        {
            return node;
        }

        if (stack.Id == stackId)
        {
            return edit(stack);
        }

        return stack with { Children = [.. stack.Children.Select(c => Replace(c, stackId, edit))] };
    }

    private static TileNode? ReplaceNode(TileNode node, string stackId, TileNode replacement)
    {
        if (node is not TileStack stack)
        {
            return null;
        }

        if (stack.Id == stackId)
        {
            return replacement;
        }

        var children = new List<TileNode>();
        var changed = false;
        foreach (var child in stack.Children)
        {
            if (child is TileStack inner && inner.Id == stackId)
            {
                children.Add(replacement);
                changed = true;
                continue;
            }

            var replaced = ReplaceNode(child, stackId, replacement);
            if (replaced is not null)
            {
                children.Add(replaced);
                changed = true;
            }
            else
            {
                children.Add(child);
            }
        }

        return changed ? stack with { Children = children } : null;
    }
}
