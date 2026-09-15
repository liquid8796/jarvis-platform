namespace JarvisCode.Providers.ChatGptWeb;

/// <summary>One raw ChatGPT backend response: status plus the whole body, unparsed.</summary>
public sealed record ChatGptResponse(int Status, string Body)
{
    /// <summary>Transport-level failures (timeout, DNS, TLS, a wedged browser) report this status.</summary>
    public const int TransportFailure = -1;

    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>The cookies handed to a transport for one session, in both shapes a transport may need.</summary>
public sealed record ChatGptCookieContext(string Header, string InjectionJson)
{
    /// <summary>The local conversation whose browser page must be isolated from other sessions.</summary>
    public string ScopeId { get; init; } = "";
}

/// <summary>
/// The text of one turn, any file the model produced while writing it, and whether the turn ran
/// ChatGPT's own tools — its sandbox container, its code interpreter, its connectors. That last one
/// is the difference between an answer and a claim: work done there happened on OpenAI's machines
/// and is not evidence of work in the local project. All sessions reject that turn even when it
/// also contains local action lines or no Jarvis tools were attached.
/// </summary>
public sealed record ChatGptTurn(string Text, IReadOnlyList<string> Assets, bool UsedOwnTools = false);

/// <summary>One picture riding with a message: what it is, and its bytes as base64.</summary>
public sealed record ChatGptImage(string MediaType, string Base64);

/// <summary>One live choice exposed by ChatGPT's own composer.</summary>
public sealed record ChatGptPickerOption(string Key, string Label, bool Selected = false);

/// <summary>
/// The controls currently rendered by ChatGPT. They deliberately carry the page's labels instead
/// of a locally maintained catalog, so a newly rolled-out model or Power rung works without an app
/// update.
/// </summary>
public sealed record ChatGptComposerControls(
    string CurrentModelLabel,
    IReadOnlyList<ChatGptPickerOption> Models,
    IReadOnlyList<ChatGptPickerOption> Efforts);

/// <summary>
/// One send: the text, the pictures that go with it, and where it lands.
/// </summary>
/// <param name="ModelLabel">
/// The model as ChatGPT's own picker labels it ("GPT-5.6 Sol"), because the picker offers no slug
/// to select by; null leaves the account's own choice alone.
/// </param>
/// <param name="GizmoId">The project a new chat is created in, or null for none.</param>
/// <param name="ConversationId">The chat to continue, or null to start one.</param>
/// <param name="Images">
/// Attached ahead of the text. A transport that answers false to
/// <see cref="IChatGptTransport.CarriesImages"/> is never given any.
/// </param>
/// <param name="ImagesOmittedNote">
/// What to add to the message when the page refuses the attachments. The model is told the
/// pictures are missing rather than left answering about a screenshot it never saw.
/// </param>
public sealed record ChatGptAsk(
    string Prompt,
    string? ModelLabel,
    string? GizmoId,
    string? ConversationId,
    IReadOnlyList<ChatGptImage> Images,
    string ImagesOmittedNote)
{
    public ChatGptAsk(string prompt, string? modelLabel, string? gizmoId, string? conversationId)
        : this(prompt, modelLabel, gizmoId, conversationId, [], "")
    {
    }

    /// <summary>
    /// Where the page's own working text is reported while the answer is written — the whole of it
    /// each time, not the change. A thinking model can spend minutes with nothing on the page but
    /// this, which is the difference between a session that looks stuck and one that looks busy.
    /// Null asks for none of it.
    /// </summary>
    public Action<string>? Progress { get; init; }

    /// <summary>
    /// A display-only snapshot of the answer currently rendered on the page. It is not the
    /// authoritative markdown and must never be persisted or parsed into executable actions.
    /// </summary>
    public Action<string>? AnswerPreview { get; init; }

    /// <summary>Monitor this send for work routed outside Jarvis, including tool-less requests.</summary>
    public bool EnforceLocalExecution { get; init; }

    /// <summary>The exact active-branch user-message count expected after this send.</summary>
    public int ExpectedUserMessageCount { get; init; }

    /// <summary>
    /// Supplies the existing authentication token only for the fixed-origin provenance read.
    /// A delegate keeps credentials out of the record's generated diagnostic string.
    /// </summary>
    public Func<string>? ProvenanceAccessToken { get; init; }

    /// <summary>
    /// Values selected from the page's live composer controls. Empty keeps the pre-controls send
    /// path byte-for-byte unchanged; transports that do not expose such controls simply ignore it.
    /// </summary>
    public IReadOnlyDictionary<string, string> ComposerOptions { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What driving the ChatGPT page produced: the answer, and the chat it landed in.</summary>
public sealed record ChatGptUiReply(string Text, string? ConversationId, string? Error)
{
    /// <summary>Native work was observed; automatic fallback/resend must not replay this turn.</summary>
    public bool NativeToolViolation { get; init; }

    /// <summary>A required project route disappeared before send; its cached id must be re-resolved.</summary>
    public bool ProjectUnavailable { get; init; }

    public static ChatGptUiReply Failed(string error) => new("", null, error);
}

/// <summary>
/// How the ChatGPT web provider reaches the backend. Implementations never throw for ordinary
/// failures — every outcome comes back as a <see cref="ChatGptResponse"/>.
/// </summary>
public interface IChatGptTransport
{
    /// <summary>
    /// Sends one call to a backend path (e.g. <c>/backend-api/conversation</c>). An SSE response is
    /// buffered whole into <see cref="ChatGptResponse.Body"/>.
    /// </summary>
    Task<ChatGptResponse> SendAsync(
        HttpMethod method,
        string path,
        string? jsonBody,
        string? bearerToken,
        string accept,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks by driving the ChatGPT page itself — typing into the composer and reading the answer
    /// back — rather than by calling the message endpoint.
    ///
    /// That endpoint is gated by a Cloudflare Turnstile token which only ChatGPT's own client can
    /// produce; letting its client send the message means this app never touches that check.
    /// </summary>
    Task<ChatGptUiReply> AskAsync(ChatGptAsk ask, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this transport can attach pictures to a message. False — the default — means the
    /// caller says in the text that they were dropped instead of leaving the model blind to them.
    /// </summary>
    bool CarriesImages => false;

    /// <summary>
    /// The models this account's own composer picker offers, by the label it shows. Empty when the
    /// transport cannot read them, which is not an error: the user can still type an id by hand.
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Reads the model and effort controls directly from the active composer. The compatibility
    /// default promotes the older model-label API and leaves unsupported effort controls empty.
    /// </summary>
    async Task<ChatGptComposerControls> ReadComposerControlsAsync(CancellationToken cancellationToken)
    {
        var models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return new ChatGptComposerControls(
            "",
            [.. models.Select(static label => new ChatGptPickerOption(label, label))],
            []);
    }

    /// <summary>
    /// Reads controls after selecting <paramref name="modelLabel"/>. The default preserves
    /// transports that can list controls but cannot actively switch a page before discovery.
    /// </summary>
    Task<ChatGptComposerControls> ReadComposerControlsAsync(
        string? modelLabel,
        CancellationToken cancellationToken) => ReadComposerControlsAsync(cancellationToken);

    /// <summary>Reads composer choices, optionally skipping the physical Power sweep.</summary>
    async Task<ChatGptComposerControls> ReadComposerControlsAsync(
        string? modelLabel,
        bool includeEfforts,
        CancellationToken cancellationToken)
    {
        var controls = await ReadComposerControlsAsync(modelLabel, cancellationToken).ConfigureAwait(false);
        return includeEfforts ? controls : controls with { Efforts = [] };
    }

    /// <summary>
    /// Saves a file the conversation points at — an image the model drew — and returns where it
    /// landed. Null when this transport cannot carry bytes, which leaves the answer without the
    /// file rather than losing the turn over it.
    /// </summary>
    Task<string?> SaveAssetAsync(string assetId, string bearer, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// Where the App registers its browser-backed transport.
///
/// ChatGPT's origin sits behind a Cloudflare managed challenge that is bound to the caller's TLS
/// fingerprint, so a plain <see cref="HttpClient"/> is turned away even with valid cookies. The
/// transport that works runs each call as a fetch inside an embedded browser, which needs the engine
/// and therefore cannot live in this platform-neutral project — the App registers it at startup
/// and the provider asks here for it, falling back to <see cref="ChatGptHttpTransport"/> (which
/// reports that limitation rather than failing obscurely).
/// </summary>
public static class ChatGptTransportRegistry
{
    /// <summary>
    /// How long a transport is given for one call. One value because a transport is shared: the
    /// browser is built once for a cookie set and keeps whatever timeout it was first asked for, so
    /// a caller in a hurry — the settings page reading the model picker — would otherwise leave
    /// every later turn on its own short deadline.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    private static readonly Lock Sync = new();
    private static Func<ChatGptCookieContext, TimeSpan, IChatGptTransport>? _factory;

    /// <summary>True once the App has registered its browser transport.</summary>
    public static bool HasBrowserTransport
    {
        get
        {
            lock (Sync)
            {
                return _factory is not null;
            }
        }
    }

    public static void Register(Func<ChatGptCookieContext, TimeSpan, IChatGptTransport> factory)
    {
        lock (Sync)
        {
            _factory = factory;
        }
    }

    /// <summary>Only for tests, which must not inherit another test's transport.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _factory = null;
        }
    }

    public static IChatGptTransport Create(ChatGptCookieContext cookies, TimeSpan timeout)
    {
        Func<ChatGptCookieContext, TimeSpan, IChatGptTransport>? factory;
        lock (Sync)
        {
            factory = _factory;
        }

        return factory?.Invoke(cookies, timeout) ?? new ChatGptHttpTransport(cookies.Header, timeout);
    }
}
