using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class TranscriptScrollingTests
{
    private const int WindowsDefaultLines = 3;

    [Fact]
    public void OneNotchAtTheDefaultSpeedIsWhatWpfItselfScrolls()
    {
        // Three lines of 16px, which is the step the transcript's prose already had.
        Assert.Equal(
            -48,
            TranscriptScrolling.VerticalOffsetDelta(120, 1.0, WindowsDefaultLines, 800),
            precision: 6);
    }

    [Fact]
    public void WheelDownMovesTheOffsetForward()
    {
        Assert.Equal(
            48,
            TranscriptScrolling.VerticalOffsetDelta(-120, 1.0, WindowsDefaultLines, 800),
            precision: 6);
    }

    [Fact]
    public void TheSpeedMultipliesTheStep()
    {
        Assert.Equal(
            -96,
            TranscriptScrolling.VerticalOffsetDelta(120, 2.0, WindowsDefaultLines, 800),
            precision: 6);
        Assert.Equal(
            -12,
            TranscriptScrolling.VerticalOffsetDelta(120, 0.25, WindowsDefaultLines, 800),
            precision: 6);
    }

    [Fact]
    public void TheUsersLineCountIsHonoured()
    {
        Assert.Equal(
            -16,
            TranscriptScrolling.VerticalOffsetDelta(120, 1.0, 1, 800),
            precision: 6);
        Assert.Equal(
            -160,
            TranscriptScrolling.VerticalOffsetDelta(120, 1.0, 10, 800),
            precision: 6);
    }

    /// <summary>A high-resolution wheel reports a fraction of a notch.</summary>
    [Fact]
    public void APartialNotchScrollsAPartialStep()
    {
        Assert.Equal(
            -16,
            TranscriptScrolling.VerticalOffsetDelta(40, 1.0, WindowsDefaultLines, 800),
            precision: 6);
    }

    /// <summary>Windows' "One screen at a time" arrives as a negative line count.</summary>
    [Fact]
    public void ANegativeLineCountPagesByTheViewport()
    {
        Assert.Equal(
            -800,
            TranscriptScrolling.VerticalOffsetDelta(120, 1.0, -1, 800),
            precision: 6);
    }

    [Fact]
    public void WheelScrollingTurnedOffScrollsNothing()
    {
        Assert.Equal(0, TranscriptScrolling.VerticalOffsetDelta(120, 1.0, 0, 800));
    }

    [Fact]
    public void NothingToDoIsNoMovement()
    {
        Assert.Equal(0, TranscriptScrolling.VerticalOffsetDelta(0, 1.0, WindowsDefaultLines, 800));
        Assert.Equal(0, TranscriptScrolling.VerticalOffsetDelta(120, 0, WindowsDefaultLines, 800));
    }

    /// <summary>A viewport that has not been measured yet cannot page backwards.</summary>
    [Fact]
    public void AnUnmeasuredViewportPagesNothing()
    {
        Assert.Equal(0, TranscriptScrolling.VerticalOffsetDelta(120, 1.0, -1, 0));
    }
    [Fact]
    public void The_tail_is_the_last_sixty_pixels_of_the_scroll()
    {
        Assert.True(TranscriptScrolling.IsAtTail(940, 1000));
        Assert.True(TranscriptScrolling.IsAtTail(1000, 1000));
        Assert.False(TranscriptScrolling.IsAtTail(939, 1000));
    }

    [Fact]
    public void A_transcript_that_cannot_scroll_is_always_at_its_tail()
    {
        Assert.True(TranscriptScrolling.IsAtTail(0, 0));
        Assert.True(TranscriptScrolling.IsAtTail(0, -1));
    }
}
