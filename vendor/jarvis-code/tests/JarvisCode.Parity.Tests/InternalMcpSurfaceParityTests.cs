using System.Net.Http;
using System.IO;
using System.Text;
using JarvisCode.App.Services;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The check the surface manifest could not make until it had a vocabulary for
/// MCP servers.
///
/// The desktop shell does not hand its engine a flat list of app tools: it
/// declares in-process MCP servers and proxies each tool to the model as
/// <c>mcp__{server}__{tool}</c>. That partition is a real part of the surface —
/// the wire name says which subsystem answered — and nothing here measured it.
/// A port could register every capability under a bare name, look complete
/// against every other check in this suite, and still share none of its tool
/// names with the reference.
///
/// These tests read the server and tool names out of the installed desktop build
/// and require this port to answer each one or declare it in
/// <c>Deltas/reference-surface-deltas.tsv</c>.
/// </summary>
public sealed class InternalMcpSurfaceParityTests
{
    /// <summary>
    /// The servers a Code session gets, with the tools each carries — read from
    /// <see cref="McpReferenceFixture"/>, the recording of the installed
    /// desktop's own declarations rather than a table retyped from them.
    /// </summary>
    private static readonly Dictionary<string, string[]> ReferenceServers =
        McpReferenceFixture.Servers.ToDictionary(
            static s => s.WireName,
            static s => s.Tools.Select(static t => t.Name).ToArray(),
            StringComparer.Ordinal);

    /// <summary>Every wire name the reference offers a Code session.</summary>
    private static IEnumerable<string> ReferenceWireNames() =>
        ReferenceServers.SelectMany(static entry =>
            entry.Value.Select(tool => $"mcp__{entry.Key}__{tool}"));

