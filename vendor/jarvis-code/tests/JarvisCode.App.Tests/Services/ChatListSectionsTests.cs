using JarvisCode.App.Services;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Tests.Services;

public class ChatListSectionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 15, 0, 0, TimeSpan.FromHours(7));

    private static SessionSummary Chat(string id, double daysAgo, string? title = null) =>
        new(id, title ?? id, Now.AddDays(-daysAgo), 2);

    [Fact]
    public void Recents_are_bucketed_by_day_the_reference_way()
    {
        var sessions = new[]
        {
            Chat("today", 0.2),
            Chat("yesterday", 1.2),
            Chat("threeDaysAgo", 3.2),
            Chat("ancient", 30),
        };

        var sections = ChatListSections.Build(sessions, [], null, 20, Now);
        Assert.Equal(
            [ChatListSections.Today, ChatListSections.Yesterday, "Aug 30", ChatListSections.Older],
            sections.Select(static s => s.Label));
        Assert.Equal(["today"], sections[0].Sessions.Select(static s => s.Id));
        Assert.Equal(["ancient"], sections[^1].Sessions.Select(static s => s.Id));
    }

    [Fact]
    public void An_empty_day_takes_no_header()
    {
        var sections = ChatListSections.Build([Chat("a", 0.1), Chat("b", 4.1)], [], null, 20, Now);
        Assert.Equal([ChatListSections.Today, "Aug 29"], sections.Select(static s => s.Label));
    }

    [Fact]
    public void Starred_chats_lead_in_a_collapsible_section()
    {
        var sessions = new[] { Chat("a", 0.1), Chat("b", 0.2) };
        var sections = ChatListSections.Build(sessions, new HashSet<string> { "b" }, null, 20, Now);
        Assert.Equal(ChatListSections.Starred, sections[0].Label);
        Assert.True(sections[0].Collapsible);
        Assert.Equal(["b"], sections[0].Sessions.Select(static s => s.Id));
        // …and a starred chat is not repeated in the day buckets.
        Assert.Equal(["a"], sections[1].Sessions.Select(static s => s.Id));
    }

    [Fact]
    public void A_search_flattens_the_list_and_hides_the_sections()
    {
        var sessions = new[] { Chat("a", 0.1, "Alpha report"), Chat("b", 0.2, "Beta notes") };
        var sections = ChatListSections.Build(sessions, new HashSet<string> { "b" }, "beta", 20, Now);
        var only = Assert.Single(sections);
        Assert.Equal(ChatListSections.Recents, only.Label);
        Assert.Equal(["b"], only.Sessions.Select(static s => s.Id));
    }

    [Fact]
    public void Recents_are_paged_and_show_more_says_so()
    {
        var sessions = Enumerable.Range(0, 45).Select(i => Chat("s" + i, i * 0.01)).ToList();
        Assert.True(ChatListSections.HasMore(sessions, [], 20));
        var shown = ChatListSections.Build(sessions, [], null, 20, Now).Sum(static s => s.Sessions.Count);
        Assert.Equal(20, shown);

        Assert.Equal(
            40,
            ChatListSections.Build(sessions, [], null, 40, Now).Sum(static s => s.Sessions.Count));
        Assert.False(ChatListSections.HasMore(sessions, [], 45));
    }

    [Fact]
    public void Starred_chats_do_not_count_against_the_page()
    {
        var sessions = Enumerable.Range(0, 25).Select(i => Chat("s" + i, i * 0.01)).ToList();
        var starred = new HashSet<string> { "s0", "s1" };
        Assert.True(ChatListSections.HasMore(sessions, starred, 20));
        Assert.False(ChatListSections.HasMore(sessions, starred, 23));
    }
}
