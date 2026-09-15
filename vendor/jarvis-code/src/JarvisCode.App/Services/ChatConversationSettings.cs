namespace JarvisCode.App.Services;

/// <summary>
/// How connector tools reach a conversation, the reference's own two rows under
/// Connectors › Tool access.
/// </summary>
public enum ChatToolAccess
{
    /// <summary>"Load tools when needed" — the deferred surface. The reference's "on", and its default.</summary>
    LoadWhenNeeded,

    /// <summary>"Tools already loaded" — every tool advertised up front. The reference's "off".</summary>
    AlreadyLoaded,
}

/// <summary>What one chat conversation has switched on, as it is stored.</summary>
public sealed class ChatConversationSetting
{
    /// <summary>The reference's <c>enabled_web_search</c>; unset follows the app's own default.</summary>
    public bool? WebSearch { get; set; }

    /// <summary>Whether the model may think on this conversation; unset means it may.</summary>
    public bool? ExtendedThinking { get; set; }

    /// <summary>The reference's <c>tool_search_mode</c>: "on" or "off"; unset is its "auto".</summary>
    public string? ToolAccess { get; set; }

    /// <summary>Connector (MCP server) names switched on for this conversation.</summary>
    public List<string> Connectors { get; set; } = [];
}

/// <summary>The three switches resolved against their defaults.</summary>
/// <param name="WebSearch">Whether the turn carries a web-search tool.</param>
/// <param name="ExtendedThinking">Whether the model may think.</param>
/// <param name="ToolAccess">How connector tools are advertised.</param>
/// <param name="Connectors">Connector names switched on.</param>
public readonly record struct ChatConversationState(
    bool WebSearch,
    bool ExtendedThinking,
    ChatToolAccess ToolAccess,
    IReadOnlyList<string> Connectors);

/// <summary>
/// Per-conversation switches on the Chat surface, which is where the reference keeps
/// them: its composer toggles write conversation settings
/// (<c>enabled_web_search</c>, <c>tool_search_mode</c>, the per-connector map), not
/// account settings, so one chat can search the web while the next does not.
///
/// Measured from the reference desktop's tools menu (<c>c752b32f8-DqlexaAe.js</c>:
/// <c>toggleSearchTool("enabled_web_search", …)</c> and the Tool access submenu,
/// whose current row is <c>"off" === (tool_search_mode ?? "auto") ? "off" : "on"</c>
/// — so "load when needed" is what an untouched conversation gets).
/// </summary>
public sealed class ChatConversationSettings(UiSettingsStore store)
{
    /// <summary>The reference's "on".</summary>
    public const string LoadWhenNeeded = "on";

    /// <summary>The reference's "off".</summary>
    public const string AlreadyLoaded = "off";

    /// <summary>
    /// Resolves one conversation's switches. <paramref name="webSearchDefault"/> is
    /// the app's own setting, which stands in for the account default the reference
    /// falls back to.
    /// </summary>
    public ChatConversationState Get(string sessionId, bool webSearchDefault)
    {
        var stored = store.Current.ChatConversations.GetValueOrDefault(sessionId);
        return Resolve(stored, webSearchDefault);
    }

    /// <summary>The resolution itself, without a store — the part worth testing.</summary>
    public static ChatConversationState Resolve(ChatConversationSetting? stored, bool webSearchDefault) =>
        new(
            stored?.WebSearch ?? webSearchDefault,
            stored?.ExtendedThinking ?? true,
            stored?.ToolAccess == AlreadyLoaded ? ChatToolAccess.AlreadyLoaded : ChatToolAccess.LoadWhenNeeded,
            stored?.Connectors is { Count: > 0 } connectors ? connectors : []);

    public void SetWebSearch(string sessionId, bool enabled) =>
        Update(sessionId, entry => entry.WebSearch = enabled);

    public void SetExtendedThinking(string sessionId, bool enabled) =>
        Update(sessionId, entry => entry.ExtendedThinking = enabled);

    public void SetToolAccess(string sessionId, ChatToolAccess access) =>
        Update(sessionId, entry => entry.ToolAccess =
            access == ChatToolAccess.AlreadyLoaded ? AlreadyLoaded : LoadWhenNeeded);

    /// <summary>Switches one connector on or off for this conversation.</summary>
    public void SetConnector(string sessionId, string server, bool enabled) =>
        Update(sessionId, entry =>
        {
            entry.Connectors.RemoveAll(name => string.Equals(name, server, StringComparison.OrdinalIgnoreCase));
            if (enabled)
            {
                entry.Connectors.Add(server);
            }
        });

    /// <summary>Drops a deleted conversation's switches so the file does not grow forever.</summary>
    public void Forget(string sessionId)
    {
        if (store.Current.ChatConversations.Remove(sessionId))
        {
            store.Save();
        }
    }

    private void Update(string sessionId, Action<ChatConversationSetting> change)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        if (!store.Current.ChatConversations.TryGetValue(sessionId, out var entry))
        {
            entry = new ChatConversationSetting();
            store.Current.ChatConversations[sessionId] = entry;
        }

        change(entry);
        store.Save();
    }
}