    [Fact]
    public void Every_reference_mcp_tool_is_carried_or_declared()
    {
        var declared = SurfaceDeltas.Rows
            .Where(static r => r.Kind is "mcptool" or "mcpserver")
            .ToList();
        var declaredTools = declared
            .Where(static r => r.Kind == "mcptool")
            .Select(static r => r.Name)
            .ToHashSet(StringComparer.Ordinal);
        var declaredServers = declared
            .Where(static r => r.Kind == "mcpserver")
            .Select(static r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        var carried = CarriedWireNames();
        var unaccounted = new List<string>();
        foreach (var name in ReferenceWireNames())
        {
            if (carried.Contains(name) || declaredTools.Contains(name))
            {
                continue;
            }

            // A whole server may be declared instead of each of its tools.
            var server = name["mcp__".Length..];
            server = server[..server.IndexOf("__", StringComparison.Ordinal)];
            if (declaredServers.Contains(server))
            {
                continue;
            }

            unaccounted.Add(name);
        }

        Assert.True(unaccounted.Count == 0, new StringBuilder()
            .AppendLine("The installed desktop offers these in-process MCP tools; this build neither carries")
            .AppendLine("them nor declares them in Deltas/reference-surface-deltas.tsv:")
            .AppendLine(string.Join("\n", unaccounted.Select(static n => "    " + n)))
            .ToString());
    }

    [Fact]
    public void Carried_mcp_tools_are_all_the_reference_s()
    {
        var reference = ReferenceWireNames().ToHashSet(StringComparer.Ordinal);
        var ours = CarriedWireNames()
            .Where(name => !reference.Contains(name))
            .ToList();

        Assert.True(ours.Count == 0, new StringBuilder()
            .AppendLine("These wire names are not the reference's. A tool the reference does not have must not")
            .AppendLine("wear an mcp__ name that implies it does — register it bare, or declare it as an")
            .AppendLine("addition in Deltas/reference-surface-deltas.tsv:")
            .AppendLine(string.Join("\n", ours.Select(static n => "    " + n)))
            .ToString());
    }

    [Fact]
    public void Server_names_are_spelled_the_reference_s_way()
    {
        // The shell's own list is what the deferral predicate and the transcript
        // renderer key off, so a typo there silently defers a whole server behind
        // tool_search or renders its rows as a generic MCP call.
        foreach (var name in InternalMcpServers.ServerNames)
        {
            Assert.True(
                ReferenceServers.ContainsKey(name),
                $"InternalMcpServers.ServerNames carries \"{name}\", which is not a reference server name.");
        }
    }

    /// <summary>
    /// The other direction of the same rule. A server wired into the workspace
    /// but missing from <see cref="InternalMcpServers.ServerNames"/> still
    /// composes its tools, so nothing looks wrong — but the turn's deferral
    /// predicate would hide the whole server behind tool_search, and the
    /// transcript would render its rows as a generic "Used {server}: {tool}".
    /// </summary>
    [Fact]
    public void Every_carried_tool_is_recognised_as_this_shells_own()
    {
        var unrecognised = CarriedWireNames()
            .Where(static name => !InternalMcpServers.IsInternal(name))
            .ToList();

        Assert.True(unrecognised.Count == 0, new StringBuilder()
            .AppendLine("These tools are composed by the shell but their server is missing from")
            .AppendLine("InternalMcpServers.ServerNames, so the turn would defer them behind tool_search:")
            .AppendLine(string.Join(Environment.NewLine, unrecognised.Select(static n => "    " + n)))
            .ToString());
    }

    [Fact]
    public void Legacy_aliases_are_resolvable_but_never_advertised()
    {
        using var bridge = new JarvisCode.App.Services.BrowserBridge();
        var tools = InternalMcpServers.Compose(
            [JarvisBrowserTools.Server(bridge)], new InternalMcpSessionContext());

        var advertised = tools.Select(static t => t.Name).ToList();
        Assert.All(advertised, name =>
            Assert.StartsWith("mcp__claude-in-chrome__", name, StringComparison.Ordinal));

        var registry = new JarvisCode.Core.Tools.ToolRegistry(tools);
        Assert.NotNull(registry.Find("mcp__claude-in-chrome__read_page"));
        Assert.NotNull(registry.Find("browser_read_page"));
        Assert.DoesNotContain("browser_read_page", advertised);

        // The bare current name is not an alias — Claude_Browser has a read_page too.
        Assert.Null(registry.Find("read_page"));
    }

    /// <summary>
    /// The servers whose tools the reference marks <c>alwaysLoad</c> in a
    /// <c>ccd</c> session, so they are never hidden behind tool_search.
    ///
    /// Measured two ways. In the bundle: <c>Claude_Browser</c> gets the flag at
    /// the site that pushes its definition, <c>ccd_session</c> and
    /// <c>terminal</c> on each of their tools, and <c>visualize</c> in
    /// <c>getImagineServerDef</c>; <c>claude-in-chrome</c> gets it only when
    /// <c>le(model, sessionType) = ce(model) &amp;&amp; sessionType !== 'ccd'</c>,
    /// which is never true here. And from a live ccd session, where exactly
    /// those servers' tools arrive loaded and the other six arrive deferred.
    ///
    /// <c>terminal</c> joined the set in 1.44121.2.0: its <c>read_terminal</c>
    /// declares <c>alwaysLoad:!0</c> where the 1.40609.1.0 declaration did not,
    /// and a live session on the new build receives it loaded.
    /// </summary>
    private static readonly HashSet<string> AlwaysLoadServers = new(StringComparer.Ordinal)
    {
        "Claude_Browser",
        "ccd_session",
        "terminal",
        "visualize",

        // The Android emulator's one tool declares alwaysLoad in the bundle. It
        // is absent from the live measurement for the same reason its whole
        // server is: the flag that gates it ships off.
        McpReferenceFixture.AndroidWireName,
    };

    [Fact]
    public void Only_the_reference_s_alwaysLoad_servers_are_exempt_from_deferral()
    {
        var exempt = ComposedTools()
            .Where(JarvisCode.App.Services.InternalMcpServers.IsAlwaysLoad)
            .Select(static t => Server(t.Name))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            exempt.SetEquals(AlwaysLoadServers),
            new StringBuilder()
                .AppendLine("The servers this build exempts from tool_search deferral are not the reference's.")
                .AppendLine($"    reference: {string.Join(", ", AlwaysLoadServers.Order(StringComparer.Ordinal))}")
                .AppendLine($"    this build: {string.Join(", ", exempt.Order(StringComparer.Ordinal))}")
                .ToString());
    }

    [Fact]
    public void Every_other_internal_tool_is_deferrable()
    {
        var stuck = ComposedTools()
            .Where(static t => !JarvisCode.App.Services.InternalMcpServers.IsAlwaysLoad(t))
            .Where(static t => !JarvisCode.App.Services.TurnContextFactory.Deferrable(t))
            .Select(static t => t.Name)
            .ToList();

        Assert.True(stuck.Count == 0, new StringBuilder()
            .AppendLine("These internal tools carry no alwaysLoad and yet the turn would not defer them.")
            .AppendLine("The reference defers every MCP tool that is not alwaysLoad:")
            .AppendLine(string.Join(Environment.NewLine, stuck.Select(static n => "    " + n)))
            .ToString());
    }

