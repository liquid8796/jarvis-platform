using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class TranscriptWindowTests
{
    /// <summary>Prefix sums for <paramref name="count"/> rows of a fixed height.</summary>
    private static double[] Rows(int count, double height)
    {
        var offsets = new double[count + 1];
        for (int i = 0; i < count; i++)
        {
            offsets[i + 1] = offsets[i] + height;
        }

        return offsets;
    }

    [Fact]
    public void The_index_at_a_pixel_is_the_row_whose_box_holds_it()
    {
        var offsets = Rows(4, 100);
        Assert.Equal(0, TranscriptWindow.IndexAt(offsets, 0));
        Assert.Equal(0, TranscriptWindow.IndexAt(offsets, 99.9));
        Assert.Equal(1, TranscriptWindow.IndexAt(offsets, 100));
        Assert.Equal(3, TranscriptWindow.IndexAt(offsets, 350));
    }

    [Fact]
    public void A_pixel_past_the_end_still_names_the_last_row()
    {
        var offsets = Rows(3, 50);
        Assert.Equal(2, TranscriptWindow.IndexAt(offsets, 10_000));
    }

    [Fact]
    public void An_empty_list_builds_nothing()
    {
        var window = TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = [0],
            ViewportHeight = 500,
            ScrollTop = 0,
        });

        Assert.Equal(0, window.First);
        Assert.Equal(-1, window.Last);
        Assert.Equal(0, window.Count);
        Assert.False(window.Contains(0));
    }

    [Fact]
    public void The_window_is_the_viewport_plus_its_overscan_in_pixels()
    {
        // 1000 rows of 100px; the reader is at 50,000 with a 500px viewport, far
        // from either end so no end-snap reaches. Overscan is 600px each way,
        // which is six rows above and six below the five the viewport shows.
        var window = TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = Rows(1000, 100),
            ViewportHeight = 500,
            ScrollTop = 50_000,
            OverscanTop = TranscriptWindow.Overscan,
            OverscanBottom = TranscriptWindow.Overscan,
        });

        Assert.Equal(494, window.First);
        Assert.Equal(510, window.Last);
    }

    [Fact]
    public void The_overscan_is_a_pixel_budget_rather_than_a_row_count()
    {
        // The row directly above the viewport is 5000px tall, so including it spends
        // the whole 600px budget at once and nothing above it is built — where six
        // 100px rows would have been. The budget is checked before a row is taken,
        // which is why the row that overshoots is still included.
        var offsets = new double[] { 0, 100, 5_100, 5_200, 5_300 };
        var window = TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = offsets,
            ViewportHeight = 100,
            ScrollTop = 5_100,
            OverscanTop = TranscriptWindow.Overscan,
            OverscanBottom = 0,
            MountPhase = true,
        });

        Assert.Equal(1, window.First);
        Assert.Equal(2, window.Last);
    }

    [Fact]
    public void Within_two_viewports_of_an_end_the_window_reaches_it()
    {
        // The reader is 900px from the bottom with a 500px viewport: two viewports
        // is 1000px, so the tail is built out rather than left to an estimate.
        var window = TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = Rows(100, 100),
            ViewportHeight = 500,
            ScrollTop = 8_600,
            OverscanTop = 0,
            OverscanBottom = 0,
        });

        Assert.Equal(99, window.Last);
    }

    [Fact]
    public void The_mount_window_does_not_reach_either_end()
    {
        // The same position, but during the first paint: the end-snaps stand down,
        // so only the viewport's own rows are built.
        var window = TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = Rows(100, 100),
            ViewportHeight = 500,
            ScrollTop = 8_600,
            OverscanTop = 0,
            OverscanBottom = 0,
            MountPhase = true,
        });

        Assert.Equal(86, window.First);
        Assert.Equal(90, window.Last);
    }

    [Fact]
    public void Following_windows_at_the_tail_even_when_the_scroller_lags()
    {
        // A row has just been appended, so the scroller has not caught up with the
        // content: pinned, the window is computed where the reader is about to be.
        var window = TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = Rows(100, 100),
            ViewportHeight = 500,
            ScrollTop = 0,
            ScrollableHeight = 9_500,
            OverscanTop = 0,
            OverscanBottom = 0,
            Following = true,
            MountPhase = true,
        });

        Assert.Equal(95, window.First);
        Assert.Equal(99, window.Last);
    }

    [Fact]
    public void Not_following_windows_where_the_scroller_actually_is()
    {
        var window = TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = Rows(100, 100),
            ViewportHeight = 500,
            ScrollTop = 0,
            ScrollableHeight = 9_500,
            OverscanTop = 0,
            OverscanBottom = 0,
            Following = false,
            MountPhase = true,
        });

        Assert.Equal(0, window.First);
        Assert.Equal(4, window.Last);
    }

    [Fact]
    public void The_padding_a_panel_carries_moves_the_viewport_rather_than_the_rows()
    {
        var window = TranscriptWindow.Compute(new TranscriptWindow.Inputs
        {
            Offsets = Rows(100, 100),
            ViewportHeight = 500,
            ScrollTop = 1_000,
            PaddingTop = 400,
            OverscanTop = 0,
            OverscanBottom = 0,
            MountPhase = true,
        });

        Assert.Equal(6, window.First);
    }
}
