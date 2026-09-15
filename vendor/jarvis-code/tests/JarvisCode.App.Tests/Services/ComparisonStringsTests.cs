using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ComparisonStringsTests
{
    [Fact]
    public void The_blind_labels_are_the_two_sides_upper_cased()
    {
        Assert.Equal("Model A", ComparisonStrings.BlindLabel(0));
        Assert.Equal("Model B", ComparisonStrings.BlindLabel(1));
    }

    [Fact]
    public void A_panel_label_names_the_effort_only_when_there_is_one()
    {
        Assert.Equal("Claude Opus 5", ComparisonStrings.PanelLabel("Claude Opus 5", null));
        Assert.Equal("Claude Opus 5", ComparisonStrings.PanelLabel("Claude Opus 5", ""));
        Assert.Equal("Claude Opus 5 High", ComparisonStrings.PanelLabel("Claude Opus 5", "High"));
    }

    [Fact]
    public void Two_panels_on_one_model_are_numbered_apart()
    {
        Assert.Equal(["Opus #1", "Opus #2"], ComparisonStrings.Disambiguate(["Opus", "Opus"]));
        Assert.Equal(["Opus", "Sonnet"], ComparisonStrings.Disambiguate(["Opus", "Sonnet"]));
    }

    [Fact]
    public void The_counter_says_vote_once_and_votes_after_that()
    {
        Assert.Equal("1 vote this session", ComparisonStrings.VotesThisSession(1));
        Assert.Equal("2 votes this session", ComparisonStrings.VotesThisSession(2));
        Assert.Equal("0 votes this session", ComparisonStrings.VotesThisSession(0));
    }

    [Fact]
    public void The_reason_question_names_the_winner_or_asks_about_the_tie()
    {
        Assert.Equal("Why Model B?", ComparisonStrings.ReasonQuestion("Model B"));
        Assert.Equal("What made it a tie?", ComparisonStrings.ReasonQuestion(null));
    }

    [Fact]
    public void The_recorded_line_lower_cases_the_reasons_behind_a_middle_dot()
    {
        Assert.Equal("Preference recorded — Model A", ComparisonStrings.PreferenceRecorded("Model A", null));
        Assert.Equal("Preference recorded — Model A", ComparisonStrings.PreferenceRecorded("Model A", []));
        Assert.Equal(
            "Preference recorded — tie · both good, too similar to tell",
            ComparisonStrings.PreferenceRecorded("tie", ["Both good", "Too similar to tell"]));
    }

    [Fact]
    public void An_arms_conversation_is_named_after_its_panel_and_the_prompt()
    {
        Assert.Equal(
            "Compare 1 (Claude Opus 5): why is the sky blue?",
            ComparisonStrings.SessionName(0, "Claude Opus 5", "why is the sky blue?", blind: false));

        // Blind leaves the model out: a title in the chat list would give it away.
        Assert.Equal(
            "Compare 2: why is the sky blue?",
            ComparisonStrings.SessionName(1, "Claude Opus 5", "why is the sky blue?", blind: true));
    }

    [Fact]
    public void A_long_prompt_is_cut_at_the_references_forty_eight_characters()
    {
        var prompt = new string('x', 100);
        var name = ComparisonStrings.SessionName(0, "M", prompt, blind: true);
        Assert.Equal("Compare 1: " + new string('x', 48), name);
    }

    [Fact]
    public void The_reason_chips_are_the_references_two_default_lists()
    {
        Assert.Equal(
            ["More accurate", "Better reasoning", "Clearer writing", "Better formatting",
             "Followed instructions", "Better tone", "Other"],
            ComparisonStrings.WinReasons);
        Assert.Equal(
            ["Both good", "Both wrong", "Too similar to tell", "Not enough signal", "Other"],
            ComparisonStrings.TieReasons);
    }

    [Fact]
    public void A_panels_settings_button_is_named_by_its_position()
    {
        Assert.Equal("Panel 1 settings", ComparisonStrings.PanelSettingsLabel(0));
        Assert.Equal("Panel 2 settings", ComparisonStrings.PanelSettingsLabel(1));
    }
}
