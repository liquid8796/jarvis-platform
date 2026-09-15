using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The harness turn of a session whose MCP surface is large enough to engage
/// tool search: the reference names every deferred tool in a
/// <c>&lt;system-reminder&gt;</c> that <em>leads</em> the turn, ahead of the
/// agent roster and the skill listing, and names each one once.
/// </summary>
public sealed class DeferredToolNoticeTests
{
    private sealed class Stub(string name, string description = "stub") : ITool
    {
        public string Name => name;
        public string Description => description;
        public JsonObject InputSchema => new() { ["type"] = "object" };
        public bool IsReadOnly => true;
        public string DescribeCall(JsonObject arguments) => name;

        public Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    /// <summary>A shell server whose tools are past the port's engagement threshold.</summary>
    private static IReadOnlyList<ITool> BigMcpSurface()
    {
        var body = new string('d', 900);
        return InternalMcpServers.Compose(
            [
                new InternalMcpServerDefinition(
                    "claude-in-chrome",
                    [.. Enumerable.Range(0, 30).Select(i => new Stub("tool_" + i, body))]),
            ],
            new InternalMcpSessionContext());
    }

    [Fact]
    public void A_large_mcp_surface_engages_deferral_and_takes_the_built_ins_with_it()
    {
        List<ITool> tools = [new Stub("Read"), new Stub("WebSearch"), new Stub("Monitor"), .. BigMcpSurface()];

        Assert.True(TurnContextFactory.WillDefer(tools, "claude-opus-5"));
        // The reference defers every tool that is not alwaysLoad, built-ins included.
        Assert.True(TurnContextFactory.Deferrable(new Stub("WebSearch")));
        Assert.True(TurnContextFactory.Deferrable(new Stub("Monitor")));
        Assert.False(TurnContextFactory.Deferrable(new Stub("Read")));
    }

    [Fact]
    public void The_block_leads_the_harness_turn_and_names_each_tool_once()
    {
        List<ITool> tools = [new Stub("Read"), new Stub("WebSearch"), new Stub("Monitor"), .. BigMcpSurface()];
        var registry = new DeferredToolRegistry(tools, TurnContextFactory.Deferrable);
        var state = new SkillSessionState();

        var block = DeferredToolNotice.Build(state, registry, mcp: null, nonInteractive: false);

        Assert.NotNull(block);
        Assert.StartsWith(
            JarvisCode.Core.Agent.DeferredToolAnnouncements.NowAvailableHeader + "\nMonitor\nWebSearch\nmcp__",
            block);
        Assert.DoesNotContain("\nRead\n", block);

        // The sections the view model composes: the deferred block, then the
        // roster, then the skill listing — the order a live 2.1.257 session sends.
        List<string> sections = [block!];
        sections.AddRange(HarnessSystemMessage.Sections(
            customAgents: null, skillListingBody: "- demo: a skill", includeAgentTypes: true));
        Assert.Equal(3, sections.Count);
        Assert.StartsWith("The following deferred tools are now available", sections[0]);
        Assert.StartsWith(HarnessSystemMessage.AgentTypesHeader, sections[1]);
        Assert.StartsWith("The following skills are available", sections[2]);

        // A second turn announces nothing, because nothing new appeared.
        Assert.Null(DeferredToolNotice.Build(
            state, new DeferredToolRegistry(tools, TurnContextFactory.Deferrable), mcp: null, nonInteractive: false));
    }

    [Fact]
    public async Task A_tool_fetched_in_one_turn_is_still_advertised_in_the_next()
    {
        // The asymmetry this pins: the fetched set used to live on the registry,
        // which is rebuilt every user turn, while the announcement delta lives on
        // the session. So the next turn deferred the tool again and said nothing
        // about it — the model still had the schema from the turn before and its
        // call came back "Unknown tool". The reference cannot reach that state:
        // its ToolSearch answers with tool_reference blocks, and the tool_result
        // carrying them rides every later request.
        List<ITool> tools = [new Stub("Read"), new Stub("WebSearch"), new Stub("Monitor"), .. BigMcpSurface()];
        var state = new SkillSessionState();

        var first = new DeferredToolRegistry(tools, TurnContextFactory.Deferrable, state.LoadedDeferredTools);
        Assert.NotNull(DeferredToolNotice.Build(state, first, mcp: null, nonInteractive: false));
        var fetched = await first.Find("ToolSearch")!.ExecuteAsync(
            new JsonObject { ["query"] = "select:WebSearch" },
            new ToolExecutionContext { WorkingDirectory = "." },
            CancellationToken.None);
        Assert.False(fetched.IsError, fetched.Content);
        Assert.Contains("WebSearch", state.LoadedDeferredTools);

        var second = new DeferredToolRegistry(tools, TurnContextFactory.Deferrable, state.LoadedDeferredTools);
        Assert.Contains("WebSearch", second.All.Select(t => t.Name));
        Assert.NotNull(second.Find("WebSearch"));
        // Nothing new to announce — and now that silence is the truth rather
        // than what hid the defect.
        Assert.Null(DeferredToolNotice.Build(state, second, mcp: null, nonInteractive: false));
        // Monitor was never fetched, so it is still deferred and still hidden.
        Assert.DoesNotContain("Monitor", second.All.Select(t => t.Name));
    }

    [Fact]
    public void With_every_tool_loaded_there_is_no_block_at_all()
    {
        // Tool search is a per-model answer, not a size one: the reference
        // refuses it for the two haiku-3 spellings (its sW), and there every
        // built-in is advertised and no deferred-tools reminder is sent.
        List<ITool> tools = [new Stub("Read"), new Stub("WebSearch"), new Stub("Monitor")];

        Assert.False(TurnContextFactory.WillDefer(tools, "claude-3-5-haiku-20241022"));
        Assert.Null(DeferredToolNotice.Build(
            new SkillSessionState(), deferred: null, mcp: null, nonInteractive: true));
    }

    [Fact]
    public void Tool_search_answers_to_its_old_name_without_advertising_it()
    {
        var registry = new DeferredToolRegistry(
            [new Stub("Read"), new Stub("WebSearch")], TurnContextFactory.Deferrable);

        Assert.Contains("ToolSearch", registry.All.Select(t => t.Name));
        Assert.DoesNotContain("tool_search", registry.All.Select(t => t.Name));
        Assert.NotNull(registry.Find("tool_search"));
    }

    /// <summary>The tasks and tool-result directories the reference's own paths are shaped after.</summary>
    [Fact]
    public void Task_output_and_persisted_results_live_where_the_reference_puts_them()
    {
        var cwd = Path.GetTempPath();

        Assert.EndsWith(Path.Combine("tasks"), SessionScratchpad.TasksDirectory(cwd));
        Assert.EndsWith(
            Path.Combine("session-1", "tool-results"), SessionScratchpad.ToolResultsDirectory(cwd, "session-1"));
        Assert.EndsWith(
            Path.Combine("session-1", "agents"), SessionScratchpad.AgentTranscriptsDirectory(cwd, "session-1"));
    }
}
