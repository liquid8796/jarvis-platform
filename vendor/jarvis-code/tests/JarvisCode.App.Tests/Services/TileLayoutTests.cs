using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The pane mosaic. Every rule here is the reference's own tile module (ion-dist chunk
/// cde8ce059-Bx5SPiHJ.js), so a change to the shape of a layout shows up as a failure
/// rather than as a pane that quietly moved.
/// </summary>
public class TileLayoutTests
{
    private static TileLayout Row(params string[] ids)
    {
        var layout = TileLayoutOps.Single(ids[0]);
        foreach (var id in ids.Skip(1))
        {
            layout = TileLayoutOps.AppendRight(layout, id);
        }

        return layout;
    }

    [Fact]
    public void ASingleTileIsWrappedInARowRoot()
    {
        var layout = TileLayoutOps.Single("chat");
        Assert.Equal(TileDirection.Row, layout.Root.Direction);
        Assert.Equal(["chat"], layout.TileIds);
    }

    [Fact]
    public void AppendRightJoinsTheRootRow()
    {
        var layout = Row("chat", "terminal", "changes");
        Assert.Equal(["chat", "terminal", "changes"], layout.TileIds);
        Assert.Equal(3, layout.Root.Children.Count);
    }

    [Fact]
    public void AppendBelowWrapsTheAnchorInAColumn()
    {
        var layout = TileLayoutOps.AppendBelow(Row("chat", "terminal"), "terminal", "changes");
        var column = layout.Root.Children.OfType<TileStack>().Single();
        Assert.Equal(TileDirection.Column, column.Direction);
        Assert.Equal(["terminal", "changes"], TileLayoutOps.TileIds(column));
    }

    [Fact]
    public void ASecondTileBelowJoinsTheSameColumnRatherThanNesting()
    {
        var layout = TileLayoutOps.AppendBelow(Row("chat", "terminal"), "terminal", "changes");
        layout = TileLayoutOps.AppendBelow(layout, "changes", "browser");
        var column = layout.Root.Children.OfType<TileStack>().Single();
        Assert.Equal(["terminal", "changes", "browser"], TileLayoutOps.TileIds(column));
    }

    [Fact]
    public void RemovingTheLastTileOfAStackCollapsesIt()
    {
        var layout = TileLayoutOps.AppendBelow(Row("chat", "terminal"), "terminal", "changes");
        layout = TileLayoutOps.Remove(layout, "changes");
        Assert.Equal(["chat", "terminal"], layout.TileIds);
        Assert.All(layout.Root.Children, child => Assert.IsType<TileLeaf>(child));
    }

    [Fact]
    public void RemovingAnAbsentTileLeavesTheLayoutAlone()
    {
        var layout = Row("chat", "terminal");
        Assert.Same(layout, TileLayoutOps.Remove(layout, "browser"));
    }

    [Fact]
    public void RenameKeepsThePlaceAndTheFlex()
    {
        var layout = Row("chat", "terminal");
        var renamed = TileLayoutOps.Rename(layout, "terminal", "browser");
        Assert.Equal(["chat", "browser"], renamed.TileIds);
    }

    [Fact]
    public void SoloKeepsThePaneAndTheConversationWhenAsked()
    {
        var layout = Row("chat", "terminal", "changes");
        var solo = TileLayoutOps.Solo(layout, "changes", "chat");
        Assert.NotNull(solo);
        Assert.Equal(["chat", "changes"], solo!.TileIds);
    }

    [Fact]
    public void SoloAnswersNullForAPaneThatIsNotOpen()
        => Assert.Null(TileLayoutOps.Solo(Row("chat"), "changes"));

    [Fact]
    public void AnInsertTargetReordersInsideTheStack()
    {
        var layout = Row("chat", "terminal", "changes");
        var moved = TileLayoutOps.Move(layout, "changes", new TileInsertTarget(layout.Root.Id, 0));
        Assert.Equal(["changes", "chat", "terminal"], moved.TileIds);
    }

    [Fact]
    public void AWrapTargetPutsTheTileBesideAnotherInANewStack()
    {
        var layout = Row("chat", "terminal", "changes");
        var moved = TileLayoutOps.Move(layout, "changes", new TileWrapTarget("chat", TileDirection.Column, 1));
        var column = moved.Root.Children.OfType<TileStack>().Single();
        Assert.Equal(TileDirection.Column, column.Direction);
        Assert.Equal(["chat", "changes"], TileLayoutOps.TileIds(column));
    }

    [Fact]
    public void ACrossAxisSplitWrapsTheWholeStack()
    {
        var layout = Row("chat", "terminal");
        var moved = TileLayoutOps.Move(
            layout, "terminal", new TileSplitTarget(layout.Root.Id, 1, TileDirection.Column));
        Assert.Equal(TileDirection.Column, moved.Root.Direction);
        Assert.Equal(["chat", "terminal"], moved.TileIds);
    }

    [Fact]
    public void NormalizeFlattensAStackOfTheSameDirection()
    {
        var inner = new TileStack("inner", TileDirection.Row, [new TileLeaf("a"), new TileLeaf("b")]);
        var outer = new TileStack("outer", TileDirection.Row, [inner, new TileLeaf("c")]);
        var normalized = (TileStack)TileLayoutOps.Normalize(outer);
        Assert.Equal(3, normalized.Children.Count);
        Assert.Equal(["a", "b", "c"], TileLayoutOps.TileIds(normalized));
    }

