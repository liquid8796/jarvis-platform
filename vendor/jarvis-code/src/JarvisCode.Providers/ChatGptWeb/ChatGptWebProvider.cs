using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Core.Utilities;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.ChatGptWeb;

/// <summary>
/// Talks to ChatGPT with the user's own browser session instead of an API key.
///
/// The message itself is not sent by calling the backend: that endpoint now requires a Cloudflare
/// Turnstile token that only ChatGPT's own client can mint, so the transport drives the real page —
/// types the prompt, presses send, reads the answer — and ChatGPT's client deals with its own
/// checks. Everything around the message is plain authenticated API: finding or creating the
/// project, naming the new chat, and deleting a chat once it has taken enough messages.
///
/// The page takes a message and gives back an answer, so tool calling is carried in the message
/// itself (see <see cref="ChatGptToolProtocol"/>) and pictures ride the composer's attachments.
/// </summary>
public sealed class ChatGptWebProvider(IApiKeySource cookieSource, IChatGptWebOptions options) : ILlmProvider, IProviderCapabilities
{
    public const string ProviderId = "chatgpt-web";

    /// <summary>Slug that lets the account pick whichever model it is entitled to.</summary>
    public const string AutoModelSlug = "auto";

    /// <summary>Namespace for model ids discovered from ChatGPT's live picker.</summary>
    public const string DynamicModelPrefix = ProviderId + ":";

    /// <summary>Provider-option key for the numeric index of ChatGPT's Power slider.</summary>
    public const string PowerOptionKey = "power";

    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    /// <summary>How much of an answer an error quotes back.</summary>
    private const int ExcerptChars = 200;

    private const int MaxActionCorrections = 2;

    /// <summary>The project control plane must never inherit the twenty-minute answer deadline.</summary>
    private static readonly TimeSpan ProjectSetupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long an interrupted create is reconciled without risking a duplicate POST.</summary>
    private static readonly TimeSpan PendingProjectRetryDelay = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PendingProjectClockSkew = TimeSpan.FromMinutes(1);

    private const string PendingProjectPrefix = "pending:";

    /// <summary>A corrupt cursor must not turn project setup into an unbounded walk.</summary>
    private const int MaxProjectPages = 100;

    private const string ProjectListPath =
        "/backend-api/gizmos/snorlax/sidebar?owned_only=true&conversations_per_gizmo=0&limit=50";

    /// <summary>
    /// How many times the stored conversation is asked for the turn before the page's own copy is
    /// taken instead, and how long to leave between asking.
    /// </summary>
    private const int StoreCatchUpAttempts = 4;

