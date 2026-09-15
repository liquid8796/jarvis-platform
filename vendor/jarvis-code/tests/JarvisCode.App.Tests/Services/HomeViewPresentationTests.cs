using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The home view's rules: which sessions need attention, how many rows fit, how a
/// pull request becomes a pill, and the narrow clock beside each row.
/// </summary>
public sealed class HomeViewPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static HomeSessionInput Session(string id, int minutesAgo = 5) =>
        new(id, id, Now.AddMinutes(-minutesAgo), false, false, false, false, false);

    [Theory]
    [InlineData(600, 3)]
    [InlineData(800, 4)]
    [InlineData(900, 5)]
    [InlineData(1000, 6)]
    [InlineData(1200, 7)]
    public void The_row_budget_grows_with_the_window(double height, int expected)
    {
        Assert.Equal(expected, HomeViewPresentation.RowBudget(height));
        Assert.Equal(Math.Min(expected, 5), HomeViewPresentation.SessionLimit(height));
    }

    [Fact]
    public void The_greeting_switches_once_there_is_something_to_act_on()
    {
        Assert.Equal("What’s up next?", HomeViewPresentation.Greeting(true, null));
        Assert.Equal("What’s up next, Sam?", HomeViewPresentation.Greeting(true, "Sam"));
        Assert.Equal("Welcome back", HomeViewPresentation.Greeting(false, null));
        Assert.Equal("Welcome back, Sam", HomeViewPresentation.Greeting(false, "Sam"));
    }

    [Fact]
    public void The_greeting_name_is_the_email_local_part_then_the_first_word()
    {
        Assert.Equal("sam", HomeViewPresentation.GreetingName("sam@example.test", "Samantha Doe"));
        Assert.Equal("Samantha", HomeViewPresentation.GreetingName(null, "Samantha Doe"));
        Assert.Equal("Samantha", HomeViewPresentation.GreetingName("not an address", "Samantha Doe"));
        Assert.Null(HomeViewPresentation.GreetingName(null, null));
    }

    [Fact]
    public void Attention_rows_lead_with_the_blocked_ones_newest_first()
    {
        var rows = HomeViewPresentation.AttentionRows(
        [
            Session("unread-old", 40) with { IsUnread = true },
            Session("blocked-old", 30) with { NeedsInput = true },
            Session("unread-new", 2) with { IsUnread = true },
            Session("blocked-new", 1) with { NeedsInput = true },
        ]);

        Assert.Equal(["blocked-new", "blocked-old", "unread-new", "unread-old"],
            rows.Select(r => r.Session.Id));
        Assert.Equal(HomeSessionKind.Blocked, rows[0].Kind);
        Assert.Equal(HomeSessionKind.Unread, rows[2].Kind);
    }

    [Fact]
    public void Archived_pinned_running_and_dismissed_sessions_never_list()
    {
        var rows = HomeViewPresentation.AttentionRows(
        [
            Session("archived") with { IsUnread = true, IsArchived = true },
            Session("pinned") with { IsUnread = true, IsPinned = true },
            Session("running") with { NeedsInput = true, IsRunning = true },
            Session("dismissed") with { IsUnread = true, DismissedAt = Now },
            Session("kept") with { IsUnread = true },
        ]);
        Assert.Equal(["kept"], rows.Select(r => r.Session.Id));
    }

    [Fact]
    public void A_dismissal_older_than_the_activity_stops_hiding_the_row()
    {
        var rows = HomeViewPresentation.AttentionRows(
            [Session("stale", 1) with { IsUnread = true, DismissedAt = Now.AddHours(-2) }]);
        Assert.Single(rows);
    }

    [Theory]
    [InlineData(PrDisplayState.Merged, HomePrCategory.InReview, "Merged")]
    [InlineData(PrDisplayState.Closed, HomePrCategory.InReview, "Closed")]
    [InlineData(PrDisplayState.Draft, HomePrCategory.Draft, "Draft")]
    [InlineData(PrDisplayState.Conflicting, HomePrCategory.NeedsAttention, "Merge conflicts")]
    [InlineData(PrDisplayState.ChangesRequested, HomePrCategory.NeedsAttention, "Changes requested")]
    [InlineData(PrDisplayState.Queued, HomePrCategory.ReadyToMerge, "Queued")]
    [InlineData(PrDisplayState.Open, HomePrCategory.InReview, "Ready for review")]
    public void The_pr_pill_follows_the_state(PrDisplayState state, HomePrCategory category, string label)
    {
        var (actual, pill) = HomeViewPresentation.Classify(state, null);
        Assert.Equal(category, actual);
        Assert.Equal(label, pill.Label);
    }

    [Fact]
    public void A_failing_check_outranks_everything_but_conflicts_and_review()
    {
        var (category, pill) = HomeViewPresentation.Classify(PrDisplayState.Queued, new CiCounts(1, 1, 0));
        Assert.Equal(HomePrCategory.NeedsAttention, category);
        Assert.Equal("CI failing", pill.Label);
        Assert.Equal(HomePrTone.Red, pill.Tone);
    }

    [Fact]
    public void An_approval_reads_ready_to_merge_only_once_nothing_is_running()
    {
        Assert.Equal("Ready to merge",
            HomeViewPresentation.Classify(PrDisplayState.Approved, new CiCounts(3, 0, 0)).Pill.Label);
        Assert.Equal("Ready to merge",
            HomeViewPresentation.Classify(PrDisplayState.Approved, null).Pill.Label);
        Assert.Equal("Approved",
            HomeViewPresentation.Classify(PrDisplayState.Approved, new CiCounts(1, 0, 2)).Pill.Label);
    }

    [Fact]
    public void A_running_check_run_shows_its_count()
    {
        var (_, pill) = HomeViewPresentation.Classify(PrDisplayState.Open, new CiCounts(2, 0, 3));
        Assert.Equal("CI 2/5", pill.Label);
    }

    [Fact]
    public void Pr_rows_drop_drafts_and_settled_prs_dedupe_and_sort_urgent_first()
    {
        var rows = HomeViewPresentation.PrRows(
        [
            Entry("a", 10, new PrInfo(1, "https://x/1", PrDisplayState.Open)),
            Entry("b", 20, new PrInfo(2, "https://x/2", PrDisplayState.Conflicting)),
            Entry("c", 5, new PrInfo(3, "https://x/3", PrDisplayState.Draft)),
            Entry("d", 1, new PrInfo(4, "https://x/4", PrDisplayState.Merged)),
            Entry("e", 30, new PrInfo(1, "https://x/1", PrDisplayState.Open)),
        ]);

        Assert.Equal([2, 1], rows.Select(r => r.Entry.Pr.Number));
        Assert.Equal(HomePrCategory.NeedsAttention, rows[0].Category);
    }

    private static HomePrInput Entry(string id, int minutesAgo, PrInfo pr) =>
        new(id, id, Now.AddMinutes(-minutesAgo), pr, "owner/repo", "feature");

    [Theory]
    [InlineData(30, "now")]
    [InlineData(60, "1m ago")]
    [InlineData(60 * 5, "5m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(3600 * 5, "5h ago")]
    [InlineData(86400, "yesterday")]
    [InlineData(86400 * 3, "3d ago")]
    [InlineData(604800, "last wk.")]
    [InlineData(604800 * 3, "3w ago")]
    [InlineData(2592000, "last mo.")]
    [InlineData(2592000 * 4, "4mo ago")]
    [InlineData(31536000, "last yr.")]
    [InlineData(31536000 * 2, "2y ago")]
    public void The_narrow_clock_is_intl_narrow_relative_time(int secondsAgo, string expected) =>
        Assert.Equal(expected, HomeViewPresentation.NarrowRelative(Now.AddSeconds(-secondsAgo), Now));
}
