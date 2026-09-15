namespace JarvisCode.App.Services;

/// <summary>
/// What the Chat surface says when a turn does not finish, ported from the
/// reference's own card (<c>c3e2391e3-3lB_ip9x.js</c>, its <c>Xu</c>). The
/// reference names the assistant in both sentences, so both are branded here.
/// </summary>
public static class ChatTurnNotices
{
    /// <summary>A turn the user stopped.</summary>
    public const string Interrupted = "Jarvis’s response was interrupted.";        // NpgeK1NYiO

    /// <summary>A turn that failed for any other reason.</summary>
    public const string CouldNotFinish =
        "Jarvis couldn’t finish this response. Try again in a moment.";            // kDl9O2T8pK

    /// <summary>The card's two actions.</summary>
    public const string EditPrompt = "Edit prompt";                                 // TmqQS2M3WB

    public const string TryAgain = "Try again";                                     // FazwRldA7z
}
