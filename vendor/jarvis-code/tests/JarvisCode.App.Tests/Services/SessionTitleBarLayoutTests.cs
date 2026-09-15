using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The session titlebar's fitting rules — the reference's <c>Th</c>, <c>Eh</c> and
/// <c>Dh</c>. What matters here is not that the bar folds things away but that it folds
/// them away at the reference's own thresholds and refuses to change its mind inside the
/// hysteresis band, which is the whole reason those constants exist.
/// </summary>
public sealed class SessionTitleBarLayoutTests
{
    // ---- Th: how much room is spare ----

    [Fact]
    public void Slack_is_the_free_space_once_the_title_has_what_it_wants()
    {
        // 40 spare, the title holding 60 of the 90 it wants: giving it the rest costs 30.
        Assert.Equal(10, SessionTitleBarLayout.Slack(freePx: 40, titleClientPx: 60, titleNaturalPx: 90));
    }

    [Fact]
    public void A_title_past_the_cap_is_only_charged_up_to_it()
    {
        // The reference's `bh`: one enormous title must not be able to fold the whole rail,
        // so anything past 100px of natural width is not counted against the fit.
        Assert.Equal(
            SessionTitleBarLayout.Slack(20, 50, SessionTitleBarLayout.TitleNaturalCap),
            SessionTitleBarLayout.Slack(20, 50, 4000));
    }

    // ---- Eh: how the hidden-toggle count moves ----

    [Fact]
    public void Negative_slack_folds_away_as_many_toggles_as_it_takes()
    {
        Assert.Equal(2, SessionTitleBarLayout.HiddenDelta(slack: -35, toggleWidth: 30, hidden: 0));
    }

    [Fact]
    public void Folding_never_passes_the_reference_cap_of_five()
    {
        Assert.Equal(5, SessionTitleBarLayout.HiddenDelta(-1000, 30, 0));
        Assert.Equal(1, SessionTitleBarLayout.HiddenDelta(-1000, 30, hidden: 4));
        Assert.Equal(0, SessionTitleBarLayout.HiddenDelta(-1000, 30, hidden: 5));
    }

    [Fact]
    public void One_pixel_of_squeeze_is_not_enough_to_fold_anything()
    {
        // Its `e < -1`, not `e < 0`.
        Assert.Equal(0, SessionTitleBarLayout.HiddenDelta(-1, 30, 0));
        Assert.Equal(1, SessionTitleBarLayout.HiddenDelta(-1.5, 30, 0));
    }

    [Fact]
    public void A_toggle_comes_back_only_once_its_own_width_and_the_hysteresis_are_free()
    {
        // Its `e >= t + xh`: 30 wide plus 24 of hysteresis.
        Assert.Equal(0, SessionTitleBarLayout.HiddenDelta(53, 30, hidden: 2));
        Assert.Equal(-1, SessionTitleBarLayout.HiddenDelta(54, 30, hidden: 2));
        Assert.Equal(-2, SessionTitleBarLayout.HiddenDelta(84, 30, hidden: 2));
    }

    [Fact]
    public void Nothing_comes_back_when_nothing_is_hidden()
    {
        Assert.Equal(0, SessionTitleBarLayout.HiddenDelta(500, 30, hidden: 0));
    }

    [Fact]
    public void Restoring_never_returns_more_than_are_hidden()
    {
        Assert.Equal(-2, SessionTitleBarLayout.HiddenDelta(5000, 30, hidden: 2));
    }

    [Fact]
    public void The_band_between_folding_and_restoring_leaves_the_count_alone()
    {
        // This is the point of the two thresholds: a bar sitting between them keeps the
        // state it has instead of flickering between the two.
        for (var slack = -1.0; slack < 54; slack += 1)
        {
            Assert.Equal(0, SessionTitleBarLayout.HiddenDelta(slack, 30, hidden: 1));
        }
    }

    // ---- Dh: whether the pill shows its label ----

    [Fact]
    public void The_pill_collapses_only_when_the_bar_is_full_and_the_title_is_clipped()
    {
        Assert.True(Compact(false, freePx: 0, client: 80, natural: 200));
        Assert.False(Compact(false, freePx: 1, client: 80, natural: 200));
        Assert.False(Compact(false, freePx: 0, client: 200, natural: 200));
    }

    [Fact]
    public void A_collapsed_pill_needs_room_the_toggles_and_the_budget_to_expand_again()
    {
        // Every one of the three has to hold; each is checked by taking it away alone.
        Assert.True(Compact(true, freePx: 96, client: 80, natural: 200, hidden: 0, budget: 10, atCollapse: 0)
            is false);
        Assert.True(Compact(true, freePx: 95, client: 80, natural: 200, hidden: 0, budget: 10, atCollapse: 0));
        Assert.True(Compact(true, freePx: 96, client: 80, natural: 200, hidden: 1, budget: 10, atCollapse: 0));
        Assert.True(Compact(true, freePx: 96, client: 80, natural: 200, hidden: 0, budget: 1, atCollapse: 0));
    }

    [Fact]
    public void The_budget_has_to_beat_its_collapse_value_by_more_than_a_pixel()
    {
        // Its `wh`: a budget that merely returned to where it was does not expand the pill.
        Assert.True(Compact(true, 96, 80, 200, 0, budget: 51, atCollapse: 50));
        Assert.False(Compact(true, 96, 80, 200, 0, budget: 52, atCollapse: 50));
    }

    [Fact]
    public void A_pill_that_never_collapsed_has_no_mark_to_beat()
    {
        // The panel seeds `pillBudgetAtCollapse` at negative infinity, so the budget test
        // passes trivially until a collapse records one.
        Assert.False(Compact(true, 96, 80, 200, 0, budget: 0, atCollapse: double.NegativeInfinity));
    }

    private static bool Compact(
        bool compact,
        double freePx,
        double client,
        double natural,
        int hidden = 0,
        double budget = 0,
        double atCollapse = double.NegativeInfinity) =>
        SessionTitleBarLayout.PillsCompact(compact, freePx, client, natural, hidden, budget, atCollapse);
}
