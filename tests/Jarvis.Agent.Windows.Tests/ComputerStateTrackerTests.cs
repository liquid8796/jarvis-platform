using Jarvis.Agent.Windows;

namespace Jarvis.Agent.Windows.Tests;

public sealed class ComputerStateTrackerTests
{
    private static ComputerObservation Observation(string title) => new(title, 42, "Synthetic", null, []);

    [Fact]
    public void New_observation_invalidates_previous_state_in_same_session()
    {
        var tracker = new ComputerStateTracker();
        var first = tracker.Capture("session-a", Observation("one"));
        var second = tracker.Capture("session-a", Observation("two"));

        Assert.NotEqual(first.StateId, second.StateId);
        Assert.True(second.Generation > first.Generation);
        var stale = Assert.Throws<InvalidOperationException>(() => tracker.Validate("session-a", first.StateId));
        Assert.Contains("stale", stale.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(second.StateId, tracker.Validate("session-a", second.StateId).StateId);
    }

    [Fact]
    public void State_ids_are_session_scoped()
    {
        var tracker = new ComputerStateTracker();
        var state = tracker.Capture("session-a", Observation("one"));

        var ex = Assert.Throws<InvalidOperationException>(() => tracker.Validate("session-b", state.StateId));

        Assert.Contains("session", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_invalidation_rejects_prior_state()
    {
        var tracker = new ComputerStateTracker();
        var state = tracker.Capture("session-a", Observation("one"));

        tracker.Invalidate("session-a");

        var ex = Assert.Throws<InvalidOperationException>(() => tracker.Validate("session-a", state.StateId));
        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void History_is_bounded()
    {
        var tracker = new ComputerStateTracker(capacity: 3);
        var states = Enumerable.Range(0, 5).Select(i => tracker.Capture("session-" + i, Observation(i.ToString()))).ToArray();

        Assert.True(tracker.StoredStateCount <= 3);
        Assert.Throws<InvalidOperationException>(() => tracker.Validate(states[0].SessionId, states[0].StateId));
        Assert.Equal(states[^1].StateId, tracker.Validate(states[^1].SessionId, states[^1].StateId).StateId);
    }
}
