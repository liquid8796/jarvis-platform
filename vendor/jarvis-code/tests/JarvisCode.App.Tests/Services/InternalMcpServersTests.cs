using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

public sealed class InternalMcpServersTests
{
    private static ITool Tool(string name, bool readOnly = true) =>
        McpToolBuilderProbe.Tool(name, readOnly);

    [Theory]
    [InlineData("mcp__ccd_session__spawn_task", "ccd_session", "spawn_task")]
    [InlineData("mcp__claude-in-chrome__read_page", "claude-in-chrome", "read_page")]
    [InlineData("mcp__Claude_Browser__tabs_context", "Claude_Browser", "tabs_context")]
    public void Wire_names_split_into_server_and_tool(string wire, string server, string tool)
    {
        Assert.True(InternalMcpServers.TrySplit(wire, out var actualServer, out var actualTool));
        Assert.Equal(server, actualServer);
        Assert.Equal(tool, actualTool);
        Assert.Equal(tool, InternalMcpServers.ShortName(wire));
    }

    [Theory]
    [InlineData("read_page")]          // bare
    [InlineData("mcp__onlyserver")]    // no tool half
    [InlineData("mcp__server__")]      // empty tool half
    [InlineData("mcp____tool")]        // empty server half
    public void A_name_that_is_not_server_and_tool_does_not_split(string name)
    {
        Assert.False(InternalMcpServers.TrySplit(name, out _, out _));

        // ShortName must return the name unchanged rather than a fragment: the
        // transcript renderer and the batch validator both key off it.
        Assert.Equal(name, InternalMcpServers.ShortName(name));
    }

    [Fact]
    public void Composing_applies_the_server_prefix()
    {
        var composed = InternalMcpServers.Compose(
            [new InternalMcpServerDefinition("terminal", [Tool("read_terminal")])],
            new InternalMcpSessionContext());

        Assert.Equal(["mcp__terminal__read_terminal"], composed.Select(static t => t.Name));

        var registry = new ToolRegistry(composed);
        Assert.NotNull(registry.Find("mcp__terminal__read_terminal"));
        Assert.Null(registry.Find("nope"));
    }

    /// <summary>
    /// The reference's shadow rule: a server the user configured under a name a
    /// shell server also uses keeps the name. Without it both would compose
    /// <c>mcp__terminal__…</c> and one of the two would silently disappear from
    /// the registry.
    /// </summary>
    [Fact]
    public void A_configured_server_of_the_same_name_takes_the_name_back()
    {
        var definitions = new[]
        {
            new InternalMcpServerDefinition("terminal", [Tool("read_terminal")]),
            new InternalMcpServerDefinition("ccd_session", [Tool("spawn_task")]),
        };

        // Folded the reference's way, so "Terminal" and "terminal" are one name.
        var composed = InternalMcpServers.Compose(
            definitions, new InternalMcpSessionContext(), toggles: null,
            configuredServerNames: ["Terminal"]);

        Assert.Equal(["mcp__ccd_session__spawn_task"], composed.Select(static t => t.Name));

        // With nothing configured, both servers compose as before.
        Assert.Equal(2, InternalMcpServers.Compose(definitions, new InternalMcpSessionContext()).Count);
    }

    /// <summary>
    /// Claude_Browser and claude-in-chrome both carry a read_page, a computer and
    /// a navigate. If the bare name were an alias it would resolve to whichever
    /// composed last, which is a coin toss dressed as a lookup.
    /// </summary>
    [Fact]
    public void A_name_two_servers_share_does_not_resolve_bare()
    {
        var composed = InternalMcpServers.Compose(
            [
                new InternalMcpServerDefinition("Claude_Browser", [Tool("read_page")]),
                new InternalMcpServerDefinition("claude-in-chrome", [Tool("read_page")]),
            ],
            new InternalMcpSessionContext());

        var registry = new ToolRegistry(composed);
        Assert.NotNull(registry.Find("mcp__Claude_Browser__read_page"));
        Assert.NotNull(registry.Find("mcp__claude-in-chrome__read_page"));
        Assert.Null(registry.Find("read_page"));
    }

    [Fact]
    public void A_legacy_name_resolves_without_being_advertised()
    {
        var composed = InternalMcpServers.Compose(
            [
                new InternalMcpServerDefinition("claude-in-chrome", [Tool("read_page")])
                {
                    LegacyNames = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["read_page"] = ["browser_read_page"],
                    },
                },
            ],
            new InternalMcpSessionContext());

