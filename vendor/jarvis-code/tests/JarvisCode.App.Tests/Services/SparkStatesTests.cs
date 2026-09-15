using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class SparkStatesTests
{
    [Theory]
    [InlineData(SparkState.Waiting, 16, 600)]
    [InlineData(SparkState.Thinking, 9, 90)]
    [InlineData(SparkState.Writing, 8, 90)]
    [InlineData(SparkState.Entrance, 6, 70)]
    [InlineData(SparkState.Exit, 6, 70)]
    [InlineData(SparkState.Tickle, 7, 40)]
    public void The_strips_are_the_measured_ones(SparkState state, int frames, double speed)
    {
        var spec = SparkStates.Spec(state);
        Assert.NotNull(spec);
        Assert.Equal(frames, spec!.Value.FrameCount);
        Assert.Equal(speed, spec.Value.Speed);
        Assert.Equal(frames * speed, spec.Value.DurationMs);
    }

    [Fact]
    public void Idle_has_no_strip_and_is_not_animated()
    {
        Assert.Null(SparkStates.Spec(SparkState.Idle));
        Assert.False(SparkStates.IsAnimated(SparkState.Idle));
        Assert.Equal(0, SparkStates.FrameAt(SparkState.Idle, 5_000));
    }

    [Theory]
    [InlineData(SparkState.Entrance)]
    [InlineData(SparkState.Exit)]
    [InlineData(SparkState.Tickle)]
    public void The_one_shot_set_is_the_references_own(SparkState state)
        => Assert.True(SparkStates.IsOneShot(state));

    [Theory]
    [InlineData(SparkState.Waiting)]
    [InlineData(SparkState.Thinking)]
    [InlineData(SparkState.Writing)]
    [InlineData(SparkState.Idle)]
    public void Everything_else_loops(SparkState state)
        => Assert.False(SparkStates.IsOneShot(state));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(89, 0)]
    [InlineData(90, 1)]
    [InlineData(179, 1)]
    [InlineData(180, 2)]
    [InlineData(719, 7)]
    public void Each_frame_is_held_for_its_own_speed(double elapsed, int frame)
        => Assert.Equal(frame, SparkStates.FrameAt(SparkState.Writing, elapsed));

    [Fact]
    public void A_looping_strip_wraps_at_the_end_of_its_pass()
    {
        // writing is 8 frames at 90ms: 720ms is the start of the second pass.
        Assert.Equal(0, SparkStates.FrameAt(SparkState.Writing, 720));
        Assert.Equal(3, SparkStates.FrameAt(SparkState.Writing, 720 + (3 * 90)));
    }

    [Fact]
    public void A_one_shot_strip_holds_its_last_frame()
    {
        // tickle is 7 frames at 40ms; past 280ms the reference's fill:forwards leaves
        // the last frame on screen rather than starting over.
        Assert.Equal(6, SparkStates.FrameAt(SparkState.Tickle, 280));
        Assert.Equal(6, SparkStates.FrameAt(SparkState.Tickle, 10_000));
    }

    [Fact]
    public void A_negative_or_zero_elapsed_is_the_first_frame()
    {
        Assert.Equal(0, SparkStates.FrameAt(SparkState.Waiting, 0));
        Assert.Equal(0, SparkStates.FrameAt(SparkState.Waiting, -50));
    }

    [Fact]
    public void Only_a_one_shot_state_ever_ends()
    {
        Assert.False(SparkStates.HasEnded(SparkState.Tickle, 279));
        Assert.True(SparkStates.HasEnded(SparkState.Tickle, 280));
        Assert.False(SparkStates.HasEnded(SparkState.Waiting, 1_000_000));
        Assert.False(SparkStates.HasEnded(SparkState.Idle, 1_000_000));
    }

    [Fact]
    public void Every_frame_of_every_state_is_drawable()
    {
        foreach (var state in Enum.GetValues<SparkState>())
        {
            var frames = SparkStates.Spec(state)?.FrameCount ?? 1;
            for (var frame = 0; frame < frames; frame++)
            {
                var (scale, rotation, opacity) = SparkStates.Frame(state, frame);
                Assert.InRange(scale, 0, 2);
                Assert.InRange(opacity, 0, 1);
                Assert.InRange(rotation, -360, 360);
            }
        }
    }

    [Fact]
    public void A_frame_outside_the_strip_is_clamped_rather_than_thrown()
    {
        var (scale, _, _) = SparkStates.Frame(SparkState.Waiting, 99);
        Assert.Equal(SparkStates.Frame(SparkState.Waiting, 15).Scale, scale);
        Assert.Equal(SparkStates.Frame(SparkState.Waiting, 0).Scale,
            SparkStates.Frame(SparkState.Waiting, -4).Scale);
    }

    [Fact]
    public void The_static_state_draws_the_mark_untouched()
        => Assert.Equal((1.0, 0.0, 1.0), SparkStates.Frame(SparkState.Idle, 0));

    [Fact]
    public void Exit_shrinks_away_and_entrance_arrives_at_full_size()
    {
        var last = SparkStates.Spec(SparkState.Exit)!.Value.FrameCount - 1;
        Assert.Equal(0, SparkStates.Frame(SparkState.Exit, last).Scale, 6);
        Assert.Equal(1, SparkStates.Frame(SparkState.Entrance, last).Scale, 6);
    }
}
