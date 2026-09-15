using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Tests.Tools;

public sealed class DeferredToolsTests
{
    private class FakeTool(string name, string description = "a tool") : ITool
    {
        public string Name => name;
        public string Description => description;
        public JsonObject InputSchema => new() { ["type"] = "object" };
        public bool IsReadOnly => true;
        public string DescribeCall(JsonObject arguments) => name;
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class AliasedFakeTool(string name, params string[] aliases)
        : FakeTool(name), IAliasedTool
    {
        public IReadOnlyList<string> Aliases => aliases;
    }

    private static readonly ToolExecutionContext Context = new() { WorkingDirectory = "." };

    private static DeferredToolRegistry Registry(ISet<string>? loaded = null) => new(
        [
            new FakeTool("Read"),
            new FakeTool("mcp__gh__create_issue", "Creates a GitHub issue"),
            new FakeTool("mcp__gh__list_prs", "Lists pull requests"),
            new FakeTool("mcp__db__query", "Runs a SQL query"),
        ],
        t => t.Name.StartsWith("mcp__", StringComparison.Ordinal),
        loaded);

    /// <summary>The names this turn's request would carry.</summary>
    private static List<string> Advertised(DeferredToolRegistry registry) =>
        [.. registry.All.Select(t => t.Name)];

    [Fact]
    public void DeferredToolsAreHidden_ButToolSearchIsAdvertised()
    {
        var registry = Registry();
        var names = registry.All.Select(t => t.Name).ToList();
        Assert.Contains("Read", names);
        // The reference's name, with the port's old one still resolving so a
        // stored session replays.
        Assert.Contains("ToolSearch", names);
        Assert.DoesNotContain("tool_search", names);
        Assert.NotNull(registry.Find("tool_search"));
        Assert.DoesNotContain("mcp__gh__create_issue", names);
        // Deferring narrows what is advertised, not what exists. The harness
        // tells the model a deferred call "will fail with InputValidationError",
        // which is an answer about the arguments rather than about the name, so
        // the name still resolves.
        Assert.NotNull(registry.Find("mcp__gh__create_issue"));

        // The reference's doc does not list the deferred names — the harness
        // names them in a <system-reminder> instead.
        var search = registry.Find("ToolSearch")!;
        Assert.StartsWith("Fetches full schema definitions for deferred tools", search.Description);
        Assert.DoesNotContain("mcp__db__query", search.Description);
    }

    [Fact]
    public async Task SelectQuery_EnablesExactTools()
    {
        var registry = Registry();
        var search = registry.Find("ToolSearch")!;

        var result = await search.ExecuteAsync(
            new JsonObject { ["query"] = "select:mcp__gh__create_issue,mcp__db__query" }, Context, default);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("mcp__gh__create_issue", result.Content);
        Assert.Contains("mcp__gh__create_issue", Advertised(registry));
        Assert.Contains("mcp__db__query", Advertised(registry));
        Assert.DoesNotContain("mcp__gh__list_prs", Advertised(registry));
        Assert.Equal(["mcp__gh__list_prs"], registry.DeferredNames);
        // The advertised set grew, which is what makes the orchestrator re-send tools.
        Assert.Equal(4, registry.All.Count);
    }

    [Fact]
    public async Task KeywordQuery_RanksByMatches_AndRespectsMaxResults()
    {
        var registry = Registry();
        var search = registry.Find("ToolSearch")!;

        var result = await search.ExecuteAsync(
            new JsonObject { ["query"] = "github issue", ["max_results"] = 1 }, Context, default);

        Assert.False(result.IsError, result.Content);
        // create_issue matches both keywords; list_prs only one.
        Assert.Contains("mcp__gh__create_issue", Advertised(registry));
        Assert.DoesNotContain("mcp__gh__list_prs", Advertised(registry));
    }

    [Fact]
    public async Task NoMatch_AnswersTheReferenceSentence()
    {
        var registry = Registry();
        var search = registry.Find("ToolSearch")!;

        var result = await search.ExecuteAsync(
            new JsonObject { ["query"] = "kubernetes" }, Context, default);

        // The reference answers an ordinary result, not an error, and does not
        // list what is left.
        Assert.False(result.IsError, result.Content);
        Assert.Equal("No matching deferred tools found", result.Content);

        var empty = await search.ExecuteAsync([], Context, default);
        Assert.True(empty.IsError);
    }

    [Fact]
    public async Task AFetchedToolStaysLoadedForTheRestOfTheSession()
    {
        // The reference answers ToolSearch with tool_reference blocks the API
        // expands server-side, and that tool_result is replayed on every later
        // request — so a fetched tool stays advertised for the whole session
        // without the harness tracking anything. This port writes the schemas
        // itself, so the session carries the loaded names and the registry each
        // turn builds is seeded from them.
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var first = Registry(loaded);
        var result = await first.Find("ToolSearch")!.ExecuteAsync(
            new JsonObject { ["query"] = "select:mcp__gh__create_issue" }, Context, default);
        Assert.False(result.IsError, result.Content);
        Assert.Equal(["mcp__gh__create_issue"], loaded);

        var next = Registry(loaded);
        Assert.Contains("mcp__gh__create_issue", Advertised(next));
        Assert.DoesNotContain("mcp__gh__create_issue", next.DeferredNames);
        // It was named to the model once; the delta says nothing about it again.
        Assert.DoesNotContain("mcp__gh__create_issue", next.TakeUnannouncedNames());
        Assert.NotNull(next.Find("mcp__gh__create_issue"));
        // Its neighbours are untouched.
        Assert.DoesNotContain("mcp__db__query", Advertised(next));

        // The set is the whole mechanism: without it the next turn defers the
        // tool again and the model is never told, which is what left a fetched
        // tool unadvertised one user turn after it was fetched.
        var unseeded = Registry();
        Assert.DoesNotContain("mcp__gh__create_issue", Advertised(unseeded));
        Assert.Contains("mcp__gh__create_issue", unseeded.DeferredNames);
    }

    [Fact]
    public void ADeferredToolResolvesUnderItsAlias()
    {
        // The renamed internal MCP tools keep their old bare name as an alias so
        // a stored session replays; deferral must not take that away.
        var registry = new DeferredToolRegistry(
            [new AliasedFakeTool("mcp__computer-use__computer_batch", "computer_batch")],
            static t => t.Name.StartsWith("mcp__", StringComparison.Ordinal));

        Assert.Equal("mcp__computer-use__computer_batch", registry.Find("computer_batch")?.Name);
    }

    [Fact]
    public async Task ReSearchingALoadedToolStillAnswersWithItsSchema()
    {
        var loaded = new HashSet<string> { "mcp__db__query" };
        var registry = Registry(loaded);

        var result = await registry.Find("ToolSearch")!.ExecuteAsync(
            new JsonObject { ["query"] = "select:mcp__db__query" }, Context, default);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("mcp__db__query", result.Content);
    }
}
