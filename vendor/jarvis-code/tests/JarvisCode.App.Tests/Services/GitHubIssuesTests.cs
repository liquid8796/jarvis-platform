using System.Text.Json;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class GitHubIssuesTests
{
    private static GitHubIssue Issue(string title = "Broken thing") =>
        new(12, title, "open", "owner/repo", "https://github.com/owner/repo/issues/12", ["bug", "p1"]);

    [Fact]
    public void CopyIsTheReferences()
    {
        Assert.Equal("Import issue", GitHubIssues.Title);
        Assert.Equal("Search by issue number or title", GitHubIssues.SearchPlaceholder);
        Assert.Equal("Loading...", GitHubIssues.Loading);
        Assert.Equal("Something went wrong. You can try again.", GitHubIssues.Failed);
        Assert.Equal("Select a repository to see issues.", GitHubIssues.SelectARepository);
        Assert.Equal("No issues you’re involved with here. Search to find any issue.", GitHubIssues.NoIssues);
        Assert.Equal("No issues match your search.", GitHubIssues.NoMatches);
    }

    [Fact]
    public void AnEmptyQueryAsksForTheIssuesYouAreInvolvedWith()
    {
        Assert.Equal("repo:o/r is:issue is:open involves:@me", GitHubIssues.SearchQuery("o/r", null));
        Assert.Equal("repo:o/r is:issue is:open involves:@me", GitHubIssues.SearchQuery("o/r", "  "));
    }

    [Fact]
    public void ATypedQueryNarrowsTheSameSearch()
    {
        Assert.Equal("repo:o/r is:issue is:open crash", GitHubIssues.SearchQuery("o/r", " crash "));
    }

    [Fact]
    public void ThePathsAreTheReferencesRestCalls()
    {
        Assert.StartsWith("search/issues?per_page=50&q=", GitHubIssues.SearchPath("o/r", null));
        Assert.Equal("repos/o/r/issues/9", GitHubIssues.IssuePath("o/r", 9));
    }

    [Fact]
    public void TheKeyIsRepoHashNumber()
    {
        Assert.Equal("owner/repo#12", Issue().Key);
    }

    [Fact]
    public void WithoutABodyTheContextNamesTheUrl()
    {
        Assert.Equal(
            "GitHub issue: https://github.com/owner/repo/issues/12",
            GitHubIssues.ContextBlock(Issue(), null));
    }

    [Fact]
    public void TheContextBlockIsTheReferencesShape()
    {
        var block = GitHubIssues.ContextBlock(
            Issue(), new GitHubIssueDetail("It crashes.", "octocat", ["bug", "p1"]));
        Assert.Equal(
            string.Join("\n",
            [
                "<github_issue>",
                "Repository: owner/repo",
                "Issue: #12 — Broken thing",
                "URL: https://github.com/owner/repo/issues/12",
                "Author: @octocat",
                "Labels: bug, p1",
                "",
                "It crashes.",
                "</github_issue>",
            ]),
            block);
    }

    [Fact]
    public void AnEmptyBodyReadsAsNoDescription()
    {
        var block = GitHubIssues.ContextBlock(Issue(), new GitHubIssueDetail("", "", []));
        Assert.Contains("(no description)", block);
        Assert.DoesNotContain("Author:", block);
        Assert.DoesNotContain("Labels:", block);
    }

    [Fact]
    public void ALongBodyIsTruncatedWithTheReferencesNote()
    {
        var body = new string('x', GitHubIssues.MaxBodyChars + 10);
        var block = GitHubIssues.ContextBlock(Issue(), new GitHubIssueDetail(body, "", []));
        Assert.Contains(
            "[... truncated; full issue at https://github.com/owner/repo/issues/12]", block);
    }

    [Fact]
    public void ControlAndFormatCharactersAreStripped()
    {
        Assert.Equal("ab", GitHubIssues.StripControls("a​b"));
        Assert.Equal("ab", GitHubIssues.StripControls("ab"));
        // The reference keeps these Arabic marks, which carry meaning.
        Assert.Equal("a؀b", GitHubIssues.StripControls("a؀b"));
        // Ordinary whitespace survives.
        Assert.Equal("a\nb\tc", GitHubIssues.StripControls("a\nb\tc"));
    }

    [Fact]
    public void SearchResponsesParseIntoRows()
    {
        const string json = """
        {"items":[{"number":3,"title":"T","state":"OPEN","html_url":"u","labels":[{"name":"bug"}]}]}
        """;
        using var document = JsonDocument.Parse(json);
        var rows = GitHubIssues.ParseSearch(document.RootElement, "o/r");
        var row = Assert.Single(rows);
        Assert.Equal(3, row.Number);
        Assert.Equal("T", row.Title);
        Assert.Equal("open", row.State);
        Assert.Equal("o/r", row.Repo);
        Assert.Equal("u", row.Url);
        Assert.Equal(["bug"], row.Labels);
    }

    [Fact]
    public void AnUnexpectedResponseIsNoRows()
    {
        using var document = JsonDocument.Parse("""{"message":"Not Found"}""");
        Assert.Empty(GitHubIssues.ParseSearch(document.RootElement, "o/r"));
    }

    [Fact]
    public void DetailResponsesCarryTheBodyAndAuthor()
    {
        const string json = """
        {"body":"B","user":{"login":"octocat"},"labels":[{"name":"bug"},{"name":"p1"}]}
        """;
        using var document = JsonDocument.Parse(json);
        var detail = GitHubIssues.ParseDetail(document.RootElement);
        Assert.NotNull(detail);
        Assert.Equal("B", detail.Body);
        Assert.Equal("octocat", detail.Author);
        Assert.Equal(["bug", "p1"], detail.Labels);
    }
}
