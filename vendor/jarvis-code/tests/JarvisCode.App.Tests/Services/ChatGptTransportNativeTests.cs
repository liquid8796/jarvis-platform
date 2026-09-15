using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using JarvisCode.App.Services;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class ChatGptTransportNativeTests
{
    [ElectronFact]
    public Task ProjectRouteLostAtThePausedSendIsAbortedAndMarkedUnavailable() =>
        RunOnDispatcherAsync(ExerciseProjectRouteGuardAsync);

    [ElectronFact]
    public Task ChromiumSendsConsecutiveExactPromptsAcrossComposerRemountAndCancellationCannotSendLater() =>
        RunOnDispatcherAsync(ExerciseAsync);

    [ElectronFact]
    public Task CancellationDuringNativeInputClosesThePageWithoutSending() =>
        RunOnDispatcherAsync(ExerciseNativeCancellationAsync);

    [ElectronFact]
    public Task NativeDraftRecoveryClearsRejectedPasteWithTrustedInputAndSendsExactlyOnce() =>
        RunOnDispatcherAsync(dispatcher => ExerciseNativeDraftRecoveryAsync(dispatcher, "recover"));

    [ElectronFact]
    public Task NativeDraftRecoveryReselectsReplacementEditorAfterDraftRestoration() =>
        RunOnDispatcherAsync(dispatcher => ExerciseNativeDraftRecoveryAsync(dispatcher, "remount"));

    [ElectronFact]
    public Task NativeDraftRecoveryClearsRestoredWhitespaceBeforeInsertingTheCurrentPrompt() =>
        RunOnDispatcherAsync(dispatcher => ExerciseNativeDraftRecoveryAsync(dispatcher, "whitespace"));

    [ElectronFact]
    public Task NativeDraftRecoveryFailsClosedWhenTheEditorKeepsRejectingDeletion() =>
        RunOnDispatcherAsync(dispatcher => ExerciseNativeDraftRecoveryAsync(dispatcher, "reject"));

    [ElectronFact]
    public Task NativeDraftRecoveryCancellationWhileClearingCannotSendLater() =>
        RunOnDispatcherAsync(dispatcher => ExerciseNativeDraftRecoveryAsync(dispatcher, "cancel"));

    [ElectronFact]
    public Task ColdFirstPromptWaitsForTheReplacementPowerRowWithoutDiscovery() =>
        RunOnDispatcherAsync(dispatcher => ExerciseColdPowerAsync(dispatcher, remountPower: true));

    [ElectronFact]
    public Task ColdFirstPromptIgnoresHiddenStalePowerBeforeTheActiveRow() =>
        RunOnDispatcherAsync(async dispatcher =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
                await ExerciseColdPowerAsync(dispatcher, hiddenStalePower: true);
        });

    [ElectronFact]
    public Task ColdDiscoveryReadsReplacementPowerAndRestoresItsValueWithoutSending() =>
        RunOnDispatcherAsync(dispatcher => ExerciseColdPowerAsync(
            dispatcher, remountPower: true, hiddenStalePower: true, discover: true));

    [ElectronFact]
    public Task ColdDiscoveryWaitsForTheWholePowerRowToMountAfterThreeSeconds() =>
        RunOnDispatcherAsync(dispatcher => ExerciseColdPowerAsync(
            dispatcher, remountPower: true, absentPower: true, discover: true));

    [ElectronFact]
    public Task CancellationWhileColdPowerIsPendingCannotSendAfterItMounts() =>
        RunOnDispatcherAsync(dispatcher => ExerciseColdPowerAsync(dispatcher, remountPower: true, cancel: true));

    private static async Task RunOnDispatcherAsync(Func<Dispatcher, Task> exercise)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await exercise(dispatcher); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(100));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static async Task ExerciseProjectRouteGuardAsync(Dispatcher dispatcher)
    {
        const string gizmoId = "g-p-fixture";
        await using var server = new OfflineChatGptServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var profile = Path.Combine(Path.GetTempPath(), "jarvis-chatgpt-project-guard", Guid.NewGuid().ToString("N"));
        await using var host = new ElectronPaneHost(new ElectronRuntime(), ElectronPaneHost.DefaultAppDirectory, profile,
            [$"host-resolver-rules=MAP chatgpt.com 127.0.0.1:{server.Port}, MAP * ~NOTFOUND",
             "ignore-certificate-errors", "disable-background-networking", "no-proxy-server"]);
        await using var session = new ElectronPaneSession(host, dispatcher, ownsHost: false);
        await session.EnsureHostWindowAsync(persistSessions: false, cancellationToken: deadline.Token, offscreen: true);
        var tab = await session.CreateTabAsync(foreground: true, cancellationToken: deadline.Token);
        await session.NavigateAsync(tab, $"https://chatgpt.com/g/{gizmoId}/project", deadline.Token);

        var transport = (ChatGptWebViewTransport)Activator.CreateInstance(typeof(ChatGptWebViewTransport),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [dispatcher, new ChatGptCookieContext("", "[]") { ScopeId = "project-route-guard" }, TimeSpan.FromSeconds(20)], null)!;
        Set("_session", session); Set("_tabId", tab); Set("_initialized", true);
        void Set(string name, object value) => typeof(ChatGptWebViewTransport)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, value);

        // Reproduce a delayed client-side redirect at the last possible moment: after OpenAsync
        // accepted the project page, but synchronously before ChatGPT creates its fetch request.
        await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), """
            (() => {
              const button = document.querySelector('[data-testid="send-button"]');
              const send = button.onclick;
              button.onclick = function(event) {
                history.replaceState(null, '', '/');
                return send.call(this, event);
              };
            })()
            """, replMode: false, cancellationToken: deadline.Token);

        var reply = await transport.AskAsync(
            new ChatGptAsk("must remain in the configured project", null, gizmoId, null), deadline.Token);

        Assert.True(reply.ProjectUnavailable);
        Assert.Contains("redirected away", reply.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was sent", reply.Error!, StringComparison.Ordinal);
        Assert.Empty(server.Bodies);
    }

    private static async Task ExerciseAsync(Dispatcher dispatcher)
    {
        await using var server = new OfflineChatGptServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(85));
        var profile = Path.Combine(Path.GetTempPath(), "jarvis-chatgpt-offline", Guid.NewGuid().ToString("N"));
        await using var host = new ElectronPaneHost(new ElectronRuntime(), ElectronPaneHost.DefaultAppDirectory, profile,
            [$"host-resolver-rules=MAP chatgpt.com 127.0.0.1:{server.Port}, MAP * ~NOTFOUND",
             "ignore-certificate-errors", "disable-background-networking", "no-proxy-server"]);
        await using var session = new ElectronPaneSession(host, dispatcher, ownsHost: false);
        await session.EnsureHostWindowAsync(persistSessions: false, cancellationToken: deadline.Token, offscreen: true);
        var tab = await session.CreateTabAsync(foreground: true, cancellationToken: deadline.Token);
        await session.NavigateAsync(tab, "https://chatgpt.com/", deadline.Token);

        // Inject only the already-created offline engine. The actual AskAsync, composer script,
        // operation cancellation, Fetch gate and result polling are all production code.
        var transport = (ChatGptWebViewTransport)Activator.CreateInstance(typeof(ChatGptWebViewTransport),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [dispatcher, new ChatGptCookieContext("", "[]") { ScopeId = "offline-fixture" }, TimeSpan.FromSeconds(35)], null)!;
        Set("_session", session); Set("_tabId", tab); Set("_initialized", true);
        void Set(string name, object value) => typeof(ChatGptWebViewTransport)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, value);

        await BrowserPaneCdp.EvaluateAsync(
            session.Cdp(tab),
            "document.querySelector('[role=slider]').removeAttribute('role')",
            replMode: false, cancellationToken: deadline.Token);
        var modelsOnly = await transport.ReadComposerControlsAsync(
            modelLabel: null, includeEfforts: false, cancellationToken: deadline.Token);
        Assert.Empty(modelsOnly.Efforts);
        await BrowserPaneCdp.EvaluateAsync(
            session.Cdp(tab),
            "document.querySelector('[aria-valuenow]').setAttribute('role','slider')",
            replMode: false, cancellationToken: deadline.Token);
        Assert.Equal("1", (await BrowserPaneCdp.EvaluateAsync(
            session.Cdp(tab), "document.querySelector('[role=slider]').getAttribute('aria-valuenow')",
            replMode: false, cancellationToken: deadline.Token))!.GetValue<string>());

        var controls = await transport.ReadComposerControlsAsync("Latest", deadline.Token);
        Assert.Equal("6 Pro", controls.CurrentModelLabel);
        Assert.Equal(new[] { "Latest", "GPT-5.6 Sol", "GPT-5.5" }, controls.Models.Select(option => option.Label));
        Assert.Equal(new[] { "Instant", "Medium", "High", "Extra High", "Pro" }, controls.Efforts.Select(option => option.Label));
        Assert.Equal("2", Assert.Single(controls.Efforts, option => option.Selected).Key);
        var restoredState = JsonNode.Parse((await BrowserPaneCdp.EvaluateAsync(
            session.Cdp(tab),
            "JSON.stringify({model:document.querySelector('[role=menuitemradio][aria-checked=true]').textContent," +
            "power:document.querySelector('[role=slider]').getAttribute('aria-valuenow')})",
            replMode: false, cancellationToken: deadline.Token))!.GetValue<string>())!;
        Assert.Equal("Latest", restoredState["model"]!.GetValue<string>());
        Assert.Equal("2", restoredState["power"]!.GetValue<string>());

        var prompt = string.Concat(Enumerable.Repeat("# System\r\nfile_path 💡 `code` \\windows\n", 3000));
        var reply = await transport.AskAsync(new ChatGptAsk(prompt, "Latest", null, null)
        {
            ComposerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ChatGptWebProvider.PowerOptionKey] = "1",
            },
        }, deadline.Token);
        Assert.Null(reply.Error);
        Assert.Equal("Exact final answer", reply.Text);
        Assert.Single(server.Bodies);
        var body = JsonNode.Parse(server.Bodies.Single())!;
        Assert.Equal(ChatGptWebViewTransport.NormalizePrompt(prompt), body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
        Assert.Equal("Latest", body["model"]!.GetValue<string>());
        Assert.Equal(1, body["power"]!.GetValue<int>());

        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", reply.ConversationId);
        // The completed turn leaves its editor temporarily noneditable. The next picker opening
        // and editor mount arrive separately, as on a React conversation continuation.
        await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "window.beginContinuationTransition()",
            replMode: false, cancellationToken: deadline.Token);
        var followup = string.Concat(Enumerable.Repeat("# Follow-up\r\nKeep the active prompt 💡 intact.\n", 250));
        var secondReply = await transport.AskAsync(new ChatGptAsk(followup, "Latest", null, reply.ConversationId)
        {
            ComposerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ChatGptWebProvider.PowerOptionKey] = "3",
            },
        }, deadline.Token);
        Assert.Null(secondReply.Error);
        Assert.Equal("Exact follow-up answer", secondReply.Text);
        Assert.Equal(reply.ConversationId, secondReply.ConversationId);
        var bodies = server.Bodies.Select(value => JsonNode.Parse(value)!).ToArray();
        Assert.Equal(2, bodies.Length);
        Assert.Equal(ChatGptWebViewTransport.NormalizePrompt(prompt),
            bodies[0]["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
        Assert.Equal(ChatGptWebViewTransport.NormalizePrompt(followup),
            bodies[1]["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
        Assert.Equal("Latest", bodies[1]["model"]!.GetValue<string>());
        Assert.Equal(3, bodies[1]["power"]!.GetValue<int>());
        var diagnostics = JsonNode.Parse((await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
            "JSON.stringify(window.fixture)", replMode: false, cancellationToken: deadline.Token))!.GetValue<string>())!;
        Assert.Equal(2, diagnostics["inputs"]!.AsArray().Count);
        Assert.True(diagnostics["inputs"]![0]!.GetValue<string>() == ChatGptWebViewTransport.NormalizePrompt(prompt),
            "The first live editor must contain exactly its own prompt before send.");
        var actualFollowup = diagnostics["inputs"]![1]!.GetValue<string>();
        var expectedFollowup = ChatGptWebViewTransport.NormalizePrompt(followup);
        // Chromium represents a terminal native-input newline with a caret-only <div><br></div>.
        // innerText adds one layout newline for that block; keep every prompt character intact.
        if (diagnostics["terminalCaretBlocks"]![1]!.GetValue<bool>()
            && actualFollowup == expectedFollowup + "\n")
            actualFollowup = actualFollowup[..^1];
        var firstDifference = Enumerable.Range(0, Math.Min(actualFollowup.Length, expectedFollowup.Length))
            .FirstOrDefault(index => actualFollowup[index] != expectedFollowup[index], -1);
        Assert.True(actualFollowup == expectedFollowup,
            $"Native editor must preserve the prompt: actual length {actualFollowup.Length}, expected {expectedFollowup.Length}, " +
            $"first difference {firstDifference}, actual code {(firstDifference >= 0 ? (int)actualFollowup[firstDifference] : -1)}, " +
            $"expected code {(firstDifference >= 0 ? (int)expectedFollowup[firstDifference] : -1)}.");
        Assert.True(diagnostics["ignoredPastes"]!.GetValue<int>() > 0);
        Assert.True(diagnostics["trustedInputs"]!.GetValue<int>() > 0);
        Assert.True(diagnostics["pickerRemounted"]!.GetValue<bool>());
        Assert.True(diagnostics["editorRemounted"]!.GetValue<bool>());
        Assert.Equal("Unrelated page content", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
            "document.querySelector('#unrelated-content').textContent", replMode: false,
            cancellationToken: deadline.Token))!.GetValue<string>());

        server.HideComposer = true;
        await session.NavigateAsync(tab, "https://chatgpt.com/?cancel-fixture", deadline.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var cancelledAsk = transport.AskAsync(new ChatGptAsk("must never send", null, null, null), cancellation.Token);
        var started = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var value = await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
                "Object.values(window.__jarvisChatGpt || {}).some(v => v && typeof v === 'object' && v.done === false)", false);
            if (value?.GetValue<bool>() == true) { started = true; break; }
            await Task.Delay(25, deadline.Token);
        }
        Assert.True(started, "the cancellation composer operation must actually start before it is cancelled");
        cancellation.Cancel();
        var cancelled = await cancelledAsk.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Contains("cancel", cancelled.Error!, StringComparison.OrdinalIgnoreCase);
        if (typeof(ChatGptWebViewTransport).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(transport) is not null)
        {
            await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "window.mountLateComposer()",
                replMode: false, cancellationToken: deadline.Token);
            await Task.Delay(300, deadline.Token);
            Assert.Equal("", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
                "document.querySelector('#prompt-textarea').innerText", replMode: false,
                cancellationToken: deadline.Token))!.GetValue<string>());
        }
        await Task.Delay(300, deadline.Token);
        Assert.Equal(2, server.Bodies.Count);
    }

    private static async Task ExerciseNativeCancellationAsync(Dispatcher dispatcher)
    {
        await using var server = new OfflineChatGptServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var profile = Path.Combine(Path.GetTempPath(), "jarvis-chatgpt-offline", Guid.NewGuid().ToString("N"));
        await using var host = new ElectronPaneHost(new ElectronRuntime(), ElectronPaneHost.DefaultAppDirectory, profile,
            [$"host-resolver-rules=MAP chatgpt.com 127.0.0.1:{server.Port}, MAP * ~NOTFOUND",
             "ignore-certificate-errors", "disable-background-networking", "no-proxy-server"]);
        await using var session = new ElectronPaneSession(host, dispatcher, ownsHost: false);
        await session.EnsureHostWindowAsync(persistSessions: false, cancellationToken: deadline.Token, offscreen: true);
        var tab = await session.CreateTabAsync(foreground: true, cancellationToken: deadline.Token);
        await session.NavigateAsync(tab, "https://chatgpt.com/", deadline.Token);
        var transport = (ChatGptWebViewTransport)Activator.CreateInstance(typeof(ChatGptWebViewTransport),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [dispatcher, new ChatGptCookieContext("", "[]") { ScopeId = "offline-native-cancel" }, TimeSpan.FromSeconds(35)], null)!;
        typeof(ChatGptWebViewTransport).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, session);
        typeof(ChatGptWebViewTransport).GetField("_tabId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, tab);
        typeof(ChatGptWebViewTransport).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, true);
        // Stop after native input actually reaches Chromium, while further chunks are pending.
        // The transport must close that page before releasing it to any later turn.
        await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
            "document.querySelector('#prompt-textarea').remove(); window.fixture.trustedInputs = 0; window.mountLateComposer(true)",
            replMode: false, cancellationToken: deadline.Token);
        using var nativeCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var nativeAsk = transport.AskAsync(new ChatGptAsk(
            string.Concat(Enumerable.Repeat("Cancel pending native input 💡\n", 5000)), null, null, null), nativeCancellation.Token);
        var nativeStarted = false;
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var value = await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "window.fixture.trustedInputs", false,
                cancellationToken: deadline.Token);
            if (value?.GetValue<int>() > 0) { nativeStarted = true; break; }
            await Task.Delay(10, deadline.Token);
        }
        Assert.True(nativeStarted, "At least one trusted native input must reach the editor before cancellation.");
        nativeCancellation.Cancel();
        var nativeCancelled = await nativeAsk.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Contains("cancel", nativeCancelled.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(typeof(ChatGptWebViewTransport).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(transport));
        await Task.Delay(300, deadline.Token);
        Assert.Empty(server.Bodies);
    }

    private static async Task ExerciseNativeDraftRecoveryAsync(Dispatcher dispatcher, string mode)
    {
        await using var server = new OfflineChatGptServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var profile = Path.Combine(Path.GetTempPath(), "jarvis-chatgpt-offline", Guid.NewGuid().ToString("N"));
        await using var host = new ElectronPaneHost(new ElectronRuntime(), ElectronPaneHost.DefaultAppDirectory, profile,
            [$"host-resolver-rules=MAP chatgpt.com 127.0.0.1:{server.Port}, MAP * ~NOTFOUND",
             "ignore-certificate-errors", "disable-background-networking", "no-proxy-server"]);
        await using var session = new ElectronPaneSession(host, dispatcher, ownsHost: false);
        await session.EnsureHostWindowAsync(persistSessions: false, cancellationToken: deadline.Token, offscreen: true);
        await session.ShowAsync(deadline.Token);
        var tab = await session.CreateTabAsync(foreground: true, cancellationToken: deadline.Token);
        await session.NavigateAsync(tab, "https://chatgpt.com/", deadline.Token);
        var transport = (ChatGptWebViewTransport)Activator.CreateInstance(typeof(ChatGptWebViewTransport),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [dispatcher, new ChatGptCookieContext("", "[]") { ScopeId = "offline-native-draft" }, TimeSpan.FromSeconds(35)], null)!;
        typeof(ChatGptWebViewTransport).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, session);
        typeof(ChatGptWebViewTransport).GetField("_tabId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, tab);
        typeof(ChatGptWebViewTransport).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, true);

        // Model a ProseMirror draft whose synthetic paste was ignored/partially restored and
        // whose legacy execCommand delete does nothing. Only real Chromium editing can clear it.
        await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), """
            (() => {
              const mode = __MODE__;
              const stale = 'STALE PREVIOUS DRAFT — not the current request';
              window.fixture.draftDeletes = 0;
              window.fixture.draftRemounts = 0;
              const exec = document.execCommand.bind(document);
              document.execCommand = (command, ...args) =>
                command.toLowerCase() === 'delete' ? false : exec(command, ...args);
              const attachDraft = () => {
                const box = document.querySelector('#prompt-textarea');
                box.textContent = stale;
                box.addEventListener('paste', e => {
                  if (!e.isTrusted) box.textContent = stale;
                });
                box.addEventListener('beforeinput', e => {
                  if (!e.isTrusted || !e.inputType.startsWith('delete')) return;
                  window.fixture.draftDeletes++;
                  if (mode === 'reject' || mode === 'cancel') e.preventDefault();
                  if (mode === 'whitespace' && window.fixture.draftDeletes === 1) {
                    e.preventDefault();
                    // Whitespace is a real old draft, not an empty ProseMirror caret block.
                    box.textContent = '  \n\t  \n';
                  }
                  if (mode === 'remount' && window.fixture.draftRemounts === 0) {
                    e.preventDefault();
                    box.remove(); window.mountLateComposer(true); attachDraft();
                    window.fixture.draftRemounts++;
                  }
                  if (mode === 'cancel') {
                    // A delayed framework restoration must not revive the cancelled send.
                    setTimeout(() => { if (box.isConnected) box.textContent = stale; }, 250);
                  }
                });
              };
              document.querySelector('#prompt-textarea').remove();
              window.mountLateComposer(true); attachDraft();
              document.querySelector('#prompt-textarea').textContent = '';
            })()
            """.Replace("__MODE__", JsonValue.Create(mode)!.ToJsonString()), false, cancellationToken: deadline.Token);

        var prompt = string.Concat(Enumerable.Repeat("# Current request\r\nKeep every character 💡 `code` \\windows\n", 180))
            + "The actual end of this request.";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var ask = transport.AskAsync(new ChatGptAsk(prompt, null, null, null), cancellation.Token);
        if (mode == "cancel")
        {
            var clearingStarted = false;
            for (var attempt = 0; attempt < 1500 && !ask.IsCompleted; attempt++)
            {
                var state = await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "window.fixture.draftDeletes", false,
                    cancellationToken: deadline.Token);
                if (state?.GetValue<int>() > 0) { clearingStarted = true; break; }
                await Task.Delay(10, deadline.Token);
            }
            Assert.True(clearingStarted, "Cancel only after trusted native deletion reaches the old draft.");
            cancellation.Cancel();
            var cancelled = await ask.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Contains("cancel", cancelled.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(typeof(ChatGptWebViewTransport).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(transport));
            await Task.Delay(500, deadline.Token);
            Assert.Empty(server.Bodies);
            return;
        }

        var reply = await ask;
        var diagnostics = JsonNode.Parse((await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
            "JSON.stringify({...window.fixture, editorHtml:document.querySelector('#prompt-textarea')?.innerHTML," +
            "editorText:document.querySelector('#prompt-textarea')?.textContent," +
            "editorInnerText:document.querySelector('#prompt-textarea')?.innerText})", false,
            cancellationToken: deadline.Token))!.GetValue<string>())!;
        Assert.True(diagnostics["ignoredPastes"]!.GetValue<int>() > 0, "Synthetic paste must actually fail in this fixture.");
        Assert.True(diagnostics["draftDeletes"]!.GetValue<int>() > 0,
            "Trusted native deletion must reach the editor; a synthetic execCommand is deliberately ineffective. " + reply.Error);
        Assert.Equal("Unrelated page content", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
            "document.querySelector('#unrelated-content')?.textContent", false,
            cancellationToken: deadline.Token))?.GetValue<string>());
        if (mode == "reject")
        {
            Assert.NotNull(reply.Error);
            Assert.Contains("nothing was sent", reply.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(diagnostics["inputs"]!.AsArray());
            await Task.Delay(300, deadline.Token);
            Assert.Empty(server.Bodies);
            return;
        }

        Assert.True(reply.Error is null, reply.Error + "\n" + diagnostics);
        Assert.Equal("Exact final answer", reply.Text);
        var expected = ChatGptWebViewTransport.NormalizePrompt(prompt);
        // Check the editor too: the outgoing request gate always restores the exact requested
        // wire text and must not hide an editor containing the stale draft plus the new prompt.
        Assert.Equal(expected, Assert.Single(diagnostics["inputs"]!.AsArray())!.GetValue<string>());
        Assert.True(diagnostics["trustedInputs"]!.GetValue<int>() > 0);
        var body = JsonNode.Parse(Assert.Single(server.Bodies))!;
        Assert.Equal(expected, body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
        if (mode == "remount") Assert.Equal(1, diagnostics["draftRemounts"]!.GetValue<int>());
        if (mode == "whitespace") Assert.True(diagnostics["draftDeletes"]!.GetValue<int>() >= 2,
            "The restored spaces/newlines must be deleted too, not accepted as an already empty draft.");
    }

    private static async Task ExerciseColdPowerAsync(
        Dispatcher dispatcher, bool remountPower = false, bool hiddenStalePower = false,
        bool absentPower = false, bool discover = false, bool cancel = false)
    {
        await using var server = new OfflineChatGptServer
        {
            RemountColdPower = remountPower,
            HiddenStalePower = hiddenStalePower,
            WholePowerInitiallyAbsent = absentPower,
            PowerMountDelayMilliseconds = absentPower ? 3500 : cancel ? 2000 : 350,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var profile = Path.Combine(Path.GetTempPath(), "jarvis-chatgpt-offline", Guid.NewGuid().ToString("N"));
        await using var host = new ElectronPaneHost(new ElectronRuntime(), ElectronPaneHost.DefaultAppDirectory, profile,
            [$"host-resolver-rules=MAP chatgpt.com 127.0.0.1:{server.Port}, MAP * ~NOTFOUND",
             "ignore-certificate-errors", "disable-background-networking", "no-proxy-server"]);
        await using var session = new ElectronPaneSession(host, dispatcher, ownsHost: false);
        await session.EnsureHostWindowAsync(persistSessions: false, cancellationToken: deadline.Token, offscreen: true);
        // Production shows its Electron host before using native input. Keep the fixture host
        // offscreen, but mount its compositor too; a never-shown window can ignore every key/click.
        await session.ShowAsync(deadline.Token);
        var tab = await session.CreateTabAsync(foreground: true, cancellationToken: deadline.Token);
        await session.NavigateAsync(tab, "https://chatgpt.com/", deadline.Token);
        var transport = (ChatGptWebViewTransport)Activator.CreateInstance(typeof(ChatGptWebViewTransport),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [dispatcher, new ChatGptCookieContext("", "[]") { ScopeId = "offline-cold-power" }, TimeSpan.FromSeconds(35)], null)!;
        typeof(ChatGptWebViewTransport).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, session);
        typeof(ChatGptWebViewTransport).GetField("_tabId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, tab);
        typeof(ChatGptWebViewTransport).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, true);

        if (discover)
        {
            // This is the first operation on a cold page: no earlier model/effort discovery
            // has opened its picker or allowed the placeholder Power row to hydrate.
            var controls = await transport.ReadComposerControlsAsync("Latest", deadline.Token);
            Assert.Equal(new[] { "Latest", "GPT-5.6 Sol", "GPT-5.5" }, controls.Models.Select(option => option.Label));
            Assert.Equal(new[] { "Instant", "Medium", "High", "Extra High", "Pro" }, controls.Efforts.Select(option => option.Label));
            Assert.Equal("2", Assert.Single(controls.Efforts, option => option.Selected).Key);
            Assert.Equal("2", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
                "document.querySelector('#picker [role=slider]').getAttribute('aria-valuenow')", false,
                cancellationToken: deadline.Token))!.GetValue<string>());
            Assert.Empty(server.Bodies);
        }
        else
        {
            var prompt = string.Concat(Enumerable.Repeat("# First prompt\r\nKeep all text 💡 `code` \\windows\n", 100));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            // Deliberately do not call ReadComposerControlsAsync before this first AskAsync.
            var ask = transport.AskAsync(new ChatGptAsk(prompt, "Latest", null, null)
            {
                ComposerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ChatGptWebProvider.PowerOptionKey] = "3",
                },
            }, cancellation.Token);
            if (cancel)
            {
                var pending = false;
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    var state = await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
                        "window.fixture.powerMountPending && !window.fixture.powerRemounted", false,
                        cancellationToken: deadline.Token);
                    if (state?.GetValue<bool>() == true) { pending = true; break; }
                    await Task.Delay(25, deadline.Token);
                }
                Assert.True(pending, "Cancel only once model selection has started the delayed Power mount.");
                cancellation.Cancel();
                var reply = await ask.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.Contains("cancel", reply.Error!, StringComparison.OrdinalIgnoreCase);
                // Let the original mount timer expire: neither a late slider nor a live editor
                // may revive the cancelled configure/paste/send operation.
                await Task.Delay(server.PowerMountDelayMilliseconds + 300, deadline.Token);
                Assert.Empty(server.Bodies);
                return;
            }

            var result = await ask;
            var diagnostics = result.Error is null ? "" : (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
                """
                (() => {
                  const slider = document.querySelector('#picker [role=slider]');
                  const rect = slider?.getBoundingClientRect();
                  return JSON.stringify({ fixture: window.fixture, readyState: document.readyState,
                    focused: document.hasFocus(), active: document.activeElement?.outerHTML,
                    slider: slider?.outerHTML, rect,
                    hit: rect && document.elementFromPoint(rect.left + rect.width / 4,
                      rect.top + rect.height / 2)?.outerHTML });
                })()
                """, false, cancellationToken: deadline.Token))?.GetValue<string>();
            Assert.True(result.Error is null, result.Error + "\n" + diagnostics);
            Assert.Equal("Exact final answer", result.Text);
            var request = JsonNode.Parse(Assert.Single(server.Bodies))!;
            Assert.Equal("Latest", request["model"]!.GetValue<string>());
            Assert.Equal(3, request["power"]!.GetValue<int>());
            Assert.Equal(ChatGptWebViewTransport.NormalizePrompt(prompt),
                request["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
        }

        if (remountPower)
        {
            Assert.True((await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
                "window.fixture.powerRemounted", false, cancellationToken: deadline.Token))!.GetValue<bool>());
        }
        if (hiddenStalePower)
        {
            Assert.Equal("1", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
                "document.querySelector('#stale-power [role=slider]').getAttribute('aria-valuenow')", false,
                cancellationToken: deadline.Token))!.GetValue<string>());
        }
    }

    private sealed class OfflineChatGptServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _connections = [];
        public readonly ConcurrentQueue<string> Bodies = new();
        public bool HideComposer;
        public bool RemountColdPower;
        public bool HiddenStalePower;
        public bool WholePowerInitiallyAbsent;
        public int PowerMountDelayMilliseconds = 350;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public OfflineChatGptServer()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=chatgpt.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Schannel cannot serve the ephemeral key returned by CreateSelfSigned on Windows.
            // A disposable user-key import supplies its native key handle; no trust store changes.
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.UserKeySet);
            _listener.Start();
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            await using (var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
            {
                try
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                    }, _stop.Token);
                    var bytes = new List<byte>();
                    var one = new byte[1];
                    while (bytes.Count < 65536)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                        bytes.Add(one[0]);
                        if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
                    }
                    var headers = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                    var length = headers.Where(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        .Select(h => int.Parse(h[(h.IndexOf(':') + 1)..].Trim())).SingleOrDefault();
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var post = headers[0].StartsWith("POST /backend-api/f/conversation ", StringComparison.Ordinal);
                    if (post) Bodies.Enqueue(Encoding.UTF8.GetString(body));
                    var response = Encoding.UTF8.GetBytes(post ? "{}" : Page
                        .Replace("__COMPOSER__", HideComposer ? "" : "<div id='prompt-textarea' contenteditable='true'></div>")
                        .Replace("__COLD_POWER__", RemountColdPower ? "true" : "false")
                        .Replace("__HIDDEN_STALE_POWER__", HiddenStalePower ? "true" : "false")
                        .Replace("__ABSENT_POWER__", WholePowerInitiallyAbsent ? "true" : "false")
                        .Replace("__POWER_MOUNT_DELAY__", PowerMountDelayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(post ? "application/json" : "text/html")}; charset=utf-8\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(response, _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException)
                {
                    Console.Error.WriteLine("Offline HTTPS fixture: " + ex);
                    throw;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_connections);
            _certificate.Dispose();
            _stop.Dispose();
        }

        private const string Page = """
            <!doctype html><meta charset="utf-8"><title>Offline ChatGPT transport fixture</title>
            <style>
              #prompt-textarea { white-space:pre-wrap; min-height:24px; }
              [role="menuitem"][aria-label="Power"] { display:block; width:240px; height:32px; }
              [role="slider"] { display:block; width:216px; height:24px; margin:4px 12px; }
            </style>
            <button type="button" class="__composer-pill" aria-haspopup="menu" aria-expanded="false">5.5 High</button>
            <div id="picker" role="menu">
              <div role="menuitem" aria-label="Select model">6 Pro</div>
              <div role="menuitem" aria-label="Power" tabindex="0" aria-describedby="power-status power-help">
                <span role="slider" aria-valuemin="0" aria-valuemax="2" aria-valuenow="1"></span>
              </div>
              <span id="power-status">High, 2 of 3.</span>
              <span id="power-help">Use Left and Right arrow keys to adjust power.</span>
              <div role="menuitemradio" aria-checked="false">Latest</div>
              <div role="menuitemradio" aria-checked="false">GPT-5.6 Sol</div>
              <div role="menuitemradio" aria-checked="true">GPT-5.5</div>
            </div>
            <form>__COMPOSER__<button type="button" data-testid="send-button">Send</button></form>
            <div id="unrelated-content">Unrelated page content</div>
            <script>
            window.fixture = { inputs: [], terminalCaretBlocks: [], ignoredPastes: 0, trustedInputs: 0, pickerRemounted: false, editorRemounted: false, powerMountPending: false, powerRemounted: false, powerEvents: [] };
            let picker = document.querySelector('#picker');
            const pill = document.querySelector('.__composer-pill');
            let power;
            let slider;
            let powerStatus;
            let effortLabels = ['Instant', 'High', 'Pro'];
            const coldPower = __COLD_POWER__;
            const initialPower = picker.querySelector('[aria-label="Power"]');
            const powerTemplate = initialPower.cloneNode(true);
            if (coldPower) {
              // React's initial menuitem exists before its slider does; hydration replaces
              // the entire menuitem, so waiting against this captured node never succeeds.
              initialPower.replaceChildren(document.createTextNode('Loading power'));
              if (__ABSENT_POWER__) initialPower.remove();
              picker.hidden = true;
            }
            if (__HIDDEN_STALE_POWER__) {
              const stale = document.createElement('div');
              stale.id = 'stale-power'; stale.style.display = 'none';
              stale.innerHTML = '<div role="menuitem" aria-label="Power" tabindex="0"><span role="slider" aria-valuemin="0" aria-valuemax="1" aria-valuenow="1" aria-valuetext="Stale"></span></div>';
              picker.before(stale);
            }
            pill.onclick = () => { picker.hidden = false; pill.setAttribute('aria-expanded', 'true'); };
            const setPower = value => {
              if (!slider) return;
              const maximum = Number(slider.getAttribute('aria-valuemax'));
              const index = Math.max(0, Math.min(maximum, value));
              slider.setAttribute('aria-valuenow', String(index));
              powerStatus.textContent = `${effortLabels[index]}, ${index + 1} of ${effortLabels.length}.`;
            };
            const bindPicker = () => {
              power = picker.querySelector('[role="menuitem"][aria-label="Power"]') || initialPower;
              slider = power.querySelector('[role="slider"]');
              powerStatus = picker.querySelector('#power-status');
              power.onkeydown = e => {
                window.fixture.powerEvents.push({ key: e.key, trusted: e.isTrusted });
                const current = Number(slider.getAttribute('aria-valuenow'));
                if (e.key === 'Home') setPower(0);
                else if (e.key === 'ArrowLeft') setPower(current - 1);
                else if (e.key === 'ArrowRight') setPower(current + 1);
              };
              power.onclick = e => {
                window.fixture.powerEvents.push({ click: true, trusted: e.isTrusted, x: e.clientX, y: e.clientY });
                const rect = power.getBoundingClientRect();
                setPower(Math.round(((e.clientX - rect.left) / rect.width) * Number(slider.getAttribute('aria-valuemax'))));
              };
              document.querySelectorAll('[role="menuitemradio"]').forEach(row => row.onclick = () => {
                document.querySelectorAll('[role="menuitemradio"]').forEach(other => other.setAttribute('aria-checked', String(other === row)));
                if (row.textContent === 'Latest') {
                  effortLabels = ['Instant', 'Medium', 'High', 'Extra High', 'Pro'];
                  slider?.setAttribute('aria-valuemax', '4');
                  pill.textContent = '6 Pro';
                  setPower(2);
                } else {
                  effortLabels = ['Instant', 'High', 'Pro'];
                  slider?.setAttribute('aria-valuemax', '2');
                  pill.textContent = row.textContent;
                  setPower(1);
                }
                if (coldPower && !window.fixture.powerMountPending && !window.fixture.powerRemounted) {
                  window.fixture.powerMountPending = true;
                  const placeholder = power;
                  setTimeout(() => {
                    const replacement = powerTemplate.cloneNode(true);
                    const liveSlider = replacement.querySelector('[role="slider"]');
                    liveSlider.setAttribute('aria-valuemax', String(effortLabels.length - 1));
                    if (placeholder.isConnected) placeholder.replaceWith(replacement);
                    else picker.querySelector('[aria-label="Select model"]').after(replacement);
                    bindPicker(); setPower(effortLabels.length === 5 ? 2 : 1);
                    window.fixture.powerMountPending = false;
                    window.fixture.powerRemounted = true;
                  }, __POWER_MOUNT_DELAY__);
                }
              });
            };
            bindPicker();
            const bindEditor = (box, trustedOnly) => {
              box.addEventListener('paste', e => {
                e.preventDefault();
                if (!box.isContentEditable) return;
                if (trustedOnly && !e.isTrusted) { window.fixture.ignoredPastes++; return; }
                const selection = window.getSelection();
                let range = selection.rangeCount ? selection.getRangeAt(0) : document.createRange();
                if (!box.contains(range.commonAncestorContainer)) {
                  range.selectNodeContents(box); range.collapse(false);
                }
                range.deleteContents();
                const text = document.createTextNode(e.clipboardData.getData('text/plain'));
                range.insertNode(text); range.setStartAfter(text); range.collapse(true);
                selection.removeAllRanges(); selection.addRange(range);
              });
              box.addEventListener('input', e => {
                if (trustedOnly && e.isTrusted && e.inputType === 'insertText') window.fixture.trustedInputs++;
              });
            };
            const initialBox = document.querySelector('#prompt-textarea');
            if (initialBox) bindEditor(initialBox, false);
            window.mountLateComposer = (trustedOnly = false) => {
              const box = document.createElement('div');
              box.id = 'prompt-textarea'; box.contentEditable = 'true';
              bindEditor(box, trustedOnly); document.querySelector('form').prepend(box);
            };
            window.beginContinuationTransition = () => {
              const oldBox = document.querySelector('#prompt-textarea');
              oldBox.setAttribute('contenteditable', 'false');
              const oldPicker = picker;
              oldPicker.remove();
              setTimeout(() => {
                picker = oldPicker.cloneNode(true);
                pill.after(picker); bindPicker(); window.fixture.pickerRemounted = true;
              }, 300);
              setTimeout(() => {
                const replacement = document.createElement('div');
                replacement.id = 'prompt-textarea'; replacement.contentEditable = 'true';
                bindEditor(replacement, true); oldBox.replaceWith(replacement);
                window.fixture.editorRemounted = true;
              }, 1600);
            };
            const bindSend = button => { button.onclick = async e => {
              const box = document.querySelector('#prompt-textarea');
              if (!box || !box.isContentEditable || !(box.innerText || '').trim()) return;
              window.fixture.inputs.push(box.innerText.replace(/\r\n?/g, '\n'));
              window.fixture.terminalCaretBlocks.push(box.lastElementChild?.tagName === 'DIV'
                && box.lastElementChild.childNodes.length === 1 && box.lastElementChild.firstChild.nodeName === 'BR');
              const turn = window.fixture.inputs.length;
              e.target.remove();
              box.textContent = ''; box.setAttribute('contenteditable', 'false');
              await fetch('/backend-api/f/conversation', { method: 'POST', headers: {'Content-Type':'application/json'},
                body: JSON.stringify({model:document.querySelector('[role="menuitemradio"][aria-checked="true"]').textContent,
                  power:Number(slider.getAttribute('aria-valuenow')), messages:[{author:{role:'user'},
                  content:{content_type:'text',parts:['escaped editor text instead of the exact prompt']}}]}) });
              history.replaceState(null, '', '/c/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee');
              setTimeout(() => {
                const answer = turn === 1 ? 'Exact final answer' : 'Exact follow-up answer';
                document.body.insertAdjacentHTML('beforeend', `<div data-testid="conversation-turn-${turn}"><div data-message-author-role="assistant" data-message-id="answer-${turn}">${answer}</div><button data-testid="copy-turn-action-button">Copy</button></div>`);
                const next = document.createElement('button');
                next.type = 'button'; next.dataset.testid = 'send-button'; next.textContent = 'Send';
                document.querySelector('form').append(next); bindSend(next);
              }, 250);
            }; };
            bindSend(document.querySelector('[data-testid="send-button"]'));
            </script>
            """;
    }
}
