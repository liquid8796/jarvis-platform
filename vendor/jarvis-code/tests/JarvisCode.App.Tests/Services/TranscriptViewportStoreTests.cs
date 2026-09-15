using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class TranscriptViewportStoreTests
{
    private static TranscriptViewportStore.Snapshot Sized(double width, double text) =>
        new()
        {
            IsPinned = false,
            AnchorKey = "h12.0",
            AnchorOffsetPx = 40,
            Sizes = new Dictionary<string, double> { ["h12.0"] = 220 },
            ColumnWidth = width,
            TextSize = text,
        };

    [Fact]
    public void A_saved_viewport_comes_back_whole()
    {
        var store = new TranscriptViewportStore();
        store.Save("s1", Sized(960, 14));

        var back = store.Load("s1", 960, 14);
        Assert.NotNull(back);
        Assert.False(back.IsPinned);
        Assert.Equal("h12.0", back.AnchorKey);
        Assert.Equal(40, back.AnchorOffsetPx);
        Assert.Equal(220, back.Sizes["h12.0"]);
    }

    [Fact]
    public void A_session_nobody_saved_answers_nothing()
    {
        var store = new TranscriptViewportStore();
        Assert.Null(store.Load("s1", 960, 14));
        Assert.Null(store.Load("", 960, 14));
    }

    [Fact]
    public void Heights_measured_at_another_column_are_dropped_and_the_anchor_is_kept()
    {
        var store = new TranscriptViewportStore();
        store.Save("s1", Sized(960, 14));

        var narrower = store.Load("s1", 700, 14);
        Assert.NotNull(narrower);
        Assert.Empty(narrower.Sizes);
        Assert.Equal("h12.0", narrower.AnchorKey);

        var larger = store.Load("s1", 960, 16);
        Assert.NotNull(larger);
        Assert.Empty(larger.Sizes);
    }

    [Fact]
    public void The_oldest_session_is_evicted_once_the_store_is_full()
    {
        var store = new TranscriptViewportStore();
        for (int i = 0; i < TranscriptViewportStore.Capacity + 5; i++)
        {
            store.Save($"s{i}", Sized(960, 14));
        }

        Assert.Null(store.Load("s0", 960, 14));
        Assert.Null(store.Load("s4", 960, 14));
        Assert.NotNull(store.Load("s5", 960, 14));
        Assert.NotNull(store.Load($"s{TranscriptViewportStore.Capacity + 4}", 960, 14));
    }

    [Fact]
    public void Saving_a_session_again_does_not_spend_another_slot()
    {
        var store = new TranscriptViewportStore();
        for (int i = 0; i < TranscriptViewportStore.Capacity; i++)
        {
            store.Save($"s{i}", Sized(960, 14));
        }

        for (int i = 0; i < 20; i++)
        {
            store.Save("s0", Sized(960, 14));
        }

        Assert.NotNull(store.Load("s0", 960, 14));
        Assert.NotNull(store.Load("s1", 960, 14));
    }

    [Fact]
    public void A_forgotten_session_leaves_no_trace()
    {
        var store = new TranscriptViewportStore();
        store.Save("s1", Sized(960, 14));
        store.Forget("s1");
        Assert.Null(store.Load("s1", 960, 14));

        store.Save("s2", Sized(960, 14));
        store.Clear();
        Assert.Null(store.Load("s2", 960, 14));
    }
}
