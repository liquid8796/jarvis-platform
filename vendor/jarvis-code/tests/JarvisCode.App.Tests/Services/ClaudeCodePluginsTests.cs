using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// Reading Claude Code's own installed plugins, whose layout files a plugin under
/// <c>cache/{marketplace}/{plugin}/{version}</c> rather than under a folder named
/// after it.
/// </summary>
public class ClaudeCodePluginsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "jarvis-ccplugins-" + Guid.NewGuid().ToString("N"));

    private string InstalledFile => Path.Combine(_root, "installed_plugins.json");
    private string SettingsFile => Path.Combine(_root, "settings.json");

    public ClaudeCodePluginsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not go is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    private string PluginDirectory(string name)
    {
        var path = Path.Combine(_root, "cache", name, "1.0.0");
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteInstalled(params (string Key, string Path)[] entries)
    {
        var rows = entries.Select(e =>
            $"\"{e.Key}\": [{{\"scope\": \"user\", \"installPath\": {System.Text.Json.JsonSerializer.Serialize(e.Path)}}}]");
        File.WriteAllText(InstalledFile, $"{{\"version\": 2, \"plugins\": {{{string.Join(",", rows)}}}}}");
    }

    private IReadOnlyList<(string Name, string Directory)> Installed() =>
        ClaudeCodePlugins.Installed(InstalledFile, SettingsFile);

    [Fact]
    public void NothingIsReadWhenClaudeCodeHasNoManifest() => Assert.Empty(Installed());

    [Fact]
    public void PluginNameIsThePartBeforeTheMarketplace()
    {
        var directory = PluginDirectory("watch");
        WriteInstalled(("watch@claude-video", directory));

        var installed = Installed();

        Assert.Equal(("watch", directory), Assert.Single(installed));
    }

    [Fact]
    public void PluginDisabledInClaudeCodeIsNotRead()
    {
        WriteInstalled(("watch@claude-video", PluginDirectory("watch")));
        File.WriteAllText(SettingsFile, "{\"enabledPlugins\": {\"watch@claude-video\": false}}");

        Assert.Empty(Installed());
    }

    [Fact]
    public void PluginMissingFromEnabledMapIsStillRead()
    {
        // A freshly installed plugin is on until it is switched off, as it is there.
        WriteInstalled(("watch@claude-video", PluginDirectory("watch")));
        File.WriteAllText(SettingsFile, "{\"enabledPlugins\": {\"other@somewhere\": true}}");

        Assert.Single(Installed());
    }

    [Fact]
    public void InstallPathThatIsGoneIsSkipped()
    {
        WriteInstalled(("watch@claude-video", Path.Combine(_root, "cache", "gone", "1.0.0")));

        Assert.Empty(Installed());
    }

    [Fact]
    public void UnreadableManifestIsNotAnError()
    {
        File.WriteAllText(InstalledFile, "{ this is not json");

        Assert.Empty(Installed());
    }

    [Fact]
    public void SkillsArriveUnderThePluginsOwnName()
    {
        var directory = PluginDirectory("watch");
        var skill = Path.Combine(directory, "skills", "watch");
        Directory.CreateDirectory(skill);
        File.WriteAllText(
            Path.Combine(skill, "SKILL.md"),
            "---\nname: watch\ndescription: Watch a video.\n---\n\nBody.\n");
        WriteInstalled(("watch@claude-video", directory));

        var content = JarvisCode.Core.Customization.Plugins.LoadDirectories(
            Installed(), ClaudeCodePlugins.Scope, _root, include: null);

        Assert.Equal("watch", Assert.Single(content.Installed).Name);
        Assert.Equal(ClaudeCodePlugins.Scope, content.Installed[0].Scope);
        Assert.Contains(content.Skills, s => s.Name == "watch:watch");
    }

    [Fact]
    public void HooksTravelWithThePlugin()
    {
        var directory = PluginDirectory("watch");
        File.WriteAllText(Path.Combine(directory, "hooks.json"), "{}");
        WriteInstalled(("watch@claude-video", directory));

        var content = JarvisCode.Core.Customization.Plugins.LoadDirectories(
            Installed(), ClaudeCodePlugins.Scope, _root, include: null);

        Assert.Single(content.HookFiles);
    }

    [Fact]
    public void NonStringInstallPathIsSkippedRatherThanThrowing()
    {
        File.WriteAllText(
            InstalledFile,
            "{\"version\": 2, \"plugins\": {\"watch@claude-video\": [{\"installPath\": 7}]}}");

        Assert.Empty(Installed());
    }

    [Fact]
    public void KeyWithNoNameBeforeTheMarketplaceIsSkipped()
    {
        WriteInstalled(("@claude-video", PluginDirectory("watch")));

        Assert.Empty(Installed());
    }
}
