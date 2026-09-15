using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Tests.Services;

public class TranscriptRowEstimatesTests
{
    [Fact]
    public void The_column_is_divided_into_characters_by_the_reference_ratio()
    {
        // 960px at 7.5px a character is 128 characters, and the reader's text size
        // divides it again: Large wraps sooner, Small later.
        Assert.Equal(128, TranscriptRowEstimates.CharsPerLine(960, 1));
        Assert.Equal(112, TranscriptRowEstimates.CharsPerLine(960, 16d / 14), 3);
        Assert.Equal(137.85, TranscriptRowEstimates.CharsPerLine(960, 13d / 14), 2);
    }

    [Fact]
    public void A_line_is_worth_a_line_and_a_wrapped_one_is_worth_what_it_wraps_to()
    {
        Assert.Equal(20, TranscriptRowEstimates.TextHeight("short", 100, false));
        Assert.Equal(60, TranscriptRowEstimates.TextHeight(new string('x', 250), 100, false));
        Assert.Equal(40, TranscriptRowEstimates.TextHeight("one\ntwo", 100, false));
    }

    [Fact]
    public void Empty_text_still_takes_a_line()
    {
        Assert.Equal(20, TranscriptRowEstimates.TextHeight("", 100, false));
        Assert.Equal(20, TranscriptRowEstimates.TextHeight(null, 100, false));
    }

    [Fact]
    public void A_fence_marker_and_a_blank_line_cost_less_than_prose()
    {
        // Fence-aware: the two markers and the blank lines around them are 10px
        // each, and only the code between them is measured as lines.
        Assert.Equal(
            TranscriptRowEstimates.BlankLine * 2 + TranscriptRowEstimates.LineHeight,
            TranscriptRowEstimates.TextHeight("```\ncode\n```", 100, true));

        // Without fence awareness the markers are prose like anything else.
        Assert.Equal(60, TranscriptRowEstimates.TextHeight("```\ncode\n```", 100, false));
    }

    [Fact]
    public void A_blank_line_inside_a_fence_is_still_a_line_of_code()
    {
        Assert.Equal(
            TranscriptRowEstimates.BlankLine + TranscriptRowEstimates.LineHeight +
            TranscriptRowEstimates.BlankLine,
            TranscriptRowEstimates.TextHeight("```\n\n```", 100, true));
    }

    [Fact]
    public void A_table_row_costs_more_than_the_line_it_sits_on()
    {
        Assert.Equal(
            TranscriptRowEstimates.LineHeight + TranscriptRowEstimates.TableRow,
            TranscriptRowEstimates.TextHeight("| a | b |", 100, false));
    }

    [Fact]
    public void Two_hundred_lines_worth_of_characters_answers_the_clamp()
    {
        var huge = new string('x', 100 * 200);
        Assert.Equal(TranscriptRowEstimates.EntryClamp,
            TranscriptRowEstimates.TextHeight(huge, 100, false));
    }

    [Fact]
    public void A_user_bubble_is_its_chrome_plus_clamped_text()
    {
        var row = new UserMessageItem { Text = "hello" };
        Assert.Equal(
            TranscriptRowEstimates.UserChrome + TranscriptRowEstimates.LineHeight +
            TranscriptRowEstimates.UserActions,
            TranscriptRowEstimates.For(row, 960, 1));

        // However long it gets, the reference clamps the bubble at 256px.
        var long_ = new UserMessageItem { Text = string.Join("\n", Enumerable.Repeat("line", 200)) };
        Assert.Equal(
            TranscriptRowEstimates.UserChrome + TranscriptRowEstimates.UserTextClamp +
            TranscriptRowEstimates.UserActions,
            TranscriptRowEstimates.For(long_, 960, 1));
    }

    [Fact]
    public void An_answer_is_its_text_and_never_more_than_the_entry_clamp()
    {
        var row = new AssistantTextItem { Markdown = "one line" };
        Assert.Equal(
            TranscriptRowEstimates.LineHeight + TranscriptRowEstimates.BlankLine,
            TranscriptRowEstimates.For(row, 960, 1));

        var huge = new AssistantTextItem { Markdown = new string('x', 200 * 200) };
        Assert.Equal(TranscriptRowEstimates.EntryClamp, TranscriptRowEstimates.For(huge, 960, 1));
    }

    [Fact]
    public void A_thinking_cell_is_its_header_until_somebody_opens_it()
    {
        var row = new ThinkingItem { Text = string.Join("\n", Enumerable.Repeat("thought", 50)) };
        Assert.Equal(TranscriptRowEstimates.ThinkingRow, TranscriptRowEstimates.For(row, 960, 1));
    }

    [Fact]
    public void A_tool_run_is_worth_its_rows()
    {
        var run = new ToolGroupItem();
        run.AddCall(new ToolCallItem { CallId = "1", ToolName = "Read", Description = "" });
        run.AddCall(new ToolCallItem { CallId = "2", ToolName = "Edit", Description = "" });
        run.RefreshTitle();

        // Two calls under a summary header: three rows of the reference's 40px.
        Assert.Equal(3 * TranscriptRowEstimates.ToolRow, TranscriptRowEstimates.For(run, 960, 1));
    }

    [Fact]
    public void A_bare_run_is_one_row_with_no_header_over_it()
    {
        var run = new ToolGroupItem();
        run.AddCall(new ToolCallItem { CallId = "1", ToolName = "Read", Description = "" });
        run.RefreshTitle();

        Assert.True(run.RendersBareRow);
        Assert.Equal(TranscriptRowEstimates.ToolRow, TranscriptRowEstimates.For(run, 960, 1));
    }

    [Fact]
    public void The_fixed_rows_carry_the_reference_constants()
    {
        Assert.Equal(TranscriptRowEstimates.ChapterRow,
            TranscriptRowEstimates.For(new ChapterItem { Id = "c1", Title = "x" }, 960, 1));
        Assert.Equal(TranscriptRowEstimates.AssistantChrome,
            TranscriptRowEstimates.For(new AssistantFooterItem(), 960, 1));
    }
}
