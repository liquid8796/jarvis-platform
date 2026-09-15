namespace JarvisCode.Core.Settings;

/// <summary>
/// One ChatGPT chat the browser session is keeping the thread of.
///
/// The provider is handed a conversation and has to decide whether the account already holds a chat
/// for it; the answer binds a local scope, account, prompt and full tool contract to its history.
/// The host persists one scoped record atomically in browser runtime state so desktop and CLI
/// processes can continue the same session without overwriting each other's application settings.
/// </summary>
public sealed class ChatGptChatEntry
{
    /// <summary>The hash of the conversation this chat answers, account included.</summary>
    public string Key { get; set; } = "";

    /// <summary>Versioned hash of the account and local session/fork identity.</summary>
    public string ScopeKey { get; set; } = "";

    /// <summary>The complete tool contract used to open this chat, including schemas and descriptions.</summary>
    public string ToolContractHash { get; set; } = "";

    /// <summary>The ChatGPT conversation id.</summary>
    public string ConversationId { get; set; } = "";

    /// <summary>How many messages this app has sent into it, which is what rotation counts.</summary>
    public int SentMessages { get; set; }

    /// <summary>
    /// Legacy name-only metadata retained for settings-file compatibility. Current adapters use
    /// ToolContractHash so schema changes and removed actions also invalidate the remote contract.
    /// </summary>
    public List<string> DescribedTools { get; set; } = [];

    /// <summary>When this chat was last written to, which is the order the oldest is dropped in.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
