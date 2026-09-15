using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Tests.Services;

public class TranscriptEntrySplitTests
{
    [Fact]
    public void Sixteen_items_is_heavy_however_short_they_are()
    {
        Assert.False(TranscriptEntrySplit.ShouldSplit(15, 0));
        Assert.True(TranscriptEntrySplit.ShouldSplit(16, 0));
    }

    [Fact]
    public void Two_thousand_pixels_is_heavy_however_few_rows_carry_them()
    {
        Assert.False(TranscriptEntrySplit.ShouldSplit(1, 1_999));
        Assert.True(TranscriptEntrySplit.ShouldSplit(1, 2_000));
    }

    [Fact]
    public void A_short_run_is_rendered_whole_and_a_long_one_is_split()
    {
        var small = new ToolGroupItem();
        for (int i = 0; i < 4; i++)
        {
            small.AddCall(new ToolCallItem { CallId = $"s{i}", ToolName = "Read", Description = "" });
        }

        small.RefreshTitle();
        Assert.False(TranscriptEntrySplit.ShouldSplit(small));

        var big = new ToolGroupItem();
        for (int i = 0; i < 20; i++)
        {
            big.AddCall(new ToolCallItem { CallId = $"b{i}", ToolName = "Read", Description = "" });
        }

        big.RefreshTitle();
        Assert.True(TranscriptEntrySplit.ShouldSplit(big));
    }

    [Fact]
    public void One_enormous_answer_is_heavy_on_its_own()
    {
        var small = new AssistantTextItem { Markdown = "one line" };
        Assert.False(TranscriptEntrySplit.ShouldSplit(small));

        // Sixty lines that each wrap to three at the 128-character measurement
        // column: 3600px, which is past the 2000 the reference calls heavy.
        var huge = new AssistantTextItem
        {
            Markdown = string.Join("\n", Enumerable.Repeat(new string('x', 300), 60)),
        };
        Assert.True(TranscriptEntrySplit.ShouldSplit(huge));
    }

    [Fact]
    public void The_measurement_column_is_the_same_on_every_window()
    {
        // The reference decides this at its own medium column so a conversation
        // splits the same way however wide the reader's window is.
        Assert.Equal(960, TranscriptEntrySplit.MeasurementWidth);
    }
}
