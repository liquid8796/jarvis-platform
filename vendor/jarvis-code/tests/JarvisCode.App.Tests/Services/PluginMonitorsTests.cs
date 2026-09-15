using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class PluginMonitorsTests
{
    [Fact]
    public void CopyIsTheReferences()
    {
        Assert.Equal("Monitors", PluginMonitors.SectionHeading);
        Assert.Equal("No command is declared for this monitor.", PluginMonitors.NoCommand);
        Assert.Equal("Runs", PluginMonitors.RunsLabel);
        Assert.Equal("Shell command", PluginMonitors.ShellCommandLabel);
    }

    [Fact]
    public void AMonitorParsesItsFourFields()
    {
        const string json = """
        [{"name":"ci","command":"watch ci","description":"Watches CI","when":"always"}]
        """;
        var monitor = Assert.Single(PluginMonitors.Parse("acme", json));
        Assert.Equal("acme", monitor.PluginName);
        Assert.Equal("ci", monitor.Name);
        Assert.Equal("watch ci", monitor.Command);
        Assert.Equal("Watches CI", monitor.Description);
        Assert.Equal("always", monitor.When);
        Assert.Null(monitor.SkillName);
        Assert.Equal("acme:ci", monitor.Key);
    }

    [Fact]
    public void WhenDefaultsToAlways()
    {
        var monitor = Assert.Single(PluginMonitors.Parse("p", """[{"name":"a","command":"c"}]"""));
        Assert.Equal("always", monitor.When);
    }

    [Fact]
    public void AnUnknownTriggerFallsBackToAlways()
    {
        var monitor = Assert.Single(
            PluginMonitors.Parse("p", """[{"name":"a","command":"c","when":"on-tuesday"}]"""));
        Assert.Equal("always", monitor.When);
    }

    [Fact]
    public void OnSkillInvokeNamesItsSkill()
    {
        var monitor = Assert.Single(
            PluginMonitors.Parse("p", """[{"name":"a","command":"c","when":"on-skill-invoke:deploy"}]"""));
        Assert.Equal("deploy", monitor.SkillName);
        Assert.Equal("on-skill-invoke:deploy", PluginMonitors.RunsValue(monitor));
    }

    [Fact]
    public void TheTriggerMustNameASkill()
    {
        Assert.True(PluginMonitors.IsValidWhen("always"));
        Assert.True(PluginMonitors.IsValidWhen("on-skill-invoke:x"));
        Assert.False(PluginMonitors.IsValidWhen("on-skill-invoke:"));
        Assert.False(PluginMonitors.IsValidWhen(""));
    }

    [Fact]
    public void ARowWithoutANameOrACommandIsDropped()
    {
        Assert.Empty(PluginMonitors.Parse("p", """[{"name":"a"},{"command":"c"},{}]"""));
    }

    [Fact]
    public void ANameMayAppearOnlyOncePerPlugin()
    {
        var monitors = PluginMonitors.Parse(
            "p", """[{"name":"a","command":"first"},{"name":"a","command":"second"}]""");
        var monitor = Assert.Single(monitors);
        Assert.Equal("first", monitor.Command);
    }

    [Fact]
    public void MalformedJsonIsNoMonitors()
    {
        Assert.Empty(PluginMonitors.Parse("p", "not json"));
        Assert.Empty(PluginMonitors.Parse("p", """{"monitors":[]}"""));
    }

    [Fact]
    public void TheCommandsPlaceholdersExpand()
    {
        var expanded = PluginMonitors.Expand(
            "cd \"${CLAUDE_PLUGIN_ROOT}\" && node ${CLAUDE_PLUGIN_DATA}/x.js ${CLAUDE_PROJECT_DIR} ${TOKEN}",
            "/root", "/data", "/project", name => name == "TOKEN" ? "abc" : null);
        Assert.Equal("cd \"/root\" && node /data/x.js /project abc", expanded);
    }

    [Fact]
    public void AnUnsetVariableExpandsToNothing()
    {
        Assert.Equal("a  b", PluginMonitors.Expand("a ${NOPE} b", "", "", "", _ => null));
    }

    [Fact]
    public void AlwaysMonitorsArmAtSessionStart()
    {
        var always = new PluginMonitor("p", "a", "c", "", "always");
        var onSkill = new PluginMonitor("p", "b", "c", "", "on-skill-invoke:deploy");
        (PluginMonitor, string)[] discovered = [(always, "/p"), (onSkill, "/p")];

        Assert.Equal(
            ["a"], PluginMonitors.ArmedBy(discovered, null).Select(e => e.Monitor.Name));
        Assert.Equal(
            ["b"], PluginMonitors.ArmedBy(discovered, "deploy").Select(e => e.Monitor.Name));
        Assert.Empty(PluginMonitors.ArmedBy(discovered, "other"));
    }

    [Fact]
    public void TheDefaultDeclarationFileIsTheReferences()
    {
        Assert.Equal(
            System.IO.Path.Combine("monitors", "monitors.json"), PluginMonitors.DefaultRelativePath);
    }
}
