using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ComparisonSessionTests
{
    [Fact]
    public void A_fresh_session_is_idle_with_nothing_recorded()
    {
        var session = new ComparisonSession();
        Assert.Equal(ComparisonPhase.Idle, session.Phase);
        Assert.Empty(session.Turns);
        Assert.Equal(0, session.Votes);
        Assert.Null(session.LastTurn);
    }

    [Fact]
    public void Both_panels_answering_is_what_raises_the_question()
    {
        var session = new ComparisonSession();
        Assert.True(session.BeginTurn("why?"));
        Assert.Equal(ComparisonPhase.Streaming, session.Phase);

        session.Settle(0, null);
        Assert.Equal(ComparisonPhase.Streaming, session.Phase);

        session.Settle(1, null);
        Assert.Equal(ComparisonPhase.Vote, session.Phase);
    }

    [Fact]
    public void A_pair_where_one_arm_failed_is_not_a_pair_to_prefer_between()
    {
        var session = new ComparisonSession();
        session.BeginTurn("why?");
        session.Settle(0, "overloaded_error");
        session.Settle(1, null);
        Assert.Equal(ComparisonPhase.Idle, session.Phase);
        Assert.Equal("overloaded_error", session.LastTurn!.Errors[0]);
        Assert.Null(session.LastTurn.Errors[1]);
    }

    [Fact]
    public void The_composer_refuses_while_both_arms_are_answering()
    {
        var session = new ComparisonSession();
        session.BeginTurn("first");
        Assert.False(session.BeginTurn("second"));
        Assert.Single(session.Turns);
    }

    [Fact]
    public void An_empty_prompt_starts_nothing()
    {
        var session = new ComparisonSession();
        Assert.False(session.BeginTurn("   "));
        Assert.Empty(session.Turns);
    }

    [Fact]
    public void The_walk_is_vote_then_reason_then_saved()
    {
        var session = Answered();
        session.Vote(ComparisonVote.B);
        Assert.Equal(ComparisonPhase.Reason, session.Phase);
        Assert.Equal(ComparisonVote.B, session.LastTurn!.Vote);

        session.SaveVote(["Clearer writing"], "  it read better  ");
        Assert.Equal(ComparisonPhase.Saved, session.Phase);
        Assert.Equal(1, session.Votes);
        Assert.Equal(["Clearer writing"], session.LastTurn.Reasons);
        Assert.Equal("it read better", session.LastTurn.Comment);
    }

    [Fact]
    public void Back_to_vote_takes_the_pick_and_its_answers_back()
    {
        var session = Answered();
        session.Vote(ComparisonVote.A);
        session.BackToVote();
        Assert.Equal(ComparisonPhase.Vote, session.Phase);
        Assert.Null(session.LastTurn!.Vote);
        Assert.Equal(0, session.Votes);
    }

    [Fact]
    public void A_turn_is_only_saved_once()
    {
        var session = Answered();
        session.Vote(ComparisonVote.A);
        session.SaveVote(null, null);
        session.SaveVote(["More accurate"], "again");
        Assert.Equal(1, session.Votes);
        Assert.Null(session.LastTurn!.Reasons);
    }

    [Fact]
    public void Saving_without_a_pick_behind_it_does_nothing()
    {
        var session = Answered();
        session.SaveVote(["More accurate"], null);
        Assert.Equal(ComparisonPhase.Vote, session.Phase);
        Assert.Equal(0, session.Votes);
    }

    [Fact]
    public void Change_takes_the_recorded_preference_back_off_the_count()
    {
        var session = Answered();
        session.Vote(ComparisonVote.Tie);
        session.SaveVote(["Both good"], null);
        Assert.Equal(1, session.Votes);

        session.UndoVote();
        Assert.Equal(ComparisonPhase.Vote, session.Phase);
        Assert.Equal(0, session.Votes);
        Assert.Null(session.LastTurn!.Vote);
        Assert.Null(session.LastTurn.Reasons);

        // And the turn can then be voted on again, which the once-per-turn guard
        // would otherwise have closed off for good.
        session.Vote(ComparisonVote.A);
        session.SaveVote(null, null);
        Assert.Equal(1, session.Votes);
    }

    [Fact]
    public void Sending_out_of_the_reason_phase_saves_the_pending_vote_first()
    {
        var session = Answered();
        session.Vote(ComparisonVote.A);
        session.BeginTurn("next");
        Assert.Equal(1, session.Votes);
        Assert.Equal(ComparisonPhase.Streaming, session.Phase);
        Assert.Equal(2, session.Turns.Count);
    }

    [Fact]
    public void Sending_out_of_the_vote_phase_skips_it_and_records_nothing()
    {
        var session = Answered();
        session.BeginTurn("next");
        Assert.Equal(0, session.Votes);
        Assert.Equal(ComparisonPhase.Streaming, session.Phase);
    }

    [Fact]
    public void A_panels_memory_is_fixed_the_moment_the_chat_starts()
    {
        var session = new ComparisonSession();
        Assert.False(session.MemoryLocked(0));
        Assert.False(session.MemoryLocked(1));

        session.BeginTurn("why?");
        Assert.True(session.MemoryLocked(0));
        Assert.True(session.MemoryLocked(1));

        session.Settle(0, "boom");
        session.Settle(1, "boom");
        // Back at idle, but the conversations exist, so it stays fixed.
        Assert.Equal(ComparisonPhase.Idle, session.Phase);
        Assert.True(session.MemoryLocked(0));
    }

    [Fact]
    public void The_sides_are_only_shuffled_before_the_first_turn()
    {
        var session = new ComparisonSession();
        Assert.True(session.CanShuffleSides);
        Assert.True(session.ShouldShuffleSides(() => 0.1));
        Assert.False(session.ShouldShuffleSides(() => 0.9));

        session.BeginTurn("why?");
        Assert.False(session.CanShuffleSides);
        Assert.False(session.ShouldShuffleSides(() => 0.0));
    }

    [Fact]
    public void Every_transition_reports_itself_once()
    {
        var session = new ComparisonSession();
        var changes = 0;
        session.Changed += () => changes++;

        session.BeginTurn("why?");
        session.Settle(0, null);
        session.Settle(1, null);
        session.Vote(ComparisonVote.A);
        session.SaveVote(null, null);
        Assert.Equal(5, changes);

        // A refused transition is not a change.
        session.SaveVote(null, null);
        Assert.Equal(5, changes);
    }

    private static ComparisonSession Answered()
    {
        var session = new ComparisonSession();
        session.BeginTurn("why?");
        session.Settle(0, null);
        session.Settle(1, null);
        return session;
    }
}
