using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ChatStatusLabelsTests
{
    [Theory]
    [InlineData(0.0, null)]
    [InlineData(4.9, null)]
    [InlineData(5.0, ChatStatusLabels.Gathering)]
    [InlineData(14.9, ChatStatusLabels.Gathering)]
    [InlineData(15.0, ChatStatusLabels.Long)]
    [InlineData(29.9, ChatStatusLabels.Long)]
    [InlineData(30.0, ChatStatusLabels.Longest)]
    [InlineData(600.0, ChatStatusLabels.Longest)]
    public void Ladder_switches_on_the_reference_thresholds(double elapsed, string? expected)
        => Assert.Equal(expected, ChatStatusLabels.Ladder(elapsed, pick: 0));

    [Theory]
    [InlineData(0, ChatStatusLabels.Gathering)]
    [InlineData(1, ChatStatusLabels.Contemplating)]
    [InlineData(2, ChatStatusLabels.Pondering)]
    [InlineData(3, ChatStatusLabels.Ruminating)]
    [InlineData(4, ChatStatusLabels.Pondering)]
    public void Ladder_first_label_is_the_drawn_pick(int pick, string expected)
        => Assert.Equal(expected, ChatStatusLabels.Ladder(5, pick));

    [Fact]
    public void Ladder_pick_wraps_in_both_directions()
    {
        Assert.Equal(ChatStatusLabels.Contemplating, ChatStatusLabels.Ladder(5, pick: 6));
        Assert.Equal(ChatStatusLabels.Ruminating, ChatStatusLabels.Ladder(5, pick: -2));
    }

    /// <summary>"pondering" is in the reference's array twice, so it is twice as likely.</summary>
    [Fact]
    public void First_picks_are_the_reference_array()
    {
        Assert.Equal(5, ChatStatusLabels.FirstPicks.Count);
        Assert.Equal(2, ChatStatusLabels.FirstPicks.Count(p => p == ChatStatusLabels.Pondering));
    }

    [Theory]
    [InlineData(ChatRetryCause.RateLimit, "Rate limit reached")]
    [InlineData(ChatRetryCause.Overloaded, "Server is busy")]
    [InlineData(ChatRetryCause.AuthenticationFailed, "Authentication failed")]
    [InlineData(ChatRetryCause.RequestFailed, "Request failed")]
    public void Cause_labels_match_the_reference(ChatRetryCause cause, string expected)
        => Assert.Equal(expected, ChatStatusLabels.CauseLabel(cause));

    [Fact]
    public void Retry_label_counts_down_then_says_now()
    {
        Assert.Equal(
            "Rate limit reached. Retrying in 3s (attempt 2 of 4)",
            ChatStatusLabels.RetryLabel(ChatRetryCause.RateLimit, 3, 2, 4));
        Assert.Equal(
            "Server is busy. Retrying now (attempt 4 of 4)",
            ChatStatusLabels.RetryLabel(ChatRetryCause.Overloaded, 0, 4, 4));
    }

    [Fact]
    public void Taking_longer_numbers_the_attempt_one_above_the_count()
        => Assert.Equal(
            "Taking longer than usual. Trying again shortly (attempt 2)",
            ChatStatusLabels.TakingLongerLabel(1));

    [Theory]
    [InlineData(0.0, 0)]
    // 100 * (1 - e^-0.2) = 18.1269…
    [InlineData(5.0, 18)]
    // 100 * (1 - e^-1) = 63.2120…
    [InlineData(25.0, 63)]
    // 100 * (1 - e^-2) = 86.4664…
    [InlineData(50.0, 86)]
    // The curve is clamped at 95 however long the pass takes.
    [InlineData(300.0, 95)]
    public void Compaction_progress_follows_the_reference_curve(double elapsed, int expected)
        => Assert.Equal(expected, ChatStatusLabels.CompactionProgress(elapsed));

    [Fact]
    public void Compaction_progress_never_reaches_a_hundred_on_its_own()
        => Assert.True(ChatStatusLabels.CompactionProgress(1e6) <= 95);

    [Theory]
    [InlineData(0.0, null)]
    [InlineData(29.9, null)]
    [InlineData(30.0, "Still thinking...")]
    [InlineData(59.9, "Still thinking...")]
    [InlineData(60.0, "Working through a complex response...")]
    public void Waiting_escalation_switches_at_thirty_and_sixty(double elapsed, string? expected)
        => Assert.Equal(expected, ChatStatusLabels.WaitingEscalation(elapsed));
}
