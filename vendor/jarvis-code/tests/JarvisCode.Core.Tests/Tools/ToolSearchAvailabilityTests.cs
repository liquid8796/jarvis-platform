using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Tests.Tools;

/// <summary>
/// The reference's tool-search gate (CLI 2.1.257's eJe / sW / y_). Measured by
/// reading the module and confirmed on the wire: a print run with
/// ENABLE_TOOL_SEARCH=1 advertises ToolSearch and drops every deferrable
/// built-in, where the same run without it advertises all 27.
/// </summary>
public sealed class ToolSearchAvailabilityTests
{
    [Fact]
    public void The_default_mode_is_tool_search()
    {
        Assert.Equal(ToolSearchAvailability.Mode.ToolSearch, ToolSearchAvailability.ResolveMode(null));
        Assert.Equal(ToolSearchAvailability.Mode.ToolSearch, ToolSearchAvailability.ResolveMode(""));
    }

    [Theory]
    [InlineData("1", ToolSearchAvailability.Mode.ToolSearch)]
    [InlineData("true", ToolSearchAvailability.Mode.ToolSearch)]
    [InlineData("0", ToolSearchAvailability.Mode.Standard)]
    [InlineData("false", ToolSearchAvailability.Mode.Standard)]
    [InlineData("auto", ToolSearchAvailability.Mode.ToolSearchAuto)]
    [InlineData("auto:50", ToolSearchAvailability.Mode.ToolSearchAuto)]
    [InlineData("auto:0", ToolSearchAvailability.Mode.ToolSearch)]
    [InlineData("auto:100", ToolSearchAvailability.Mode.Standard)]
    public void The_environment_override_resolves_the_way_the_reference_resolves_it(
        string value, ToolSearchAvailability.Mode expected) =>
        Assert.Equal(expected, ToolSearchAvailability.ResolveMode(value));

    [Fact]
    public void Only_auto_colon_n_carries_a_percentage()
    {
        // Its AEn returns null for anything that is not auto:N — a bare integer
        // is not a percentage, which is why "100" reads as truthy, not standard.
        Assert.Null(ToolSearchAvailability.ParseAutoPercent("100"));
        Assert.Null(ToolSearchAvailability.ParseAutoPercent("auto"));
        Assert.Equal(100, ToolSearchAvailability.ParseAutoPercent("auto:250"));
        Assert.Equal(0, ToolSearchAvailability.ParseAutoPercent("auto:-5"));
        Assert.Null(ToolSearchAvailability.ParseAutoPercent("auto:x"));
    }

    [Theory]
    [InlineData("claude-3-5-haiku-20241022", false)]
    [InlineData("claude-3-haiku-20240307", false)]
    [InlineData("CLAUDE-3-HAIKU", false)]
    [InlineData("us.anthropic.claude-3-5-haiku-20241022-v1:0", false)]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-haiku-4-5-20251001", true)]
    [InlineData("claude-sonnet-5", true)]
    [InlineData(null, true)]
    public void The_two_haiku_three_spellings_are_the_only_unsupported_models(string? model, bool supported) =>
        Assert.Equal(supported, ToolSearchAvailability.ModelSupports(model));

    [Fact]
    public void Standard_mode_and_an_unsupported_model_each_turn_it_off()
    {
        Assert.True(ToolSearchAvailability.IsEnabled("claude-opus-5", null));
        Assert.False(ToolSearchAvailability.IsEnabled("claude-opus-5", "false"));
        Assert.False(ToolSearchAvailability.IsEnabled("claude-3-5-haiku-20241022", null));
    }
}
