using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Services;

/// <summary>
/// Runs the ChatGPT backend calls inside an embedded browser holding the user's cookies.
///
/// A plain HttpClient cannot get past ChatGPT's Cloudflare check, which is bound to a real
/// browser's TLS fingerprint, so every call is issued as a <c>fetch()</c> from inside a page on
/// chatgpt.com: the cookies, the fingerprint and any clearance the browser earned all apply
/// automatically. The window it needs exists but is parked off-screen, so cookie mode is invisible
/// until <see cref="ShowWindow"/> reveals it — which is how a user completes a challenge or a
/// sign-in by hand when the session needs one.
///
/// Browsers are isolated by cookie set and conversation scope. Up to four can run concurrently;
/// their profiles persist so clearance survives a restart.
/// </summary>
public sealed class ChatGptWebViewTransport : IChatGptTransport
{
    private const string Origin = "https://chatgpt.com";

    /// <summary>Where a file the model produced is put, under the profile.</summary>
    private const string DownloadFolder = "downloads";

    /// <summary>
    /// The bytes cross as base64 inside a script result, so the ceiling is what that can carry
    /// rather than what the disk can hold. A 4K image lands well inside it.
    /// </summary>
    private const int MaxAssetBytes = 12_000_000;

    /// <summary>
    /// What this browser really is. ChatGPT sits behind a check that reads the TLS handshake and the
    /// client hints beside this string, so a Chrome pinned to another year is a mismatch it can see
    /// — the engine says 148 in every other channel while the header said 131. The major version is
    /// the engine's own; the rest is the frozen suffix Chrome itself sends.
    /// </summary>
    internal static string UserAgent =>
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + $"Chrome/{ChromiumMajor}.0.0.0 Safari/537.36";

    /// <summary>The engine's own major version, which is what the brand list has to agree with.</summary>
    internal static string ChromiumMajor =>
        ElectronRuntime.ChromiumVersion.Split('.')[0];

    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, WeakReference<ChatGptWebViewTransport>> Pool = new(StringComparer.Ordinal);
    private static ChatGptWebViewTransport? _lastShown;
    private static readonly Timer PoolTimer = new(_ => PrunePool(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    private static readonly SemaphoreSlim BrowserSlots = new(4, 4);
    private static int _browserWaiters;
    private static string _profileFolder = "";

    private readonly Dispatcher _dispatcher;
    private readonly ChatGptCookieContext _cookies;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _ready = new(1, 1);

    /// <summary>
    /// One page per scope, one operation at a time. Independent sessions have their own page;
    /// operations inside this scope must not navigate away from each other's pending requests.
    /// </summary>
    private readonly SemaphoreSlim _page = new(1, 1);

    private ElectronPaneSession? _session;
    private JarvisCode.App.Controls.ElectronPaneView? _view;
    private string? _tabId;
    private Window? _host;
    private bool _initialized;
    private int _users;
    private long _lastUsed = Environment.TickCount64;
    private readonly string _scopeKey;
    private Action<int>? _engineExited;
    private Task _closing = Task.CompletedTask;
    private bool _ownsBrowserSlot;
    private readonly object _usageSync = new();
    private string BrowserProfileDirectory => Path.Combine(_profileFolder, "chatgpt", "scopes", _scopeKey);

    private ChatGptWebViewTransport(Dispatcher dispatcher, ChatGptCookieContext cookies, TimeSpan timeout)
    {
        _dispatcher = dispatcher;
        _cookies = cookies;
        _timeout = timeout;
        _scopeKey = ScopeKey(cookies);
    }

    /// <summary>
    /// Points the provider at this transport. Called once at startup; the folder is where the
    /// browser profile lives, so Cloudflare clearance is not re-earned on every launch.
    /// </summary>
    public static void Register(Dispatcher dispatcher, string profileFolder)
    {
        _profileFolder = profileFolder;
        ChatGptTransportRegistry.Register((cookies, timeout) => GetOrCreate(dispatcher, cookies, timeout));
    }

    /// <summary>
    /// Brings the browser on screen so a challenge or sign-in can be completed by hand. False when
    /// there is no browser running to show — it only exists once a turn or the Test button has
    /// started one.
    /// </summary>
    public static bool ShowWindow()
    {
        ChatGptWebViewTransport? transport;
        lock (Sync)
        {
            transport = _lastShown;
        }

        if (transport is null)
        {
            return false;
        }

        return transport._dispatcher.Invoke(() =>
        {
            // A window the user closed cannot be shown again — WPF throws rather than reopening
            // it — and closing it is exactly how someone dismisses this window.
            if (transport._host is not { } host)
            {
                return false;
            }

            host.WindowState = WindowState.Normal;
            host.ShowInTaskbar = true;
            host.Left = 120;
            host.Top = 120;
            host.Width = 1100;
            host.Height = 820;
            host.Show();
            host.Activate();
            return true;
        });
    }

    private static ChatGptWebViewTransport GetOrCreate(Dispatcher dispatcher, ChatGptCookieContext cookies, TimeSpan timeout)
    {
        lock (Sync)
        {
            var key = ScopeKey(cookies);
            if (Pool.TryGetValue(key, out var reference) && reference.TryGetTarget(out var existing))
            {
                _lastShown = existing;
                Interlocked.Exchange(ref existing._lastUsed, Environment.TickCount64);
                return existing;
            }
            var transport = new ChatGptWebViewTransport(dispatcher, cookies, timeout);
            Pool[key] = new(transport);
            _lastShown = transport;
            return transport;
        }
    }

    internal static string ScopeKey(ChatGptCookieContext cookies) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(cookies.InjectionJson + "\n" + (cookies.ScopeId ?? "settings")))).ToLowerInvariant();

    private IDisposable Retain()
    {
        lock (_usageSync) Interlocked.Increment(ref _users);
        return new UseLease(this);
    }

    private sealed class UseLease(ChatGptWebViewTransport owner) : IDisposable
    {
        public void Dispose()
        {
            bool releaseIdle;
            lock (owner._usageSync)
            {
                Interlocked.Exchange(ref owner._lastUsed, Environment.TickCount64);
                releaseIdle = Interlocked.Decrement(ref owner._users) == 0 && Volatile.Read(ref _browserWaiters) > 0;
            }
            if (releaseIdle) owner.CloseIfIdle();
        }
    }

    private void CloseIfIdle()
    {
        if (!_dispatcher.HasShutdownStarted)
            _ = _dispatcher.InvokeAsync(async () =>
            {
                Task closing;
                lock (_usageSync)
                {
                    // CloseAsync clears the handles and publishes _closing before its first
                    // await. A concurrent Retain consequently waits for disposal before restart.
                    closing = _users == 0 ? CloseAsync() : Task.CompletedTask;
                }
                await closing;
            }).Task.Unwrap();
    }

    private async Task AcquireBrowserSlotAsync(CancellationToken cancellationToken)
    {
        if (await BrowserSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        { _ownsBrowserSlot = true; return; }
        Interlocked.Increment(ref _browserWaiters);
        try
        {
            ChatGptWebViewTransport? idle;
            lock (Sync)
                idle = Pool.Values.Select(r => r.TryGetTarget(out var t) ? t : null)
                    .Where(t => t is not null && t._ownsBrowserSlot && Volatile.Read(ref t._users) == 0)
                    .OrderBy(t => Interlocked.Read(ref t!._lastUsed)).FirstOrDefault();
            idle?.CloseIfIdle();
            await BrowserSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ownsBrowserSlot = true;
        }
        finally { Interlocked.Decrement(ref _browserWaiters); }
    }

    private static void PrunePool()
    {
        List<ChatGptWebViewTransport> stale;
        lock (Sync)
        {
            stale = [];
            foreach (var (key, reference) in Pool.ToArray())
            {
                if (!reference.TryGetTarget(out var transport)) { Pool.Remove(key); continue; }
                if (Volatile.Read(ref transport._users) == 0
                    && Environment.TickCount64 - Interlocked.Read(ref transport._lastUsed) > 600_000)
                {
                    stale.Add(transport);
                    if (ReferenceEquals(_lastShown, transport)) _lastShown = null;
                }
            }
        }
        foreach (var transport in stale)
        {
            transport.CloseIfIdle();
        }
    }

    /// <summary>Closes every scoped browser before the hosting dispatcher is shut down.</summary>
    public static async Task ShutdownAsync()
    {
        ChatGptWebViewTransport[] transports;
        lock (Sync)
        {
            transports = Pool.Values.Select(r => r.TryGetTarget(out var value) ? value : null)
                .OfType<ChatGptWebViewTransport>().ToArray();
            Pool.Clear();
            _lastShown = null;
        }
        var closing = new List<Task>();
        foreach (var transport in transports)
        {
            if (!transport._dispatcher.HasShutdownStarted)
            {
                if (transport._dispatcher.CheckAccess())
                    closing.Add(transport.CloseAsync());
                else
                    closing.Add(transport._dispatcher.InvokeAsync(transport.CloseAsync).Task.Unwrap());
            }
        }
        await Task.WhenAll(closing).ConfigureAwait(false);
    }

    public async Task<ChatGptResponse> SendAsync(
        HttpMethod method,
        string path,
        string? jsonBody,
        string? bearerToken,
        string accept,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        using var use = Retain();
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Origin + path;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var destination)
            || destination.GetLeftPart(UriPartial.Authority) != Origin)
            return new ChatGptResponse(ChatGptResponse.TransportFailure,
                "BROWSER_ERROR: authenticated requests must remain on the ChatGPT origin.");
        var script = BuildFetchScript(method, url, jsonBody, bearerToken, accept, extraHeaders, _timeout);

