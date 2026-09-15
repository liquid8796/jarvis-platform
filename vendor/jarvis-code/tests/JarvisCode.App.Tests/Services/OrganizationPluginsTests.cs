using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>The four tool-policy values and how they reach the permission gate.</summary>
public sealed class ToolPoliciesTests
{
    private static Dictionary<string, Dictionary<string, string>> Policies(
        params (string Server, string Tool, string Value)[] rows)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (server, tool, value) in rows)
        {
            if (!map.TryGetValue(server, out var tools))
            {
                tools = new Dictionary<string, string>(StringComparer.Ordinal);
                map[server] = tools;
            }
            tools[tool] = value;
        }
        return map;
    }

    [Fact]
    public void TheFourValuesCarryTheReferencesLabels()
    {
        Assert.Equal(["allow", "ask", "ask-session", "blocked"], ToolPolicyValues.All);
        Assert.Equal("Always allow", ToolPolicyValues.Label(ToolPolicyValues.Allow));
        Assert.Equal("Ask each time", ToolPolicyValues.Label(ToolPolicyValues.Ask));
        Assert.Equal("Ask once per session", ToolPolicyValues.Label(ToolPolicyValues.AskSession));
        Assert.Equal("Blocked", ToolPolicyValues.Label(ToolPolicyValues.Blocked));
    }

    [Fact]
    public void AnythingOutsideTheSetReadsAsBlocked()
    {
        Assert.Equal("allow", ToolPolicyValues.Normalize("allow"));
        Assert.Equal("blocked", ToolPolicyValues.Normalize("nonsense"));
        Assert.Equal("blocked", ToolPolicyValues.Normalize(null));
        Assert.Equal("blocked", ToolPolicyValues.Normalize("Allow"));
    }

    [Fact]
    public void AllowAndBlockedBecomeRuleLinesOnTheWireName()
    {
        var lines = ToolPolicies.ToRuleLines(Policies(
            ("linear", "create_issue", ToolPolicyValues.Allow),
            ("linear", "delete_issue", ToolPolicyValues.Blocked),
            ("linear", "search", ToolPolicyValues.Ask),
            ("linear", "list", ToolPolicyValues.AskSession)));

        Assert.Equal(
            ["allow mcp__linear__create_issue", "deny mcp__linear__delete_issue"], lines);
    }

    [Fact]
    public void OnlyAskEachTimeLandsInTheAlwaysAskSet()
    {
        var alwaysAsk = ToolPolicies.AlwaysAskTools(Policies(
            ("linear", "create_issue", ToolPolicyValues.Allow),
            ("linear", "search", ToolPolicyValues.Ask),
            ("notion", "write", ToolPolicyValues.AskSession)));

        Assert.Equal(["mcp__linear__search"], alwaysAsk);
    }

    [Fact]
    public void AServerNameIsNormalizedTheWayTheWireNormalizesIt() =>
        Assert.Equal("mcp__my_server__do_it", ToolPolicies.WireName("my server", "do it"));

    [Fact]
    public void TheDocumentShapeIsTheReferencesAndItRoundTrips()
    {
        var document = JsonNode.Parse(
            """{"mcpServers":{"linear":{"toolPolicy":{"create":"allow","drop":"nope"}}}}""");

        var parsed = ToolPolicies.FromDocument(document);

        Assert.Equal("allow", parsed["linear"]["create"]);
        Assert.Equal("blocked", parsed["linear"]["drop"]);

        var written = ToolPolicies.ToDocument(parsed);
        Assert.Equal("allow", written["mcpServers"]!["linear"]!["toolPolicy"]!["create"]!.GetValue<string>());
    }

    [Fact]
    public void ADocumentWithNoServersReadsAsEmpty()
    {
        Assert.Empty(ToolPolicies.FromDocument(JsonNode.Parse("{}")));
        Assert.Empty(ToolPolicies.FromDocument(null));
    }

    [Fact]
    public void APolicyForAServerNoPluginShipsIsPending()
    {
        var policies = Policies(("linear", "x", "allow"), ("future", "y", "allow"));

        var (detected, pending) = ToolPolicies.Split(["linear", "notion"], policies);

        Assert.Equal(["linear", "notion"], detected);
        Assert.Equal(["future"], pending);
    }
}

