using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ChatQueueTests
{
    [Fact]
    public void The_header_is_the_count_alone()
    {
        Assert.Equal("1 message queued", ChatQueue.CountLabel(1));
        Assert.Equal("3 messages queued", ChatQueue.CountLabel(3));
    }

    [Fact]
    public void The_status_sentence_is_the_one_a_reader_is_given()
    {
        Assert.Equal(
            "1 message queued. Will send after the current response.",
            ChatQueue.QueuedStatus(1));
        Assert.Equal(
            "2 messages queued. Will send after the current response.",
            ChatQueue.QueuedStatus(2));
    }

    [Fact]
    public void The_preview_is_the_text_with_its_attachments_marked()
    {
        Assert.Equal(
            "look at this @notes.md [Image]",
            ChatQueue.Preview("look at this", [(false, "notes.md"), (true, "shot.png")]));
    }

    [Fact]
    public void The_preview_collapses_whitespace()
        => Assert.Equal("one two three", ChatQueue.Preview("one\n  two\t\tthree", []));

    [Fact]
    public void An_empty_message_previews_as_an_ellipsis()
    {
        Assert.Equal("…", ChatQueue.Preview("", []));
        Assert.Equal("…", ChatQueue.Preview("   ", []));
    }

    [Fact]
    public void Attachments_alone_still_preview()
        => Assert.Equal("[Image]", ChatQueue.Preview("", [(true, "shot.png")]));

    [Fact]
    public void The_collapsed_stack_names_the_next_message()
        => Assert.Equal("Next: write the report", ChatQueue.NextLabel("write the report"));
}
