using System.Drawing;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class ComputerUseObservationStoreTests
{
    private static readonly Rectangle Bounds = new(10, 20, 800, 600);

    [Fact]
    public void New_capture_replaces_previous_observation_for_the_same_scope_and_window()
    {
        var store = new ComputerUseObservationStore<string>();
        var first = store.Capture("session-a", (nint)42, Bounds, ["first"], hasScreenshot: true);
        var second = store.Capture("session-a", (nint)42, Bounds, ["second"], hasScreenshot: true);

        var stale = Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-a", (nint)42, first.ObservationId, Bounds, first.ScreenshotId, requireScreenshot: true));
        Assert.Equal("STALE_OBSERVATION", stale.Code);
        Assert.Same(second, store.RequireCurrent(
            "session-a", (nint)42, second.ObservationId, Bounds, second.ScreenshotId, requireScreenshot: true));
    }

    [Fact]
    public void Observation_is_bound_to_scope_window_bounds_and_screenshot()
    {
        var store = new ComputerUseObservationStore<string>();
        var observation = store.Capture("session-a", (nint)42, Bounds, ["node"], hasScreenshot: true);

        Assert.Equal("STALE_OBSERVATION", Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-b", (nint)42, observation.ObservationId, Bounds)).Code);
        Assert.Equal("STALE_OBSERVATION", Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-a", (nint)43, observation.ObservationId, Bounds)).Code);
        Assert.Equal("STALE_OBSERVATION", Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-a", (nint)42, observation.ObservationId, new Rectangle(10, 20, 801, 600))).Code);

        var fresh = store.Capture("session-a", (nint)42, Bounds, ["node"], hasScreenshot: true);
        Assert.Equal("STALE_OBSERVATION", Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-a", (nint)42, fresh.ObservationId, Bounds, "shot_00000000000000000000000000000000",
            requireScreenshot: true)).Code);
    }

    [Fact]
    public void Coordinate_action_requires_a_screenshot_from_the_same_observation()
    {
        var store = new ComputerUseObservationStore<string>();
        var textOnly = store.Capture("session-a", (nint)42, Bounds, ["node"], hasScreenshot: false);

        var missing = Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-a", (nint)42, textOnly.ObservationId, Bounds, screenshotId: null, requireScreenshot: true));
        Assert.Equal("STALE_OBSERVATION", missing.Code);

        var screenshot = store.Capture("session-a", (nint)42, Bounds, ["node"], hasScreenshot: true);
        Assert.Same(screenshot, store.RequireCurrent("session-a", (nint)42, screenshot.ObservationId,
            Bounds, screenshot.ScreenshotId, requireScreenshot: true));
    }

    [Fact]
    public void Consuming_an_observation_prevents_blind_replay()
    {
        var store = new ComputerUseObservationStore<string>();
        var observation = store.Capture("session-a", (nint)42, Bounds, ["node"], hasScreenshot: true);
        store.Consume(observation);

        var replay = Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-a", (nint)42, observation.ObservationId, Bounds));
        Assert.Equal("STALE_OBSERVATION", replay.Code);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Expired_observation_and_scope_invalidation_are_rejected()
    {
        var store = new ComputerUseObservationStore<string>(ttl: TimeSpan.FromSeconds(1));
        var expired = store.Capture("session-a", (nint)42, Bounds, ["node"], hasScreenshot: false,
            observedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2));
        Assert.Equal("STALE_OBSERVATION", Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-a", (nint)42, expired.ObservationId, Bounds)).Code);

        var current = store.Capture("session-a", (nint)42, Bounds, ["node"], hasScreenshot: false);
        store.InvalidateScope("session-a");
        Assert.Equal("STALE_OBSERVATION", Assert.Throws<AgentRequestException>(() => store.RequireCurrent(
            "session-a", (nint)42, current.ObservationId, Bounds)).Code);
    }
}
