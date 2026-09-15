using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;

namespace JarvisCode.App.Tests.Services;

public sealed class CustomizationScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-customization-scopes-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Project_and_local_hooks_are_distinct_and_native_file_shadows_compatibility_file()
    {
        Write(".jarvis/settings.json", "{\"hooks\":{}}");
        Write(".claude/settings.json", "{\"hooks\":{}}");
        Write(".claude/settings.local.json", "{\"hooks\":{}}");
        var paths = ProfilePaths.Create("scope-test-" + Guid.NewGuid().ToString("N"));
        var projectOnly = new TurnCustomizations { SettingSources = new HashSet<string> { "project" } };
        var project = projectOnly.AutomaticHookFiles(_root, paths).ToArray();
        Assert.Equal(Path.Combine(_root, ".jarvis", "settings.json"), Assert.Single(project));
        var localOnly = projectOnly with { SettingSources = new HashSet<string> { "local" } };
        Assert.Equal(Path.Combine(_root, ".claude", "settings.local.json"), Assert.Single(localOnly.AutomaticHookFiles(_root, paths)));
        Assert.Empty((projectOnly with { DisableAutomaticDiscovery = true }).AutomaticHookFiles(_root, paths));
    }

    [Fact]
    public void Plugin_styles_are_namespaced_and_workflows_work_without_automatic_discovery()
    {
        Write("styles/review.md", "---\nname: Review\ndescription: Review style\n---\nBe precise.");
        Write("flows/check.js", "export default async function () { return 42; }");
        Write(".jarvis/workflows/ambient.js", "Ambient workflow");
        var scope = new TurnCustomizations
        {
            DisableAutomaticDiscovery = true,
            PluginOutputStylePaths = [("alpha", Path.Combine(_root, "styles"))],
            PluginWorkflowPaths = [("alpha", Path.Combine(_root, "flows"))],
        };
        var style = OutputStyles.Find("alpha:Review", pluginPaths: scope.PluginOutputStylePaths);
        Assert.NotNull(style);
        Assert.Equal("Be precise.", style.Prompt);
        var store = new WorkflowStore(_root, explicitWorkflows: scope.WorkflowDefinitions(), discover: false);
        Assert.Equal("alpha:check", Assert.Single(store.Names()));
        Assert.Equal(Path.Combine(_root, "flows", "check.js"), store.Find("alpha:check"));
        Assert.Null(store.Find("ambient"));
    }

    private void Write(string relative, string text)
    {
        var file = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