    private static readonly TimeSpan StoreCatchUpDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>Shared with every other caller of the transport, which is one browser.</summary>
    private static readonly TimeSpan RequestTimeout = ChatGptTransportRegistry.DefaultTimeout;
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Compatibility aliases for model ids saved before live picker discovery existed. Newly
    /// discovered models carry a namespaced picker label and never need an entry here.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyModelLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5-6-sol"] = "GPT-5.6 Sol",
        ["gpt-5-5"] = "GPT-5.5",
        // Kept for conversations saved before Astra moved behind ChatGPT's "Latest" row.
        ["gpt-6-astra"] = "Latest",
    };

    private readonly Lock _sync = new();
    private readonly Lock _persistenceSync = new();
    private readonly Dictionary<string, ScopeGate> _scopeGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _projectGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _verifiedProjectIds = new(StringComparer.Ordinal);
    private string? _accessToken;
    private string _accessTokenOwner = "";
    private DateTimeOffset _accessTokenExpiry;

    public string Id => ProviderId;

    public string DisplayName => "ChatGPT (browser session)";

    public ProviderCapabilities Capabilities { get; } = new(false, false, false, false)
    {
        SupportsPostToolStallRetry = false,
        SupportsToolFreeInference = false,
    };

    /// <summary>The picker label for a model id, or null for the account's own current choice.</summary>
    internal static string? PickerLabel(string? modelId)
    {
        if (modelId is null or "" || modelId.Equals(AutoModelSlug, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = modelId.StartsWith(DynamicModelPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[DynamicModelPrefix.Length..]
            : modelId;
        return LegacyModelLabels.TryGetValue(id, out var label) ? label : id;
    }

    /// <summary>
    /// Reads the controls that this account is actually entitled to from its ChatGPT composer.
    /// Model keys are namespaced for the local catalog; labels stay exactly as the page renders
    /// them so they can be selected again without maintaining a second model list here.
    /// </summary>
    public Task<ChatGptComposerControls> ReadComposerControlsAsync(
        string? scopeId,
        CancellationToken cancellationToken) =>
        ReadComposerControlsAsync(
            scopeId, modelId: null, includeEfforts: true, cancellationToken: cancellationToken);

    /// <summary>Reads the live controls after selecting the requested local model.</summary>
    public Task<ChatGptComposerControls> ReadComposerControlsAsync(
        string? scopeId,
        string? modelId,
        CancellationToken cancellationToken) =>
        ReadComposerControlsAsync(
            scopeId, modelId, includeEfforts: true, cancellationToken: cancellationToken);

    public async Task<ChatGptComposerControls> ReadComposerControlsAsync(
        string? scopeId,
        string? modelId,
        bool includeEfforts,
        CancellationToken cancellationToken)
    {
        var jar = ChatGptCookieJar.Parse(cookieSource.GetKey(ProviderId));
        if (!jar.IsUsable)
        {
            throw new ProviderException(
                "ChatGPT browser session: failed to authenticate — no usable cookies. Open Settings › "
                + "Providers › ChatGPT and import a cookie export from a signed-in chatgpt.com tab "
                + $"({jar.Describe()})");
        }

        var transport = ChatGptTransportRegistry.Create(
            new ChatGptCookieContext(jar.ToHeader(), jar.ToInjectionJson()) { ScopeId = scopeId ?? "" },
            RequestTimeout);
        var controls = await transport.ReadComposerControlsAsync(
                PickerLabel(modelId), includeEfforts, cancellationToken)
            .ConfigureAwait(false);
        return controls with
        {
            Models =
            [
                .. controls.Models.Select(static option => option with
                {
                    Key = option.Key.StartsWith(DynamicModelPrefix, StringComparison.OrdinalIgnoreCase)
                        ? option.Key
                        : DynamicModelPrefix + option.Key,
                }),
            ],
        };
    }

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var jar = ChatGptCookieJar.Parse(cookieSource.GetKey(ProviderId));
        if (!jar.IsUsable)
        {
            throw new ProviderException(
                "ChatGPT browser session: failed to authenticate — no usable cookies. Open Settings › "
                + "Providers › ChatGPT and import a cookie export from a signed-in chatgpt.com tab "
                + $"({jar.Describe()})");
        }

        var account = CookieFingerprint(jar);
        // Callers without a session identity are isolated too. They may make a one-shot request,
        // but cannot safely resume a chat by coincidentally sharing another caller's transcript.
        var scope = string.IsNullOrWhiteSpace(request.ConversationScopeId)
            ? $"ephemeral:{Guid.NewGuid():N}"
            : request.ConversationScopeId;
        var scopeKey = HashParts("chatgpt-scope-v2", account, scope);
        var gate = RentScopeGate(scopeKey);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await using var scopeLease = string.IsNullOrWhiteSpace(request.ConversationScopeId)
                ? null : await options.AcquireChatGptScopeAsync(scopeKey, cancellationToken).ConfigureAwait(false);
            await foreach (var providerEvent in StreamScopedAsync(
                request, jar, account, scope, scopeKey, cancellationToken).ConfigureAwait(false))
            {
                yield return providerEvent;
            }
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReturnScopeGate(scopeKey, gate);
        }
    }

    private async IAsyncEnumerable<ProviderEvent> StreamScopedAsync(
        LlmRequest request,
        ChatGptCookieJar jar,
        string account,
        string scope,
        string scopeKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var transport = ChatGptTransportRegistry.Create(
            new ChatGptCookieContext(jar.ToHeader(), jar.ToInjectionJson()) { ScopeId = scope }, RequestTimeout);
        var projectName = (options.ChatGptProjectName ?? "").Trim();
        var projectScopeKey = HashParts("chatgpt-project-v2", account, projectName);
        var (bearer, gizmoId) = await PrepareProjectRouteAsync(
                transport, account, projectName, projectScopeKey, cancellationToken)
            .ConfigureAwait(false);
        if (projectName.Length > 0 && gizmoId is not { Length: > 0 })
        {
            throw new ProviderException(
                $"ChatGPT project \"{projectName}\" was not resolved. Nothing was sent.");
        }

        // A contract change, project change, rewind or fork opens a new remote context. Matching
        // names alone misses changed schemas and removed tools, so the complete contract is hashed.
        var contractHash = ToolContractHash(request);
        var identity = HashParts("chatgpt-history-v2", scopeKey, projectName,
            gizmoId ?? "", request.ModelId, FullSystemPrompt(request), contractHash);
        var key = HistoryKey(identity, HistoryEntries(request, precedingOnly: true));
        var chat = Resume(scopeKey, key);

        // The remote state stops being resumable before the first operation that can advance or
        // delete it. Cancellation and errors therefore recover by replaying the local transcript.
        if (!string.IsNullOrWhiteSpace(request.ConversationScopeId)) ForgetScope(scopeKey);

        if (chat is not null && chat.SentMessages >= options.ResolveRotateAfterMessages())
        {
            // The chat has taken its share of messages: it goes, and the next one starts clean.
            await DeleteChatAsync(transport, bearer, chat.ConversationId, cancellationToken).ConfigureAwait(false);
            chat = null;
        }

        // A fresh chat has none of the history, so it is given all of it; a continuing one only
        // needs what is new — plus any action the session has gained since the contract was sent.
        var prompt = chat is null
            ? FlattenHistory(request)
            : Continuation(request);

        // Pictures cross as attachments on the composer, not as text. What the channel cannot take
        // is said in the message: a screenshot silently dropped leaves the model answering about
        // something it never saw.
        var images = TurnImages(request, includeHistory: chat is null);
        var carried = transport.CarriesImages
            ? images.TakeLast(ChatGptToolProtocol.MaxImages).ToList()
            : [];
        if (images.Count > carried.Count)
        {
            prompt += "\n\n" + ChatGptToolProtocol.ImagesOmitted(images.Count - carried.Count);
        }

        // The page's own working text — the thinking, the searching — is reported while the answer
        // is written, so a turn that spends four minutes thinking says so instead of standing
        // still. It is shown and not kept: it is ChatGPT's summary of its reasoning, not the
        // reasoning, and the answer itself is read from the stored conversation further down.
        var conversationId = chat?.ConversationId;
        var sentMessages = chat?.SentMessages ?? 0;
        var correctionAttempts = 0;
        var correctionUsage = Usage.Zero;
        long correctionContextTokens = 0;
        while (true)
        {
            var progress = Channel.CreateUnbounded<(string Text, bool Preview)>();
            var ask = new ChatGptAsk(
                prompt,
                PickerLabel(request.ModelId),
                gizmoId,
                conversationId,
                correctionAttempts == 0 ? [.. carried.Select(image => new ChatGptImage(image.MediaType, image.Base64Data))] : [],
                correctionAttempts == 0 ? ChatGptToolProtocol.ImagesOmitted(carried.Count) : "")
            {
                Progress = text => progress.Writer.TryWrite((text, false)),
                // Every web response is untrusted until stored provenance proves its execution
                // route, including requests with no local tools. Never preview rejected work.
                AnswerPreview = null,
                EnforceLocalExecution = true,
                ExpectedUserMessageCount = sentMessages + 1,
                ProvenanceAccessToken = () => bearer,
                // Auto deliberately follows whatever each browser page currently uses. A persisted
                // Power key has no model-stable meaning there and must not leak in from old/manual state.
                ComposerOptions = request.ModelId.Equals(AutoModelSlug, StringComparison.OrdinalIgnoreCase)
                    ? new Dictionary<string, string>()
                    : request.ProviderOptions,
            };

            cancellationToken.ThrowIfCancellationRequested();
            using var askingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var asking = AskWithoutAutomaticReplayAsync(transport, ask, askingCancellation.Token);
            await using var pendingAsk = new PendingAsk(asking, askingCancellation);
            _ = asking.ContinueWith(
                finished =>
                {
                    // Complete the progress reader on every outcome, including when the consumer
                    // abandons it. PendingAsk also waits for browser cleanup before releasing a scope.
                    _ = finished.Exception;
                    progress.Writer.TryComplete();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var reported = "";
            await foreach (var update in progress.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var text = update.Text;
                if (update.Preview)
                {
                    if (ChatGptToolProtocol.CanStreamProse(text))
                        yield return new TextPreviewEvent(text);
                    continue;
                }
                var delta = ProgressDelta(reported, text);
                reported = text;
                if (delta.Length > 0)
                {
                    yield return new ThinkingDeltaEvent(0, delta);
                }
            }

            var reply = await asking.ConfigureAwait(false);

            if (reply.Error is { Length: > 0 } error)
            {
                if (reply.ProjectUnavailable)
                {
                    _verifiedProjectIds.TryRemove(projectScopeKey, out _);
                }

                // An Ask failure does not prove the remote turn never started. Cleanup may also
                // replace a native-violation error, so no browser Ask failure is an automatic replay.
                throw new ProviderException(Classified(error)) { CanRetry = false };
            }

            if (correctionAttempts > 0 && !string.Equals(conversationId, reply.ConversationId, StringComparison.Ordinal))
                throw new ProviderException("ChatGPT changed conversations while correcting an action batch. No actions were run.") { CanRetry = false };

            if (conversationId is null && reply.ConversationId is { Length: > 0 } fresh)
            {
                await NameChatAsync(transport, bearer, fresh, cancellationToken).ConfigureAwait(false);
            }
            conversationId = reply.ConversationId;
            sentMessages++;

            // What the page shows is markdown that has already been rendered: escapes are eaten and
            // runs of spaces collapse, which quietly ruins any JSON or code the reply carries. The
            // conversation itself still holds what the model wrote, so that is what gets parsed — and
            // it is asked first, because a turn that ends on a card the page is waiting to have clicked
            // leaves its answer in the conversation and nothing on the page to read. One more message
            // than the chat has taken before is what this turn should have left in it.
            var turn = await ExactAnswerAsync(
                    transport, bearer, reply, sentMessages, cancellationToken)
                .ConfigureAwait(false);

            if (turn is null)
            {
                throw new ProviderException(
                    "ChatGPT's stored response could not be verified for this session. No actions or "
                    + "files from the displayed response were accepted. Retry this step; if it keeps "
                    + "happening, open the ChatGPT window to check the session before trying again.") { CanRetry = false };
            }

            // No rendered-text fallback: even a tool-less request can trigger ChatGPT's native
            // runtime. An image-only stored turn also cannot borrow unverified page text.
            var answer = turn.Text;

            // Enforce the workspace boundary before downloading files, dispatching even a mixed batch
            // of local actions, or accepting the response into resumable history. Earlier local steps
            // may have changed the workspace, so do not claim it is untouched; this remote turn is the
            // thing we cannot accept as local work. Only known native search/image routes are
            // exceptions; unknown native tool families fail closed rather than silently widening it.
            if (turn.UsedOwnTools)
            {
                throw new ProviderException(
                    "ChatGPT used its own sandbox or connectors instead of this session's Jarvis tools. "
                    + "This response was rejected: no actions or files from it were accepted as work in "
                    + "your local project. Earlier Jarvis tool results are unchanged. Retry this step "
                    + "using the session's tools. "
                    + $"It answered: {Excerpt(answer)}") { CanRetry = false };
            }

            // Validate the entire candidate before any assets or local actions are accepted. A model
            // may fix its own malformed JSON, but the bridge must never guess at braces or arguments.
            ChatGptReplyContent? content = null;
            ProviderException? actionError = null;
            try { content = request.Tools.Count > 0 ? ChatGptToolProtocol.Parse(answer) : new ChatGptReplyContent(answer, []); }
            catch (ProviderException ex) { actionError = ex; }
            if (actionError is not null || (correctionAttempts > 0 && content!.Actions.Count == 0))
            {
                if (request.Tools.Count == 0 || turn is null || string.IsNullOrEmpty(conversationId))
                    throw new ProviderException(actionError?.Message
                        ?? "ChatGPT did not return a corrected action batch. No actions were run.", actionError) { CanRetry = false };
                if (correctionAttempts >= MaxActionCorrections)
                    throw new ProviderException(
                        "ChatGPT could not correct its action batch after two format retries. No actions were run.", actionError) { CanRetry = false };

                cancellationToken.ThrowIfCancellationRequested();
                correctionAttempts++;
                DiagnosticLog.Write($"chatgpt: correcting rejected action format ({correctionAttempts}/{MaxActionCorrections}); no actions dispatched");
                yield return new ThinkingDeltaEvent(0,
                    $"\nCorrecting ChatGPT action format ({correctionAttempts}/{MaxActionCorrections}); no actions from this batch have run.\n");
                // The rejected response already exists in this remote conversation. Do not replay
                // user images, expose its payload as new instructions, or invent a successful RESULT.
                var rejectedUsage = EstimatedUsage(request, answer);
                correctionUsage = correctionUsage.Add(rejectedUsage with
                {
                    InputTokens = rejectedUsage.InputTokens + correctionContextTokens,
                });
                prompt = ChatGptToolProtocol.CorrectionPrompt();
                correctionContextTokens += rejectedUsage.OutputTokens + TokenEstimator.Estimate(prompt);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (turn is { Assets.Count: > 0 })
            {
                answer = await WithSavedAssetsAsync(transport, bearer, answer, turn.Assets, cancellationToken)
                    .ConfigureAwait(false);
                try { content = request.Tools.Count > 0 ? ChatGptToolProtocol.Parse(answer) : new ChatGptReplyContent(answer, []); }
                catch (ProviderException ex) { throw new ProviderException(ex.Message, ex) { CanRetry = false }; }
            }

            if (answer.Length == 0)
            {
                throw new ProviderException("ChatGPT answered with nothing. Open the ChatGPT window from Settings to see what it is showing.") { CanRetry = false };
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (content!.Text.Length > 0)
            {
                yield return new TextDeltaEvent(content.Text);
            }

            for (var i = 0; i < content.Actions.Count; i++)
            {
                var action = content.Actions[i];
                yield return new ToolCallStartedEvent(i, $"chatgpt_{Guid.NewGuid():N}", action.Name);
                yield return new ToolCallArgumentsDeltaEvent(i, action.ArgumentsJson);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(request.ConversationScopeId))
                Remember(request, RenderAssistant(content), conversationId, sentMessages,
                    scopeKey, identity, contractHash);
            var usage = EstimatedUsage(request, answer);
            yield return new ResponseCompletedEvent(
                content.Actions.Count > 0,
                correctionUsage.Add(usage with { InputTokens = usage.InputTokens + correctionContextTokens }),
                content.Actions.Count > 0 ? StopReasons.ToolUse : StopReasons.EndTurn);
            yield break;
        }
    }

    private static async Task<ChatGptUiReply> AskWithoutAutomaticReplayAsync(
        IChatGptTransport transport, ChatGptAsk ask, CancellationToken cancellationToken)
    {
        try { return await transport.AskAsync(ask, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A transport exception can occur after a send or while stopping native work. The
            // absence of a reply is not proof that replaying the request is safe.
            throw new ProviderException("ChatGPT browser request failed before its result could be verified.", ex)
            {
                CanRetry = false,
            };
        }
    }

    /// <summary>
    /// What the turn cost, as an estimate rather than a count: the web session reports no token
    /// figures at all, and zero is not a smaller lie than an estimate — it is the one figure that
    /// makes the context ring, the auto-compaction threshold and the token budget all read as an
    /// empty conversation however long it runs. The input side is the whole conversation the
    /// account now holds, which is what the next message is answered against.
    /// </summary>
    internal static Usage EstimatedUsage(LlmRequest request, string answer) => new(
        TokenEstimator.Estimate(FlattenHistory(request)),
        TokenEstimator.Estimate(answer)) { IsEstimated = true };

    /// <summary>
    /// What is new in the page's working text. It usually grows a character at a time, which is a
    /// suffix; a phase that replaces it outright ("Thinking…" becoming "Searched the web") is sent
    /// whole on its own line, since it is a new thing being said rather than more of the last one.
    /// </summary>
    internal static string ProgressDelta(string reported, string current) =>
        current.Length == 0 || current == reported ? ""
            : current.StartsWith(reported, StringComparison.Ordinal) ? current[reported.Length..]
            // Text that shrank back into what was already sent is the page re-measuring itself, not
            // the model saying something; repeating it would print the thinking twice.
            : reported.StartsWith(current, StringComparison.Ordinal) ? ""
            : (reported.Length == 0 ? current : "\n" + current);

    // ---- reading the answer back ---------------------------------------------------------------

    /// <summary>
    /// The turn as the model wrote it, read out of the stored conversation. Null when the answer
    /// cannot be trusted to be that turn — the caller then falls back to the rendered text, which
    /// is right for prose and only lossy for code.
    /// </summary>
    /// <summary>
    /// An image the model drew is not text and cannot ride this channel, so it is fetched to disk
    /// and the answer says where — the file is then a path like any other, which the shell can move
    /// wherever the user wants it. A file that will not come down is said so rather than dropped.
    /// </summary>
    private static async Task<string> WithSavedAssetsAsync(
        IChatGptTransport transport,
        string bearer,
        string answer,
        IReadOnlyList<string> assets,
        CancellationToken cancellationToken)
    {
        var lines = new StringBuilder(answer);
        foreach (var asset in assets)
        {
            var path = await transport.SaveAssetAsync(asset, bearer, cancellationToken).ConfigureAwait(false);
            if (lines.Length > 0)
            {
                lines.Append('\n');
            }

            lines.Append(path is { Length: > 0 }
                ? $"[file saved to {path}]"
                : "[the model produced a file this session could not download]");
        }

        return lines.ToString();
    }

    private static async Task<ChatGptTurn?> ExactAnswerAsync(
        IChatGptTransport transport,
        string bearer,
        ChatGptUiReply reply,
        int sentMessages,
        CancellationToken cancellationToken)
    {
        if (reply.ConversationId is not { Length: > 0 } conversationId)
        {
            Fallback("the reply carried no conversation id");
            return null;
        }

        // The page finishes before the store does: the message that prompted the turn is there
        // while the answer to it is still an empty shell, and reading that once and giving up hands
        // back the page's mangled copy instead. Asking again a moment later is what it takes.
        for (var attempt = 1; ; attempt++)
        {
            var response = await transport.SendAsync(
                HttpMethod.Get,
                $"/backend-api/conversation/{conversationId}",
                null,
                bearer,
                "application/json",
                null,
                cancellationToken).ConfigureAwait(false);

            // Only a conversation that reads is worth asking again for: anything else — a refusal,
            // a body that is not one — will not become the turn by waiting.
            if (!response.IsSuccess)
            {
                Fallback($"the conversation read answered HTTP {response.Status}");
                return null;
            }

            if (!IsConversation(response.Body))
            {
                Fallback($"the body was not a conversation ({response.Body.Length} chars)");
                return null;
            }

            if (ExactAnswer(response.Body, sentMessages) is { } turn)
            {
                DiagnosticLog.Write(
                    $"chatgpt: turn read from the conversation on try {attempt} "
                    + $"({turn.Text.Length} chars, {turn.Assets.Count} file(s))");
                return turn;
            }

            if (attempt >= StoreCatchUpAttempts)
            {
                Fallback($"the conversation still held no answer after {attempt} tries");
                return null;
            }

            await Task.Delay(StoreCatchUpDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Why the stored turn could not be verified. No response is accepted from rendered-page
    /// fallback, because that copy has no execution provenance. Metadata only.
    /// </summary>
    private static void Fallback(string reason) =>
        DiagnosticLog.Write($"chatgpt: stored response unavailable — {reason}");

    /// <summary>A body that is a conversation, whether or not the turn has landed in it yet.</summary>
    internal static bool IsConversation(string json) =>
        ParseJson(json) is JsonObject conversation &&
        conversation["mapping"] is JsonObject &&
        conversation["current_node"] is not null;

    /// <summary>
    /// The newest turn's assistant text, walking the conversation tree back from the node it ends
    /// on. Modern turns separate progress from the final answer by channel. Only final text is
    /// executable output; older turns without channel metadata retain their original text order.
    ///
    /// The store can lag a moment behind the page, and reading the turn before this one would
    /// replay a tool call that has already run — so the conversation has to hold as many messages
    /// as have been sent into it. Counting them is the only honest way to ask: the composer is a
    /// markdown editor and rewrites what it is given, escaping underscores and turning a URL into a
    /// link, so what comes back is not what went in and comparing the text says nothing.
    /// </summary>
    internal static ChatGptTurn? ExactAnswer(string conversationJson, int sentMessages)
    {
        if (ParseJson(conversationJson) is not JsonObject conversation ||
            conversation["mapping"] is not JsonObject mapping ||
            conversation["current_node"].AsText() is not { Length: > 0 } node)
        {
            return null;
        }

        var stored = ActiveUserMessageCount(mapping, node);
        if (stored < 0)
        {
            Fallback("the active conversation branch was incomplete or cyclic");
            return null;
        }
        if (stored != sentMessages)
        {
            Fallback($"the conversation holds {stored} of the {sentMessages} messages sent into it");
            return null;
        }

        var legacyParts = new List<string>();
        var finalParts = new List<string>();
        var hasChannels = false;
        var unfinishedFinal = false;
        var assets = new List<string>();
        var usedOwnTools = false;
        // A single reasoning turn can contain many tool/status messages. An arbitrary short walk
        // limit silently discarded the tool provenance and fell back to rendered success. The
        // visited set bounds malformed/cyclic data without truncating a legitimate long turn.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(node))
        {
            if (mapping[node] is not JsonObject entry)
            {
                return null;
            }

            if (entry["message"] is JsonObject message)
            {
                var role = message["author"]?["role"].AsText();
                if (role == UserRole)
                {
                    return unfinishedFinal ? null
                        : Turn(Joined(hasChannels ? finalParts : legacyParts), Assets(assets), usedOwnTools);
                }

                usedOwnTools |= AddressesOwnWorkTool(message);

                if (role == AssistantRole && message["recipient"].AsText() is null or "" or "all")
                {
                    var channel = message["channel"].AsText();
                    hasChannels |= !string.IsNullOrEmpty(channel);
                    // The store can contain a nonempty partial final after the rendered page
                    // finishes. Wait for the committed response before parsing or correcting it.
                    if (channel == "final" && message["status"].AsText() is { Length: > 0 } status
                        && status != "finished_successfully") unfinishedFinal = true;
                    if (message["content"]?["content_type"].AsText() == "text")
                    {
                        // Concatenating a progress update with a final action turns a valid
                        // envelope into prose. Keep channel boundaries, not marker heuristics.
                        if (channel == "final") finalParts.Add(MessageText(message));
                        else if (string.IsNullOrEmpty(channel)) legacyParts.Add(MessageText(message));
                    }
                }

                // Drawing an image leaves the picture on a tool message and nothing on the
                // assistant's own, which is why a turn that made one used to read as empty.
                CollectAssets(message, assets);

            }

            if (entry["parent"].AsText() is not { Length: > 0 } parent)
            {
                // The root: everything walked was this turn, which is what a first message looks
                // like only when the prompt itself is missing, so there is nothing to trust here.
                return null;
            }

            node = parent;
        }

        return null;
    }

    /// <summary>
    /// Only the active branch counts. Edited messages on abandoned branches cannot prove the
    /// newest send landed, and a remote branch ahead of the local transcript is not resumable.
    /// </summary>
    private static int ActiveUserMessageCount(JsonObject mapping, string node)
    {
        var count = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(node) || mapping[node] is not JsonObject entry) return -1;
            if (entry["message"]?["author"]?["role"].AsText() == UserRole) count++;
            if (entry["parent"] is null) return count;
            if (entry["parent"] is not JsonValue parentValue
                || !parentValue.TryGetValue<string>(out var parent)) return -1;
            if (parent.Length == 0) return count;
            node = parent;
        }
    }

    /// <summary>
    /// Keep tool provenance even when a connector card or empty sandbox reply left no final
    /// answer. Otherwise rendered-page fallback would erase the very evidence the guard needs.
    /// </summary>
    private static ChatGptTurn? Turn(string? text, List<string> assets, bool usedOwnTools) =>
        text is { Length: > 0 } || assets.Count > 0 || usedOwnTools
            ? new ChatGptTurn(text ?? "", assets, usedOwnTools)
            : null;

    /// <summary>
    /// Read execution provenance from assistant routing or a tool result's author, never from
    /// prose mentioning a tool. Tool results still identify their runtime when the corresponding
    /// call is absent/redacted. Native search and image tools are not workspace execution.
    /// </summary>
    internal static bool AddressesOwnWorkTool(JsonObject message) =>
        message["author"]?["role"].AsText() switch
        {
            AssistantRole => IsOwnWorkTool(message["recipient"].AsText()),
            "tool" => !IsNativeSearchOrImageTool(message["author"]?["name"].AsText()),
            _ => false,
        };

    private static bool IsOwnWorkTool(string? name) => name is { Length: > 0 } && name != "all"
        && !IsNativeSearchOrImageTool(name);

    private static bool IsNativeSearchOrImageTool(string? name) => name is "web" or "image_gen"
        || name?.StartsWith("web.", StringComparison.Ordinal) == true
        || name?.StartsWith("image_gen.", StringComparison.Ordinal) == true;

    /// <summary>
    /// Reads provenance before a final answer exists. Only a complete active-branch walk ending
    /// at this send's user message can prove native work; old branches and quoted text cannot.
    /// </summary>
    public static bool CurrentTurnUsesOwnWorkTools(string conversationJson, int expectedUserMessageCount)
    {
        if (expectedUserMessageCount <= 0) return false;
        try
        {
            if (ParseJson(conversationJson) is not JsonObject conversation
                || conversation["mapping"] is not JsonObject mapping
                || conversation["current_node"].AsText() is not { Length: > 0 } node
                || ActiveUserMessageCount(mapping, node) != expectedUserMessageCount) return false;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var usedOwnTools = false;
            while (visited.Add(node) && mapping[node] is JsonObject entry)
            {
                if (entry["message"] is JsonObject message)
                {
                    if (message["author"]?["role"].AsText() == UserRole) return usedOwnTools;
                    usedOwnTools |= AddressesOwnWorkTool(message);
                }
                if (entry["parent"].AsText() is not { Length: > 0 } parent) return false;
                node = parent;
            }
        }
        catch (InvalidOperationException) { }
        return false;
    }

    /// <summary>
    /// The opening of an answer, for an error that has to quote it. One line and short, because it
    /// rides a message rather than the transcript.
    /// </summary>
    internal static string Excerpt(string text)
    {
        var flattened = string.Join(
            " ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flattened.Length <= ExcerptChars ? flattened : flattened[..ExcerptChars].TrimEnd() + "…";
    }

    /// <summary>Newest first while walking, so the order is put back before they are used.</summary>
    private static List<string> Assets(List<string> assets)
    {
        assets.Reverse();
        return assets;
    }

    private static void CollectAssets(JsonObject message, List<string> assets)
    {
        if (message["content"]?["parts"] is not JsonArray parts)
        {
            return;
        }

        foreach (var part in parts.OfType<JsonObject>())
        {
            if (part["asset_pointer"].AsText() is { Length: > 0 } pointer)
            {
                // "sediment://file_abc" and "file-service://file-abc" both name the file after the
                // scheme; the id is what the download endpoint takes.
                var id = pointer[(pointer.LastIndexOf('/') + 1)..];
                if (id.Length > 0 && !assets.Contains(id))
                {
                    assets.Add(id);
                }
            }
        }
    }

    private static string MessageText(JsonObject message) =>
        message["content"]?["parts"] is not JsonArray parts
            ? ""
            : string.Concat(parts.Select(p => p.AsText() ?? ""));

    /// <summary>The collected messages run newest first, and blank ones carry nothing to show.</summary>
    private static string? Joined(List<string> parts)
    {
        parts.Reverse();
        var text = string.Join("\n\n", parts.Where(p => p.Trim().Length > 0));
        return text.Length > 0 ? text : null;
    }

    // ---- project ------------------------------------------------------------------------------

    private async Task<(string Bearer, string? GizmoId)> PrepareProjectRouteAsync(
        IChatGptTransport transport,
        string account,
        string name,
        string projectScopeKey,
        CancellationToken cancellationToken)
    {
        if (name.Length == 0)
        {
            return (await AuthenticateAsync(transport, account, cancellationToken).ConfigureAwait(false), null);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProjectSetupTimeout);
        try
        {
            var bearer = await AuthenticateAsync(transport, account, deadline.Token).ConfigureAwait(false);
            var gizmoId = await ResolveProjectSerializedAsync(
                    transport, bearer, name, projectScopeKey, deadline.Token)
                .ConfigureAwait(false);
            return (bearer, gizmoId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(
                $"ChatGPT did not finish setting up project \"{name}\" within "
                + $"{ProjectSetupTimeout.TotalSeconds:0} seconds. Nothing was sent; try again.");
        }
    }

    private async Task<string?> ResolveProjectSerializedAsync(
        IChatGptTransport transport,
        string bearer,
        string name,
        string projectScopeKey,
        CancellationToken cancellationToken)
    {
        // The common path avoids network calls after this provider has verified the persisted pin.
        // Re-reading the tiny durable pin also observes another process replacing or tombstoning it.
        if (TryGetCoherentProject(projectScopeKey, out var cached))
        {
            return cached;
        }

        var entered = false;
        try
        {
            await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (TryGetCoherentProject(projectScopeKey, out cached))
            {
                return cached;
            }

            // Independent session pages may initialize together, but a missing configured project
            // must be created once and pinned before the second process begins its lookup.
            await using var projectLease = await options.AcquireChatGptScopeAsync(projectScopeKey, cancellationToken)
                .ConfigureAwait(false);
            return await ResolveProjectAsync(transport, bearer, name, projectScopeKey, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                _projectGate.Release();
            }
        }
    }

    private bool TryGetCoherentProject(string projectScopeKey, out string projectId)
    {
        if (_verifiedProjectIds.TryGetValue(projectScopeKey, out var cached)
            && string.Equals(options.GetChatGptProjectId(projectScopeKey), cached, StringComparison.Ordinal))
        {
            projectId = cached;
            return true;
        }

        _verifiedProjectIds.TryRemove(projectScopeKey, out _);
        projectId = "";
        return false;
    }

    /// <summary>
    /// The gizmo id of the configured project, creating the project the first time. Null when no
    /// project is configured, which leaves chats outside any project.
    /// </summary>
    private async Task<string?> ResolveProjectAsync(
        IChatGptTransport transport,
        string bearer,
        string name,
        string projectScopeKey,
        CancellationToken cancellationToken)
    {
        if (name.Length == 0)
        {
            return null;
        }

        // A durable pin avoids an expensive sidebar walk, but it is not permanent truth: projects
        // can be deleted or access can be revoked while Jarvis is closed. One direct lookup is both
        // faster than the sidebar and prevents a stale project URL from redirecting a send to root.
        ProjectLookup? lookup = null;
        var pinned = options.GetChatGptProjectId(projectScopeKey) ?? "";
        if (pinned.StartsWith(PendingProjectPrefix, StringComparison.Ordinal))
        {
            // The previous process may have lost the response after ChatGPT committed the POST.
            // Reconcile by name before another non-idempotent create is even considered.
            lookup = await FindProjectAsync(transport, bearer, name, cancellationToken).ConfigureAwait(false);
            if (!lookup.Succeeded)
            {
                throw new ProviderException(
                    $"ChatGPT could not reconcile an earlier attempt to create project \"{name}\" "
                    + $"({lookup.Failure}). Nothing was sent; retry when the project list is available.");
            }

            if (lookup.Id is { Length: > 0 } recovered)
            {
                options.SaveChatGptProjectId(projectScopeKey, recovered);
                _verifiedProjectIds[projectScopeKey] = recovered;
                return recovered;
            }

            var now = DateTimeOffset.UtcNow;
            if (PendingProjectStartedAt(pinned) is not { } startedAt
                || startedAt > now + PendingProjectClockSkew)
            {
                options.SaveChatGptProjectId(projectScopeKey, PendingProjectMarker());
                throw new ProviderException(
                    $"ChatGPT project \"{name}\" had an unreadable pending-creation marker. Jarvis repaired "
                    + "the marker and sent nothing; retry in two minutes so it can reconcile without a duplicate.");
            }

            if (now - startedAt < PendingProjectRetryDelay)
            {
                throw new ProviderException(
                    $"A previous attempt to create ChatGPT project \"{name}\" may still be completing. "
                    + "Nothing was sent; retry shortly so Jarvis can reconcile it without creating a duplicate.");
            }

            // The safety window elapsed and an exhaustive lookup still proves the project absent.
            // Clear the marker and reuse that lookup result instead of paying for a second listing.
            options.SaveChatGptProjectId(projectScopeKey, "");
        }
        else if (pinned is { Length: > 0 })
        {
            var validation = await ValidatePinnedProjectAsync(transport, bearer, name, pinned, cancellationToken)
                .ConfigureAwait(false);
            if (!validation.Succeeded)
            {
                throw ProjectLookupFailure(name, validation.Failure);
            }

            if (validation.Id is { Length: > 0 } verified)
            {
                _verifiedProjectIds[projectScopeKey] = verified;
                return verified;
            }

            // Persist an authoritative tombstone before looking up or creating a replacement. A
            // second process acquiring the same scope must never resurrect the stale settings pin.
            options.SaveChatGptProjectId(projectScopeKey, "");
        }

        lookup ??= await FindProjectAsync(transport, bearer, name, cancellationToken).ConfigureAwait(false);
        if (!lookup.Succeeded)
        {
            throw ProjectLookupFailure(name, lookup.Failure);
        }

        string? resolved = lookup.Id;
        if (resolved is not { Length: > 0 })
        {
            // Persist intent before the POST. If cancellation or process loss makes its outcome
            // unknowable, the next run reconciles by name rather than immediately creating a twin.
            cancellationToken.ThrowIfCancellationRequested();
            options.SaveChatGptProjectId(projectScopeKey, PendingProjectMarker());
            resolved = await CreateProjectAsync(transport, bearer, name, cancellationToken).ConfigureAwait(false);
        }

        if (resolved is not { Length: > 0 })
        {
            // A successful POST may race the sidebar store or return a newly changed envelope. Look
            // up the name once more, but never send this turn outside the configured project.
            var created = await FindProjectAsync(transport, bearer, name, cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                throw new ProviderException(
                    $"ChatGPT accepted project \"{name}\" but could not verify its new id ({created.Failure}). "
                    + "Nothing was sent; try again so Jarvis can resolve the project that was just created.");
            }

            resolved = created.Id;
            if (resolved is not { Length: > 0 })
            {
                throw new ProviderException(
                    $"ChatGPT accepted project \"{name}\" but did not return its id. Nothing was sent; "
                    + "try again so Jarvis can resolve the project that was just created.");
            }
        }

        options.SaveChatGptProjectId(projectScopeKey, resolved);
        _verifiedProjectIds[projectScopeKey] = resolved;

        return resolved;
    }

    private static string PendingProjectMarker() =>
        PendingProjectPrefix + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static DateTimeOffset? PendingProjectStartedAt(string marker)
    {
        if (!marker.StartsWith(PendingProjectPrefix, StringComparison.Ordinal)
            || !long.TryParse(marker.AsSpan(PendingProjectPrefix.Length), out var seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ProviderException ProjectLookupFailure(string name, string? failure) => new(
        $"ChatGPT would not verify your projects, so \"{name}\" could not be resolved ({failure}). "
        + "Nothing was created or sent — try again, or open Settings and clear the project name to chat "
        + "outside a project.");

    /// <summary>Checks a remembered id without paying for a full sidebar listing.</summary>
    private static async Task<ProjectLookup> ValidatePinnedProjectAsync(
        IChatGptTransport transport,
        string bearer,
        string name,
        string id,
        CancellationToken cancellationToken)
    {
        if (!id.StartsWith("g-p-", StringComparison.Ordinal))
        {
            return ProjectLookup.Absent();
        }

        var response = await transport.SendAsync(
            HttpMethod.Get, "/backend-api/gizmos/" + Uri.EscapeDataString(id), null, bearer,
            "application/json", null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (response.Status is 403 or 404)
        {
            return ProjectLookup.Absent();
        }

        if (!response.IsSuccess)
        {
            return ProjectLookup.Failed(Summarize(response));
        }

        if (ParseJson(response.Body) is not { } root)
        {
            return ProjectLookup.Failed("the remembered project came back in a form this app could not read");
        }

        var matchingIdSeen = false;
        var readableNameSeen = false;
        foreach (var (projectId, projectName) in Projects(root))
        {
            if (!string.Equals(projectId, id, StringComparison.Ordinal))
            {
                continue;
            }

            matchingIdSeen = true;
            if (string.IsNullOrWhiteSpace(projectName))
            {
                continue;
            }

            readableNameSeen = true;
            if (string.Equals(projectName, name, StringComparison.OrdinalIgnoreCase))
            {
                return ProjectLookup.Found(projectId);
            }
        }

        if (!matchingIdSeen)
        {
            return ProjectLookup.Failed("the remembered project response did not contain its id");
        }

        return readableNameSeen
            ? ProjectLookup.Absent()
            : ProjectLookup.Failed("the remembered project response did not contain a readable name");
    }

    /// <summary>
    /// Looks the project up by name. "Not there" and "could not look" are different answers: only
    /// the first may lead to creating one, or a hiccup in the listing would quietly leave the user
    /// with two projects of the same name.
    /// </summary>
    internal static async Task<ProjectLookup> FindProjectAsync(
        IChatGptTransport transport,
        string bearer,
        string name,
        CancellationToken cancellationToken)
    {
        var path = ProjectListPath;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var readableProjectIds = new HashSet<string>(StringComparer.Ordinal);
        var unreadableProjectIds = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxProjectPages; page++)
        {
            var response = await transport.SendAsync(
                HttpMethod.Get, path, null, bearer, "application/json", null, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!response.IsSuccess)
            {
                return ProjectLookup.Failed(Summarize(response));
            }

            if (ParseJson(response.Body) is not { } root)
            {
                return ProjectLookup.Failed("the project list came back in a form this app could not read");
            }

            // The sidebar's shape is not contractual, so rather than walking a fixed path this looks
            // anywhere in it for a project id paired with the name the user typed.
            foreach (var (id, projectName) in Projects(root))
            {
                if (string.IsNullOrWhiteSpace(projectName))
                {
                    if (!readableProjectIds.Contains(id))
                    {
                        unreadableProjectIds.Add(id);
                    }
                    continue;
                }

                readableProjectIds.Add(id);
                unreadableProjectIds.Remove(id);
                if (string.Equals(projectName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return ProjectLookup.Found(id);
                }
            }

            var cursorNode = (root as JsonObject)?["cursor"];
            if (cursorNode is null)
            {
                return unreadableProjectIds.Count == 0
                    ? ProjectLookup.Absent()
                    : ProjectLookup.Failed(
                        $"the project list contained {unreadableProjectIds.Count} project(s) without a readable name");
            }

            var cursor = Text(cursorNode);
            if (string.IsNullOrEmpty(cursor))
            {
                return ProjectLookup.Failed("the project list returned an unreadable pagination cursor");
            }

            if (!cursors.Add(cursor))
            {
                return ProjectLookup.Failed("the project list repeated a pagination cursor");
            }

            path = ProjectListPath + "&cursor=" + Uri.EscapeDataString(cursor);
        }

        return ProjectLookup.Failed("the project list exceeded its safe pagination limit");
    }

    /// <summary>The three answers a project lookup can give.</summary>
    internal sealed record ProjectLookup(bool Succeeded, string? Id, string? Failure)
    {
        public static ProjectLookup Found(string id) => new(true, id, null);

        public static ProjectLookup Absent() => new(true, null, null);

        public static ProjectLookup Failed(string failure) => new(false, null, failure);
    }

    private static async Task<string?> CreateProjectAsync(
        IChatGptTransport transport,
        string bearer,
        string name,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["instructions"] = "",
            ["name"] = name,
            ["memory_scope"] = "project_v2",
        }.ToJsonString();

        var response = await transport.SendAsync(
            HttpMethod.Post, "/backend-api/projects", body, bearer, "application/json", null, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!response.IsSuccess)
        {
            throw new ProviderException(
                $"ChatGPT would not create the project \"{name}\" (HTTP {response.Status}). "
                + "Create it by hand at chatgpt.com and the name will be found next time.");
        }

        // A successful response without a readable id may still have created the project. The
        // caller re-lists once and refuses to send unless that new id can be resolved.
        if (ParseJson(response.Body) is not { } root)
        {
            return null;
        }

        var candidates = Projects(root).ToList();
        return candidates.FirstOrDefault(
                project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)).Id
            ?? (candidates.Count == 1 ? candidates[0].Id : null);
    }

    /// <summary>
    /// What a project is called, wherever this payload keeps it. Measured against the live sidebar
    /// on 2026-09-14: entries can nest the project at <c>items[].gizmo</c> or
    /// <c>items[].gizmo.gizmo</c>. The project carries <c>id</c>, <c>instructions</c>, <c>display</c>
    /// and a dozen more — and no <c>name</c> of its own. The human-readable name is
    /// <c>display.name</c>, so fixed-path parsing can miss every project and create a duplicate.
    /// </summary>
    private static string? ProjectName(JsonObject entry) =>
        Text(entry["name"])
        ?? Text(entry["title"])
        ?? (entry["display"] is JsonObject display ? Text(display["name"]) ?? Text(display["title"]) : null);

    /// <summary>
    /// The string a node carries, or null when it is not one. A name that is not a string is a
    /// project this app cannot match rather than a response worth throwing over, which is what
    /// reading it as a promised string would do.
    /// </summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Every "g-p-…" id in a payload with the name that belongs to it.</summary>
    internal static IEnumerable<(string Id, string? Name)> Projects(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["id"] is JsonValue value
                    && value.TryGetValue<string>(out var id)
                    && id.StartsWith("g-p-", StringComparison.Ordinal))
                {
                    yield return (id, ProjectName(obj));
                }

                foreach (var (_, child) in obj)
                {
                    if (child is not null)
                    {
                        foreach (var found in Projects(child))
                        {
                            yield return found;
                        }
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        foreach (var found in Projects(item))
                        {
                            yield return found;
                        }
                    }
                }

                break;
        }
    }

    // ---- chat housekeeping --------------------------------------------------------------------

    /// <summary>Gives a new chat a recognisable name, so the project does not fill with "New chat".</summary>
    private static async Task NameChatAsync(
        IChatGptTransport transport,
        string bearer,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var title = $"jarvis-{Guid.NewGuid().ToString("N")[..8]}";
        var body = new JsonObject { ["title"] = title }.ToJsonString();
        await transport.SendAsync(
            HttpMethod.Post, $"/backend-api/conversation/id/{conversationId}/rename", body, bearer,
            "application/json", null, cancellationToken).ConfigureAwait(false);

        // A chat that keeps its default name is untidy, not broken, so a failure here is ignored.
    }

    private static async Task DeleteChatAsync(
        IChatGptTransport transport,
        string bearer,
        string conversationId,
        CancellationToken cancellationToken) =>
        // Rotation must go ahead even if the delete is refused, or the app would keep talking into
        // a chat it has already decided to abandon.
        await transport.SendAsync(
            HttpMethod.Delete, $"/backend-api/conversation/id/{conversationId}", null, bearer,
            "application/json", null, cancellationToken).ConfigureAwait(false);

    // ---- auth ---------------------------------------------------------------------------------

    private async Task<string> AuthenticateAsync(
        IChatGptTransport transport,
        string cookieFingerprint,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_accessToken is { Length: > 0 } cached
                && _accessTokenOwner == cookieFingerprint
                && DateTimeOffset.UtcNow < _accessTokenExpiry)
            {
                return cached;
            }
        }

        var response = await transport.SendAsync(
            HttpMethod.Get, "/api/auth/session", null, null, "application/json", null, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!response.IsSuccess)
        {
            throw new ProviderException($"ChatGPT failed while signing in: {Summarize(response)}");
        }

        var token = (ParseJson(response.Body) as JsonObject)?["accessToken"].AsText();
        if (string.IsNullOrEmpty(token))
        {
            throw new ProviderException(
                "ChatGPT returned no access token: failed to authenticate — the session cookie is expired "
                + "or signed out. "
                + "Export fresh cookies from a signed-in chatgpt.com tab and import them again.");
        }

        lock (_sync)
        {
            _accessToken = token;
            _accessTokenOwner = cookieFingerprint;
            _accessTokenExpiry = DateTimeOffset.UtcNow + AccessTokenLifetime;
        }

        return token;
    }

    // ---- history ------------------------------------------------------------------------------

    /// <summary>
    /// The whole conversation as one message. ChatGPT's composer takes text, not a role-tagged
    /// history, so the roles are spelled out — and the system prompt with them, as there is
    /// nowhere else to put it.
    /// </summary>
    internal static string FlattenHistory(LlmRequest request)
    {
        var builder = new StringBuilder();
        var system = FullSystemPrompt(request);
        if (system.Length > 0)
        {
            builder.Append(system).Append("\n\n");
        }

        if (request.Tools.Count > 0)
        {
            builder.Append(ChatGptToolProtocol.Instructions(request.Tools)).Append('\n');
        }
        else
        {
            builder.Append(ChatGptToolProtocol.TextOnlyBoundary).Append("\n\n");
        }

        var (preceding, latest) = Split(request);
        foreach (var (role, text) in preceding)
        {
            builder.Append(role == UserRole ? "User: " : "Assistant: ").Append(text).Append("\n\n");
        }

        if (latest.Length > 0)
        {
            builder.Append("=== CURRENT MESSAGE ===\n").Append(latest);
        }

        if (request.Tools.Count > 0)
        {
            builder.Append("\n\n").Append(ChatGptToolProtocol.Closing());
        }

        return builder.ToString().TrimEnd();
    }

    private static string FullSystemPrompt(LlmRequest request) => string.Join("\n\n",
        request.LeadingSystemBlocks.Select(block => block.Text).Append(request.SystemPrompt)
            .Where(text => text.Length > 0));

    private static string ToolContractHash(LlmRequest request) => HashParts(
        request.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => JsonSerializer.Serialize(new
            {
                tool.Name,
                tool.Description,
                InputSchema = tool.InputSchema,
            })).Prepend(ChatGptToolProtocol.ContractVersion).ToArray());

    /// <summary>
    /// What a chat the server already holds still has to be told: everything on the user side since
    /// its last answer. After a tool ran that is the tool's result — and whatever the harness added
    /// behind it, which is why this is a run of turns rather than one.
    /// </summary>
    internal static string LatestTurnText(LlmRequest request) => Split(request).Latest;

    /// <summary>
    /// A matching chat already has the exact current contract. Changed or removed actions change
    /// its identity and force a fresh chat with the complete replacement contract.
    /// </summary>
    private static string Continuation(LlmRequest request)
    {
        var latest = LatestTurnText(request);

        // The contract went out once, at the top of a message the model has long since read
        // past; the reminder rides every turn, because that is where the sandbox keeps winning.
        return request.Tools.Count == 0
            ? $"{latest}\n\n{ChatGptToolProtocol.TextOnlyBoundary}"
            : $"{latest}\n\n{ChatGptToolProtocol.Closing()}";
    }

    /// <summary>
    /// The conversation split at the last answer: everything the server already has, and everything
    /// it does not. The tail is a run because one round trip leaves several user-side messages
    /// behind — the tool results, then the harness's own trailing turn — and sending only the last
    /// of them dropped the results the model was waiting for.
    /// </summary>
    private static (List<(string Role, string Text)> Preceding, string Latest) Split(LlmRequest request)
    {
        var folded = Folded(request);
        var start = TrailingRunStart(folded);
        return (Rendered(folded.Take(start)), string.Join("\n\n", Rendered(folded.Skip(start)).Select(t => t.Text)));
    }

    /// <summary>Where the run of user-side messages after the last answer begins.</summary>
    private static int TrailingRunStart(IReadOnlyList<ChatMessage> folded)
    {
        var start = folded.Count;
        while (start > 0 && folded[start - 1].Role == Role.User)
        {
            start--;
        }

        return start;
    }

    /// <summary>
    /// The messages as this channel sees them. A harness system turn is folded onto the user turn it
    /// accompanies, exactly as every wire without a mid-conversation system role folds it; left
    /// standing it reads as a message of its own and hides the one before it.
    /// </summary>
    private static IReadOnlyList<ChatMessage> Folded(LlmRequest request) =>
        HarnessSystemTurns.Fold(request.Messages);

    /// <summary>
    /// The conversation as role-tagged text. Tool calls and their results are rendered into it
    /// because the channel carries nothing else — and because a message holding only a call or only
    /// a result has no text of its own, so it would otherwise drop out of the history entirely and
    /// leave the model answering about a tool it never saw run.
    /// </summary>
    private static List<(string Role, string Text)> Rendered(IEnumerable<ChatMessage> messages)
    {
        var turns = new List<(string Role, string Text)>();
        foreach (var message in messages)
        {
            var text = RenderMessage(message);
            if (text.Length > 0)
            {
                turns.Add((message.Role == Role.User ? UserRole : AssistantRole, text));
            }
        }

        return turns;
    }

    /// <summary>
    /// The pictures the newest user-side turns carry: what the user attached, and what a tool
    /// handed back. Older ones are not collected — the chat already holds the turns they belonged
    /// to, and re-attaching them would send the same screenshot on every message.
    /// </summary>
    internal static List<ImageBlock> LatestTurnImages(LlmRequest request) =>
        TurnImages(request, includeHistory: false);

    private static List<ImageBlock> TurnImages(LlmRequest request, bool includeHistory)
    {
        var folded = Folded(request);
        var start = includeHistory ? 0 : TrailingRunStart(folded);
        var images = new List<ImageBlock>();
        foreach (var message in folded.Skip(start))
        {
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case ImageBlock image:
                        images.Add(image);
                        break;
                    case ToolResultBlock { Images: { Count: > 0 } fromTool }:
                        images.AddRange(fromTool);
                        break;
                }
            }
        }

        return images;
    }

    private static string RenderMessage(ChatMessage message)
    {
        var parts = new List<string>();
        // Core normally copies follow-up instructions into their own TextBlocks. A standalone
        // result still needs its follow-up, but the normal shape must not inject each skill twice.
        var followUpCopies = message.Content.OfType<TextBlock>()
            .GroupBy(text => text.Text, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Trim().Length > 0:
                    parts.Add(text.Text);
                    break;
                // The picture itself rides the message as an attachment, and only for the newest
                // turn; the marker is what keeps an older one from reading as a message about
                // nothing.
                case ImageBlock:
                    parts.Add("[image]");
                    break;
                case ToolCallBlock call:
                    parts.Add(ChatGptToolProtocol.RenderCall(call.Name, call.ArgumentsJson));
                    break;
                case ToolResultBlock result:
                    var renderedResult = result;
                    if (result.FollowUpText is { Length: > 0 } followUp &&
                        followUpCopies.TryGetValue(followUp, out var copies) && copies > 0)
                    {
                        followUpCopies[followUp] = copies - 1;
                        renderedResult = result with { FollowUpText = null };
                    }
                    parts.Add(ChatGptToolProtocol.RenderResult(renderedResult));
                    break;
            }
        }

        return string.Join("\n", parts);
    }

    /// <summary>The assistant's turn as the next request will replay it: prose first, then actions.</summary>
    private static string RenderAssistant(ChatGptReplyContent content)
    {
        var parts = new List<string>();
        if (content.Text.Length > 0)
        {
            parts.Add(content.Text);
        }

        parts.AddRange(content.Actions.Select(a => ChatGptToolProtocol.RenderCall(a.Name, a.ArgumentsJson)));
        return string.Join("\n", parts);
    }

    private Chat? Resume(string scopeKey, string key)
    {
        // The OS lease is already held. Reload each time: another process may have advanced or
        // invalidated this scope since the preceding local call, even if this provider stayed alive.
        lock (_persistenceSync)
        {
            var entry = options.ReadChatGptChat(scopeKey);
            return entry is { ConversationId.Length: > 0, ToolContractHash.Length: > 0 }
                && entry.ScopeKey == scopeKey && entry.Key == key
                ? new Chat(entry.ConversationId, entry.SentMessages) : null;
        }
    }

    private void ForgetScope(string scopeKey) => SaveScope(scopeKey, null, required: true);

    private void SaveScope(string scopeKey, ChatGptChatEntry? entry, bool required = false)
    {
        // Real hosts atomically replace this scope's dedicated file. This small lock also keeps
        // the compatibility snapshot implementation safe for concurrent in-process test adapters.
        lock (_persistenceSync)
        {
            try
            {
                options.SaveChatGptChat(scopeKey, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or JsonException)
            {
                if (required)
                    throw new ProviderException("ChatGPT could not save the conversation recovery state. "
                        + "The message was not sent; check that the browser state directory is writable.", ex);
                // Invalidation is already durable. A failed commit loses only the optimization of
                // continuing this remote chat; the next turn safely replays the local transcript.
                DiagnosticLog.Write($"chatgpt: the chat could not be stored — {ex.Message}");
            }
        }
    }

    private void Remember(
        LlmRequest request,
        string assistantText,
        string? conversationId,
        int sentMessages,
        string scopeKey,
        string identity,
        string contractHash)
    {
        if (conversationId is not { Length: > 0 })
        {
            return;
        }

        // Every message as its own entry, which is how the next turn will render them when it looks
        // this chat up again. Folding the newest run into one entry — the shape it is sent in —
        // hashed differently the moment that run held more than one message, and the chat was lost.
        var full = new List<string>(HistoryEntries(request, precedingOnly: false))
        {
            $"{AssistantRole}:{assistantText}",
        };

        SaveScope(scopeKey, new ChatGptChatEntry
        {
            Key = HistoryKey(identity, full),
            ScopeKey = scopeKey,
            ToolContractHash = contractHash,
            ConversationId = conversationId,
            SentMessages = sentMessages,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// The state the server already holds: everything except the messages about to be sent. Hashing
    /// it is what ties a stateless request back to the chat it belongs to.
    /// </summary>
    private static List<string> HistoryEntries(LlmRequest request, bool precedingOnly)
    {
        var folded = Folded(request);
        var messages = precedingOnly ? folded.Take(TrailingRunStart(folded)) : folded;
        return messages.Select(message =>
        {
            var rendered = RenderMessage(message);
            var images = message.Content.SelectMany(block => block switch
            {
                ImageBlock image => new[] { image },
                ToolResultBlock { Images: { } toolImages } => toolImages,
                _ => Enumerable.Empty<ImageBlock>(),
            });
            var fingerprints = images.Select(image => HashParts(image.MediaType, image.Base64Data)).ToArray();
            var imageSuffix = fingerprints.Length == 0 ? "" : $"\n[images:{HashParts(fingerprints)}]";
            return rendered.Length == 0 && imageSuffix.Length == 0 ? null
                : $"{(message.Role == Role.User ? UserRole : AssistantRole)}:{rendered}{imageSuffix}";
        }).Where(entry => entry is not null).Select(entry => entry!).ToList();
    }

    /// <summary>
    /// JSON framing prevents distinct message boundaries or whitespace from colliding in a key.
    /// </summary>
    private static string HistoryKey(string systemPrompt, IEnumerable<string> messages) =>
        HashParts([systemPrompt, .. messages]);

    private static string HashParts(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    private ScopeGate RentScopeGate(string scopeKey)
    {
        lock (_sync)
        {
            if (!_scopeGates.TryGetValue(scopeKey, out var gate))
                _scopeGates[scopeKey] = gate = new ScopeGate();
            gate.Users++;
            return gate;
        }
    }

    private void ReturnScopeGate(string scopeKey, ScopeGate gate)
    {
        lock (_sync)
        {
            if (--gate.Users == 0)
            {
                _scopeGates.Remove(scopeKey);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class ScopeGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class PendingAsk(Task<ChatGptUiReply> task, CancellationTokenSource cancellation) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Disposing an iterator while it shows progress is cancellation too. Keep both the
            // local gate and the cross-process lease until the browser has stopped its composer.
            if (!task.IsCompleted) cancellation.Cancel();
            try { await task.ConfigureAwait(false); }
            catch (Exception)
            {
                // The normal await reports the failure; cleanup must not replace a cancellation
                // or a consumer failure with the abandoned request's exception.
            }
        }
    }

    private static string CookieFingerprint(ChatGptCookieJar jar) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jar.ToHeader())));

    private static string SafeJson(string body) => body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[')
        ? body
        : "{}";

    /// <summary>
    /// Every response read here has a sensible answer for "unreadable" — a failed lookup, a missing
    /// token, the rendered text instead of the stored turn — and none of them is served by an
    /// exception thrown from the middle of a turn. A body that opens like JSON can still be
    /// truncated, so the shape check above is not enough on its own.
    /// </summary>
    private static JsonNode? ParseJson(string body)
    {
        try
        {
            return JsonNode.Parse(SafeJson(body));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Summarize(ChatGptResponse response) => response.Status switch
    {
        // A transport failure already carries the sentence that explains it — the browser being
        // closed, a timeout — and "HTTP -1" would throw that away.
        ChatGptResponse.TransportFailure => Classified(response.Body),
        401 or 403 => $"the session was rejected (HTTP {response.Status}) — the cookies are expired or "
            + "from another account, so this app failed to authenticate.",
        429 => "the account is being rate-limited; it may have hit your usage limit.",
        _ => $"HTTP {response.Status}.",
    };

    /// <summary>
    /// A failure worded so the transcript's error card can place it. That card classifies by the
    /// words in the message — it is a port of the reference's own table — so a session that was
    /// refused has to say it failed to authenticate and one that ran out of time has to say it
    /// timed out; anything else lands in the card's catch-all, which offers the wrong thing to try.
    /// </summary>
    internal static string Classified(string failure) =>
        failure.Contains(TimeoutMarker, StringComparison.Ordinal)
            ? $"Request timed out. {WithoutMarker(failure)}"
            : NeedsSigningIn(failure) ? $"Failed to authenticate. {failure}" : failure;

    /// <summary>How the transport spells a wait that ran out.</summary>
    private const string TimeoutMarker = "TIMEOUT";

    /// <summary>
    /// The transport's own marker is dropped when it opens the sentence, so the message reads as
    /// one thing rather than as "Request timed out. TIMEOUT: …".
    /// </summary>
    private static string WithoutMarker(string failure) =>
        failure.StartsWith($"{TimeoutMarker}: ", StringComparison.Ordinal)
            ? failure[(TimeoutMarker.Length + 2)..]
            : failure;

    /// <summary>The two states only a person can clear: a challenge, and a signed-out browser.</summary>
    private static bool NeedsSigningIn(string failure) =>
        failure.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
        || failure.Contains("Cloudflare is challenging", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One accepted remote chat, bound to its local scope and complete tool contract.
    /// </summary>
    private sealed record Chat(string ConversationId, int SentMessages);
}