    /// <summary>
    /// The deferral mechanism must actually engage on a Code session. This
    /// build gates it on size where the reference gates it on model capability
    /// (declared in the delta manifest), so the gate is only equivalent while
    /// the shell's own servers clear it on their own.
    /// </summary>
    [Fact]
    public void The_shells_own_servers_are_enough_to_engage_deferral()
    {
        var deferrable = ComposedTools()
            .Where(JarvisCode.App.Services.TurnContextFactory.Deferrable)
            .ToList();

        Assert.True(
            JarvisCode.App.Services.TurnContextFactory.WillDefer([.. ComposedTools()], "claude-opus-5"),
            $"The shell composes {deferrable.Count} deferrable internal tools, so a Code turn on a model the " +
            "reference carries tool search for must defer them — otherwise every one would be advertised and " +
            "the reference's alwaysLoad partition would have no observable effect.");
    }

    private static string Server(string wireName)
    {
        JarvisCode.App.Services.InternalMcpServers.TrySplit(wireName, out var server, out _);
        return server;
    }

    /// <summary>The wire names this build actually composes for a Code session.</summary>
    private static HashSet<string> CarriedWireNames() =>
        ComposedTools().Select(static t => t.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every tool a Code session's shell composes, as the shell composes it —
    /// renamed onto the wire and carrying its server's alwaysLoad answer, which
    /// is what the deferral checks above read.
    /// </summary>
    /// <summary>
    /// Where the composed scheduled-tasks server keeps its tasks. create's doc
    /// interpolates it, so the docs check needs the same value the tools saw.
    /// </summary>
    internal static readonly string ScheduledTasksRoot =
        Path.Combine(Path.GetTempPath(), "jarvis-parity-scheduled");

    /// <summary>The same set, for the sibling docs/schema checks.</summary>
    internal static IReadOnlyList<JarvisCode.Core.Tools.ITool> Composed() => ComposedTools();

    private static IReadOnlyList<JarvisCode.Core.Tools.ITool> ComposedTools()
    {
        using var bridge = new JarvisCode.App.Services.BrowserBridge();
        var context = new InternalMcpSessionContext();
        var profile = Path.Combine(Path.GetTempPath(), "jarvis-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            // computer-use is built from a real (throwaway) settings store rather
            // than a hand-written list, so this measures what the app registers.
            var settings = new UiSettingsStore(Path.Combine(profile, "ui-settings.json"));
            settings.Current.ComputerUseEnabled = true;

            // The teach tools exist only when a surface can raise the overlay,
            // which the real workspace always can, so the check measures that set.
            var teach = new TeachController(static () => null, () => settings.Current);

            List<InternalMcpServerDefinition> servers =
            [
                BrowserPaneTools.Server(
                    new NullPaneDriver(),
                    new PreviewServers(
                        new JarvisCode.Core.BackgroundTasks.BackgroundTaskManager(), static _ => { })),
                JarvisBrowserTools.Server(bridge),
                ComputerUseTools.Server(settings, teach),
                CcdSessionTools.Server(new NullSessionHost()),
                CcdSessionMgmtTools.Server(new NullSessionMgmtHost()),
                CcdDirectoryTools.Server(new NullDirectoryHost()),
                TerminalMcpTools.Server(new NullTerminalReader()),
                ConnectorMcpTools.Server(
                    new McpRegistrySearchTool(new HttpClient()),
                    static () => [],
                    static _ => { }),
                ScheduledTaskTools.Server(
                    new ScheduledTaskStore(ScheduledTasksRoot),
                    static () => DateTimeOffset.Now),
                VisualizeTools.Server(),

                // The reference removes this server where adb is absent, and so
                // does this build; the check is about what it advertises, not
                // about whether this machine has an SDK, so adb is faked here.
                AndroidEmulatorTools.Server(
                    new UiSettings { AndroidToolsEnabled = true },
                    panel: null,
                    findAdb: static () => "adb.exe")!,
            ];

            return InternalMcpServers.Compose(servers, context);
        }
        finally
        {
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp profile is not worth failing a parity check over.
            }
        }
    }
}
