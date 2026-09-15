using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The one-lookup-per-folder cache the sidebar badge, the home view and the PR bar
/// share.
/// </summary>
public sealed class PrStatusCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_unread_folder_reports_nothing_and_says_so()
    {
        var cache = new PrStatusCache();
        Assert.Null(cache.For(@"C:\work\alpha"));
        Assert.False(cache.Knows(@"C:\work\alpha"));
    }

    [Fact]
    public void A_recorded_lookup_is_read_back_and_raises_changed()
    {
        var cache = new PrStatusCache();
        var changed = 0;
        cache.Changed += (_, _) => changed++;

        var pr = new PrInfo(7, "https://example.test/pr/7", PrDisplayState.Open);
        cache.Record(@"C:\work\alpha", pr);

        Assert.Same(pr, cache.For(@"C:\work\alpha"));
        Assert.True(cache.Knows(@"C:\work\alpha"));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void A_folder_with_no_pull_request_is_still_a_known_answer()
    {
        var cache = new PrStatusCache();
        cache.Record(@"C:\work\alpha", null);
        Assert.True(cache.Knows(@"C:\work\alpha"));
        Assert.Empty(cache.Stale([@"C:\work\alpha"], 8, Now));
    }

    [Fact]
    public void Stale_skips_the_fresh_the_blank_and_the_repeated_and_stops_at_the_limit()
    {
        var cache = new PrStatusCache { Freshness = TimeSpan.FromMinutes(2) };
        cache.Record(@"C:\work\alpha", null);

        var stale = cache.Stale(["", @"C:\work\alpha", @"C:\work\beta", @"C:\work\beta", @"C:\work\gamma"],
            limit: 1, now: DateTimeOffset.UtcNow);
        Assert.Equal([@"C:\work\beta"], stale);
    }

    [Fact]
    public void An_answer_older_than_the_freshness_window_is_stale_again()
    {
        var cache = new PrStatusCache { Freshness = TimeSpan.FromMinutes(2) };
        cache.Record(@"C:\work\alpha", null);
        var later = DateTimeOffset.UtcNow.AddMinutes(5);
        Assert.Equal([@"C:\work\alpha"], cache.Stale([@"C:\work\alpha"], 8, later));
    }

    [Fact]
    public void The_folder_key_ignores_case_the_way_windows_paths_do()
    {
        var cache = new PrStatusCache();
        cache.Record(@"C:\Work\Alpha", new PrInfo(1, "u", PrDisplayState.Open));
        Assert.NotNull(cache.For(@"c:\work\alpha"));
    }
}
