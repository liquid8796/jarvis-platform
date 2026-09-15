using System.Diagnostics;
using System.IO;

namespace JarvisCode.Parity.Tests;

public sealed class ReferenceProcessIsolationTests
{
    [Fact]
    public void Argument_checks_remove_ambient_auth_and_use_owned_workspace_config_and_profile()
    {
        var isolation = new CliProcessIsolation();
        var info = new ProcessStartInfo("unused.exe");
        info.Environment["ANTHROPIC_AUTH_TOKEN"] = "fixture-token";
        info.Environment["AWS_PROFILE"] = "fixture-account";
        info.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = "fixture-token";
        info.Environment["JARVISCODE_PROFILE"] = "existing-user-profile";
        try
        {
            isolation.Configure(info);
            Assert.False(info.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
            Assert.False(info.Environment.ContainsKey("AWS_PROFILE"));
            Assert.False(info.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
            Assert.Equal(isolation.Profile, info.Environment["JARVISCODE_PROFILE"]);
            Assert.StartsWith(isolation.Root + Path.DirectorySeparatorChar, info.WorkingDirectory);
            Assert.StartsWith(isolation.Root + Path.DirectorySeparatorChar, info.Environment["CLAUDE_CONFIG_DIR"]!);
            Directory.CreateDirectory(isolation.ProfileRoot);
            File.WriteAllText(Path.Combine(isolation.ProfileRoot, "fixture.txt"), "owned fixture");
        }
        finally { isolation.Dispose(); }
        Assert.False(Directory.Exists(isolation.Root));
        Assert.False(Directory.Exists(isolation.ProfileRoot));
    }

    [Fact]
    public void Parallel_argument_checks_never_share_profiles_or_workspaces()
    {
        using var first = new CliProcessIsolation();
        using var second = new CliProcessIsolation();
        Assert.NotEqual(first.Root, second.Root);
        Assert.NotEqual(first.Profile, second.Profile);
    }
}
