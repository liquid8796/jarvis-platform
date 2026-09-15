namespace JarvisCode.App.Services;

/// <summary>
/// What the composer's primary action is called, ported from the reference desktop's
/// own send and stop buttons (<c>lx</c>, <c>cx</c> and <c>sx</c> in
/// <c>shared-11-CL4cxK09.js</c>, desktop 1.44121.2.0).
///
/// The send button there is <c>icon:"ArrowUp"</c> on <c>variant:"brand"</c> — a
/// filled accent button, not the ghost return arrow this port used to draw, which is
/// its <c>replyLook</c> variant instead. Stopping swaps in the secondary Stop and
/// advertises Escape, which stops the turn.
/// </summary>
public static class ChatComposerActions
{
    /// <summary>The send button's accessible name.</summary>
    public const string SendMessage = "Send message";           // Xx0WZVz8QS

    /// <summary>What the send button is called while a task is being started instead.</summary>
    public const string StartTask = "Start task";               // 6Jb0AX1uQx

    /// <summary>The stop button's accessible name.</summary>
    public const string StopResponse = "Stop response";         // RANC4/S/j1

    /// <summary>The stop button's tooltip, which the reference pairs with an Esc hint.</summary>
    public const string StopTooltip = "Stop Jarvis response";   // BonFmr6O3r

    /// <summary>
    /// The send action's secondary hint while a turn is live. The reference's own
    /// <c>fy()</c> answers <c>{sendShortcut:"enter"}</c> with this on
    /// <c>cmd+enter</c> the whole time an answer is streaming, which is why Enter
    /// there sends into the queue rather than stopping the turn.
    /// </summary>
    public const string Interrupt = "Interrupt";                // PYWls5W28p

    /// <summary>
    /// Whether the primary action is Stop rather than Send. The reference's <c>_y</c>
    /// hands the slot back to Send as soon as there is something to send — its
    /// <c>O = sendReplacesStopMidTurn &amp;&amp; (hasDraftContent &amp;&amp; !sendDisabled || stopWithheld)</c>
    /// — so Stop holds it only while a turn is running and the composer is empty.
    /// Submitting mid-turn queues the prompt instead of ending the turn, which is why
    /// stopping keeps chords of its own (Esc, and Ctrl+Enter for <see cref="Interrupt"/>).
    /// </summary>
    public static bool ShowsStop(bool turnRunning, bool hasDraftContent) =>
        turnRunning && !hasDraftContent;
}
