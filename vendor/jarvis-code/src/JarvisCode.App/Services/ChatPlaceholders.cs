namespace JarvisCode.App.Services;

/// <summary>
/// What the composer's dictation is doing. The reference resolves this before
/// anything else, so a listening composer never shows a conversation placeholder.
/// </summary>
public enum ChatDictation
{
    Off,
    Connecting,
    Listening,
    Processing,
    Speaking,
    Reconnecting,
}

/// <summary>
/// Everything the placeholder ladder reads, in the reference's own argument names.
/// A record rather than ten parameters, because the reference passes one options
/// object and a caller that has to remember the order of ten booleans will get it
/// wrong.
/// </summary>
/// <param name="Dictation">The voice session's state, which outranks every other rung.</param>
/// <param name="Placeholder">An explicit placeholder the caller supplied.</param>
/// <param name="RolePickerActive">The role picker is open.</param>
/// <param name="AnsweringQuestion">The composer is answering a question card.</param>
/// <param name="HiddenQuestion">That question's own text, when it has one.</param>
/// <param name="ModelFallbackNotice">A fallback-model notice is up.</param>
/// <param name="ReviewChips">Review comments are staged on the composer.</param>
/// <param name="NewConversation">Nothing has been sent in this conversation yet.</param>
/// <param name="AgentMode">The conversation is an agent-mode one.</param>
/// <param name="ExternalConversation">The conversation is somebody else's.</param>
public readonly record struct ChatPlaceholderState(
    ChatDictation Dictation = ChatDictation.Off,
    string? Placeholder = null,
    bool RolePickerActive = false,
    bool AnsweringQuestion = false,
    string? HiddenQuestion = null,
    bool ModelFallbackNotice = false,
    bool ReviewChips = false,
    bool NewConversation = false,
    bool AgentMode = false,
    bool ExternalConversation = false);

/// <summary>
/// What the Chat composer's placeholder says, ported rung for rung from the
/// reference desktop's own resolver (<c>XF</c> in <c>shared-11-CL4cxK09.js</c>,
/// desktop 1.44121.2.0). "How can I help you today?" is only its
/// <em>new-conversation</em> rung — a conversation that has started reads
/// "Write a message…" there — and this port used to send the first sentence for
/// every state of the surface, which is the one rung a reader would never see.
/// </summary>
public static class ChatPlaceholders
{
    public const string Connecting = "Connecting...";                     // 5y2qWOlFVf
    public const string Listening = "Listening...";                       // YAeW6K6mK9
    public const string Processing = "Processing...";                     // 6OWS4p9SXV
    public const string Speaking = "Jarvis is speaking...";               // mKbWqS2HCv
    public const string Reconnecting = "Reconnecting...";                 // dOGweUbyRk
    public const string SomethingElse = "Something else";                 // qm/eL5Y8Fl
    public const string OrReplyDirectly = "Or reply directly…";           // NwUAfHUQN1
    public const string AddressReviewComments = "Address these review comments"; // G6en+ZMAgk
    public const string NewConversation = "How can I help you today?";    // XLcM6WHfQR
    public const string AgentMode = "Reply at any time, even when Jarvis is working"; // 2eHqp+TzbJ
    public const string ExternalConversation = "Reply…";                  // p9MPkbVUdx
    public const string WriteAMessage = "Write a message…";               // zElqHZzItw

    /// <summary>The reference's ladder, in the reference's order.</summary>
    public static string Resolve(ChatPlaceholderState state)
    {
        // Dictation first, and unconditionally: the reference resolves the voice
        // session before it even looks at the conversation.
        switch (state.Dictation)
        {
            case ChatDictation.Connecting:
                return Connecting;
            case ChatDictation.Listening:
                return Listening;
            case ChatDictation.Processing:
                return Processing;
            case ChatDictation.Speaking:
                return Speaking;
            case ChatDictation.Reconnecting:
                return Reconnecting;
        }

        if (!string.IsNullOrEmpty(state.Placeholder))
        {
            return state.Placeholder;
        }

        if (state.RolePickerActive)
        {
            return SomethingElse;
        }

        if (state.AnsweringQuestion && !string.IsNullOrEmpty(state.HiddenQuestion))
        {
            return state.HiddenQuestion;
        }

        if (state.AnsweringQuestion || state.ModelFallbackNotice)
        {
            return OrReplyDirectly;
        }

        if (state.ReviewChips)
        {
            return AddressReviewComments;
        }

        if (state.NewConversation)
        {
            return NewConversation;
        }

        if (state.AgentMode)
        {
            return AgentMode;
        }

        return state.ExternalConversation ? ExternalConversation : WriteAMessage;
    }
}
