using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ChatComposerActionsTests
{
    [Fact]
    public void An_idle_composer_always_sends()
    {
        Assert.False(ChatComposerActions.ShowsStop(turnRunning: false, hasDraftContent: false));
        Assert.False(ChatComposerActions.ShowsStop(turnRunning: false, hasDraftContent: true));
    }

    [Fact]
    public void A_running_turn_shows_stop_only_while_there_is_nothing_to_send()
    {
        Assert.True(ChatComposerActions.ShowsStop(turnRunning: true, hasDraftContent: false));
    }

    /// <summary>
    /// The reference keeps Enter as the send shortcut for the whole time an answer
    /// is streaming and puts the prompt in the queue, so a typed draft takes the
    /// slot back from Stop rather than arming it.
    /// </summary>
    [Fact]
    public void A_draft_typed_mid_turn_takes_the_slot_back_from_stop()
    {
        Assert.False(ChatComposerActions.ShowsStop(turnRunning: true, hasDraftContent: true));
    }
}
