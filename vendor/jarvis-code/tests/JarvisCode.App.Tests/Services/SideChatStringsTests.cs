using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class SideChatStringsTests
{
    [Fact]
    public void The_header_counts_what_the_side_chat_can_see()
    {
        Assert.Equal("Sees 1 message from main chat", SideChatStrings.SeesMessages(1));
        Assert.Equal("Sees 12 messages from main chat", SideChatStrings.SeesMessages(12));
        Assert.Equal("Sees 0 messages from main chat", SideChatStrings.SeesMessages(0));
    }

    [Fact]
    public void A_branch_takes_everything_through_that_answer()
    {
        IReadOnlyList<SideChatLine> lines =
        [
            new(true, "why?"),
            new(false, "because"),
            new(true, "and then?"),
            new(false, "then this"),
        ];

        Assert.Equal(2, SideChatStrings.Branch(lines, 1).Count);
        Assert.Equal(4, SideChatStrings.Branch(lines, 3).Count);
    }

    [Fact]
    public void A_branch_index_outside_the_transcript_is_clamped()
    {
        IReadOnlyList<SideChatLine> lines = [new(true, "why?"), new(false, "because")];
        Assert.Equal(2, SideChatStrings.Branch(lines, 9).Count);
        Assert.Empty(SideChatStrings.Branch(lines, -3));
    }
}
