using JarvisCode.Cli;

namespace JarvisCode.Cli.Tests;

public sealed class ChromeFlagTests
{
    [Theory]
    [InlineData("--chrome", true)]
    [InlineData("--no-chrome", false)]
    public void Browser_flag_is_a_supported_local_capability(string flag, bool enabled)
    {
        var parsed = CommandLine.Parse([flag], RootOptions.Specs);
        Assert.Null(parsed.Error);
        Assert.Equal(enabled, CliOptions.From(parsed).ChromeEnabled);
        Assert.False(RootOptions.Unsupported.ContainsKey(flag.TrimStart('-')));
    }

    [Fact]
    public void Last_browser_flag_wins_without_enabling_it_by_default()
    {
        Assert.Null(CliOptions.From(CommandLine.Parse([], RootOptions.Specs)).ChromeEnabled);
        Assert.False(CliOptions.From(CommandLine.Parse(["--chrome", "--no-chrome"], RootOptions.Specs)).ChromeEnabled);
        Assert.True(CliOptions.From(CommandLine.Parse(["--no-chrome", "--chrome"], RootOptions.Specs)).ChromeEnabled);
    }
}
