using JarvisCode.App.Services;
using JarvisCode.Core.Routines;

namespace JarvisCode.App.Tests.Services;

public sealed class RoutineApprovalsTests
{
    private static string K(string tool, string? content = null) => RoutineApprovals.Key(tool, content);

    [Fact]
    public void KeyIsToolNameNulRuleContent()
    {
        Assert.Equal("Bash\0git status", RoutineApprovals.Key("Bash", "git status"));
        Assert.Equal("Foo\0", RoutineApprovals.Key("Foo", null));
    }

    [Fact]
    public void KeyHalvesRoundTrip()
    {
        Assert.Equal("Bash", RoutineApprovals.ToolNameOf(K("Bash", "git status")));
        Assert.Equal("git status", RoutineApprovals.RuleContentOf(K("Bash", "git status")));
        Assert.Equal("Foo", RoutineApprovals.ToolNameOf(K("Foo")));
        Assert.Null(RoutineApprovals.RuleContentOf(K("Foo")));
    }

    [Fact]
    public void ABareApprovalOfABroadToolIsNeverStored()
    {
        Assert.True(RoutineApprovals.IsTooBroad("Bash", null));
        Assert.False(RoutineApprovals.IsTooBroad("Bash", "git status"));
        Assert.False(RoutineApprovals.IsTooBroad("SomeMcpTool", null));

        var stored = RoutineApprovals.Add([], [("Bash", null), ("Bash", "git status")]);
        Assert.Equal([K("Bash", "git status")], stored);
    }

    [Fact]
    public void AddDeduplicatesAndKeepsOrder()
    {
        var stored = RoutineApprovals.Add([K("A")], [("B", null), ("A", null), ("C", "x")]);
        Assert.Equal([K("A"), K("B"), K("C", "x")], stored);
    }

    [Fact]
    public void RemoveWithoutContentTakesEveryRuleOfTheTool()
    {
        IReadOnlyList<string> stored = [K("A", "x"), K("A", "y"), K("B", "z")];
        Assert.Equal([K("B", "z")], RoutineApprovals.Remove(stored, "A", null));
    }

    [Fact]
    public void RemoveWithContentTakesOnlyThatRule()
    {
        IReadOnlyList<string> stored = [K("A", "x"), K("A", "y")];
        Assert.Equal([K("A", "y")], RoutineApprovals.Remove(stored, "A", "x"));
    }

    [Fact]
    public void NothingRequestedIsNeverAutoApproved()
    {
        Assert.False(RoutineApprovals.ShouldAutoApprove([K("A", "x")], "A", []));
    }

    [Fact]
    public void EveryRequestedRuleMustBeStored()
    {
        IReadOnlyList<string> stored = [K("A", "x")];
        Assert.True(RoutineApprovals.ShouldAutoApprove(stored, "A", [("A", "x")]));
        Assert.False(RoutineApprovals.ShouldAutoApprove(stored, "A", [("A", "x"), ("A", "y")]));
    }

    [Fact]
    public void ABareStoredApprovalCoversItsToolsRules()
    {
        IReadOnlyList<string> stored = [K("SomeMcpTool")];
        Assert.True(RoutineApprovals.ShouldAutoApprove(stored, "SomeMcpTool", [("SomeMcpTool", "anything")]));
    }

    [Fact]
    public void ABareStoredApprovalOfABroadToolIsIgnored()
    {
        IReadOnlyList<string> stored = [K("Bash")];
        Assert.False(RoutineApprovals.ShouldAutoApprove(stored, "Bash", [("Bash", "rm -rf /")]));
    }