        Assert.Equal(["mcp__claude-in-chrome__read_page"], composed.Select(static t => t.Name));
        Assert.NotNull(new ToolRegistry(composed).Find("browser_read_page"));
    }

    /// <summary>
    /// One reference tool can replace a whole family of this app's own — the
    /// Android emulator's control took over four bare android_* tools — so a
    /// tool carries as many aliases as it needs and advertises none of them.
    /// </summary>
    [Fact]
    public void Several_legacy_names_resolve_onto_one_tool()
    {
        var composed = InternalMcpServers.Compose(
            [
                new InternalMcpServerDefinition("Claude_Code_Android_Emulator", [Tool("control")])
                {
                    LegacyNames = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["control"] = ["android_devices", "android_screenshot", "android_input"],
                    },
                },
            ],
            new InternalMcpSessionContext());

        Assert.Equal(["mcp__Claude_Code_Android_Emulator__control"], composed.Select(static t => t.Name));
        var registry = new ToolRegistry(composed);
        Assert.NotNull(registry.Find("android_devices"));
        Assert.NotNull(registry.Find("android_screenshot"));
        Assert.NotNull(registry.Find("android_input"));
        Assert.Null(registry.Find("control"));
    }

    [Fact]
    public void A_disabled_server_contributes_nothing()
    {
        var composed = InternalMcpServers.Compose(
            [
                new InternalMcpServerDefinition("terminal", [Tool("read_terminal")])
                {
                    IsEnabled = static _ => false,
                },
            ],
            new InternalMcpSessionContext());

        Assert.Empty(composed);
    }

    [Fact]
    public void A_tool_switched_off_is_dropped_and_an_emptied_server_is_skipped()
    {
        var definitions = new[]
        {
            new InternalMcpServerDefinition("terminal", [Tool("read_terminal")]),
            new InternalMcpServerDefinition("ccd_directory", [Tool("request_directory"), Tool("change_directory")]),
        };

        var toggles = new Dictionary<string, bool>
        {
            [InternalMcpServers.ToggleKey("terminal", "read_terminal")] = false,
            [InternalMcpServers.ToggleKey("ccd_directory", "change_directory")] = false,
        };

        var composed = InternalMcpServers.Compose(definitions, new InternalMcpSessionContext(), toggles);

        // terminal lost its only tool and vanishes entirely; ccd_directory keeps one.
        Assert.Equal(["mcp__ccd_directory__request_directory"], composed.Select(static t => t.Name));
    }

    [Fact]
    public void An_absent_toggle_leaves_a_tool_on()
    {
        var composed = InternalMcpServers.Compose(
            [new InternalMcpServerDefinition("terminal", [Tool("read_terminal")])],
            new InternalMcpSessionContext(),
            new Dictionary<string, bool> { ["local:terminal:something_else"] = false });

        Assert.Single(composed);
    }

    [Fact]
    public void Dynamic_tools_join_the_static_ones()
    {
        var live = false;
        var definition = new InternalMcpServerDefinition("computer-use", [Tool("request_access")])
        {
            GetDynamicTools = () => live ? [Tool("screenshot")] : [],
        };

        Assert.Single(InternalMcpServers.Compose([definition], new InternalMcpSessionContext()));

        live = true;
        Assert.Equal(2, InternalMcpServers.Compose([definition], new InternalMcpSessionContext()).Count);
    }

    [Fact]
    public void Only_this_shells_servers_count_as_internal()
    {
        Assert.True(InternalMcpServers.IsInternal("mcp__terminal__read_terminal"));
        Assert.True(InternalMcpServers.IsInternal("mcp__Claude_Browser__navigate"));

        // A tool from a configured MCP server must stay deferrable.
        Assert.False(InternalMcpServers.IsInternal("mcp__github__create_issue"));
        Assert.False(InternalMcpServers.IsInternal("Read"));
    }

    [Fact]
    public void The_toggle_key_is_the_references_shape()
    {
        Assert.Equal("local:terminal:read_terminal", InternalMcpServers.ToggleKey("terminal", "read_terminal"));
    }
}

/// <summary>A minimal tool, so the composer tests are about composition only.</summary>
internal static class McpToolBuilderProbe
{
    public static ITool Tool(string name, bool readOnly) => new Probe(name, readOnly);

    private sealed class Probe(string name, bool readOnly) : ITool
    {
        public string Name => name;

        public string Description => $"{name} probe";

        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

        public bool IsReadOnly => readOnly;

        public string DescribeCall(JsonObject arguments) => $"{name}()";

        public Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Success(name));
    }
}
