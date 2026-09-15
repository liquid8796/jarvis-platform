using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Tests.ViewModels;

public class ChatTurnStatusTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    private static ChatTurnStatus Started(int pick = 0)
    {
        var status = new ChatTurnStatus();
        status.BeginTurn(Start, pick);
        return status;
    }

    [Fact]
    public void Idle_shows_nothing()
        => Assert.False(new ChatTurnStatus().Describe(Start).Visible);

    [Fact]
    public void Waiting_shows_the_ladder_after_five_seconds()
    {
        var status = Started();
        Assert.False(status.Describe(Start.AddSeconds(4)).Visible);
        Assert.Equal(ChatStatusLabels.Gathering, status.Describe(Start.AddSeconds(5)).Label);
        Assert.Equal(ChatStatusLabels.Long, status.Describe(Start.AddSeconds(15)).Label);
        Assert.Equal(ChatStatusLabels.Longest, status.Describe(Start.AddSeconds(30)).Label);
    }

    [Fact]
    public void Content_stands_the_ladder_down()
    {
        var status = Started();
        status.OnContent();
        Assert.False(status.Describe(Start.AddSeconds(45)).Visible);
    }

    [Fact]
    public void A_tool_round_trip_puts_the_line_back()
    {
        var status = Started();
        status.OnContent();
        status.OnAwaitingModel(Start.AddSeconds(10));
        Assert.False(status.Describe(Start.AddSeconds(14)).Visible);
        Assert.Equal(ChatStatusLabels.Gathering, status.Describe(Start.AddSeconds(15)).Label);
    }

    [Fact]
    public void A_round_trip_before_any_content_does_not_restart_the_clock()
    {
        var status = Started();
        status.OnAwaitingModel(Start.AddSeconds(10));
        Assert.Equal(ChatStatusLabels.Long, status.Describe(Start.AddSeconds(16)).Label);
    }

    [Fact]
    public void A_retry_replaces_the_ladder_and_counts_down()
    {
        var status = Started();
        status.OnRetry(ChatRetryCause.RateLimit, 2, 4, TimeSpan.FromSeconds(3), Start.AddSeconds(6));
        Assert.Equal(
            "Rate limit reached. Retrying in 3s (attempt 2 of 4)",
            status.Describe(Start.AddSeconds(6)).Label);
        Assert.Equal(
            "Rate limit reached. Retrying in 1s (attempt 2 of 4)",
            status.Describe(Start.AddSeconds(8.5)).Label);
        Assert.Equal(
            "Rate limit reached. Retrying now (attempt 2 of 4)",
            status.Describe(Start.AddSeconds(9)).Label);
    }

    [Fact]
    public void A_settled_retry_leaves_the_attempt_notice()
    {
        var status = Started();
        status.OnRetry(ChatRetryCause.Overloaded, 1, 4, TimeSpan.FromSeconds(1), Start.AddSeconds(2));
        status.OnRetrySettled();
        Assert.Equal(
            "Taking longer than usual. Trying again shortly (attempt 2)",
            status.Describe(Start.AddSeconds(4)).Label);
    }

    [Fact]
    public void A_retried_turn_shows_its_notice_even_once_content_arrives()
    {
        // The reference's own gate: (!hasContent || retryCount) — a turn that has
        // already been retried keeps saying so while it streams.
        var status = Started();
        status.OnRetry(ChatRetryCause.RequestFailed, 1, 4, TimeSpan.Zero, Start);
        status.OnRetrySettled();
        status.OnContent();
        Assert.True(status.Describe(Start.AddSeconds(1)).Visible);
    }

    [Fact]
    public void Compaction_wins_over_everything_and_never_retreats()
    {
        var status = Started();
        status.OnCompacting(Start.AddSeconds(10));
        var first = status.Describe(Start.AddSeconds(35));
        Assert.Equal(ChatStatusLabels.Compacting, first.Label);
        Assert.Equal(63, first.Progress);

        // A clock that steps backwards keeps the bar where it was.
        Assert.Equal(63, status.Describe(Start.AddSeconds(12)).Progress);

        status.OnCompacted();
        Assert.Equal(100, status.Describe(Start.AddSeconds(36)).Progress);
    }

    [Fact]
    public void Ending_the_turn_clears_everything()
    {
        var status = Started();
        status.OnRetry(ChatRetryCause.RateLimit, 1, 4, TimeSpan.FromSeconds(5), Start);
        status.OnCompacting(Start);
        status.EndTurn();
        Assert.False(status.Describe(Start.AddSeconds(60)).Visible);
        Assert.False(status.Running);
    }
}
