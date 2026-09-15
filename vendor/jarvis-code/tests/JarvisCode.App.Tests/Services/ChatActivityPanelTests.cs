using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ChatActivityPanelTests
{
    [Fact]
    public void Counts_read_the_reference_way()
    {
        Assert.Equal("1 search", ChatActivityPanel.SearchCount(1));
        Assert.Equal("3 searches", ChatActivityPanel.SearchCount(3));
        Assert.Equal("1 result", ChatActivityPanel.ResultCount(1));
        Assert.Equal("2 results", ChatActivityPanel.ResultCount(2));
        Assert.Equal("1 tool call", ChatActivityPanel.ToolCallCount(1));
        Assert.Equal("4 tool calls", ChatActivityPanel.ToolCallCount(4));
    }

    [Fact]
    public void A_search_is_titled_by_its_query()
    {
        var search = ChatActivityPanel.Describe(
            """{"query":"wpf cubic bezier"}""",
            "Nothing found.",
            isError: false);
        Assert.Equal("wpf cubic bezier", search.Query);
    }

    [Fact]
    public void A_search_with_no_query_is_titled_by_the_panel()
    {
        Assert.Equal(ChatActivityPanel.WebSearchTitle, ChatActivityPanel.Describe("{}", "", false).Query);
        Assert.Equal(ChatActivityPanel.WebSearchTitle, ChatActivityPanel.Describe("not json", "", false).Query);
    }

    [Fact]
    public void A_failed_search_lists_no_results()
    {
        var search = ChatActivityPanel.Describe(
            """{"query":"x"}""", "https://example.com/a", isError: true);
        Assert.True(search.IsError);
        Assert.Empty(search.Results);
    }

    [Fact]
    public void Plain_result_lines_become_rows()
    {
        var rows = ChatActivityPanel.ParseResults(
            "1. Cubic beziers explained — https://example.com/bezier\nnothing here\n- Another https://docs.example.org/x?y=1");
        Assert.Equal(2, rows.Count);
        Assert.Equal("https://example.com/bezier", rows[0].Url);
        Assert.Equal("example.com", rows[0].Domain);
        Assert.Contains("Cubic beziers", rows[0].Title, StringComparison.Ordinal);
        Assert.Equal("docs.example.org", rows[1].Domain);
    }

    [Fact]
    public void The_vendor_links_line_becomes_rows()
    {
        var rows = ChatActivityPanel.ParseResults(
            """Links: [{"title":"Docs","url":"https://example.com/a"},{"url":"https://example.com/b"}]""");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Docs", rows[0].Title);
        Assert.Equal("https://example.com/b", rows[1].Title);
    }

    [Fact]
    public void A_links_line_that_is_not_json_lists_nothing()
        => Assert.Empty(ChatActivityPanel.ParseResults("Links: No links found."));
}