        await _page.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false) is { } failure) return failure;
            var result = await RunInIsolatedWorldAsync(script, cancellationToken).ConfigureAwait(false);
            return ParseFetchResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ChatGptResponse(ChatGptResponse.TransportFailure, "CANCELLED: the request was cancelled.");
        }
        // COMException is what the host window raises once it is gone.
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            return new ChatGptResponse(ChatGptResponse.TransportFailure, $"BROWSER_ERROR: {ex.Message}");
        }
        finally { _page.Release(); }
    }

    /// <summary>The composer takes attachments, so a screenshot can go with the message.</summary>
    public bool CarriesImages => true;

    /// <summary>
    /// Sends by using the page the way a person does: open the right chat, type into the composer,
    /// press send, wait for the answer to stop growing, then read it. ChatGPT's own client performs
    /// the send, so its Turnstile and sentinel checks are its business, not this app's.
    /// </summary>
    public async Task<ChatGptUiReply> AskAsync(ChatGptAsk ask, CancellationToken cancellationToken)
    {
        using var use = Retain();
        try
        {
            await _page.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ChatGptUiReply.Failed("The turn was cancelled while another one had the page.");
        }

        try
        {
            if (await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false) is { } failure)
                return ChatGptUiReply.Failed(failure.Body);
            return await SendOnPageAsync(ask, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _page.Release();
        }
    }

    private async Task<ChatGptUiReply> SendOnPageAsync(ChatGptAsk ask, CancellationToken cancellationToken)
    {
        var target = ask.ConversationId is { Length: > 0 } existing
            ? ask.GizmoId is { Length: > 0 } existingProject
                ? $"{Origin}/g/{existingProject}/c/{existing}"
                : $"{Origin}/c/{existing}"
            : ask.GizmoId is { Length: > 0 } project ? $"{Origin}/g/{project}/project" : $"{Origin}/";
        PromptWireGate? activeWire = null;

        try
        {
            // A chat opened inside a project lives at /g/{gizmo}/c/{id}, so the page is already in
            // the right place whenever the id is in the address — matching on the whole URL would
            // reload it on every turn.
            if (await OpenAsync(target, ask.ConversationId, ask.GizmoId, cancellationToken).ConfigureAwait(false)
                is { } navigationError)
            {
                return ChatGptUiReply.Failed(navigationError.Error) with
                {
                    ProjectUnavailable = navigationError.TargetLost && ask.GizmoId is { Length: > 0 },
                };
            }

            // Provider options come from controls read off this exact page. Apply and verify them
            // before an attachment or a character of the prompt reaches the composer: silently
            // falling back to another model/effort would make the resulting answer untrustworthy.
            var hasComposerOptions = ask.ComposerOptions.Count > 0;
            if (hasComposerOptions
                && await ConfigureComposerAsync(ask, cancellationToken).ConfigureAwait(false) is { } controlError)
            {
                return ChatGptUiReply.Failed($"ChatGPT page: {controlError}");
            }

            if (ask.Images.Count > 0)
            {
                await ExecuteAsync(StashImagesScript(ask.Images)).ConfigureAwait(false);
            }

            // The page keeps its globals across a turn — the tab is not navigated when it is
            // already on the right chat — and the composer script only clears this after waits that
            // run to minutes. Read before then, the last turn's thinking is reported as this one's.
            await ExecuteAsync(ClearProgressScript).ConfigureAwait(false);

            var prompt = NormalizePrompt(ask.Prompt);
            await using var wire = new PromptWireGate(
                this, prompt, ask.ImagesOmittedNote, target, ask.ConversationId, ask.GizmoId, cancellationToken);
            activeWire = wire;
            await wire.StartAsync().ConfigureAwait(false);
            await using var provenance = new InflightWorkGuard(this, ask, () => wire.SendObserved);
            // ConfigureComposerAsync already selected and verified the model when live options
            // were supplied. Nulling it here avoids toggling the picker again; with no options the
            // original ComposeScript path is used exactly as before.
            var composeAsk = hasComposerOptions ? ask with { ModelLabel = null } : ask;
            var result = await RunInPageAsync(ComposeScript(composeAsk), cancellationToken, ask.Progress,
                    ask.AnswerPreview, () => wire.Error, cancelComposer: true, provenance.CheckAsync)
                .ConfigureAwait(false);
            var envelope = Unwrap(result);
            if (envelope?["pasteRejected"]?.GetValue<bool>() == true)
            {
                // This marker is emitted only before Send. ChatGPT sometimes remounts or ignores
                // synthetic paste on a continuing conversation; use Chromium's editing pipeline
                // once, then enter only the send/answer part of the script. Never retry a send.
                var imagesOmitted = envelope["imagesOmitted"]?.GetValue<bool>() == true;
                var nativePrompt = imagesOmitted && ask.ImagesOmittedNote.Length > 0
                    ? prompt + "\n\n" + ask.ImagesOmittedNote
                    : prompt;
                JarvisCode.Core.Utilities.DiagnosticLog.Write(
                    $"chatgpt: composer rejected paste before send; native input recovery ({nativePrompt.Length} chars)");
                await PrepareNativePromptAsync(nativePrompt, cancellationToken).ConfigureAwait(false);
                result = await RunInPageAsync(
                    ComposeScript(ask with { ModelLabel = null }, promptPrepared: true),
                    cancellationToken, ask.Progress, ask.AnswerPreview, () => wire.Error,
                    cancelComposer: true, provenance.CheckAsync)
                    .ConfigureAwait(false);
                envelope = Unwrap(result);
            }
            if (envelope?["error"]?.GetValue<string>() is { Length: > 0 } scriptError)
            {
                return ChatGptUiReply.Failed($"ChatGPT page: {scriptError}") with
                {
                    ProjectUnavailable = wire.ProjectUnavailable,
                };
            }

            return new ChatGptUiReply(
                envelope?["text"]?.GetValue<string>() ?? "",
                envelope?["conversationId"]?.GetValue<string>(),
                null);
        }
        catch (NativeWorkspaceViolationException ex)
        {
            return ChatGptUiReply.Failed(ex.Message) with { NativeToolViolation = true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ChatGptUiReply.Failed("The turn was cancelled.");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            return ChatGptUiReply.Failed($"BROWSER_ERROR: {ex.Message}") with
            {
                ProjectUnavailable = activeWire?.ProjectUnavailable == true,
            };
        }
    }

    /// <summary>
    /// Reads both live composer controls. Power is a React slider that ignores synthetic keyboard
    /// events, so its discrete labels are discovered with trusted CDP ArrowLeft/ArrowRight presses and
    /// the original value is restored before this method gives the page back.
    /// </summary>
    public Task<ChatGptComposerControls> ReadComposerControlsAsync(CancellationToken cancellationToken) =>
        ReadComposerControlsAsync(
            modelLabel: null, includeEfforts: true, cancellationToken: cancellationToken);

    public Task<ChatGptComposerControls> ReadComposerControlsAsync(
        string? modelLabel,
        CancellationToken cancellationToken) =>
        ReadComposerControlsAsync(
            modelLabel, includeEfforts: true, cancellationToken: cancellationToken);

    public async Task<ChatGptComposerControls> ReadComposerControlsAsync(
        string? modelLabel,
        bool includeEfforts,
        CancellationToken cancellationToken)
    {
        using var use = Retain();
        await _page.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false) is { } failure)
                throw new InvalidOperationException(failure.Body);
            if (await OpenAsync(Origin + "/", null, null, cancellationToken).ConfigureAwait(false) is { } navigationError)
                throw new InvalidOperationException(navigationError.Error);

            await DismissComposerMenusAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(modelLabel))
            {
                var configured = Unwrap(await RunInPageAsync(
                    ConfigureComposerScript(modelLabel, configurePower: false), cancellationToken)
                    .ConfigureAwait(false));
                if (configured?["error"]?.GetValue<string>() is { Length: > 0 } modelError)
                    throw new InvalidOperationException(modelError);
            }
            var controlsScript = includeEfforts ? ComposerControlsScript : ComposerModelsScript;
            var envelope = Unwrap(await RunInPageAsync(controlsScript, cancellationToken).ConfigureAwait(false));
            if (envelope?["error"]?.GetValue<string>() is { Length: > 0 } scriptError)
                throw new InvalidOperationException(scriptError);

            var models = new List<ChatGptPickerOption>();
            if (envelope?["models"] is JsonArray modelNodes)
            {
                foreach (var node in modelNodes.OfType<JsonObject>())
                {
                    var key = node["key"]?.GetValue<string>() ?? "";
                    var label = node["label"]?.GetValue<string>() ?? "";
                    if (key.Length > 0 && label.Length > 0)
                    {
                        models.Add(new ChatGptPickerOption(
                            key,
                            label,
                            node["selected"]?.GetValue<bool>() ?? false));
                    }
                }
            }

            IReadOnlyList<ChatGptPickerOption> efforts = [];
            if (includeEfforts && envelope?["hasPower"]?.GetValue<bool>() == true)
            {
                var original = await ReadPowerStateAsync(cancellationToken).ConfigureAwait(false);
                Exception? enumerationError = null;
                try
                {
                    efforts = await EnumeratePowerOptionsAsync(original, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    enumerationError = ex;
                    throw;
                }
                finally
                {
                    // Cancellation must not leave the user's ChatGPT setting changed. Restoration
                    // gets its own short deadline because the caller's token may already be set.
                    using var restoreDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        await SetPowerIndexAsync(original.Key, restoreDeadline.Token).ConfigureAwait(false);
                        await DismissComposerMenusAsync(restoreDeadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (enumerationError is not null
                        || ex is OperationCanceledException or TimeoutException or InvalidOperationException)
                    {
                        JarvisCode.Core.Utilities.DiagnosticLog.Write(
                            $"chatgpt: could not restore Power {original.Key} after reading it — {ex.Message}");
                        if (enumerationError is null) throw;
                    }
                }
            }
            else
            {
                await DismissComposerMenusAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ChatGptComposerControls(
                envelope?["currentModelLabel"]?.GetValue<string>() ?? "",
                models,
                efforts);
        }
        finally
        {
            _page.Release();
        }
    }

    private async Task<IReadOnlyList<ChatGptPickerOption>> EnumeratePowerOptionsAsync(
        PowerSliderState original,
        CancellationToken cancellationToken)
    {
        var state = await SetPowerIndexAsync(original.Min, cancellationToken).ConfigureAwait(false);
        var options = new List<ChatGptPickerOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var step = 0; step < MaxPowerRungs; step++)
        {
            if (!seen.Add(state.Key))
                throw new InvalidOperationException("the Power slider repeated an index while it was being read");

            options.Add(new ChatGptPickerOption(
                state.Key,
                state.Label,
                string.Equals(state.Key, original.Key, StringComparison.Ordinal)));
            if (string.Equals(state.Key, state.Max, StringComparison.Ordinal))
                return options;

            var previous = state.Key;
            state = await MovePowerAsync("ArrowRight", previous, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"the Power slider has more than {MaxPowerRungs} rungs");
    }

    private async Task<PowerSliderState> SetPowerIndexAsync(string wanted, CancellationToken cancellationToken)
    {
        var state = await ReadPowerStateAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(state.Key, wanted, StringComparison.Ordinal)) return state;

        // The live control advertises ArrowLeft/ArrowRight, but not Home/End. Walk to the bound it
        // exposes instead of assuming browser-default range semantics that React may suppress.
        for (var step = 0; step < MaxPowerRungs
             && !string.Equals(state.Key, state.Min, StringComparison.Ordinal); step++)
        {
            var previous = state.Key;
            state = await MovePowerAsync("ArrowLeft", previous, cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(state.Key, state.Min, StringComparison.Ordinal))
            throw new InvalidOperationException($"the Power slider has more than {MaxPowerRungs} rungs");

        for (var step = 0; step < MaxPowerRungs; step++)
        {
            if (string.Equals(state.Key, wanted, StringComparison.Ordinal)) return state;
            if (string.Equals(state.Key, state.Max, StringComparison.Ordinal)) break;

            var previous = state.Key;
            state = await MovePowerAsync("ArrowRight", previous, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"this ChatGPT composer has no Power index {wanted} (it offers {state.Min} through {state.Max})");
    }

    private async Task<PowerSliderState> WaitForPowerStateAsync(
        Func<PowerSliderState, bool> predicate,
        CancellationToken cancellationToken)
    {
        var state = await TryWaitForPowerStateAsync(
            predicate, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        if (state is not null) return state;

        var last = await ReadPowerStateAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"ChatGPT's Power slider did not move from index {last.Key} "
            + $"(keyboard owner focused: {last.Focused})");
    }

    private async Task<PowerSliderState?> TryWaitForPowerStateAsync(
        Func<PowerSliderState, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        PowerSliderState? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await ReadPowerStateAsync(cancellationToken).ConfigureAwait(false);
            if (predicate(last)) return last;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<PowerSliderState> MovePowerAsync(
        string key,
        string previous,
        CancellationToken cancellationToken)
    {
        static bool Changed(PowerSliderState value, string before) =>
            !string.Equals(value.Key, before, StringComparison.Ordinal);

        await PressTrustedKeyAsync(key, cancellationToken).ConfigureAwait(false);
        var moved = await TryWaitForPowerStateAsync(
            value => Changed(value, previous), TimeSpan.FromMilliseconds(250), cancellationToken)
            .ConfigureAwait(false);
        if (moved is not null) return moved;

        // An off-screen Electron WebContents can retain DOM focus while Chromium declines to route
        // keyboard input to it. A trusted click on the next discrete track stop is the fallback;
        // it runs only after proving the canonical key did not move, so a working control never
        // advances twice.
        await ClickAdjacentPowerStopAsync(key, cancellationToken).ConfigureAwait(false);
        return await WaitForPowerStateAsync(
            value => Changed(value, previous), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PowerSliderState> ReadPowerStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var envelope = Unwrap(await ExecuteAsync(PowerStateScript, cancellationToken).ConfigureAwait(false));
        if (envelope?["error"]?.GetValue<string>() is { Length: > 0 } error)
            throw new InvalidOperationException(error);

        var key = envelope?["key"]?.GetValue<string>() ?? "";
        var min = envelope?["min"]?.GetValue<string>() ?? "";
        var max = envelope?["max"]?.GetValue<string>() ?? "";
        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !int.TryParse(min, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("the Power slider did not expose numeric indices");

        return new PowerSliderState(
            key,
            min,
            max,
            envelope?["label"]?.GetValue<string>() is { Length: > 0 } label ? label : key,
            envelope?["focused"]?.GetValue<bool>() ?? false,
            envelope?["trackLeft"]?.GetValue<double>() ?? 0,
            envelope?["trackTop"]?.GetValue<double>() ?? 0,
            envelope?["trackWidth"]?.GetValue<double>() ?? 0,
            envelope?["trackHeight"]?.GetValue<double>() ?? 0);
    }

    private Task PressTrustedKeyAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session is not { } session || _tabId is not { } tabId)
                throw new InvalidOperationException("the ChatGPT window closed before its Power control was set");
            var cdp = session.Cdp(tabId);
            Exception? inputError = null;
            try
            {
                await BrowserPaneCdp.CallAsync(cdp, "Emulation.setFocusEmulationEnabled",
                    new JsonObject { ["enabled"] = true }, cancellationToken).ConfigureAwait(true);
                // Evaluating state between rungs can let a React focus guard reclaim the active
                // item. Re-focus its keyboard owner immediately before each trusted key dispatch.
                var focused = await BrowserPaneCdp.EvaluateAsync(cdp, PowerFocusScript, false, cancellationToken)
                    .ConfigureAwait(true);
                if ((key is "ArrowLeft" or "ArrowRight") && focused?.GetValue<bool>() != true)
                    throw new InvalidOperationException("the active Power control changed before keyboard input");
                await BrowserPaneCdp.PressKeyAsync(cdp, key, 0, null, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                inputError = ex;
                throw;
            }
            finally
            {
                // Focus emulation is only a delivery aid for this off-screen key. Leaving it on
                // changes focus-sensitive ChatGPT behavior when the hidden browser is later shown.
                using var restoreFocus = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await BrowserPaneCdp.CallAsync(cdp, "Emulation.setFocusEmulationEnabled",
                        new JsonObject { ["enabled"] = false }, restoreFocus.Token).ConfigureAwait(true);
                }
                catch (Exception) when (inputError is not null)
                {
                    // Preserve the original input failure after the bounded cleanup attempt.
                }
            }
        }).Task.Unwrap();
    }

    private async Task ClickAdjacentPowerStopAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var direction = key switch
        {
            "ArrowLeft" => -1,
            "ArrowRight" => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "not a Power navigation key"),
        };
        var state = await ReadPowerStateAsync(cancellationToken).ConfigureAwait(false);
        var current = int.Parse(state.Key, CultureInfo.InvariantCulture);
        var minimum = int.Parse(state.Min, CultureInfo.InvariantCulture);
        var maximum = int.Parse(state.Max, CultureInfo.InvariantCulture);
        var wanted = Math.Clamp(current + direction, minimum, maximum);
        if (wanted == current || maximum == minimum || state.TrackWidth <= 0 || state.TrackHeight <= 0)
            throw new InvalidOperationException("the Power slider did not expose a clickable next stop");

        var fraction = (double)(wanted - minimum) / (maximum - minimum);
        // Stay one physical pixel inside the hit target at both endpoints; rect.Right itself is
        // outside the element and can miss the maximum stop on Chromium.
        var inset = Math.Min(1d, state.TrackWidth / 4);
        var x = state.TrackLeft + inset + (state.TrackWidth - (2 * inset)) * fraction;
        var y = state.TrackTop + state.TrackHeight / 2;
        await _dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session is not { } session || _tabId is not { } tabId)
                throw new InvalidOperationException("the ChatGPT window closed before its Power control was set");
            await BrowserPaneCdp.MouseClickAsync(
                    session.Cdp(tabId), x, y, "left", 1, 0, cancellationToken)
                .ConfigureAwait(true);
        }).Task.Unwrap().ConfigureAwait(false);
    }

    private async Task DismissComposerMenusAsync(CancellationToken cancellationToken)
    {
        // One Escape closes the Power popover; the second closes the parent model menu. These are
        // trusted for the same reason as the slider keys and leave a clean composer for the turn.
        await PressTrustedKeyAsync("Escape", cancellationToken).ConfigureAwait(false);
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        await PressTrustedKeyAsync("Escape", cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ConfigureComposerAsync(ChatGptAsk ask, CancellationToken cancellationToken)
    {
        var requestedPower = ask.ComposerOptions
            .FirstOrDefault(static pair => pair.Key.Equals(
                ChatGptWebProvider.PowerOptionKey, StringComparison.OrdinalIgnoreCase)).Value;
        requestedPower = string.IsNullOrWhiteSpace(requestedPower) ? null : requestedPower.Trim();

        await DismissComposerMenusAsync(cancellationToken).ConfigureAwait(false);
        var envelope = Unwrap(await RunInPageAsync(
            ConfigureComposerScript(ask.ModelLabel, requestedPower is not null),
            cancellationToken).ConfigureAwait(false));
        if (envelope?["error"]?.GetValue<string>() is { Length: > 0 } error) return error;
        if (requestedPower is null)
        {
            await DismissComposerMenusAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (envelope?["hasPower"]?.GetValue<bool>() != true)
            return "the selected model has no Power control";

        try
        {
            var selected = await SetPowerIndexAsync(requestedPower, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(selected.Key, requestedPower, StringComparison.Ordinal))
                return $"ChatGPT would not switch Power to index {requestedPower}";
            await DismissComposerMenusAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private sealed record PowerSliderState(
        string Key,
        string Min,
        string Max,
        string Label,
        bool Focused,
        double TrackLeft,
        double TrackTop,
        double TrackWidth,
        double TrackHeight);

    private const int MaxPowerRungs = 32;

    /// <summary>
    /// The models this account's picker offers, read off the picker itself. There is no endpoint to
    /// ask — the composer has labels to click and no slugs — so the labels are what a model is
    /// called here, and reading them beats asking the user to guess at spellings.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var use = Retain();
        await _page.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false) is { } failure)
                throw new InvalidOperationException(failure.Body);
            if (await OpenAsync(Origin + "/", null, null, cancellationToken).ConfigureAwait(false) is { } navigationError)
            {
                throw new InvalidOperationException(navigationError.Error);
            }

            var envelope = Unwrap(await RunInPageAsync(ModelsScript, cancellationToken).ConfigureAwait(false));
            if (envelope?["error"]?.GetValue<string>() is { Length: > 0 } scriptError)
            {
                throw new InvalidOperationException(scriptError);
            }

            return envelope?["models"] is JsonArray models
                ? [.. models.Select(m => m?.GetValue<string>() ?? "").Where(m => m.Length > 0)]
                : [];
        }
        finally
        {
            _page.Release();
        }
    }

    internal static readonly string ComposerControlsScript = BuildComposerControlsScript(includePower: true);
    internal static readonly string ComposerModelsScript = BuildComposerControlsScript(includePower: false);

    private static string BuildComposerControlsScript(bool includePower) => ComposerControlsTemplate
        .Replace("__HELPERS__", PageHelpers)
        .Replace("__POWER_HELPERS__", PowerDomHelpers)
        .Replace("__INCLUDE_POWER__", includePower ? "true" : "false");

    private const string ComposerControlsTemplate = """
        (async () => {
        __HELPERS__
        __POWER_HELPERS__
          try {
            const includePower = __INCLUDE_POWER__;
            const pill = await wait(picker, 20000);
            if (!pill) return JSON.stringify({ error: 'the composer never grew its model picker' });
            const currentModelLabel = label(pill);
            const liveChoices = () => {
              const rows = choices();
              if (!rows) return null;
              const visible = rows.filter(row => row.getClientRects().length > 0);
              return visible.length ? visible : null;
            };

            let options = liveChoices();
            if (!options) {
              const currentPill = await wait(picker, 15000);
              if (!currentPill) return JSON.stringify({ error: 'the composer never grew its model picker' });
              if (currentPill.getAttribute('aria-expanded') !== 'true') press(currentPill);
              options = await wait(liveChoices, 10000);
            }
            if (!options) return JSON.stringify({ error: 'the model picker did not open' });

            const models = options.map(row => {
              const text = label(row);
              return {
                key: text,
                label: text,
                selected: row.getAttribute('aria-checked') === 'true'
                  || row.getAttribute('data-state') === 'checked'
              };
            }).filter(option => option.key.length > 0);

            if (!includePower) {
              return JSON.stringify({ currentModelLabel, models, hasPower: false });
            }
            // A cold page can replace the entire row after it first renders its shell. Resolve
            // both owner and slider from the live DOM on every poll, never from that shell.
            const control = await wait(livePowerControl, 10000);
            if (!control) {
              if (livePowerRow()) return JSON.stringify({ error: 'the active Power slider did not become ready' });
              return JSON.stringify({ currentModelLabel, models, hasPower: false });
            }
            // The visual slider is aria-hidden and tabindex=-1. The focusable Power menuitem owns
            // its keyboard contract; focusing or clicking the child can also change the value.
            control.power.focus();
            return JSON.stringify({ currentModelLabel, models, hasPower: true });
          } catch (e) {
            return JSON.stringify({ error: String(e) });
          }
        })()
        """;

    internal static string ConfigureComposerScript(string? modelLabel, bool configurePower) => """
        (async () => {
        __HELPERS__
        __POWER_HELPERS__
          try {
            const wanted = __MODEL__;
            const needsPower = __POWER__;
            const pill = await wait(picker, 15000);
            if (!pill && (wanted || needsPower)) {
              return JSON.stringify({ error: 'the composer never grew its model picker' });
            }

            const liveChoices = () => {
              const rows = choices();
              if (!rows) return null;
              const visible = rows.filter(row => row.getClientRects().length > 0);
              return visible.length ? visible : null;
            };
            const openChoices = async () => {
              let rows = liveChoices();
              if (rows) return rows;
              const currentPill = await wait(picker, 15000);
              if (!currentPill) return null;
              if (currentPill.getAttribute('aria-expanded') !== 'true') press(currentPill);
              return await wait(liveChoices, 10000);
            };

            let options = null;
            if (wanted) {
              options = await openChoices();
              if (!options) return JSON.stringify({ error: 'the model picker did not open' });
              let target = options.find(row => label(row).toLowerCase() === wanted.toLowerCase());
              if (!target) {
                return JSON.stringify({ error: 'this ChatGPT account has no ' + wanted
                  + ' in its model picker — it offers ' + options.map(label).join(', ') });
              }
              if (target.getAttribute('aria-checked') !== 'true'
                  && target.getAttribute('data-state') !== 'checked') {
                press(target);
              }

              // Verify against a freshly queried row. The pill can resolve "Latest" to "6 Pro",
              // so its display text cannot prove which selectable row is active.
              let verified = await wait(() => {
                const rows = liveChoices();
                if (!rows) return null;
                return rows.find(row => label(row).toLowerCase() === wanted.toLowerCase()
                  && (row.getAttribute('aria-checked') === 'true'
                    || row.getAttribute('data-state') === 'checked')) || null;
              }, 3000);
              if (!verified) {
                options = await openChoices();
                verified = options && options.find(row => label(row).toLowerCase() === wanted.toLowerCase()
                  && (row.getAttribute('aria-checked') === 'true'
                    || row.getAttribute('data-state') === 'checked'));
              }
              if (!verified) return JSON.stringify({ error: 'ChatGPT would not switch to ' + wanted });
            }

            if (!needsPower) return JSON.stringify({ hasPower: false });
            options = liveChoices() || await openChoices();
            if (!options) return JSON.stringify({ error: 'the model picker did not open' });
            const control = await wait(livePowerControl, 10000);
            if (!control) return JSON.stringify({ error: 'the active Power slider did not become ready for the selected model' });
            control.power.focus();
            return JSON.stringify({ hasPower: true });
          } catch (e) {
            return JSON.stringify({ error: String(e) });
          }
        })()
        """
        .Replace("__HELPERS__", PageHelpers)
        .Replace("__POWER_HELPERS__", PowerDomHelpers)
        .Replace("__MODEL__", modelLabel is { Length: > 0 }
            ? JsonValue.Create(modelLabel)!.ToJsonString()
            : "null")
        .Replace("__POWER__", configurePower ? "true" : "false");

    // The slider thumb deliberately has aria-hidden=true on ChatGPT. It is still the
    // numeric source of truth; visibility here refers to DOM layout, not accessibility exposure.
    private const string PowerDomHelpers = """
          const powerVisible = node => !!node && node.isConnected && node.getClientRects().length > 0
            && getComputedStyle(node).visibility !== 'hidden' && !node.closest('[inert]');
          const livePowerRows = () => [...document.querySelectorAll('[role="menuitem"][aria-label="Power"]')]
            .filter(powerVisible);
          const livePowerRow = () => livePowerRows()[0] || null;
          const livePowerControl = () => {
            for (const power of livePowerRows()) {
              const slider = [...power.querySelectorAll('[role="slider"]')].find(powerVisible);
              if (slider) return { power, slider };
            }
            return null;
          };
        """;

    private static readonly string PowerFocusScript = """
        (() => {
        __POWER_HELPERS__
          const control = livePowerControl();
          if (!control) return false;
          control.power.focus();
          return document.activeElement === control.power;
        })()
        """.Replace("__POWER_HELPERS__", PowerDomHelpers);

    /// <summary>
    /// Reads the label through the status ids named by aria-describedby. ChatGPT currently names
    /// two nodes there: the changing "High, 3 of 5." status and static keyboard help. Matching the
    /// ordinal status keeps the code independent of generated ids and ignores the help text.
    /// </summary>
    internal static readonly string PowerStateScript = """
        (() => {
        __POWER_HELPERS__
          try {
            const control = livePowerControl();
            if (!control) return JSON.stringify({ error: 'the active Power slider is no longer open' });
            const { power, slider } = control;
            const ids = (power.getAttribute('aria-describedby')
                || slider.getAttribute('aria-describedby') || '')
              .split(/\s+/).map(value => value.trim()).filter(Boolean);
            const descriptions = ids.map(id => document.getElementById(id))
              .filter(Boolean)
              .map(node => (node.textContent || '').replace(/\s+/g, ' ').trim())
              .filter(Boolean);
            const ordinal = descriptions.find(text => /,\s*\d+\s+of\s+\d+\.?\s*$/i.test(text));
            const rawLabel = ordinal || descriptions[0]
              || slider.getAttribute('aria-valuetext') || slider.getAttribute('aria-valuenow') || '';
            const cleanLabel = rawLabel.replace(/,\s*\d+\s+of\s+\d+\.?\s*$/i, '').trim();
            const track = power.querySelector('[data-model-reasoning-effort-slider]') || slider;
            const rect = track.getBoundingClientRect();
            return JSON.stringify({
              key: slider.getAttribute('aria-valuenow') || '',
              min: slider.getAttribute('aria-valuemin') || '',
              max: slider.getAttribute('aria-valuemax') || '',
              label: cleanLabel,
              focused: document.activeElement === power,
              trackLeft: rect.left,
              trackTop: rect.top,
              trackWidth: rect.width,
              trackHeight: rect.height
            });
          } catch (e) {
            return JSON.stringify({ error: String(e) });
          }
        })()
        """.Replace("__POWER_HELPERS__", PowerDomHelpers);

    internal static readonly string ModelsScript = """
        (async () => {
        __HELPERS__
          try {
            const pill = await wait(picker, 20000);
            if (!pill) return JSON.stringify({ error: 'the composer never grew its model picker' });
            press(pill);
            const options = await wait(choices, 10000);
            if (!options) return JSON.stringify({ error: 'the model picker did not open' });
            return JSON.stringify({ models: options.map(label).filter(t => t.length > 0) });
          } catch (e) {
            return JSON.stringify({ error: String(e) });
          }
        })()
        """.Replace("__HELPERS__", PageHelpers);

    /// <summary>
    /// Presses ChatGPT's own stop button. Fire and forget from a cancellation callback: the turn is
    /// already over on this side, and a page that has no stop button has already finished.
    /// </summary>
    private async Task StopAnswerAsync()
    {
        try
        {
            await ExecuteAsync(StopScript).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
            or TaskCanceledException or System.Runtime.InteropServices.COMException)
        {
            // The turn is already over on this side; a browser that has gone with it is the
            // ordinary case, not something to raise at the user.
            JarvisCode.Core.Utilities.DiagnosticLog.Write($"chatgpt: the stop did not land — {ex.Message}");
        }
    }

    private const string ClearProgressScript = """
        (() => {
          window.__jarvisChatGpt = window.__jarvisChatGpt || {};
          window.__jarvisChatGpt.progress = '';
          window.__jarvisChatGpt.answerPreview = '';
          return 'cleared';
        })()
        """;

    internal const string StopScript = """
        (() => {
          const stop = document.querySelector('[data-testid="stop-button"]')
            || document.querySelector('button[aria-label*="Stop" i]');
          if (stop) stop.click();
          return 'stopped';
        })()
        """;

    /// <summary>
    /// Brings a file the conversation points at down to disk. The bytes come back through the page
    /// because ChatGPT's origin will not answer anything else, and they are written under the
    /// profile rather than into the workspace: nothing here knows where that is, and a path in the
    /// answer is enough for the shell to move it wherever it is wanted.
    /// </summary>
    public async Task<string?> SaveAssetAsync(string assetId, string bearer, CancellationToken cancellationToken)
    {
        using var use = Retain();
        await _page.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false) is not null) return null;
            var result = await RunInIsolatedWorldAsync(AssetScript(assetId, bearer), cancellationToken).ConfigureAwait(false);
            var envelope = Unwrap(result);
            if (envelope?["base64"]?.GetValue<string>() is not { Length: > 0 } base64)
            {
                // The caller says in the answer that the file did not come; why it did not is only
                // worth the log, and is exactly what is missing when someone comes to ask.
                JarvisCode.Core.Utilities.DiagnosticLog.Write(
                    "chatgpt: a file did not come down — "
                    + (envelope?["error"]?.GetValue<string>() ?? "the browser returned nothing"));
                return null;
            }

            var folder = Path.Combine(_profileFolder, DownloadFolder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, LocalFileName(envelope["name"]?.GetValue<string>()));
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(base64), cancellationToken)
                .ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or FormatException
            or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // A file that will not come down is worth saying so about, which the caller does; it is
            // not worth losing the answer that came with it.
            JarvisCode.Core.Utilities.DiagnosticLog.Write($"chatgpt: a file did not come down — {ex.Message}");
            return null;
        }
        finally { _page.Release(); }
    }

    /// <summary>
    /// A name of this app's making. What the server calls the file is a path of its own
    /// ("user-abc/1234.png") and has no business deciding where anything lands; only its extension
    /// is worth keeping, and only while it still looks like one.
    /// </summary>
    private static string LocalFileName(string? serverName)
    {
        var extension = Path.GetExtension(serverName ?? "");
        if (extension.Length is < 2 or > 6 || !extension[1..].All(char.IsAsciiLetterOrDigit))
        {
            extension = ".bin";
        }

        return $"chatgpt-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{extension}";
    }

    private static string AssetScript(string assetId, string bearer) => $$$"""
        (async () => {
          if (location.origin !== 'https://chatgpt.com') return JSON.stringify({ error: 'the ChatGPT document changed' });
          const controller = new AbortController();
          operation.abort = () => controller.abort();
          if (operation.cancelled) controller.abort();
          try {
            const id = {{{JsonValue.Create(assetId)!.ToJsonString()}}};
            const meta = await fetch('https://chatgpt.com/backend-api/files/' + encodeURIComponent(id) + '/download', {
              credentials: 'include', redirect: 'error', signal: controller.signal,
              headers: { Authorization: 'Bearer ' + {{{JsonValue.Create(bearer)!.ToJsonString()}}} }
            }).then(r => r.ok ? r.json() : null);
            if (!meta || !meta.download_url) return JSON.stringify({ error: 'the file has no download of its own' });
            if (meta.file_size_bytes > {{{MaxAssetBytes}}}) {
              return JSON.stringify({ error: 'the file is larger than this can carry' });
            }

            const res = await fetch(meta.download_url, { credentials: 'include', signal: controller.signal });
            if (!res.ok) return JSON.stringify({ error: 'the download answered ' + res.status });

            const bytes = new Uint8Array(await res.arrayBuffer());
            if (bytes.length > {{{MaxAssetBytes}}}) {
              return JSON.stringify({ error: 'the file is larger than this can carry' });
            }

            // btoa takes a string, and building one in a single call blows the argument limit.
            let binary = '';
            const chunk = 0x8000;
            for (let i = 0; i < bytes.length; i += chunk) {
              binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
            }

            return JSON.stringify({ name: meta.file_name || '', base64: btoa(binary) });
          } catch (e) {
            return JSON.stringify({ error: String(e) });
          } finally {
            operation.abort = null;
          }
        })()
        """;

    /// <summary>Navigates only when the page is not already where it needs to be.</summary>
    private async Task<NavigationFailure?> OpenAsync(
        string url,
        string? conversationId,
        string? gizmoId,
        CancellationToken cancellationToken)
    {
        if (_session is not { } session || _tabId is not { } tabId)
        {
            return new($"TIMEOUT: the ChatGPT page did not open {url}.", TargetLost: false);
        }

        var current = (await session.NotesAsync(tabId, cancellationToken).ConfigureAwait(false)).Url;
        var alreadyThere = IsAtTarget(current, url, conversationId, gizmoId);
        if (alreadyThere)
        {
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            // The engine resolves after the load and returns Chromium's final URL. Verify that
            // address before any caller is allowed to reach the composer.
            var finalUrl = await session.NavigateAsync(tabId, url, deadline.Token).ConfigureAwait(false);
            return NavigationError(finalUrl, url, conversationId, gizmoId) is { } error
                ? new NavigationFailure(error, TargetLost: true)
                : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            return new($"TIMEOUT: the ChatGPT page did not open {url}.", TargetLost: false);
        }
    }

    private sealed record NavigationFailure(string Error, bool TargetLost);

    internal static bool IsAtTarget(
        string current,
        string target,
        string? conversationId,
        string? gizmoId = null)
    {
        if (!Uri.TryCreate(current, UriKind.Absolute, out var actual)
            || !Uri.TryCreate(target, UriKind.Absolute, out var wanted)
            || !string.Equals(actual.GetLeftPart(UriPartial.Authority), wanted.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            return false;
        var path = actual.AbsolutePath.TrimEnd('/');
        if (!string.IsNullOrEmpty(conversationId))
        {
            if (!path.EndsWith("/c/" + conversationId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrEmpty(gizmoId))
                return true;

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 4
                && string.Equals(segments[0], "g", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(segments[1], gizmoId, StringComparison.OrdinalIgnoreCase)
                    || segments[1].StartsWith(gizmoId + "-", StringComparison.OrdinalIgnoreCase))
                && string.Equals(segments[2], "c", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[3], conversationId, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(path, wanted.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NavigationError(
        string current,
        string target,
        string? conversationId,
        string? gizmoId = null) =>
        IsAtTarget(current, target, conversationId, gizmoId)
            ? null
            : $"BROWSER_ERROR: ChatGPT redirected away from {target}. Nothing was sent.";

    internal static string NormalizePrompt(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private async Task PrepareNativePromptAsync(string prompt, CancellationToken cancellationToken)
    {
        var text = NormalizePrompt(prompt);
        Exception? inputError = null;
        try
        {
            await DismissComposerMenusAsync(cancellationToken).ConfigureAwait(false);
            await ComposerCdpAsync("Emulation.setFocusEmulationEnabled",
                new JsonObject { ["enabled"] = true }, cancellationToken).ConfigureAwait(false);
            await ClearNativeComposerAsync(cancellationToken).ConfigureAwait(false);

            for (var offset = 0; offset < text.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(offset + 8000, text.Length);
                if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
                var focused = Unwrap(await RunInPageAsync(
                    // Replace Chromium's empty-editor padding on the first chunk instead of
                    // appending after it; subsequent chunks extend only the current prompt.
                    NativeComposerScript(selectAll: offset == 0), cancellationToken, cancelComposer: true).ConfigureAwait(false));
                if (focused?["ready"]?.GetValue<bool>() != true
                    || (offset > 0 && focused?["empty"]?.GetValue<bool>() == true))
                    throw new InvalidOperationException(focused?["error"]?.GetValue<string>()
                        ?? "the composer changed during native input; nothing was sent");

                await ComposerCdpAsync("Input.insertText",
                    new JsonObject { ["text"] = text[offset..end] }, cancellationToken).ConfigureAwait(false);
                offset = end;
            }
        }
        catch (Exception ex)
        {
            inputError = ex;
            // CDP cancellation can cancel its waiter after a packet reached Chromium. Close this
            // page before releasing its lease, so delayed native input cannot affect a later turn.
            if ((ex is OperationCanceledException or TimeoutException) && !BrowserGone)
                await _dispatcher.InvokeAsync(CloseAsync).Task.Unwrap().ConfigureAwait(false);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("native composer preparation timed out; nothing was sent", ex);
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (!BrowserGone) await ComposerCdpAsync("Emulation.setFocusEmulationEnabled",
                    new JsonObject { ["enabled"] = false }, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception) when (inputError is not null) { }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                if (!BrowserGone)
                    await _dispatcher.InvokeAsync(CloseAsync).Task.Unwrap().ConfigureAwait(false);
                throw new TimeoutException(
                    "the browser did not finish native input cleanup; nothing was sent", ex);
            }
        }
    }

    private async Task ClearNativeComposerAsync(CancellationToken cancellationToken)
    {
        // A React remount can restore the draft after a delete. Retry only preparation, never Send.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var selected = Unwrap(await RunInPageAsync(
                NativeComposerScript(selectAll: true), cancellationToken, cancelComposer: true).ConfigureAwait(false));
            if (selected?["ready"]?.GetValue<bool>() != true)
                throw new InvalidOperationException(selected?["error"]?.GetValue<string>()
                    ?? "the composer could not be selected for native clearing; nothing was sent");

            if (!await DeleteNativeComposerSelectionAsync(cancellationToken).ConfigureAwait(false)) continue;
            var cleared = Unwrap(await RunInPageAsync(
                NativeComposerEmptyScript, cancellationToken, cancelComposer: true).ConfigureAwait(false));
            if (cleared?["ready"]?.GetValue<bool>() == true) return;
            if (cleared?["error"]?.GetValue<string>() is { Length: > 0 } error)
                throw new InvalidOperationException(error);
        }

        throw new InvalidOperationException("the old composer draft could not be cleared; nothing was sent");
    }

    private async Task<bool> DeleteNativeComposerSelectionAsync(CancellationToken cancellationToken)
    {
        // The readiness operation is polled; React may move focus before that poll returns. Take
        // ownership again directly before the destructive key, with no operation polling between.
        var selected = await ComposerCdpAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = NativeComposerClearFocusScript, ["returnByValue"] = true,
        }, cancellationToken).ConfigureAwait(false);
        if (selected["result"]?["value"]?.GetValue<bool>() != true) return false;

        Exception? downError = null;
        JsonObject Key(string type) => new()
        {
            ["type"] = type, ["key"] = "Backspace", ["code"] = "Backspace",
            ["windowsVirtualKeyCode"] = 8, ["nativeVirtualKeyCode"] = 8, ["modifiers"] = 0,
        };
        try
        {
            // DOM selection stays inside the editor. Native editing handles pages that ignore or
            // roll back execCommand; Ctrl+A would risk selecting the surrounding page instead.
            await ComposerCdpAsync("Input.dispatchKeyEvent", Key("keyDown"), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            downError = ex;
            throw;
        }
        finally
        {
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await ComposerCdpAsync("Input.dispatchKeyEvent", Key("keyUp"), release.Token).ConfigureAwait(false);
            }
            catch (Exception) when (downError is not null) { }
        }
        return true;
    }

    private Task<JsonObject> ComposerCdpAsync(
        string method, JsonObject parameters, CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session is not { } session || _tabId is not { } tabId)
                throw new InvalidOperationException("the ChatGPT composer page closed before input completed");
            return session.Cdp(tabId).SendAsync(method, parameters, cancellationToken);
        }).Task.Unwrap();

    internal static string NativeComposerScript(bool selectAll) => """
        (async () => {
        __HELPERS__
        __COMPOSER_HELPERS__
          try {
            const node = await readyComposer();
            if (!selectComposer(node, __SELECT_ALL__))
              return JSON.stringify({ error: 'the composer could not be focused; nothing was sent' });
            return JSON.stringify({ ready: true, empty: !composerText(node).trim() });
          } catch (e) { return JSON.stringify({ error: String(e) }); }
        })()
        """.Replace("__HELPERS__", PageHelpers)
            .Replace("__COMPOSER_HELPERS__", ComposerHelpers)
            .Replace("__SELECT_ALL__", selectAll ? "true" : "false");

    private static readonly string NativeComposerEmptyScript = """
        (async () => {
        __HELPERS__
        __COMPOSER_HELPERS__
          try {
            await readyComposer();
            const end = Date.now() + 1200;
            let emptyNode = null;
            let emptySince = 0;
            while (Date.now() < end) {
              check();
              const node = composer();
              // A pre-wrap contenteditable can retain one LF text node after native deletion.
              // The first native insertion selects all and replaces this padding, never appends.
              const padding = node && node.childNodes.length === 1
                && node.firstChild.nodeType === 3 && node.firstChild.nodeValue === '\n';
              if (node && (!(node.textContent || '').length || padding) && !composerText(node).trim()) {
                if (node !== emptyNode) { emptyNode = node; emptySince = Date.now(); }
                if (Date.now() - emptySince >= 300 && selectComposer(node, false))
                  return JSON.stringify({ ready: true });
              } else { emptyNode = null; emptySince = 0; }
              await sleep(50);
            }
            return JSON.stringify({ ready: false });
          } catch (e) { return JSON.stringify({ error: String(e) }); }
        })()
        """.Replace("__HELPERS__", PageHelpers)
            .Replace("__COMPOSER_HELPERS__", ComposerHelpers);

    private static readonly string NativeComposerClearFocusScript = """
        (() => {
        __COMPOSER_HELPERS__
          const node = composer();
          return !!node && selectComposer(node, true);
        })()
        """.Replace("__COMPOSER_HELPERS__", ComposerHelpers);

    /// <summary>
    /// Picks the model, types the prompt and waits the answer out. The composer is a ProseMirror
    /// editor, so the text goes in through an edit command rather than by assignment, and the send
    /// button only appears once the editor has processed that edit.
    ///
    /// The answer is recognised by its own message id rather than by counting turns: ChatGPT
    /// re-mounts turns while a thinking model works, so the number on the page falls as well as
    /// rises and stays at the old count for as long as the thinking lasts. Counting made a slow
    /// answer indistinguishable from no answer at all.
    ///
    /// Both waits stay inside the C# side's own deadline, so a stuck answer is reported from here
    /// with a usable message instead of timing the whole call out.
    /// </summary>
    public static string ComposeScript(ChatGptAsk ask, bool promptPrepared = false) =>
        // The prompt goes in last on purpose: it is the untrusted half, and by then there is no
        // placeholder left in the script for anything inside it to stand in for.
        ScriptTemplate
            .Replace("__HELPERS__", PageHelpers)
            .Replace("__COMPOSER_HELPERS__", ComposerHelpers)
            .Replace("__PREPARED__", promptPrepared ? "true" : "false")
            .Replace(
                "__MODEL__",
                ask.ModelLabel is { Length: > 0 } label ? JsonValue.Create(label)!.ToJsonString() : "null")
            .Replace("__IMAGES__", ask.Images.Count.ToString(CultureInfo.InvariantCulture))
            .Replace("__NOTE__", JsonValue.Create(ask.ImagesOmittedNote)!.ToJsonString())
            .Replace("__PROMPT__", JsonValue.Create(ask.Prompt)!.ToJsonString());

    /// <summary>
    /// Parks the pictures in the page before the composer script runs. They travel separately
    /// because a screenshot is megabytes of base64 and the script is a string that would otherwise
    /// carry all of them inline.
    /// </summary>
    internal static string StashImagesScript(IReadOnlyList<ChatGptImage> images)
    {
        var payload = new JsonArray();
        foreach (var image in images)
        {
            payload.Add(new JsonObject
            {
                ["type"] = image.MediaType.Length > 0 ? image.MediaType : "image/png",
                ["b64"] = image.Base64,
            });
        }

        return $$"""
            (() => {
              window.__jarvisChatGpt = window.__jarvisChatGpt || {};
              window.__jarvisChatGpt.images = {{payload.ToJsonString()}};
              return 'stashed';
            })()
            """;
    }

    /// <summary>
    /// The page-side helpers both scripts run on. Shared rather than repeated because the model
    /// picker is a menu that opens on pointer events — a plain click() leaves it shut — and because
    /// a label read one way here and another way there would stop matching the picker's own rows.
    /// </summary>
    private const string PageHelpers = """
          const check = () => { if (typeof operation !== 'undefined' && operation.cancelled) throw new Error('CANCELLED: the turn was cancelled'); };
          const sleep = async ms => { check(); await new Promise(r => setTimeout(r, ms)); check(); };
          const wait = async (fn, ms) => { const end = Date.now() + ms;
            while (Date.now() < end) { check(); const v = fn(); if (v) return v; await sleep(150); } return null; };
          const press = el => {
            const base = { bubbles: true, cancelable: true, composed: true, pointerId: 1, pointerType: 'mouse', button: 0 };
            el.dispatchEvent(new PointerEvent('pointerdown', Object.assign({ buttons: 1 }, base)));
            el.dispatchEvent(new PointerEvent('pointerup', Object.assign({ buttons: 0 }, base)));
            el.click();
          };
          const label = el => (el.innerText || '').replace(/\s+/g, ' ').trim();
          const picker = () => document.querySelector('button.__composer-pill[aria-haspopup="menu"]');
          const choices = () => {
            const found = [...document.querySelectorAll('[role="menuitemradio"]')];
            return found.length ? found : null;
          };
        """;

    private const string ComposerHelpers = """
          const composerText = node => (node.innerText || '').replace(/\r\n?/g, '\n');
          const composer = () => [...document.querySelectorAll('#prompt-textarea, form div[contenteditable="true"]')]
            .find(node => node.isConnected && node.isContentEditable && node.getClientRects().length > 0
              && node.getAttribute('aria-disabled') !== 'true' && !node.closest('[inert]')) || null;
          const readyComposer = async () => {
            const end = Date.now() + 30000;
            while (Date.now() < end) {
              check();
              const node = composer();
              if (node && !document.querySelector('[data-testid="stop-button"]')
                  && !document.querySelector('button[aria-label*="Stop" i]')) {
                // A finished answer and its replacement editor can land in different React commits.
                await sleep(100);
                if (node === composer()) return node;
              } else await sleep(100);
            }
            throw new Error('the composer did not become editable; nothing was sent');
          };
          const selectComposer = (node, all) => {
            if (node !== composer()) return false;
            node.focus();
            if (document.activeElement !== node) return false;
            // Scope selection to this editor. execCommand(selectAll) can select the whole page
            // when focus is retained by a closing model menu.
            const range = document.createRange();
            range.selectNodeContents(node);
            if (!all) range.collapse(false);
            const selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
            return true;
          };
        """;

    private const string ScriptTemplate = """
        (async () => {
        __HELPERS__
        __COMPOSER_HELPERS__
          const answers = () => [...document.querySelectorAll('[data-message-author-role="assistant"]')];
          const turns = () => [...document.querySelectorAll('[data-testid^="conversation-turn"]')];
          // ChatGPT's own turn, whether or not it ends up holding a message. The class is what says
          // so before there is a message to read; once there is one, the message says so itself.
          const isAnswerTurn = turn => !!turn.querySelector('.agent-turn')
            || !!turn.querySelector('[data-message-author-role="assistant"]');
          const streaming = () => !!document.querySelector('[data-testid="stop-button"]')
            || !!document.querySelector('button[aria-label*="Stop" i]');
          // Attachments show as thumbnails and chips inside the composer's own form. Only the
          // change in the count is read, so anything else the form draws is a constant baseline.
          const attachments = () => document.querySelectorAll(
            'form img, form [data-testid*="attachment" i], form [aria-label*="attachment" i]').length;
          const sendButton = () => document.querySelector('[data-testid="send-button"]')
            || document.querySelector('button[aria-label*="Send" i]');
          const attach = async box => {
            check();
            const expected = __IMAGES__;
            if (!expected) return true;
            const pending = (window.__jarvisChatGpt && window.__jarvisChatGpt.images) || [];
            // The pictures were parked in the page before this ran; fewer than were sent means the
            // page moved under them, and sending as if they had arrived would be a lie.
            if (pending.length < expected) return false;
            const before = attachments();
            const data = new DataTransfer();
            pending.forEach((image, i) => {
              const raw = atob(image.b64);
              const bytes = new Uint8Array(raw.length);
              for (let j = 0; j < raw.length; j++) bytes[j] = raw.charCodeAt(j);
              const ext = String(image.type || '').split('/')[1] || 'png';
              data.items.add(new File([bytes], 'image-' + (i + 1) + '.' + ext.replace(/[^a-z0-9]/gi, ''),
                { type: image.type || 'image/png' }));
            });
            window.__jarvisChatGpt.images = [];

            // The composer's own file input is what its uploader listens to; a paste is the way in
            // when the markup no longer has one.
            let taken = false;
            const input = document.querySelector('form input[type="file"]')
              || document.querySelector('input[type="file"]');
            if (input) {
              try {
                input.files = data.files;
                input.dispatchEvent(new Event('change', { bubbles: true }));
                taken = !!await wait(() => attachments() > before, 4000);
              } catch (e) { taken = false; }
            }
            if (!taken) {
              box.focus();
              box.dispatchEvent(new ClipboardEvent('paste',
                { bubbles: true, cancelable: true, clipboardData: data }));
            }
            return !!await wait(() => attachments() >= before + pending.length, 90000);
          };
          // A turn only grows its row of copy and rate buttons once the page considers it done.
          const finished = node => {
            const turn = node.closest('[data-testid^="conversation-turn"]');
            if (!turn) return false;
            if (turn.querySelector('[data-testid="copy-turn-action-button"]')) return true;
            // A fenced code block grows a copy button of its own the moment it opens, and that
            // says nothing about the answer being over — only one outside the message does.
            return [...turn.querySelectorAll('button[aria-label*="Copy" i]')]
              .some(b => !b.closest('[data-message-author-role]'));
          };
          try {
            let box = await readyComposer();
            const promptPrepared = __PREPARED__;

            const wanted = __MODEL__;
            if (wanted) {
              // The pill lands after the input it sits under, and on the first turn of a session
              // the page is still coming up, so asking once answers for the wrong moment. It needs
              // less patience than the composer did: the markup is already loaded by then, and only
              // the label it carries still has to come back.
              const pill = await wait(picker, 15000);
              if (!pill) {
                return JSON.stringify({ error: 'the composer never grew its model picker, so '
                  + wanted + ' could not be chosen' });
              }
              press(pill);
              const options = await wait(choices, 10000);
              if (!options) return JSON.stringify({ error: 'the model picker did not open' });

              const target = options.find(o => label(o).toLowerCase() === wanted.toLowerCase());
              if (!target) {
                return JSON.stringify({ error: 'this ChatGPT account has no ' + wanted
                  + ' in its model picker — it offers ' + options.map(label).join(', ') });
              }
              if (target.getAttribute('aria-checked') !== 'true') {
                press(target);
                const took = await wait(
                  () => target.getAttribute('aria-checked') === 'true' || !document.body.contains(target),
                  5000);
                if (!took) return JSON.stringify({ error: 'ChatGPT would not switch to ' + wanted });
              }
              // The menu is left open. Measured against chatgpt.com on 2026-09-04: nothing this
              // script can dispatch closes it — a second press, Escape on the menu, on the document
              // or on a focused item, and a click on the body all leave it standing, because it
              // answers trusted input only. It costs nothing: with the menu open the composer still
              // takes focus, execCommand still types into it, and the send button still enables.
              await sleep(200);
            }

            const promptText = __PROMPT__;
            const omissionNote = __NOTE__;
            const attachedOk = promptPrepared ? !window.__jarvisChatGpt.imagesOmitted : await attach(box);

            const before = new Set(answers().map(n => n.getAttribute('data-message-id')));
            const turnsBefore = new Set(turns().map(t => t.getAttribute('data-testid')));
            // A picture the page would not take is said in the message rather than dropped in
            // silence, which would leave the model answering about a screenshot it never saw.
            const typed = attachedOk || !omissionNote ? promptText : promptText + '\n\n' + omissionNote;
            window.__jarvisChatGpt.imagesOmitted = !attachedOk;
            // Keep each paste below ChatGPT's automatic text-file threshold. The outgoing
            // conversation request is separately checked by PromptWireGate, so markdown
            // serialization can never alter the instructions delivered to the model.
            const clean = typed.replace(/\r\n?/g, '\n');
            const pasteLimit = 8000;
            const rejectedPaste = () => JSON.stringify({
              error: 'ChatGPT did not accept the prompt paste; nothing was sent',
              pasteRejected: !promptPrepared, imagesOmitted: !attachedOk
            });
            box = await readyComposer();
            if (!promptPrepared) {
              if (!selectComposer(box, true)) return rejectedPaste();
              document.execCommand('delete', false, null);
              box = await readyComposer();
              if (composerText(box).trim()) return rejectedPaste();
              for (let offset = 0; offset < clean.length;) {
                check();
                box = await readyComposer();
                // If a remount dropped previous chunks, recover the complete prompt before Send.
                if (offset > 0 && !composerText(box).trim()) return rejectedPaste();
                if (!selectComposer(box, false)) return rejectedPaste();
                const prior = composerText(box);
                let end = Math.min(offset + pasteLimit, clean.length);
                // A Unicode scalar must not be split across two clipboard events.
                if (end < clean.length && /[\uD800-\uDBFF]/.test(clean[end - 1])) end--;
                const data = new DataTransfer();
                data.setData('text/plain', clean.slice(offset, end));
                box.dispatchEvent(new ClipboardEvent('paste',
                  { clipboardData: data, bubbles: true, cancelable: true }));
                const received = await wait(() => {
                  const current = composer();
                  return current && composerText(current) !== prior ? current : null;
                }, 1500);
                if (!received) return rejectedPaste();
                box = received;
                offset = end;
                await sleep(50);
              }
            }
            box = await readyComposer();
            if (!composerText(box).trim()) return rejectedPaste();

            const send = await wait(() => {
              const b = sendButton();
              return b && !b.disabled ? b : null;
            }, 180000);
            if (!send) return JSON.stringify({ error: 'the send button never became available' });
            check();
            send.click();

            // A click can land while the editor is still settling and simply not send: the text
            // stays in the composer, the button stays put, and everything after this would sit
            // waiting for an answer nobody asked for.
            const accepted = await wait(() => streaming() || !sendButton(), 3000);
            if (!accepted) {
              const again = sendButton();
              check();
              if (again && !again.disabled) again.click();
            }

            const fresh = () => answers().filter(n => !before.has(n.getAttribute('data-message-id'))).pop() || null;
            const freshTurn = () => turns()
              .filter(t => !turnsBefore.has(t.getAttribute('data-testid')) && isAnswerTurn(t)).pop() || null;

            // What the turn shows that is not the answer itself: the thinking, the searching, the
            // "working on it". A thinking model can spend minutes with nothing else on the page, so
            // this is reported out as it grows rather than left for the reader to guess at.
            const working = () => {
              const turn = freshTurn();
              if (!turn) return '';
              const whole = (turn.innerText || '').trim();
              const node = fresh();
              const answer = node ? (node.innerText || '').trim() : '';
              // Everything before the answer starts. The turn does not end at its message — the
              // action bar's own labels are in there too — so trimming the answer off the end
              // would leave the answer itself being reported as the model's thinking.
              const at = answer ? whole.lastIndexOf(answer) : -1;
              return at < 0 ? whole : whole.slice(0, at).trim();
            };
            const report = () => { try {
              window.__jarvisChatGpt.progress = working();
              const node = fresh();
              window.__jarvisChatGpt.answerPreview = node ? (node.innerText || '').trim() : '';
            } catch (e) {} };
            window.__jarvisChatGpt.progress = '';

            // Six minutes for an answer to appear at all, which a thinking model may spend
            // entirely on the thinking, with nothing of its own on the page yet.
            let sawStreaming = false;
            let answer = null;
            let parked = null;
            let emptySince = 0;
            const arrival = Date.now() + 360000;
            while (Date.now() < arrival) {
              if (streaming()) sawStreaming = true;
              answer = fresh();
              if (answer) break;

              // Some turns never grow a message node at all: one that ends on a card only a person
              // can answer, and one whose whole answer is a picture. Waiting for a node there waits
              // out the deadline while the page has long since finished. Its action bar settles it
              // when the turn is done; short of that, an answer being written has its node within a
              // second and keeps it, so five seconds without one is the page waiting, not working.
              const turn = freshTurn();
              if (turn && !streaming()) {
                emptySince = emptySince || Date.now();
                if (finished(turn) || Date.now() - emptySince >= 5000) { parked = turn; break; }
              } else {
                emptySince = 0;
              }

              report();
              await sleep(150);
            }
            if (parked) {
              // The text is worth returning even so: the conversation holds it more cleanly, and
              // this is what is left if that cannot be read.
              return JSON.stringify({
                text: (parked.innerText || '').trim(),
                conversationId: (location.pathname.match(/\/c\/([0-9a-fA-F-]{16,})/) || [])[1] || null,
                error: null
              });
            }
            if (!answer) {
              return JSON.stringify({ error: sawStreaming
                ? 'ChatGPT was still working after six minutes and had written nothing'
                : 'the send did not take — ChatGPT never started an answer' });
            }

            // Done when the answer's own turn has grown its action bar and stopped changing. The
            // stop button is not that signal: a thinking answer drops it mid-flight, and an answer
            // that dies half-written never brings it back — taking its absence for the end returns
            // half a sentence as if it were the whole reply. The node is looked up again on every
            // pass because the one found here is replaced mid-answer.
            let text = '';
            let settled = false;
            let stable = 0;
            const growth = Date.now() + 780000;
            while (Date.now() < growth) {
              await sleep(250);
              report();
              const node = fresh();
              const current = node ? (node.innerText || '').trim() : '';
              if (node && finished(node) && !streaming() && current.length > 0 && current === text) {
                if (++stable >= 2) { settled = true; break; }
              } else {
                stable = 0;
              }
              text = current;
            }
            if (!settled) {
              return JSON.stringify({ error: streaming()
                ? 'ChatGPT was still writing after thirteen minutes'
                : 'ChatGPT stopped part way through its answer' });
            }

            const id = (location.pathname.match(/\/c\/([0-9a-fA-F-]{16,})/) || [])[1] || null;
            return JSON.stringify({
              text: text,
              conversationId: id,
              error: text.length ? null : 'ChatGPT stopped without writing an answer'
            });
          } catch (e) {
            return JSON.stringify({ error: String(e) });
          }
        })()
        """;

    /// <summary>
    /// Keeps ChatGPT's own authenticated send, but makes its serialized text match the prompt.
    /// Unknown payload shapes are refused before reaching the network.
    /// </summary>
    internal static string RewritePromptBody(string body, string prompt)
    {
        var root = JsonNode.Parse(body) as JsonObject;
        var message = (root?["messages"] as JsonArray)?.OfType<JsonObject>()
            .LastOrDefault(m => m["author"]?["role"]?.GetValue<string>() == "user");
        if (message?["content"]?["parts"] is not JsonArray parts
            || parts.Count(p => p is JsonValue value && value.TryGetValue<string>(out _)) != 1)
            throw new InvalidOperationException("ChatGPT changed its message format; the prompt was not sent.");
        for (var index = 0; index < parts.Count; index++)
        {
            if (parts[index] is JsonValue value && value.TryGetValue<string>(out _))
                parts[index] = NormalizePrompt(prompt);
        }
        return root!.ToJsonString();
    }

    private sealed class PromptWireGate(
        ChatGptWebViewTransport owner,
        string prompt,
        string omissionNote,
        string target,
        string? conversationId,
        string? gizmoId,
        CancellationToken cancellationToken) : IAsyncDisposable
    {
        private readonly ElectronPaneSession _session = owner._session!;
        private readonly string _tabId = owner._tabId!;
        private readonly object _sync = new();
        private readonly HashSet<Task> _pending = [];
        private bool _enabled;
        private bool _sendObserved;
        private bool _projectUnavailable;
        private string? _error;
        public string? Error => Volatile.Read(ref _error);
        public bool SendObserved => Volatile.Read(ref _sendObserved);
        public bool ProjectUnavailable => Volatile.Read(ref _projectUnavailable);

        public async Task StartAsync()
        {
            _session.CdpEvent += OnEvent;
            try
            {
                await SendAsync("Fetch.enable", new JsonObject
                {
                    ["patterns"] = new JsonArray(new JsonObject
                    {
                        ["urlPattern"] = "https://chatgpt.com/backend-api/*conversation*",
                        ["requestStage"] = "Request",
                    }),
                }).ConfigureAwait(false);
                _enabled = true;
            }
            catch { _session.CdpEvent -= OnEvent; throw; }
        }

        private void OnEvent(string tabId, string method, JsonObject args)
        {
            if (tabId != _tabId || method != "Fetch.requestPaused") return;
            var task = ContinueAsync(args);
            lock (_sync) _pending.Add(task);
            _ = RemoveAsync(task);
        }

        private async Task RemoveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            finally { lock (_sync) _pending.Remove(task); }
        }

        private Task<JsonObject> SendAsync(string method, JsonObject args) =>
            owner._dispatcher.InvokeAsync(() => _session.Cdp(_tabId).SendAsync(method, args)).Task.Unwrap();

        private async Task ContinueAsync(JsonObject args)
        {
            var id = args["requestId"]?.GetValue<string>();
            if (id is null) return;
            var request = args["request"] as JsonObject;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await SendAsync("Fetch.failRequest", new JsonObject { ["requestId"] = id, ["errorReason"] = "Aborted" });
                    return;
                }
                var outgoing = new JsonObject { ["requestId"] = id };
                var url = request?["url"]?.GetValue<string>() ?? "";
                var isSend = request?["method"]?.GetValue<string>() == "POST"
                    && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && uri.AbsolutePath.EndsWith("/conversation", StringComparison.Ordinal);
                if (isSend)
                {
                    // Reuse the existing page read for the last route snapshot, so the project
                    // guard adds no Chromium round-trip to a send.
                    var page = Unwrap(await owner.ExecuteAsync(
                        "JSON.stringify({url:location.href,imagesOmitted:window.__jarvisChatGpt?.imagesOmitted === true})"));
                    var omitted = page?["imagesOmitted"]?.GetValue<bool>() == true;
                    var exact = omitted && omissionNote.Length > 0 ? prompt + "\n\n" + omissionNote : prompt;
                    var body = RewritePromptBody(request?["postData"]?.GetValue<string>() ?? "", exact);
                    outgoing["postData"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                    cancellationToken.ThrowIfCancellationRequested();

                    // The request has been paused throughout this synchronous body rewrite. Refuse
                    // it if a delayed SPA redirect or user navigation moved the chat after OpenAsync.
                    var current = page?["url"]?.GetValue<string>() ?? "";
                    if (NavigationError(current, target, conversationId, gizmoId) is { } navigationError)
                    {
                        if (!string.IsNullOrWhiteSpace(gizmoId))
                            Volatile.Write(ref _projectUnavailable, true);
                        throw new InvalidOperationException(navigationError);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await SendAsync("Fetch.continueRequest", outgoing).ConfigureAwait(false);
                if (isSend) Volatile.Write(ref _sendObserved, true);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _error, ex is OperationCanceledException
                    ? "CANCELLED: the turn was cancelled before sending."
                    : "ChatGPT prompt verification failed: " + ex.Message);
                try { await SendAsync("Fetch.failRequest", new JsonObject { ["requestId"] = id, ["errorReason"] = "Aborted" }); }
                catch (Exception failure) { JarvisCode.Core.Utilities.DiagnosticLog.Write("chatgpt: aborting a send failed — " + failure.Message); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _session.CdpEvent -= OnEvent;
            Task[] pending;
            lock (_sync) pending = [.. _pending];
            await Task.WhenAll(pending).ConfigureAwait(false);
            if (_enabled && ReferenceEquals(owner._session, _session))
            {
                try { await SendAsync("Fetch.disable", new JsonObject()).ConfigureAwait(false); }
                catch (Exception ex) { JarvisCode.Core.Utilities.DiagnosticLog.Write("chatgpt: removing prompt gate failed — " + ex.Message); }
            }
        }
    }

    // ---- browser lifetime ---------------------------------------------------------------------

    /// <summary>Starts the browser and waits for a signed-in page; returns a failure to report.</summary>
    private async Task<ChatGptResponse?> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        await _ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _closing.ConfigureAwait(false);
            if (_initialized)
            {
                return null;
            }

            var error = _session is null
                ? await _dispatcher.InvokeAsync(() => StartAsync(cancellationToken)).Task.Unwrap().ConfigureAwait(false)
                : await WaitForSignedInAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                // Preserve a loaded sign-in/challenge page so the recovery button can show it.
                // A failed engine is closed; the next attempt can create it again.
                if (_session is null || error.StartsWith("BROWSER_ERROR:", StringComparison.Ordinal))
                    await _dispatcher.InvokeAsync(CloseAsync).Task.Unwrap().ConfigureAwait(false);
                return new ChatGptResponse(ChatGptResponse.TransportFailure, error);
            }

            _initialized = true;
            return null;
        }
        finally
        {
            _ready.Release();
        }
    }

    private async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AcquireBrowserSlotAsync(cancellationToken);
            // This session gets an engine of its own rather than a window in the
            // app's: it needs its own profile, and its own Chromium switches -
            // suppressing the automation flag is what lets the real browser
            // clear Cloudflare, and with it the page is flagged before the
            // cookies are even read. A switch is set before the app is ready, so
            // it cannot be a per-window setting.
            var host = new ElectronPaneHost(
                new ElectronRuntime(),
                ElectronPaneHost.DefaultAppDirectory,
                BrowserProfileDirectory,
                ["disable-blink-features=AutomationControlled", "disable-background-timer-throttling"]);

            var session = new ElectronPaneSession(host, _dispatcher);
            _session = session;
            _engineExited = _ => _dispatcher.BeginInvoke(async () =>
            {
                if (ReferenceEquals(_session, session)) await CloseAsync();
            });
            host.Exited += _engineExited;
            _view = new JarvisCode.App.Controls.ElectronPaneView();
            _host = CreateHostWindow(_view);

            // The window is the browser's life support, and once shown it has a close button the
            // user can press. Letting that go unnoticed left a dead browser behind: the next turn
            // drove a disposed browser, and showing the window again threw.
            _host.Closed += (_, _) => Forget();
            _host.Show();

            var window = await session.EnsureHostWindowAsync(persistSessions: true, cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            _view.Attach(window);
            await session.ShowAsync(cancellationToken).ConfigureAwait(true);

            await host.RequestAsync(
                "session.setUserAgent", new JsonObject { ["userAgent"] = UserAgent }, cancellationToken)
                .ConfigureAwait(true);
            await InstallCookiesAsync(host, cancellationToken).ConfigureAwait(true);

            _tabId = await session.CreateTabAsync(foreground: true, cancellationToken: cancellationToken).ConfigureAwait(true);
            await AlignClientHintsAsync(session, _tabId).ConfigureAwait(true);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                await session.NavigateAsync(_tabId, Origin + "/", deadline.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is TimeoutException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return "TIMEOUT: ChatGPT did not finish loading in the embedded browser. "
                    + "Open Settings › Providers › ChatGPT and use \"Show ChatGPT window\" to see what it is showing.";
            }

            return await WaitForSignedInAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CloseAsync().ConfigureAwait(true);
            throw;
        }
        catch (ElectronRuntimeUnavailableException ex)
        {
            // The window is opened before the engine is asked for anything, so a
            // failure here would otherwise leave an empty one behind for the user
            // to find and close.
            Close();
            return "ChatGPT's browser session needs the app's browser engine, and it is not available: "
                + ex.Message;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                       or TimeoutException or UnauthorizedAccessException)
        {
            Close();
            return $"BROWSER_ERROR: the embedded browser could not start ({ex.Message}).";
        }
    }

    /// <summary>
    /// Says the same thing in the client hints that the user-agent header says. Setting the header
    /// alone leaves Chromium answering <c>sec-ch-ua</c> from its own brand list, which names
    /// Electron — a browser nobody signs in to ChatGPT with — beside a version that then disagrees
    /// with the header. Best effort: a check that cannot be aligned is not a reason to refuse the
    /// session, and the page works either way.
    /// </summary>
    private static async Task AlignClientHintsAsync(ElectronPaneSession session, string tabId)
    {
        var brands = new JsonArray
        {
            new JsonObject { ["brand"] = "Not_A Brand", ["version"] = "8" },
            new JsonObject { ["brand"] = "Chromium", ["version"] = ChromiumMajor },
            new JsonObject { ["brand"] = "Google Chrome", ["version"] = ChromiumMajor },
        };

        try
        {
            await session.Cdp(tabId).SendAsync("Emulation.setUserAgentOverride", new JsonObject
            {
                ["userAgent"] = UserAgent,
                ["acceptLanguage"] = "en-US,en",
                ["platform"] = "Windows",
                ["userAgentMetadata"] = new JsonObject
                {
                    ["brands"] = brands,
                    ["fullVersion"] = ElectronRuntime.ChromiumVersion,
                    ["platform"] = "Windows",
                    ["platformVersion"] = "15.0.0",
                    ["architecture"] = "x86",
                    ["model"] = "",
                    ["mobile"] = false,
                    ["bitness"] = "64",
                    ["wow64"] = false,
                },
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
            or System.Runtime.InteropServices.COMException)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write($"chatgpt: the client hints were left alone — {ex.Message}");
        }
    }

    /// <summary>
    /// Polls the page until the session answers with an access token. Distinguishes "still behind
    /// Cloudflare" from "signed out", because the two need different things from the user.
    /// </summary>
    private async Task<string?> WaitForSignedInAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _timeout;
        string? lastState = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BrowserGone)
            {
                return "The ChatGPT window was closed before the session was ready. Start it again when you are set.";
            }

            var probe = await RunInPageAsync(
                """
                (async () => {
                  try {
                    const r = await fetch('/api/auth/session', { credentials: 'include' });
                    const t = await r.text();
                    const authed = r.status === 200 && t.indexOf('accessToken') >= 0;
                    return JSON.stringify({ status: r.status, state: authed ? 'ready' : (r.status === 403 ? 'challenge' : 'signed-out') });
                  } catch (e) { return JSON.stringify({ status: -1, state: 'loading' }); }
                })()
                """,
                cancellationToken).ConfigureAwait(false);

            lastState = ReadJsonString(probe, "state") ?? lastState;
            if (lastState == "ready")
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return lastState switch
        {
            "challenge" => "Cloudflare is challenging this session. Open Settings › Providers › ChatGPT, "
                + "click \"Show ChatGPT window\", complete the check once, then send the message again.",
            _ => "The embedded browser is not signed in to ChatGPT. Export cookies from a signed-in "
                + "chatgpt.com tab, or click \"Show ChatGPT window\" and sign in there once.",
        };
    }

    /// <summary>
    /// A real window is required — the engine's window is reparented into one — so it
    /// exists off-screen, out of the taskbar, and is never activated.
    /// </summary>
    private static Window CreateHostWindow(JarvisCode.App.Controls.ElectronPaneView view) => new()
    {
        Title = "ChatGPT session",
        Width = 1100,
        Height = 820,
        Left = -32000,
        Top = -32000,
        ShowInTaskbar = false,
        ShowActivated = false,
        WindowStyle = WindowStyle.SingleBorderWindow,
        Content = view,
    };

    /// <summary>
    /// The cookies the export carries, handed to the engine before the first
    /// navigation. Anything the browser refuses is dropped rather than costing
    /// the whole session, exactly as one malformed cookie used to.
    /// </summary>
    private async Task InstallCookiesAsync(ElectronPaneHost host, CancellationToken cancellationToken)
    {
        // The profile is already keyed by this exact import. Reapplying its old snapshot would
        // overwrite cookies refreshed by ChatGPT or by a manual sign-in after idle retirement.
        if (!NeedsCookieImport(BrowserProfileDirectory)) return;
        if (JsonNode.Parse(_cookies.InjectionJson) is not JsonArray cookies)
        {
            return;
        }

        var payload = new JsonArray();
        foreach (var node in cookies)
        {
            if (node is not JsonObject entry || entry["name"]?.GetValue<string>() is not { Length: > 0 } name)
            {
                continue;
            }

            var domain = entry["domain"]?.GetValue<string>() is { Length: > 0 } value ? value : ".chatgpt.com";
            var path = entry["path"]?.GetValue<string>() is { Length: > 0 } p ? p : "/";
            var secure = entry["secure"]?.GetValue<bool>() ?? false;

            var cookie = new JsonObject
            {
                // The engine takes the url a cookie would have come from rather
                // than a domain and a secure flag separately.
                ["url"] = (secure ? "https://" : "http://") + domain.TrimStart('.') + path,
                ["name"] = name,
                ["value"] = entry["value"]?.GetValue<string>() ?? "",
                ["domain"] = domain,
                ["path"] = path,
                ["secure"] = secure,
                ["httpOnly"] = entry["httpOnly"]?.GetValue<bool>() ?? false,
                ["sameSite"] = MapSameSite(entry["sameSite"]?.GetValue<string>(), secure),
            };

            if ((entry["expirationDate"]?.GetValue<double>() ?? 0) is > 0 and var expiry)
            {
                cookie["expirationDate"] = expiry;
            }

            payload.Add(cookie);
        }

        if (payload.Count == 0)
        {
            return;
        }

        var installed = await host.RequestAsync("session.setCookies", new JsonObject { ["cookies"] = payload }, cancellationToken)
            .ConfigureAwait(false);
        RecordCookieImport(BrowserProfileDirectory, installed["set"]?.GetValue<int>() ?? 0);
    }

    internal static bool NeedsCookieImport(string profileDirectory) =>
        !File.Exists(Path.Combine(profileDirectory, ".jarvis-cookies-imported"));

    internal static void RecordCookieImport(string profileDirectory, int installedCookies)
    {
        // A rejected or empty import stays retryable. The engine deliberately tolerates individual
        // malformed cookies, so a processed nonempty import does not require every entry to land.
        if (installedCookies <= 0) return;
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(Path.Combine(profileDirectory, ".jarvis-cookies-imported"), "1");
    }

    /// <summary>Chromium refuses SameSite=None on a non-secure cookie, so that pair falls back to Lax.</summary>
    private static string MapSameSite(string? sameSite, bool secure) =>
        (sameSite ?? "").Trim().ToLowerInvariant() switch
        {
            "strict" => "strict",
            "no_restriction" or "none" => secure ? "no_restriction" : "lax",
            _ => "lax",
        };

    private void Close() => _ = CloseAsync();

    private async Task CloseAsync()
    {
        var host = _host;
        Forget();
        host?.Close();
        await _closing.ConfigureAwait(false);
    }

    /// <summary>
    /// The browser is gone. Every wait below tests this, because a poll against a disposed
    /// a released browser answers nothing and would otherwise spin out its whole deadline holding the
    /// initialisation lock, leaving the next attempt stuck behind it.
    /// </summary>
    private bool BrowserGone => _session is null;

    /// <summary>
    /// Drops every handle to the browser, so the next turn builds a fresh one instead of driving
    /// a window that is no longer there.
    /// </summary>
    private void Forget()
    {
        var session = _session;
        _session = null;
        _view = null;
        _tabId = null;
        _host = null;
        _initialized = false;
        var releaseSlot = _ownsBrowserSlot;
        _ownsBrowserSlot = false;
        if (session is not null)
        {
            if (_engineExited is not null) session.Host.Exited -= _engineExited;
            _engineExited = null;
            _closing = DisposeBrowserAsync(session, releaseSlot);
        }
        else if (releaseSlot) BrowserSlots.Release();
    }

    private static async Task DisposeBrowserAsync(ElectronPaneSession session, bool releaseSlot)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        finally { if (releaseSlot) BrowserSlots.Release(); }
    }

    // ---- in-page execution --------------------------------------------------------------------

    private sealed class NativeWorkspaceViolationException(string message) : Exception(message);

    private async Task<string> RunInIsolatedWorldAsync(string expression, CancellationToken cancellationToken)
    {
        var world = await IsolatedProvenanceWorld.CreateAsync(this, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No trusted ChatGPT document is open for this authenticated request.");
        var token = "r" + Guid.NewGuid().ToString("N");
        try
        {
            await world.EvaluateAsync(KickoffScript(token, expression), cancellationToken).ConfigureAwait(false);
            var until = DateTime.UtcNow + _timeout + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < until)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = await world.EvaluateAsync(
                    $"window.__jarvisChatGpt?.['{token}']?.result || ''", cancellationToken).ConfigureAwait(false);
                if (raw is not ("\"\"" or "null" or "" or "{}")) return raw;
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("The isolated ChatGPT request did not return in time.");
        }
        finally { await world.CancelAsync(token).ConfigureAwait(false); }
    }

    /// <summary>
    /// An early warning, not a replacement for the provider's committed-turn verification. Read
    /// only the known conversation endpoint, inside the same authenticated page, while the page
    /// is still thinking. Keep one bounded read in flight so a slow store cannot stall the
    /// composer, duplicate requests, or acquire the page semaphore recursively.
    /// </summary>
    private sealed class InflightWorkGuard(
        ChatGptWebViewTransport owner, ChatGptAsk ask, Func<bool> sent) : IAsyncDisposable
    {
        private IsolatedProvenanceWorld? _world;
        private string? _pendingToken;
        private DateTime _pendingUntil;
        private DateTime _nextRead;

        public async Task<string?> CheckAsync(CancellationToken cancellationToken)
        {
            if (!ask.EnforceLocalExecution || ask.ExpectedUserMessageCount < 1 || !sent()) return null;
            cancellationToken.ThrowIfCancellationRequested();
            try { return await CheckCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // A navigation destroys this document's unique context. Never fall back to the
                // page world or reuse a numeric context id that another document could inherit.
                await ClearPendingAsync().ConfigureAwait(false);
                _world = null;
                _nextRead = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                return null;
            }
        }

        private async Task<string?> CheckCoreAsync(CancellationToken cancellationToken)
        {
            if (_pendingToken is { } token)
            {
                var raw = await _world!.EvaluateAsync(
                    $"window.__jarvisChatGpt?.['{token}']?.result || ''", cancellationToken).ConfigureAwait(false);
                if (Unwrap(raw) is { } result)
                {
                    await ClearPendingAsync().ConfigureAwait(false);
                    _nextRead = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    if (result["status"]?.GetValue<int>() is >= 200 and < 300
                        && result["body"]?.GetValue<string>() is { } body
                        && ChatGptWebProvider.CurrentTurnUsesOwnWorkTools(body, ask.ExpectedUserMessageCount))
                    {
                        throw new NativeWorkspaceViolationException("ChatGPT started its own workspace tools in this turn. Jarvis requested Stop "
                            + "and closed the page before releasing it; no local actions or downloads were accepted. "
                            + "Server-side work may already have started; automatic resend is disabled.");
                    }
                }
                else if (DateTime.UtcNow >= _pendingUntil)
                {
                    await ClearPendingAsync().ConfigureAwait(false);
                    _nextRead = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                }
                return null;
            }

            if (DateTime.UtcNow < _nextRead) return null;
            _world ??= await IsolatedProvenanceWorld.CreateAsync(owner, cancellationToken).ConfigureAwait(false);
            // Both the origin read and the later secret-bearing script target one unique isolated
            // context. Navigation destroys that context instead of moving execution to a new page.
            if (_world is null || await _world.EvaluateAsync("location.origin", cancellationToken).ConfigureAwait(false)
                != "\"https://chatgpt.com\"")
            {
                _nextRead = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                return null;
            }
            _pendingToken = "p" + Guid.NewGuid().ToString("N");
            _pendingUntil = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            await _world.EvaluateAsync(KickoffScript(_pendingToken,
                InflightWorkProbeScript(ask.ConversationId, ask.ProvenanceAccessToken?.Invoke())), cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        private async Task ClearPendingAsync()
        {
            if (_pendingToken is not { } token) return;
            _pendingToken = null;
            var world = _world;
            if (world is null) return;
            if (!await world.CancelAsync(token).ConfigureAwait(false)) _world = null;
        }

        public ValueTask DisposeAsync() => new(ClearPendingAsync());
    }

    /// <summary>
    /// Secret-bearing scripts never enter the page's main JavaScript world. The page can replace
    /// Promise/fetch or inspect scheduled callbacks there, including between origin checks. A CDP
    /// unique context id binds this private world to one document and cannot be recycled after a
    /// renderer/navigation change. Failure is not permission to evaluate in the default context.
    /// </summary>
    internal sealed class IsolatedProvenanceWorld(
        ChatGptWebViewTransport owner, ElectronPaneSession session, string tabId, string uniqueContextId)
    {
        private Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken cancellationToken) =>
            owner._dispatcher.InvokeAsync(async () =>
            {
                if (!ReferenceEquals(session, owner._session) || tabId != owner._tabId)
                    throw new InvalidOperationException("The isolated provenance document is no longer open.");
                return await session.Cdp(tabId).SendAsync(method, parameters, cancellationToken).ConfigureAwait(true);
            }).Task.Unwrap();

        internal static async Task<IsolatedProvenanceWorld?> CreateAsync(
            ChatGptWebViewTransport owner, CancellationToken cancellationToken)
        {
            if (owner._session is not { } session || owner._tabId is not { } tabId) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var sender = new IsolatedProvenanceWorld(owner, session, tabId, "");
            JsonObject tree;
            try { tree = await sender.CallAsync("Page.getFrameTree", null, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The isolated ChatGPT document could not be located.");
            }
            var frame = tree["frameTree"]?["frame"];
            if (frame?["id"]?.GetValue<string>() is not { Length: > 0 } frameId
                || !Uri.TryCreate(frame["url"]?.GetValue<string>(), UriKind.Absolute, out var page)
                || page.GetLeftPart(UriPartial.Authority) != Origin) return null;
            var name = "jarvis-provenance-" + Guid.NewGuid().ToString("N");
            var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnContext(string eventTab, string method, JsonObject args)
            {
                if (eventTab == tabId && method == "Runtime.executionContextCreated"
                    && args["context"] is JsonObject context
                    && context["name"]?.GetValue<string>() == name
                    && context["auxData"]?["frameId"]?.GetValue<string>() == frameId
                    && context["uniqueId"]?.GetValue<string>() is { Length: > 0 } uniqueId)
                    created.TrySetResult(uniqueId);
            }
            session.CdpEvent += OnContext;
            try
            {
                await sender.CallAsync("Runtime.enable", null, deadline.Token).ConfigureAwait(false);
                await sender.CallAsync("Page.createIsolatedWorld", new JsonObject
                {
                    ["frameId"] = frameId, ["worldName"] = name,
                    // Deliberately omit grantUniveralAccess: normal same-origin rules remain.
                }, deadline.Token).ConfigureAwait(false);
                var id = await created.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                var world = new IsolatedProvenanceWorld(owner, session, tabId, id);
                return await world.EvaluateAsync("location.origin", deadline.Token).ConfigureAwait(false)
                    == "\"https://chatgpt.com\"" ? world : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The isolated ChatGPT document did not become ready.");
            }
            finally { session.CdpEvent -= OnContext; }
        }

        internal async Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken)
        {
            if (uniqueContextId.Length == 0) throw new InvalidOperationException("No isolated provenance context is available.");
            var result = await CallAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = expression, ["uniqueContextId"] = uniqueContextId,
                ["returnByValue"] = true, ["awaitPromise"] = false, ["timeout"] = 3000,
            }, cancellationToken).ConfigureAwait(false);
            if (result["exceptionDetails"] is not null)
                throw new InvalidOperationException("The isolated provenance script could not be evaluated.");
            return result["result"]?["value"]?.ToJsonString() ?? "null";
        }

        internal async Task<bool> CancelAsync(string token)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await EvaluateAsync($$"""
                    (() => {
                      const pending = window.__jarvisChatGpt?.['{{token}}'];
                      if (pending) { pending.cancelled = true; pending.abort?.(); }
                      return true;
                    })()
                    """, deadline.Token).ConfigureAwait(false);
                while (await EvaluateAsync(CancelOperationScript(token), deadline.Token).ConfigureAwait(false) != "true")
                    await Task.Delay(30, deadline.Token).ConfigureAwait(false);
                await EvaluateAsync($"delete window.__jarvisChatGpt?.['{token}']", deadline.Token).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException)
            {
                // A destroyed document takes its isolated promise and fetch with it.
                return false;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                if (!owner.BrowserGone)
                    await owner._dispatcher.InvokeAsync(owner.CloseAsync).Task.Unwrap().ConfigureAwait(false);
                return false;
            }
        }
    }

    internal static string InflightWorkProbeScript(string? expectedConversationId, string? bearerToken) => $$"""
        (async () => {
          if (location.origin !== 'https://chatgpt.com') return JSON.stringify({ status: 0, body: '' });
          const id = (location.pathname.match(/\/c\/([0-9a-fA-F-]{16,})/) || [])[1];
          const expected = {{JsonValue.Create(expectedConversationId)?.ToJsonString() ?? "null"}};
          if (!id || (expected && id !== expected)) return JSON.stringify({ status: 0, body: '' });
          const controller = new AbortController();
          operation.abort = () => controller.abort();
          if (operation.cancelled) controller.abort();
          const timer = setTimeout(() => controller.abort(), 3000);
          try {
            const headers = { accept: 'application/json' };
            const bearer = {{JsonValue.Create(bearerToken)?.ToJsonString() ?? "null"}};
            if (bearer) headers.authorization = 'Bearer ' + bearer;
            const response = await fetch('https://chatgpt.com/backend-api/conversation/' + encodeURIComponent(id), {
              method: 'GET', credentials: 'include', cache: 'no-store', redirect: 'error', headers,
              signal: controller.signal
            });
            return JSON.stringify({ status: response.status, body: await response.text() });
          } catch (_) {
            // A transient store/auth failure cannot prove tool use. Final verification remains
            // mandatory at the provider before any local action or completion is accepted.
            return JSON.stringify({ status: -1, body: '' });
          } finally {
            clearTimeout(timer);
            operation.abort = null;
          }
        })()
        """;

    private static string BuildFetchScript(
        HttpMethod method,
        string url,
        string? jsonBody,
        string? bearerToken,
        string accept,
        IReadOnlyDictionary<string, string>? extraHeaders,
        TimeSpan timeout)
    {
        var headers = new JsonObject { ["accept"] = accept.Length > 0 ? accept : "*/*" };
        if (jsonBody is not null)
        {
            headers["content-type"] = "application/json";
        }

        if (!string.IsNullOrEmpty(bearerToken))
        {
            headers["authorization"] = "Bearer " + bearerToken;
        }

        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                headers[name] = value;
            }
        }

        var init = new JsonObject
        {
            ["method"] = method.Method,
            ["headers"] = headers,
            ["credentials"] = "include",
            ["redirect"] = "error",
        };
        if (jsonBody is not null)
        {
            init["body"] = jsonBody;
        }

        var milliseconds = ((int)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        // The whole body is read as text: an SSE stream comes back buffered, exactly as the plain
        // transport returns it. The abort fires before the C# deadline so a stalled stream always
        // resolves rather than leaving the call pending.
        return $$"""
            (async () => {
              if (location.origin !== 'https://chatgpt.com') return JSON.stringify({ status: -1, body: 'The ChatGPT document changed.' });
              const ac = new AbortController();
              operation.abort = () => ac.abort();
              if (operation.cancelled) ac.abort();
              const timer = setTimeout(() => { try { ac.abort(); } catch (e) {} }, {{milliseconds}});
              try {
                const init = Object.assign({{init.ToJsonString()}}, { signal: ac.signal });
                const response = await fetch({{JsonValue.Create(url)!.ToJsonString()}}, init);
                const body = await response.text();
                clearTimeout(timer);
                return JSON.stringify({ status: response.status, body: body });
              } catch (e) {
                clearTimeout(timer);
                const aborted = !!(e && e.name === 'AbortError');
                return JSON.stringify({ status: -1, body: aborted
                  ? 'TIMEOUT: the in-page request was aborted.'
                  : ('TRANSPORT_ERROR: ' + String(e)) });
              } finally {
                operation.abort = null;
              }
            })()
            """;
    }

    /// <summary>How many result polls pass between reads of the page's own working text.</summary>
    private const int ProgressEveryPolls = 8;

    private const string ProgressExpression = "(window.__jarvisChatGpt && window.__jarvisChatGpt.progress) || ''";

    /// <summary>
    /// Runs an async expression in the page and returns what it resolved to.
    ///
    /// An evaluation does not await the promise it is handed — it answers as soon as the expression
    /// has a value, which for an async one is the promise itself — so the work is kicked off
    /// synchronously, parks its result in a global under a per-call token, and this polls that
    /// global. The token keeps concurrent calls from reading each other's result.
    /// </summary>
    private async Task<string> RunInPageAsync(
        string asyncExpression, CancellationToken cancellationToken, Action<string>? progress = null,
        Action<string>? answerPreview = null, Func<string?>? failure = null, bool cancelComposer = false,
        Func<CancellationToken, Task<string?>>? inflightGuard = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = "t" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(KickoffScript(token, asyncExpression)).ConfigureAwait(false);
        var completed = false;
        try
        {
            var deadline = DateTime.UtcNow + _timeout + TimeSpan.FromSeconds(10);
            var read = $"(window.__jarvisChatGpt && window.__jarvisChatGpt['{token}'] && window.__jarvisChatGpt['{token}'].result) || ''";
            for (var poll = 0; DateTime.UtcNow < deadline; poll++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failure?.Invoke() is { } error) throw new InvalidOperationException(error);
                if (BrowserGone) throw new InvalidOperationException("the ChatGPT window was closed while it was working");
                if (inflightGuard is not null
                    && await inflightGuard(cancellationToken).ConfigureAwait(false) is { } guardError)
                    throw new InvalidOperationException(guardError);
                var raw = await ExecuteAsync(read).ConfigureAwait(false);
                if (raw is not ("\"\"" or "null" or "" or "{}"))
                {
                    completed = true;
                    return raw;
                }
                if (poll % ProgressEveryPolls == 0)
                {
                    if (progress is not null) await ReportProgressAsync(progress).ConfigureAwait(false);
                    if (answerPreview is not null)
                        await ReportTextAsync(answerPreview, "answerPreview", includeEmpty: true).ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("the embedded browser did not return a result in time");
        }
        finally
        {
            // Never hand the composer to another turn while its abandoned promise can still type
            // or press Send. A bounded cooperative shutdown falls back to disposing the browser.
            if (!completed) await QuiesceAsync(token, cancelComposer).ConfigureAwait(false);
            await ExecuteAsync($"delete window.__jarvisChatGpt?.['{token}']").ConfigureAwait(false);
        }
    }

    internal static string KickoffScript(string token, string expression) => $$"""
        (() => {
          window.__jarvisChatGpt = window.__jarvisChatGpt || {};
          const operation = { cancelled: false, done: false, result: '' };
          window.__jarvisChatGpt[{{JsonValue.Create(token)!.ToJsonString()}}] = operation;
          Promise.resolve().then(async () => {
            try { operation.result = await {{expression}}; }
            catch (e) { operation.result = JSON.stringify({ status: -1, body: 'TRANSPORT_ERROR: ' + String(e) }); }
            finally { operation.done = true; }
          });
          return 'started';
        })()
        """;

    internal static string CancelOperationScript(string token) => $$"""
        (() => {
          const operation = window.__jarvisChatGpt?.[{{JsonValue.Create(token)!.ToJsonString()}}];
          if (operation) operation.cancelled = true;
          return !operation || operation.done;
        })()
        """;

    private async Task QuiesceAsync(string token, bool cancelComposer)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        await ExecuteAsync(CancelOperationScript(token)).ConfigureAwait(false);
        if (cancelComposer) await StopAnswerAsync().ConfigureAwait(false);
        while (!BrowserGone && DateTime.UtcNow < until)
        {
            if (await ExecuteAsync(CancelOperationScript(token)).ConfigureAwait(false) == "true")
            {
                if (!cancelComposer) return;
                break;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        if (!BrowserGone)
            await _dispatcher.InvokeAsync(CloseAsync).Task.Unwrap().ConfigureAwait(false);
    }

    /// <summary>
    /// Hands on whatever the page is showing instead of an answer. A read that fails is nothing to
    /// report: the answer itself is what this call is waiting for, and it is still coming.
    /// </summary>
    private Task ReportProgressAsync(Action<string> progress) => ReportTextAsync(progress, "progress", includeEmpty: false);

    private async Task ReportTextAsync(Action<string> progress, string property, bool includeEmpty)
    {
        var raw = await ExecuteAsync($"window.__jarvisChatGpt?.{property} || ''").ConfigureAwait(false);
        try
        {
            if (JsonNode.Parse(raw) is JsonValue value
                && value.TryGetValue<string>(out var text)
                && (includeEmpty || text.Length > 0))
            {
                progress(text);
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>
    /// Runs one script in the page and answers its value as JSON text, which is
    /// the shape the callers below parse. The engine hands back the value itself,
    /// so it is re-encoded here rather than the callers being rewritten around a
    /// different envelope.
    /// </summary>
    private Task<string> ExecuteAsync(string script, CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session is not { } session || _tabId is not { } tabId)
            {
                return "";
            }

            try
            {
                var value = await BrowserPaneCdp
                    .EvaluateAsync(session.Cdp(tabId), script, replMode: false, cancellationToken)
                    .ConfigureAwait(true);
                return value?.ToJsonString() ?? "null";
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                // A page that threw, or a browser that went away mid-script:
                // the callers read an empty answer as "nothing came back".
                return "";
            }
        }).Task.Unwrap();

    /// <summary>The page answers a JSON-encoded string, so the envelope is unwrapped twice.</summary>
    private static ChatGptResponse ParseFetchResult(string pageResult)
    {
        var envelope = Unwrap(pageResult);
        if (envelope is null)
        {
            return new ChatGptResponse(ChatGptResponse.TransportFailure,
                "BROWSER_ERROR: the embedded browser returned nothing for this request.");
        }

        return new ChatGptResponse(
            envelope["status"]?.GetValue<int>() ?? ChatGptResponse.TransportFailure,
            envelope["body"]?.GetValue<string>() ?? "");
    }

    private static string? ReadJsonString(string pageResult, string property) =>
        Unwrap(pageResult)?[property]?.GetValue<string>();

    private static JsonObject? Unwrap(string pageResult)
    {
        try
        {
            // The outer parse yields the JSON string the page returned; the inner one its payload.
            if (JsonNode.Parse(pageResult) is not JsonValue outer
                || !outer.TryGetValue<string>(out var inner)
                || inner.Length == 0)
            {
                return null;
            }

            return JsonNode.Parse(inner) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
