using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class PromptHistoryTests
{
    private static ComposerHistory History() => new(["third", "second thing", "first thing"]);

    [Fact]
    public void Ctrl_R_opens_the_search_on_the_whole_history()
    {
        var view = History().BeginSearch("draft");
        Assert.Equal(ComposerHistoryMode.Search, view.Mode);
        Assert.Equal("", view.Query);
        Assert.False(view.Failed);
        Assert.True(view.HasDraft);
        Assert.Null(view.Text);
    }

    [Fact]
    public void Typing_lands_on_the_newest_match()
    {
        var history = History();
        history.BeginSearch("");
        var view = history.Search("thing");
        Assert.Equal("second thing", view.Text);
        Assert.Equal(2, view.Total);
        Assert.Equal(1, view.Index);
        Assert.False(view.Failed);
    }

    [Fact]
    public void A_query_that_matches_nothing_reads_as_failed()
    {
        var history = History();
        history.BeginSearch("");
        var view = history.Search("zzz");
        Assert.True(view.Failed);
        Assert.Null(view.Text);
        Assert.Equal(0, view.Total);
    }

    [Fact]
    public void The_arrows_cycle_the_matches_and_stop_at_the_ends()
    {
        var history = History();
        history.BeginSearch("");
        history.Search("thing");
        Assert.Equal("first thing", history.Cycle(1).Text);
        Assert.Equal("first thing", history.Cycle(1).Text);
        Assert.Equal("second thing", history.Cycle(-1).Text);
        Assert.Equal("second thing", history.Cycle(-1).Text);
    }

    [Fact]
    public void Escape_puts_the_draft_back()
    {
        var history = History();
        history.BeginSearch("half a message");
        history.Search("thing");
        var view = history.Cancel();
        Assert.Equal(ComposerHistoryMode.Off, view.Mode);
        Assert.Equal("half a message", view.Text);
    }

    [Fact]
    public void Enter_keeps_the_match()
    {
        var history = History();
        history.BeginSearch("draft");
        history.Search("second");
        Assert.Equal("second thing", history.Accept().Text);
        Assert.Equal(ComposerHistoryMode.Off, history.Mode);
    }

    [Fact]
    public void The_walk_steps_back_through_the_history_and_returns_the_draft()
    {
        var history = History();
        var first = history.Navigate(1, "draft");
        Assert.Equal(ComposerHistoryMode.Navigate, first.Mode);
        Assert.Equal("third", first.Text);
        Assert.Equal(1, first.Index);
        Assert.Equal(3, first.Total);
        Assert.True(first.HasDraft);

        Assert.Equal("second thing", history.Navigate(1, "draft").Text);
        Assert.Equal("third", history.Navigate(-1, "draft").Text);

        var back = history.Navigate(-1, "draft");
        Assert.Equal(ComposerHistoryMode.Off, back.Mode);
        Assert.Equal("draft", back.Text);
    }

    [Fact]
    public void The_walk_stops_at_the_oldest_entry()
    {
        var history = History();
        history.Navigate(1, "");
        history.Navigate(1, "");
        history.Navigate(1, "");
        Assert.Equal("first thing", history.Navigate(1, "").Text);
    }

    [Fact]
    public void An_empty_history_has_nothing_to_walk()
        => Assert.Equal(ComposerHistoryMode.Off, new ComposerHistory([]).Navigate(1, "draft").Mode);

    [Fact]
    public void The_search_status_names_the_match_and_the_failure()
    {
        Assert.Equal("Search history: thing — match 1 of 2", ComposerHistory.SearchStatus("thing", 1, 2, false));
        Assert.Equal("Search history: zzz — no match", ComposerHistory.SearchStatus("zzz", 0, 0, true));
        Assert.Equal("Search history: ", ComposerHistory.SearchStatus("", 0, 0, false));
    }

    [Fact]
    public void The_store_keeps_the_newest_first_without_repeats()
    {
        var file = Path.Combine(Path.GetTempPath(), "jarvis-history-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var store = new ComposerHistoryStore(file);
            store.Add("one");
            store.Add("two");
            store.Add("  ");
            store.Add("one");
            Assert.Equal(["one", "two"], store.Entries);
            Assert.Equal(["one", "two"], new ComposerHistoryStore(file).Entries);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_store_is_capped()
    {
        var file = Path.Combine(Path.GetTempPath(), "jarvis-history-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var store = new ComposerHistoryStore(file);
            for (var i = 0; i < ComposerHistoryStore.Capacity + 20; i++)
            {
                store.Add("prompt " + i);
            }

            Assert.Equal(ComposerHistoryStore.Capacity, store.Entries.Count);
            Assert.Equal("prompt " + (ComposerHistoryStore.Capacity + 19), store.Entries[0]);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
