using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class TranscriptOffsetsTests
{
    private static TranscriptOffsets<string> Over(params string[] rows)
    {
        var offsets = new TranscriptOffsets<string>((row, _) => row, (_, _) => 100);
        offsets.SetRows(rows);
        return offsets;
    }

    [Fact]
    public void An_unmeasured_row_contributes_its_estimate()
    {
        var offsets = Over("a", "b", "c");
        Assert.Equal([0, 100, 200, 300], offsets.Offsets);
        Assert.Equal(300, offsets.TotalSize);
        Assert.Equal(100, offsets.SizeOf(1));
        Assert.Equal(200, offsets.StartOf(2));
    }

    [Fact]
    public void A_measurement_replaces_the_estimate_under_everything_below_it()
    {
        var offsets = Over("a", "b", "c");
        Assert.True(offsets.Measure("a", 450));
        Assert.Equal([0, 450, 550, 650], offsets.Offsets);
        Assert.Equal(450, offsets.MeasuredOf("a"));
        Assert.Null(offsets.MeasuredOf("b"));
    }

    [Fact]
    public void A_sub_pixel_wobble_does_not_move_anything()
    {
        var offsets = Over("a", "b");
        Assert.True(offsets.Measure("a", 120));
        Assert.False(offsets.Measure("a", 120.4));
        Assert.True(offsets.Measure("a", 120.6));
    }

    [Fact]
    public void A_measurement_that_is_not_a_number_is_refused()
    {
        var offsets = Over("a");
        Assert.False(offsets.Measure("a", double.NaN));
        Assert.False(offsets.Measure("a", double.PositiveInfinity));
        Assert.False(offsets.Measure("a", -1));
        Assert.Null(offsets.MeasuredOf("a"));
    }

    [Fact]
    public void A_row_can_be_found_by_key_after_the_list_grows_above_it()
    {
        var offsets = Over("b", "c");
        Assert.Equal(0, offsets.IndexOfKey("b"));

        // A page of older history arrives: the row keeps its key and its measured
        // height, and only its index moves.
        offsets.Measure("b", 250);
        offsets.SetRows(["a0", "a1", "b", "c"]);
        Assert.Equal(2, offsets.IndexOfKey("b"));
        Assert.Equal(250, offsets.MeasuredOf("b"));
        Assert.Equal(200, offsets.StartOf(2));
    }

    [Fact]
    public void A_key_that_is_gone_answers_minus_one()
    {
        var offsets = Over("a", "b");
        Assert.Equal(-1, offsets.IndexOfKey("nothing"));
    }

    [Fact]
    public void Pruning_forgets_only_the_rows_that_left()
    {
        var offsets = Over("a", "b", "c");
        offsets.Measure("a", 10);
        offsets.Measure("b", 20);
        offsets.Measure("c", 30);

        offsets.SetRows(["b"]);
        offsets.PruneToRows();
        Assert.Null(offsets.MeasuredOf("a"));
        Assert.Equal(20, offsets.MeasuredOf("b"));
        Assert.Equal(1, offsets.MeasuredCount);
    }

    [Fact]
    public void A_growing_list_has_nothing_to_prune()
    {
        var offsets = Over("a");
        offsets.Measure("a", 10);
        offsets.SetRows(["a", "b", "c"]);
        offsets.PruneToRows();
        Assert.Equal(10, offsets.MeasuredOf("a"));
    }

    [Fact]
    public void A_snapshot_seeds_the_heights_and_a_reset_throws_them_away()
    {
        var offsets = Over("a", "b");
        offsets.Restore(new Dictionary<string, double> { ["a"] = 333, ["b"] = double.NaN });
        Assert.Equal(333, offsets.MeasuredOf("a"));
        Assert.Null(offsets.MeasuredOf("b"));
        Assert.Equal(433, offsets.TotalSize);

        offsets.Reset();
        Assert.Null(offsets.MeasuredOf("a"));
        Assert.Equal(200, offsets.TotalSize);
    }

    [Fact]
    public void An_empty_list_is_one_offset_of_zero()
    {
        var offsets = Over();
        Assert.Equal([0], offsets.Offsets);
        Assert.Equal(0, offsets.TotalSize);
        Assert.Equal(0, offsets.SizeOf(0));
    }
}