    [Fact]
    public void NormalizeCollapsesASingleChildStackAndKeepsTheParentFlex()
    {
        var inner = new TileStack("inner", TileDirection.Column, [new TileLeaf("a")]) { Flex = 3 };
        var normalized = TileLayoutOps.Normalize(inner);
        var leaf = Assert.IsType<TileLeaf>(normalized);
        Assert.Equal("a", leaf.TileId);
        Assert.Equal(3, leaf.Flex);
    }

    [Fact]
    public void IsStackedSeesAColumnAncestorWithSiblings()
    {
        var layout = TileLayoutOps.AppendBelow(Row("chat", "terminal"), "terminal", "changes");
        Assert.True(TileLayoutOps.IsStacked(layout.Root, "changes"));
        Assert.False(TileLayoutOps.IsStacked(layout.Root, "chat"));
    }

    [Fact]
    public void ResolveFlexSharesThePixelsAndSumsToTheChildCount()
    {
        var resolved = TileLayoutOps.ResolveFlex([1, 1], [100, 100], 400);
        Assert.Equal(2, resolved.Sum(), 3);
        Assert.Equal(resolved[0], resolved[1], 3);
    }

    [Fact]
    public void ResolveFlexPinsAChildAtItsMinimumAndReSharesTheRest()
    {
        // 300px between two tiles whose minimums are 280 and 100: the wide one is pinned
        // and the other takes what is left.
        var resolved = TileLayoutOps.ResolveFlex([1, 1], [280, 100], 300);
        Assert.True(resolved[0] > resolved[1]);
    }

    [Fact]
    public void ResolveFlexOfAnEmptyOrZeroSizedStackAnswersTheFlexesItWasGiven()
    {
        Assert.Empty(TileLayoutOps.ResolveFlex([], [], 100));
        Assert.Equal([1d, 2d], TileLayoutOps.ResolveFlex([1, 2], [10, 10], 0));
    }

    [Fact]
    public void MinSizeOfARowIsTheSumOfItsChildrenPlusTheGaps()
    {
        var layout = Row("a", "b", "c");
        var min = TileLayoutOps.MinSize(layout.Root, TileDirection.Row, _ => null);
        Assert.Equal(3 * TileLayout.MinTileWidthPx + 2 * TileLayout.Gap, min);
    }

    [Fact]
    public void MinSizeAcrossAColumnIsTheWidestChild()
    {
        var column = new TileStack("s", TileDirection.Column, [new TileLeaf("a"), new TileLeaf("b")]);
        Assert.Equal(TileLayout.MinTileWidthPx, TileLayoutOps.MinSize(column, TileDirection.Row, _ => null));
    }

    [Fact]
    public void ATileWithItsOwnOverflowMinimumTakesTheBaseWhenItIsReachable()
    {
        var leaf = new TileLeaf("chat");
        Assert.Equal(
            TileLayout.MinTileBasePx,
            TileLayoutOps.MinSize(leaf, TileDirection.Row, _ => TileLayout.ChatOverflowMinWidth));
        Assert.Equal(
            TileLayout.ChatOverflowMinWidth,
            TileLayoutOps.MinSize(leaf, TileDirection.Row, _ => TileLayout.ChatOverflowMinWidth, false));
    }

    [Fact]
    public void ResizeBoundaryMovesOneShareIntoTheOther()
    {
        var resized = TileLayoutOps.ResizeBoundary([1, 1], 0, 100, 400, 100, 100);
        Assert.Equal(1.25, resized[0], 3);
        Assert.Equal(0.75, resized[1], 3);
        Assert.Equal(2, resized.Sum(), 3);
    }

    [Fact]
    public void ResizeBoundaryRefusesWhenTheTwoMinimumsDoNotFit()
    {
        // Two 150px minimums cannot both fit in 100px, so the boundary does not move.
        var resized = TileLayoutOps.ResizeBoundary([1, 1], 0, 100, 100, 150, 150);
        Assert.Equal([1d, 1d], resized);
    }

    [Fact]
    public void ALayoutSurvivesAJsonRoundTrip()
    {
        var layout = TileLayoutOps.AppendBelow(Row("chat", "terminal"), "terminal", "changes");
        var json = TileLayoutOps.ToJson(layout.Root).ToJsonString();
        var restored = TileLayoutOps.FromJson(json);
        Assert.NotNull(restored);
        Assert.Equal(layout.TileIds, restored!.TileIds);
        Assert.Equal(layout.Root.Direction, restored.Root.Direction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"stack\",\"children\":[]}")]
    public void AMalformedStoredLayoutReadsAsNothingRatherThanThrowing(string json)
        => Assert.Null(TileLayoutOps.FromJson(json));

    [Fact]
    public void TheConfigConstantsAreTheReferenceNumbers()
    {
        Assert.Equal(12, TileLayout.Gap);
        Assert.Equal(8, TileLayout.Padding);
        Assert.Equal(100, TileLayout.MinTileBasePx);
        Assert.Equal(280, TileLayout.MinTileWidthPx);
        Assert.Equal(24, TileLayout.DragLift);
        Assert.Equal(24, TileLayout.EdgeBandPx);
        Assert.Equal(320, TileLayout.ChatOverflowMinWidth);
    }
}
