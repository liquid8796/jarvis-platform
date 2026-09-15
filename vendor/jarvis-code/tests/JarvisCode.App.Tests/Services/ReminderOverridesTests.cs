using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Settings;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The three states Settings › General offers for reminder-text overrides: off,
/// the reference's own environment tier, and that tier plus a local stand-in for
/// the per-account <c>client_data</c> map its server would send.
/// </summary>
public sealed class ReminderOverridesTests
{
    private static ToolResultReminders.Options Resolved(string? batching, string? secondary) =>
        new(SystemTurnModel: true, ModelOwnsText: false)
        {
            BatchingText = batching,
            SecondaryText = secondary,
        };

    private const string File = """
        {
          "tengu_toasty_thimble": { "claude-opus-*": "from the file", "*": "fallback" },
          "tengu_gentle_parasol": { "claude-opus-5": "secondary from the file" }
        }
        """;

    [Fact]
    public void Off_drops_every_override_and_leaves_the_model_its_own_text()
    {
        var settings = new AppSettings { ReminderOverridesEnabled = false };
        var applied = ReminderOverrides.Apply(
            Resolved("from the environment", "secondary"), settings, "claude-opus-5", () => File);

        Assert.Null(applied.BatchingText);
        Assert.Null(applied.SecondaryText);
    }

    /// <summary>
    /// Off still lets a model that owns text keep it: the reference's own
    /// <c>modelOwn</c> tier is the model's, not an override.
    /// </summary>
    [Fact]
    public void Off_keeps_the_text_a_fable_5_1_model_owns()
    {
        var settings = new AppSettings { ReminderOverridesEnabled = false };
        var options = new ToolResultReminders.Options(SystemTurnModel: true, ModelOwnsText: true)
        {
            BatchingText = "from the environment",
        };

        var applied = ReminderOverrides.Apply(options, settings, "claude-fable-5-1", () => File);
        Assert.Equal(ToolResultReminders.BatchingText, applied.BatchingText);
    }

    [Fact]
    public void The_environment_source_carries_the_environment_and_nothing_else()
    {
        var settings = new AppSettings
        {
            ReminderOverridesEnabled = true,
            ReminderOverrideSource = ReminderOverrides.EnvironmentSource,
        };

        var applied = ReminderOverrides.Apply(
            Resolved("from the environment", null), settings, "claude-opus-5", () => File);

        Assert.Equal("from the environment", applied.BatchingText);
        // The file is not read on this source, so its secondary entry stays out.
        Assert.Null(applied.SecondaryText);
    }

    [Fact]
    public void The_file_source_answers_where_the_environment_said_nothing()
    {
        var settings = new AppSettings
        {
            ReminderOverridesEnabled = true,
            ReminderOverrideSource = ReminderOverrides.FileSource,
        };

        var applied = ReminderOverrides.Apply(
            Resolved(null, null), settings, "claude-opus-5", () => File);

        Assert.Equal("from the file", applied.BatchingText);
        Assert.Equal("secondary from the file", applied.SecondaryText);
    }

    /// <summary>The environment stays the first tier, as it is in the reference.</summary>
    [Fact]
    public void The_environment_still_wins_over_the_file()
    {
        var settings = new AppSettings
        {
            ReminderOverridesEnabled = true,
            ReminderOverrideSource = ReminderOverrides.FileSource,
        };

        var applied = ReminderOverrides.Apply(
            Resolved("from the environment", null), settings, "claude-opus-5", () => File);

        Assert.Equal("from the environment", applied.BatchingText);
        Assert.Equal("secondary from the file", applied.SecondaryText);
    }

    [Fact]
    public void A_model_the_file_matches_only_by_star_gets_the_star_entry()
    {
        var settings = new AppSettings
        {
            ReminderOverridesEnabled = true,
            ReminderOverrideSource = ReminderOverrides.FileSource,
        };

        var applied = ReminderOverrides.Apply(
            Resolved(null, null), settings, "claude-sonnet-5", () => File);

        Assert.Equal("fallback", applied.BatchingText);
        // The secondary map names one model, and this is not it.
        Assert.Null(applied.SecondaryText);
    }

    [Fact]
    public void Blank_text_in_the_file_switches_that_reminder_off()
    {
        var settings = new AppSettings
        {
            ReminderOverridesEnabled = true,
            ReminderOverrideSource = ReminderOverrides.FileSource,
        };

        var applied = ReminderOverrides.Apply(
            Resolved(null, null), settings, "claude-opus-5",
            () => """{ "tengu_toasty_thimble": { "*": "   " } }""");

        Assert.Null(applied.BatchingText);
    }

    /// <summary>
    /// A malformed file is not something a turn should fail on: the reference
    /// warns and carries on when its own value is the wrong shape.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{ "tengu_toasty_thimble": "a string, not a map" }""")]
    public void A_malformed_file_leaves_the_environment_standing(string contents)
    {
        var settings = new AppSettings
        {
            ReminderOverridesEnabled = true,
            ReminderOverrideSource = ReminderOverrides.FileSource,
        };

        var applied = ReminderOverrides.Apply(
            Resolved("from the environment", null), settings, "claude-opus-5", () => contents);

        Assert.Equal("from the environment", applied.BatchingText);
        Assert.Null(applied.SecondaryText);
    }

    /// <summary>The starter file parses, so opening it and saving it changes nothing.</summary>
    [Fact]
    public void The_starter_file_parses_and_holds_no_text()
    {
        var map = ReminderOverrides.Load(() => ReminderOverrides.StarterFile);
        Assert.Equal(2, map.Count);
        Assert.Empty(map[ReminderOverrides.BatchingKey]);
        Assert.Empty(map[ReminderOverrides.SecondaryKey]);
    }

    [Fact]
    public void The_defaults_are_the_reference_behaviour()
    {
        var settings = new AppSettings();
        Assert.True(settings.ReminderOverridesEnabled);
        Assert.False(ReminderOverrides.UsesFile(settings));
    }
}