    [Fact]
    public void SentinelsAndTheLiveOnlyToolsAlwaysAsk()
    {
        IReadOnlyList<string> stored = [K("browser:navigate", "x"), K("A", "x")];
        Assert.False(RoutineApprovals.ShouldAutoApprove(stored, "browser:navigate", [("browser:navigate", "x")]));
        Assert.False(RoutineApprovals.ShouldAutoApprove(stored, "A", [("browser:navigate", "x")]));
        Assert.False(RoutineApprovals.ShouldAutoApprove(
            stored, RoutineApprovals.DirectoryMountTool, [("A", "x")]));
        Assert.False(RoutineApprovals.ShouldAutoApprove(
            stored, "mcp__scheduled-tasks__create_scheduled_task", [("A", "x")]));
    }

    [Fact]
    public void RowCopyIsTheReferences()
    {
        Assert.Equal("Always allowed", RoutineApprovals.SectionHeading);
        Assert.Equal("Approvals you grant during a run appear here.", RoutineApprovals.Empty);
        Assert.Equal("Browser", RoutineApprovals.BrowserRow);
        Assert.Equal("All websites", RoutineApprovals.AllWebsites);
        Assert.Equal("Remove approval", RoutineApprovals.RemoveApproval);
        Assert.Equal("1 website", RoutineApprovals.WebsiteCount(1));
        Assert.Equal("3 websites", RoutineApprovals.WebsiteCount(3));
    }

    [Fact]
    public void BrowserRowSaysAllWebsitesOrCountsThem()
    {
        Assert.Equal(
            "All websites",
            RoutineApprovals.BrowserValue(ChromePermissionModes.SkipAllPermissionChecks, ["a", "b"]));
        Assert.Equal(
            "2 websites", RoutineApprovals.BrowserValue(ChromePermissionModes.FollowAPlan, ["a", "b"]));
    }

    [Fact]
    public void ToolLabelParenthesisesTheRule()
    {
        Assert.Equal("Bash (git status)", RoutineApprovals.ToolLabel("Bash", "git status"));
        Assert.Equal("Bash", RoutineApprovals.ToolLabel("Bash", null));
    }

    [Fact]
    public void RuleLinesAreWhatTheGateSpeaks()
    {
        Assert.Equal(
            new[] { "Bash (git status)", "Foo" },
            RoutineApprovals.RuleLines([K("Bash", "git status"), K("Foo")]));
    }

    [Fact]
    public void UpdateChromeIsANoOpWhenNothingChanged()
    {
        var routine = new Routine
        {
            ChromePermissionMode = ChromePermissionModes.FollowAPlan,
            ChromeAllowedDomains = ["https://a"],
        };
        Assert.False(RoutineApprovals.UpdateChrome(
            routine, ChromePermissionModes.FollowAPlan, ["https://a"]));
        Assert.True(RoutineApprovals.UpdateChrome(
            routine, ChromePermissionModes.FollowAPlan, ["https://a", "https://b"]));
        Assert.Equal(["https://a", "https://b"], routine.ChromeAllowedDomains);
    }

    [Fact]
    public void AnEmptyDomainListClearsTheStoredOnes()
    {
        var routine = new Routine
        {
            ChromePermissionMode = ChromePermissionModes.FollowAPlan,
            ChromeAllowedDomains = ["https://a"],
        };
        Assert.True(RoutineApprovals.UpdateChrome(
            routine, ChromePermissionModes.SkipAllPermissionChecks, []));
        Assert.Empty(routine.ChromeAllowedDomains);
    }

    [Fact]
    public void EveryWebsiteAdmitsAnyOrigin()
    {
        var routine = new Routine { ChromePermissionMode = ChromePermissionModes.SkipAllPermissionChecks };
        Assert.True(RoutineApprovals.AllowsOrigin(routine, "https://example.com"));

        routine.ChromePermissionMode = ChromePermissionModes.FollowAPlan;
        Assert.False(RoutineApprovals.AllowsOrigin(routine, "https://example.com"));
        routine.ChromeAllowedDomains = ["https://example.com"];
        Assert.True(RoutineApprovals.AllowsOrigin(routine, "https://EXAMPLE.com"));
    }
}
