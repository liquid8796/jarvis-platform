using System.Diagnostics;
using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.Parity.Tests;

[Collection(AppLaunchCollection.Name)]
public sealed class ParityNativeFixtureTests
{
    [Fact]
    public void Native_configuration_uses_private_integration_and_a_fresh_working_directory()
    {
        var workspace = ParityNativeFixture.CreateWorkspace();
        try
        {
            var process = new ProcessStartInfo("unused");
            process.Environment["ANTHROPIC_API_KEY"] = "controlled-test-value";
            process.Environment["JARVIS_APPROVE_BASELINES"] = "1";
            process.Environment["JARVIS_PROFILE"] = "normal";
            ParityNativeFixture.Configure(process, workspace);
            Assert.Equal(workspace, process.WorkingDirectory);
            Assert.Equal("1", process.Environment["JARVIS_PARITY_ISOLATED"]);
            Assert.Equal(Path.Combine(workspace, "ide"), process.Environment["JARVIS_IDE_REGISTRY"]);
            Assert.False(process.Environment.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.False(process.Environment.ContainsKey("JARVIS_APPROVE_BASELINES"));
            Assert.False(process.Environment.ContainsKey("JARVIS_PROFILE"));
            Assert.StartsWith(workspace, process.Environment["CLAUDE_CONFIG_DIR"]);
        }
        finally { ParityNativeFixture.DeleteWorkspace(workspace); }
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void Fixture_cleanup_refuses_an_unowned_directory() =>
        Assert.Throws<InvalidOperationException>(() => ParityNativeFixture.DeleteWorkspace(Path.GetTempPath()));

    [Fact]
    public void Native_verification_does_not_import_the_users_plugins_or_skills()
    {
        var isolated = Environment.GetEnvironmentVariable("JARVIS_PARITY_ISOLATED");
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var workspace = ParityNativeFixture.CreateWorkspace();
        try
        {
            Environment.SetEnvironmentVariable("JARVIS_PARITY_ISOLATED", "1");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", workspace);
            var plugins = ClaudeCodePlugins.Load();
            Assert.Empty(plugins.Installed);
            Assert.Empty(plugins.HookFiles);
            Assert.Empty(plugins.McpFiles);
            Assert.Equal(Path.Combine(workspace, "skills"), SkillCatalog.ClaudeUserSkillsDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JARVIS_PARITY_ISOLATED", isolated);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", config);
            ParityNativeFixture.DeleteWorkspace(workspace);
        }
    }
}
