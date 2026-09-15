using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class FadeInWordsTests
{
    [Fact]
    public void A_paragraph_splits_into_words_that_keep_their_spacing()
    {
        var words = FadeInWords.Split("Where do you want to start?");
        Assert.Equal(6, words.Count);
        Assert.Equal("Where", words[0].Text);
        Assert.Equal(" ", words[0].TrailingSpace);
        Assert.Equal("start?", words[^1].Text);
        Assert.Equal("", words[^1].TrailingSpace);
    }

    [Fact]
    public void Rejoining_the_words_reproduces_the_paragraph()
    {
        const string text = "Bring me anything—a tough problem, a half-formed idea.";
        var rejoined = string.Concat(FadeInWords.Split(text).Select(w => w.Text + w.TrailingSpace));
        Assert.Equal(text, rejoined);
    }

    [Fact]
    public void Bold_markers_toggle_and_are_consumed()
    {
        var words = FadeInWords.Split("plain **bold here** plain");
        Assert.Equal(["plain", "bold", "here", "plain"], words.Select(w => w.Text));
        Assert.Equal([false, true, true, false], words.Select(w => w.IsBold));
    }

    [Fact]
    public void Leading_space_joins_the_word_before_it_rather_than_becoming_one()
    {
        var words = FadeInWords.Split("  one   two  ");
        Assert.Equal(["one", "two"], words.Select(w => w.Text));
        Assert.Equal("   ", words[0].TrailingSpace);
        Assert.Equal("  ", words[1].TrailingSpace);
    }

    [Fact]
    public void An_empty_paragraph_has_no_words()
    {
        Assert.Empty(FadeInWords.Split(""));
        Assert.Empty(FadeInWords.Split("   "));
        Assert.Empty(FadeInWords.Split("****"));
    }

    [Fact]
    public void The_first_delay_is_always_zero()
        => Assert.Equal(0, FadeInWords.Delays(10)[0]);

    [Fact]
    public void The_schedule_decelerates_towards_the_end()
    {
        // The gap is max(50, 150 / max(1, remaining / 2)), so the opening words sit on
        // the 50ms floor and the closing ones stretch out.
        var delays = FadeInWords.Delays(10);
        var gaps = delays.Zip(delays.Skip(1), (a, b) => b - a).ToArray();
        Assert.Equal(50, gaps[0]);
        Assert.True(gaps[^1] > gaps[0], "the last gap should be longer than the first");
        Assert.Equal(150, gaps[^1]);
    }

    [Fact]
    public void The_gap_ladder_is_the_measured_one()
    {
        // With six words left the divisor is 3, which lands exactly on the floor; from
        // five down the gap climbs 60, 75, 100, 150.
        var delays = FadeInWords.Delays(6);
        var gaps = delays.Zip(delays.Skip(1), (a, b) => b - a).ToArray();
        Assert.Equal([50, 60, 75, 100, 150], gaps);
    }

    [Fact]
    public void An_instant_reveal_has_no_delays_and_completes_at_once()
    {
        Assert.All(FadeInWords.Schedule(8, instant: true), d => Assert.Equal(0, d));
        Assert.Equal(0, FadeInWords.CompleteAfterMs(8, instant: true));
    }

    [Fact]
    public void Completion_is_the_last_word_plus_the_tail()
    {
        var schedule = FadeInWords.Schedule(6, instant: false);
        Assert.Equal(schedule[^1] + FadeInWords.TailMs, FadeInWords.CompleteAfterMs(6, instant: false));
    }

    [Fact]
    public void An_empty_paragraph_still_reports_after_the_tail()
        => Assert.Equal(FadeInWords.TailMs, FadeInWords.CompleteAfterMs(0, instant: false));

    [Fact]
    public void A_speed_multiplier_divides_the_schedule_and_the_tail()
    {
        var normal = FadeInWords.Schedule(6, instant: false);
        var doubled = FadeInWords.Schedule(6, instant: false, speedMultiplier: 2);
        Assert.Equal(normal.Select(d => Math.Round(d / 2, MidpointRounding.AwayFromZero)), doubled);
        Assert.Equal(doubled[^1] + 200, FadeInWords.CompleteAfterMs(6, instant: false, speedMultiplier: 2));
    }

    [Fact]
    public void A_multiplier_below_one_never_slows_the_reveal_down()
    {
        Assert.Equal(
            FadeInWords.Schedule(6, instant: false),
            FadeInWords.Schedule(6, instant: false, speedMultiplier: 0.25));
    }

    [Fact]
    public void The_onboarding_paragraphs_reveal_in_a_readable_time()
    {
        // A guard on the constants rather than the text: the three paragraphs the
        // onboarding shows should land inside a few seconds, not a minute.
        var total = new[] { ChatWelcome.OnboardingIntro, ChatWelcome.OnboardingStart }
            .Sum(p => FadeInWords.CompleteAfterMs(FadeInWords.Split(p).Count, instant: false));
        Assert.InRange(total, 1_000, 8_000);
    }
}