/// <summary>The mount-folder scan behind Organization plugins.</summary>
public sealed class OrganizationPluginScannerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jarvis-org-plugins-" + Guid.NewGuid().ToString("N")[..8]);

    public OrganizationPluginScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Plugin(string name, string manifest = """{"name":"x","version":"1.0.0"}""")
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(directory, ".claude-plugin"));
        File.WriteAllText(Path.Combine(directory, ".claude-plugin", "plugin.json"), manifest);
        return directory;
    }

    [Fact]
    public void AnUnsetFolderScansToNothing()
    {
        Assert.Empty(OrganizationPluginScanner.Scan(null).Plugins);
        Assert.Empty(OrganizationPluginScanner.Scan("").Plugins);
    }

    [Fact]
    public void AFolderThatIsNotThereIsAReadFailure()
    {
        var scan = OrganizationPluginScanner.Scan(Path.Combine(_root, "gone"));

        Assert.NotNull(scan.ReadError);
        Assert.StartsWith("Read failed: ", scan.Summary);
    }

    [Fact]
    public void PluginsAreCountedAndNonPluginEntriesSkipped()
    {
        Plugin("alpha");
        Plugin("beta");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-plugin"));

        var scan = OrganizationPluginScanner.Scan(_root);

        Assert.Equal(["alpha", "beta"], scan.Plugins.Select(p => p.Name));
        Assert.Equal(1, scan.SkippedEntries);
        Assert.Equal("2 plugins found", scan.Summary);
        Assert.Equal("(1 non-plugin entry in folder skipped)", scan.SkippedNote);
    }

    [Fact]
    public void AFolderOfNonPluginsSaysSoWithItsCount()
    {
        Directory.CreateDirectory(Path.Combine(_root, "junk"));
        Directory.CreateDirectory(Path.Combine(_root, "more-junk"));

        var scan = OrganizationPluginScanner.Scan(_root);

        Assert.Equal("No plugins found (2 non-plugin entries)", scan.Summary);
        Assert.Null(scan.SkippedNote);
    }

    [Fact]
    public void AnEmptyFolderSaysNoOrganizationPluginsFound() =>
        Assert.Equal("No organization plugins found", OrganizationPluginScanner.Scan(_root).Summary);

    [Fact]
    public void AManifestThatWillNotParseIsAnError()
    {
        Plugin("broken", "{ not json");

        var entry = Assert.Single(OrganizationPluginScanner.Scan(_root).Plugins);

        Assert.Equal("error", entry.Health);
        Assert.StartsWith("Manifest invalid: ", OrganizationPluginScanner.ManifestLine(entry));
    }

    [Fact]
    public void AMissingManifestIsPrintedAsNoPluginJson()
    {
        var directory = Path.Combine(_root, "skills-only");
        Directory.CreateDirectory(Path.Combine(directory, "skills", "ship"));
        File.WriteAllText(
            Path.Combine(directory, "skills", "ship", "SKILL.md"),
            "---\nname: ship\ndescription: Ships\n---\n\nBody");

        var entry = Assert.Single(OrganizationPluginScanner.Scan(_root).Plugins);

        Assert.Equal("No plugin.json", OrganizationPluginScanner.ManifestLine(entry));
        Assert.Equal(1, entry.SkillCount);
    }

    [Fact]
    public void TheMcpServersAPluginShipsAreWhatThePolicyIsKeyedOn()
    {
        var directory = Plugin("connected");
        File.WriteAllText(
            Path.Combine(directory, ".mcp.json"),
            """{"mcpServers":{"linear":{"url":"https://mcp.linear.app/mcp"}}}""");

        var entry = Assert.Single(OrganizationPluginScanner.Scan(_root).Plugins);

        Assert.Equal(["linear"], entry.McpServerNames);
    }

    [Fact]
    public void LastSyncedIsTheNarrowRelativeTimeTheReferenceFormats()
    {
        var now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");

        Assert.Equal("never", OrganizationPluginScanner.LastSynced(null, now));
        Assert.Equal("now", OrganizationPluginScanner.LastSynced(now.AddSeconds(-10), now));
        Assert.Equal("5m ago", OrganizationPluginScanner.LastSynced(now.AddMinutes(-5), now));
        Assert.Equal("3h ago", OrganizationPluginScanner.LastSynced(now.AddHours(-3), now));
        Assert.Equal("yesterday", OrganizationPluginScanner.LastSynced(now.AddDays(-1), now));
        Assert.Equal("9d ago", OrganizationPluginScanner.LastSynced(now.AddDays(-9), now));
    }
}
