using JarvisCode.Core.Models;
using JarvisCode.Core.Settings;
using JarvisCode.Providers;
using JarvisCode.Providers.Bedrock;
using JarvisCode.Providers.ChatGptWeb;
using JarvisCode.Providers.LlmApi;
using JarvisCode.Providers.Vertex;

namespace JarvisCode.Host;

/// <summary>
/// Holds the live <see cref="AppSettings"/> instance for the process and persists
/// changes. Also serves as the API-key source for providers: keys come from the
/// per-provider lists and rotate (sticky) when a request reports a rate limit.
/// </summary>
public sealed class SettingsService :
    IApiKeySource, IProviderEndpoints, IChatGptWebOptions, ILlmApiOptions, IBedrockOptions, IVertexOptions
{
    private readonly ISettingsStore _store;
    private readonly KeyRing _keyRing;
    private readonly ChatGptConversationStore? _chatGptState;

    public SettingsService(ISettingsStore store, string? browserStateRoot = null)
    {
        _store = store;
        Current = store.Load();
        _keyRing = new KeyRing(providerId => Current.GetApiKeys(providerId));
        if (!string.IsNullOrWhiteSpace(browserStateRoot))
            _chatGptState = new ChatGptConversationStore(Path.Combine(browserStateRoot, "chatgpt-state"));
    }

    public AppSettings Current { get; }

    public IReadOnlyList<ModelInfo> Models =>
        ModelCatalog.WithCustom(Current.CustomModels, Current.ModelContextOverrides);

    public event EventHandler? SettingsSaved;

    public void Save()
    {
        // Whether this is reached at all is the first question a settings edit that never
        // lands on disk raises, and the caller is the second. The signature is deliberately
        // untouched — the CLI passes this method as a method group, which caller attributes
        // would break — so the frame is read instead.
        JarvisCode.Core.Utilities.DiagnosticLog.Write($"settings: Save() from {Caller()}");
        _store.Save(Current);
        // Edited key lists invalidate the rotation position.
        _keyRing.Reset();
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Who asked for the save: a name for the log, never an argument or a value.</summary>
    private static string Caller()
    {
        var trace = new System.Diagnostics.StackTrace(2, false);
        var frame = trace.FrameCount > 0 ? trace.GetFrame(0) : null;
        return frame?.GetMethod() is { } method
            ? $"{method.DeclaringType?.Name}.{method.Name}"
            : "unknown";
    }

    public string? GetKey(string providerId) => _keyRing.Current(providerId);

    public string LlmApiProtocolName => Current.LlmApiProtocolName;

    public string BedrockRegion => Current.BedrockRegion;

    public string BedrockAccessKeyId => Current.BedrockAccessKeyId;

    public string VertexProjectId => Current.VertexProjectId;

    public string VertexRegion => Current.VertexRegion;

    public string ChatGptProjectName => Current.ChatGptProjectName;

    public int ChatGptRotateAfterMessages => Current.ChatGptRotateAfterMessages;

    public string ChatGptProjectId => Current.ChatGptProjectId;

    public void SaveChatGptProjectId(string projectId)
    {
        Current.ChatGptProjectId = projectId;
        Save();
    }

    public IReadOnlyList<ChatGptChatEntry> ChatGptChats => Current.ChatGptChats;

    public ValueTask<IAsyncDisposable?> AcquireChatGptScopeAsync(string scopeKey, CancellationToken cancellationToken) =>
        _chatGptState?.AcquireScopeAsync(scopeKey, cancellationToken)
        ?? ValueTask.FromResult<IAsyncDisposable?>(null);

    public ChatGptChatEntry? ReadChatGptChat(string scopeKey) => _chatGptState is { } state
        ? state.Read(scopeKey, Current.ChatGptChats)
        : Current.ChatGptChats.Where(entry => entry.ScopeKey == scopeKey)
            .OrderByDescending(entry => entry.UpdatedAt).FirstOrDefault();

    public void SaveChatGptChat(string scopeKey, ChatGptChatEntry? chat)
    {
        if (_chatGptState is { } state)
        {
            state.Save(scopeKey, chat);
            return;
        }
        SaveChatGptChats([.. Current.ChatGptChats.Where(entry => entry.ScopeKey != scopeKey),
            .. chat is null ? [] : new[] { chat }]);
    }

    public string GetChatGptProjectId(string projectScopeKey) =>
        _chatGptState?.ReadProjectId(projectScopeKey) ?? Current.ChatGptProjectId;

    public void SaveChatGptProjectId(string projectScopeKey, string projectId)
    {
        if (_chatGptState is { } state)
        {
            state.SaveProjectId(projectScopeKey, projectId);
            return;
        }
        SaveChatGptProjectId(projectId);
    }

    /// <summary>
    /// Written straight to the store rather than through <see cref="Save"/>: this lands on every
    /// message of a ChatGPT session, and Save resets key rotation for every provider and rebuilds
    /// the provider list — a chat id has no business doing either, and putting a rate-limited key
    /// back in rotation because another session sent a message is a failure nobody could explain.
    /// </summary>
    public void SaveChatGptChats(IReadOnlyList<ChatGptChatEntry> chats)
    {
        Current.ChatGptChats = [.. chats];
        _store.Save(Current);
    }

    public bool TryRotate(string providerId, string rateLimitedKey) =>
        _keyRing.RotateIfCurrent(providerId, rateLimitedKey);

    public string? GetBaseUrl(string providerId) => providerId.ToLowerInvariant() switch
    {
        "ollama" => Current.OllamaBaseUrl,
        "nvidia" => Current.NvidiaBaseUrl,
        "llmapi" => Current.LlmApiBaseUrl,
        "openrouter" => Current.OpenRouterBaseUrl,
        "tokenrouter" => Current.TokenRouterBaseUrl,
        "deepseek" => Current.DeepSeekBaseUrl,
        "zhipu" => Current.ZhipuBaseUrl,
        "minimax" => Current.MiniMaxBaseUrl,
        // A hand-written provider carries its own URL, so an edit applies to the next
        // request rather than waiting for the registry to be rebuilt.
        var id => Current.CustomProviders
            .FirstOrDefault(spec => Core.Settings.CustomProviders.NormalizeId(spec.Id) == id)?.BaseUrl,
    };
}
