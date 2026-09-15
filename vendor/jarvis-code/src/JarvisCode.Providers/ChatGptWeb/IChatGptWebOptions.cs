using JarvisCode.Core.Settings;

namespace JarvisCode.Providers.ChatGptWeb;

/// <summary>
/// The settings the ChatGPT browser session reads. Supplied by the host so this project keeps
/// knowing nothing about where settings live.
/// </summary>
public interface IChatGptWebOptions
{
    /// <summary>
    /// Name of the ChatGPT project every chat is created in. Blank keeps chats loose, outside any
    /// project. The name is resolved to a gizmo id on first use, and the project is created when
    /// no project of that name exists yet.
    /// </summary>
    string ChatGptProjectName { get; }

    /// <summary>
    /// The project id already resolved for <see cref="ChatGptProjectName"/>, or blank when it has
    /// not been resolved yet. Pinning it is what stops a restart from creating a second project
    /// with the same name if the name lookup ever stops working.
    /// </summary>
    string ChatGptProjectId { get; }

    /// <summary>Stores the id resolved for the current project name.</summary>
    void SaveChatGptProjectId(string projectId);

    /// <summary>Reads a project pin belonging to a particular account and project name.</summary>
    string GetChatGptProjectId(string projectScopeKey) => ChatGptProjectId;

    /// <summary>
    /// Saves an account-scoped runtime pin (or an internal pending-create marker) without changing
    /// unrelated application settings.
    /// </summary>
    void SaveChatGptProjectId(string projectScopeKey, string projectId) => SaveChatGptProjectId(projectId);

    /// <summary>
    /// How many messages this app may send into one chat before it is deleted and a fresh chat
    /// takes over. Counts only what the app sends, not ChatGPT's replies.
    /// </summary>
    int ChatGptRotateAfterMessages { get; }

    /// <summary>
    /// The chats the session is keeping the thread of, from the last time it saved them. Empty is
    /// the right answer for a host that does not store them: the session then behaves as it always
    /// did and opens a fresh chat.
    /// </summary>
    IReadOnlyList<ChatGptChatEntry> ChatGptChats => [];

    /// <summary>Stores the chats, replacing whatever was there.</summary>
    void SaveChatGptChats(IReadOnlyList<ChatGptChatEntry> chats)
    {
    }

    /// <summary>
    /// Holds exclusive ownership of a browser conversation across host processes. Lightweight
    /// adapters and test fixtures may omit persistence and use the provider's in-process gate.
    /// </summary>
    ValueTask<IAsyncDisposable?> AcquireChatGptScopeAsync(string scopeKey, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IAsyncDisposable?>(null);

    /// <summary>Reads the latest durable state after acquiring the scope lease.</summary>
    ChatGptChatEntry? ReadChatGptChat(string scopeKey) => ChatGptChats
        .Where(chat => chat.ScopeKey == scopeKey)
        .OrderByDescending(chat => chat.UpdatedAt).FirstOrDefault();

    /// <summary>
    /// Replaces only this scope. Null is an authoritative invalidation, including across restarts;
    /// a host must not resurrect an older settings snapshot after saving it.
    /// </summary>
    void SaveChatGptChat(string scopeKey, ChatGptChatEntry? chat) => SaveChatGptChats(
        [.. ChatGptChats.Where(entry => entry.ScopeKey != scopeKey), .. chat is null ? [] : new[] { chat }]);

    /// <summary>Below this a rotation would thrash, so anything smaller is raised to it.</summary>
    public const int MinimumRotateAfterMessages = 20;

    public const int DefaultRotateAfterMessages = 100;

    /// <summary>The configured threshold, clamped to something a chat can actually work with.</summary>
    public int ResolveRotateAfterMessages() =>
        ChatGptRotateAfterMessages < MinimumRotateAfterMessages
            ? MinimumRotateAfterMessages
            : ChatGptRotateAfterMessages;
}
