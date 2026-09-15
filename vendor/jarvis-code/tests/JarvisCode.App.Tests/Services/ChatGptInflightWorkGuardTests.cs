using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
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
public sealed class ChatGptInflightWorkGuardTests
{
    private const string ConversationId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [ElectronFact]
    public Task UnfinishedNativeContainerRouteStopsBeforeThePageProducesAFinalAnswer() =>
        RunAsync("container");

    [ElectronFact]
    public Task UnfinishedNativePythonToolResultStopsEvenWithoutTheRoutingMessage() =>
        RunAsync("python");

    [ElectronFact]
    public Task PreviousTurnAndAbandonedBranchNativeWorkDoNotStopTheCurrentTurn() =>
        RunAsync("stale");

    [ElectronFact]
    public Task NativeToolNamesInProseAreNotExecutionProvenance() =>
        RunAsync("prose");

    [ElectronFact]
    public Task DirectTransportCompatibilityWithoutPolicyDoesNotStartProvenanceReads() =>
        RunAsync("unrestricted");

    [ElectronFact]
    public Task TransientHistoryFailureRetriesWithoutOverlappingReadsAndThenStopsNativeWork() =>
        RunAsync("transient");

    [ElectronFact]
    public Task CancellationWhileAHistoryReadIsPendingQuiescesThePageWithoutResending() =>
        RunAsync("cancel");

    [ElectronFact]
    public Task MatchingConversationPathOnAnotherOriginCannotIssueAnAuthenticatedProbe() =>
        RunAsync("foreign-origin");

    [ElectronFact]
    public Task HistoryProbeRefusesRedirectsInsteadOfFollowingThemWithCredentials() =>
        RunAsync("redirect");

    [ElectronFact]
    public Task PagePromiseAndFetchOverridesCannotObserveThePrivateProbeCredential() =>
        RunAsync("adversarial");

    [ElectronFact]
    public Task AuthenticatedBackendReadsUseThePrivateWorldInsteadOfPageCallbacks() =>
        RunAsync("authenticated-read");

    [ElectronFact]
    public Task AuthenticatedAssetMetadataAndBytesUseThePrivateWorldInsteadOfPageCallbacks() =>
        RunAsync("authenticated-asset");

    private static async Task RunAsync(string scenario)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await ExerciseAsync(dispatcher, scenario); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(50));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static async Task ExerciseAsync(Dispatcher dispatcher, string scenario)
    {
        await using var server = new FixtureServer(scenario);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var profile = Path.Combine(Path.GetTempPath(), "jarvis-inflight-guard", Guid.NewGuid().ToString("N"));
        await using var host = new ElectronPaneHost(new ElectronRuntime(), ElectronPaneHost.DefaultAppDirectory, profile,
            [$"host-resolver-rules=MAP chatgpt.com 127.0.0.1:{server.Port}, MAP fixture.invalid 127.0.0.1:{server.Port}, MAP * ~NOTFOUND",
             "ignore-certificate-errors", "disable-background-networking", "no-proxy-server"]);
        await using var session = new ElectronPaneSession(host, dispatcher, ownsHost: false);
        await session.EnsureHostWindowAsync(persistSessions: false, cancellationToken: deadline.Token, offscreen: true);
        var tab = await session.CreateTabAsync(foreground: true, cancellationToken: deadline.Token);
        var transport = (ChatGptWebViewTransport)Activator.CreateInstance(typeof(ChatGptWebViewTransport),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [dispatcher, new ChatGptCookieContext("", "[]") { ScopeId = "inflight-" + scenario }, TimeSpan.FromSeconds(25)], null)!;
        Set("_session", session); Set("_tabId", tab); Set("_initialized", true);
        void Set(string name, object value) => typeof(ChatGptWebViewTransport)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, value);
        if (scenario == "foreign-origin")
        {
            await session.NavigateAsync(tab, $"https://chatgpt.com/c/{ConversationId}", deadline.Token);
            var world = await ChatGptWebViewTransport.IsolatedProvenanceWorld.CreateAsync(transport, deadline.Token);
            Assert.NotNull(world);
            Assert.Equal("\"https://chatgpt.com\"", await world.EvaluateAsync("location.origin", deadline.Token));
            await session.NavigateAsync(tab, $"https://fixture.invalid/c/{ConversationId}", deadline.Token);
            await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), AdversarialPageOverrides, false, deadline.Token);
            // Navigate after the origin check, before the secret-bearing callback is evaluated.
            // A destroyed unique context must fail; it must never fall back to this hostile page.
            await Assert.ThrowsAsync<InvalidOperationException>(() => world.EvaluateAsync(
                ChatGptWebViewTransport.KickoffScript("foreignProbe",
                    ChatGptWebViewTransport.InflightWorkProbeScript(ConversationId, "offline-fixture-token")), deadline.Token));
            Assert.Null(await ChatGptWebViewTransport.IsolatedProvenanceWorld.CreateAsync(transport, deadline.Token));
            var forbiddenRead = await transport.SendAsync(HttpMethod.Get, $"/backend-api/conversation/{ConversationId}",
                null, "offline-fixture-token", "application/json", null, deadline.Token);
            Assert.False(forbiddenRead.IsSuccess);
            Assert.Null(await transport.SaveAssetAsync("asset-test", "offline-fixture-token", deadline.Token));
            var forbiddenTarget = await transport.SendAsync(HttpMethod.Get, "https://fixture.invalid/credential-trap",
                null, "offline-fixture-token", "application/json", null, deadline.Token);
            Assert.False(forbiddenTarget.IsSuccess);
            Assert.Equal("[]", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "JSON.stringify(window.leakedCallbacks)", false, deadline.Token))!.GetValue<string>());
            Assert.Equal("[]", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "JSON.stringify(window.leakedFetches)", false, deadline.Token))!.GetValue<string>());
            Assert.Empty(server.Sends);
            Assert.Equal(0, server.HistoryReads);
            Assert.Equal(0, server.AssetMetadataReads);
            Assert.Equal(0, server.AssetDownloads);
            return;
        }
        var continuing = scenario == "stale";
        await session.NavigateAsync(tab, continuing ? $"https://chatgpt.com/c/{ConversationId}" : "https://chatgpt.com/", deadline.Token);
        if (scenario is "authenticated-read" or "authenticated-asset")
        {
            await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), AdversarialPageOverrides, false, deadline.Token);
            if (scenario == "authenticated-read")
            {
                var response = await transport.SendAsync(HttpMethod.Get, $"/backend-api/conversation/{ConversationId}",
                    null, "offline-fixture-token", "application/json", null, deadline.Token);
                Assert.True(response.IsSuccess, response.Body);
                Assert.NotNull(JsonNode.Parse(response.Body)!["mapping"]);
                Assert.Equal(1, server.HistoryReads);
            }
            else
            {
                var folderField = typeof(ChatGptWebViewTransport).GetField("_profileFolder", BindingFlags.Static | BindingFlags.NonPublic)!;
                var previousFolder = folderField.GetValue(null);
                folderField.SetValue(null, profile);
                try
                {
                    var saved = await transport.SaveAssetAsync("asset-test", "offline-fixture-token", deadline.Token);
                    Assert.NotNull(saved);
                    Assert.StartsWith(Path.GetFullPath(profile) + Path.DirectorySeparatorChar, Path.GetFullPath(saved), StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(saved, deadline.Token));
                    File.Delete(saved);
                    Assert.Equal(1, server.AssetMetadataReads);
                    Assert.Equal(1, server.AssetDownloads);
                }
                finally { folderField.SetValue(null, previousFolder); }
            }
            Assert.Equal("[]", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "JSON.stringify(window.leakedCallbacks)", false, deadline.Token))!.GetValue<string>());
            Assert.Equal("[]", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "JSON.stringify(window.leakedFetches)", false, deadline.Token))!.GetValue<string>());
            Assert.Empty(server.Sends);
            return;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var elapsed = Stopwatch.StartNew();
        var asking = transport.AskAsync(new ChatGptAsk("Use the local Jarvis tools only.", null, null,
            continuing ? ConversationId : null)
        {
            EnforceLocalExecution = scenario != "unrestricted",
            ExpectedUserMessageCount = continuing ? 2 : 1,
            ProvenanceAccessToken = () => "offline-fixture-token",
        }, cancel.Token);
        if (scenario == "cancel")
        {
            await server.FirstHistoryRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
        }
        var reply = await asking.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(server.Sends);
        Assert.Equal("Use the local Jarvis tools only.", JsonNode.Parse(server.Sends.Single())!
            ["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
        Assert.False(server.ReadBeforeSend);
        Assert.True(server.MaxConcurrentHistoryReads <= 1);
        if (scenario is "container" or "python" or "transient")
        {
            Assert.True(reply.NativeToolViolation);
            Assert.Contains("own workspace tools", reply.Error);
            Assert.Contains("Server-side work may already have started", reply.Error);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), "Native work must be detected while final output is still absent.");
            Assert.True(server.StopRequests > 0, "The page's own Stop action must be attempted before close.");
            Assert.Null(typeof(ChatGptWebViewTransport).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport));
            if (scenario == "transient") Assert.True(server.HistoryReads >= 2);
        }
        else if (scenario == "cancel")
        {
            Assert.Contains("cancel", reply.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(typeof(ChatGptWebViewTransport).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport));
        }
        else
        {
            Assert.False(reply.NativeToolViolation);
            Assert.Null(reply.Error);
            Assert.Equal("JARVIS_ACT {\"name\":\"Read\",\"arguments\":{\"file_path\":\"README.md\"}}", reply.Text);
            Assert.Equal(scenario == "unrestricted", server.HistoryReads == 0);
            Assert.Equal(0, server.StopRequests);
            Assert.Equal(0, server.RedirectedRequests);
            if (scenario == "adversarial")
            {
                Assert.Equal("[]", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "JSON.stringify(window.leakedCallbacks)", false, deadline.Token))!.GetValue<string>());
                Assert.Equal("[]", (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab), "JSON.stringify(window.leakedFetches)", false, deadline.Token))!.GetValue<string>());
                Assert.Equal(0, (await BrowserPaneCdp.EvaluateAsync(session.Cdp(tab),
                    "Object.keys(window.__jarvisChatGpt || {}).filter(key => /^p[0-9a-f]{32}$/.test(key)).length", false, deadline.Token))!.GetValue<int>());
            }
        }
        var readsAfterReturn = server.HistoryReads;
        await Task.Delay(300, deadline.Token);
        Assert.Single(server.Sends);
        Assert.Equal(readsAfterReturn, server.HistoryReads);
    }

    private const string AdversarialPageOverrides = """
        window.leakedCallbacks = []; window.leakedFetches = [];
        const realResolve = Promise.resolve.bind(Promise);
        Promise.resolve = (...args) => {
          const pending = realResolve(...args); const realThen = pending.then.bind(pending);
          pending.then = (callback, rejected) => { window.leakedCallbacks.push(String(callback)); return realThen(callback, rejected); };
          return pending;
        };
        const realFetch = window.fetch.bind(window);
        window.fetch = (...args) => { window.leakedFetches.push(args); return realFetch(...args); };
        """;

    private sealed class FixtureServer(string scenario) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate = Certificate();
        private readonly ConcurrentBag<Task> _connections = [];
        private Task? _accept;
        private int _activeReads;
        public readonly ConcurrentQueue<string> Sends = new();
        public readonly TaskCompletionSource FirstHistoryRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HistoryReads;
        public int StopRequests;
        public int RedirectedRequests;
        public int AssetMetadataReads;
        public int AssetDownloads;
        public int MaxConcurrentHistoryReads;
        public bool ReadBeforeSend;
        public int Port
        {
            get
            {
                if (_accept is null) { _listener.Start(); _accept = AcceptAsync(); }
                return ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
        }

        private static X509Certificate2 Certificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=chatgpt.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    _connections.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (OperationCanceledException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            await using (var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
            {
                var history = false;
                try
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
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
                    var length = headers.Where(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        .Select(value => int.Parse(value[(value.IndexOf(':') + 1)..].Trim())).SingleOrDefault();
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var post = headers[0].StartsWith("POST /backend-api/f/conversation ", StringComparison.Ordinal);
                    history = headers[0].StartsWith($"GET /backend-api/conversation/{ConversationId} ", StringComparison.Ordinal);
                    var stopping = headers[0].StartsWith("GET /fixture-stop ", StringComparison.Ordinal);
                    var assetMetadata = headers[0].StartsWith("GET /backend-api/files/asset-test/download ", StringComparison.Ordinal);
                    var assetBytes = headers[0].StartsWith("GET /fixture-asset-bytes ", StringComparison.Ordinal);
                    if (assetMetadata)
                    {
                        Interlocked.Increment(ref AssetMetadataReads);
                        Assert.Contains("authorization: Bearer offline-fixture-token", headers, StringComparer.OrdinalIgnoreCase);
                    }
                    if (assetBytes)
                    {
                        Interlocked.Increment(ref AssetDownloads);
                        Assert.DoesNotContain(headers, header => header.StartsWith("authorization:", StringComparison.OrdinalIgnoreCase));
                    }
                    if (headers[0].StartsWith("GET /fixture-redirect-target ", StringComparison.Ordinal))
                        Interlocked.Increment(ref RedirectedRequests);
                    if (post) Sends.Enqueue(Encoding.UTF8.GetString(body));
                    if (stopping) Interlocked.Increment(ref StopRequests);
                    var status = 200;
                    if (history)
                    {
                        ReadBeforeSend |= Sends.IsEmpty;
                        MaxConcurrentHistoryReads = Math.Max(MaxConcurrentHistoryReads, Interlocked.Increment(ref _activeReads));
                        var count = Interlocked.Increment(ref HistoryReads);
                        FirstHistoryRead.TrySetResult();
                        Assert.Contains("authorization: Bearer offline-fixture-token", headers, StringComparer.OrdinalIgnoreCase);
                        if (scenario == "cancel") await Task.Delay(5000, _stop.Token);
                        if (scenario == "transient" && count == 1) status = 503;
                        if (scenario == "redirect") status = 302;
                    }
                    var content = post || stopping ? "{}" : assetMetadata
                        ? "{\"download_url\":\"https://chatgpt.com/fixture-asset-bytes\",\"file_size_bytes\":4,\"file_name\":\"fixture.bin\"}"
                        : history ? History(scenario) : Page
                        .Replace("__FINISH__", scenario is "stale" or "prose" or "unrestricted" or "redirect" or "adversarial" ? "true" : "false")
                        .Replace("__OVERRIDES__", scenario == "adversarial" ? AdversarialPageOverrides : "");
                    var response = assetBytes ? [1, 2, 3, 4] : Encoding.UTF8.GetBytes(content);
                    var redirect = status == 302 ? "Location: https://chatgpt.com/fixture-redirect-target\r\n" : "";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\n{redirect}Content-Type: {(history || post || stopping ? "application/json" : "text/html")}; charset=utf-8\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(response, _stop.Token);
                }
                catch (Exception error) when (error is IOException or AuthenticationException) { }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                finally { if (history) Interlocked.Decrement(ref _activeReads); }
            }
        }

        private static string History(string mode)
        {
            static JsonObject Node(string? parent, string role, string? route = null, string? author = null) => new()
            {
                ["parent"] = parent,
                ["message"] = new JsonObject
                {
                    ["author"] = new JsonObject { ["role"] = role, ["name"] = author }, ["recipient"] = route,
                    ["channel"] = "analysis", ["status"] = "in_progress",
                    ["content"] = new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray("Never use container.exec, python or api_tool; return JARVIS_ACT only.") },
                },
            };
            var mapping = new JsonObject { ["user"] = Node(null, "user") };
            if (mode == "stale")
            {
                mapping["old-native"] = Node("user", "assistant", "container.exec");
                mapping["current-user"] = Node("old-native", "user");
                mapping["answer"] = Node("current-user", "assistant", "all");
                mapping["abandoned-native"] = Node("current-user", "tool", author: "python");
            }
            else mapping["answer"] = mode == "python" ? Node("user", "tool", author: "python")
                : Node("user", "assistant", mode is "prose" or "adversarial" ? "all" : "container.exec");
            return new JsonObject { ["mapping"] = mapping, ["current_node"] = "answer" }.ToJsonString();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            if (_accept is not null) await _accept;
            await Task.WhenAll(_connections);
            _certificate.Dispose();
            _stop.Dispose();
        }

        private const string Page = """
            <!doctype html><meta charset="utf-8"><title>Offline in-flight guard</title>
            <style>#prompt-textarea {white-space:pre-wrap;min-height:24px}</style>
            <form><div id="prompt-textarea" contenteditable="true"></div><button type="button" data-testid="send-button">Send</button></form>
            <script>
            const box = document.querySelector('#prompt-textarea');
            box.addEventListener('paste', e => { e.preventDefault(); box.textContent += e.clipboardData.getData('text/plain'); });
            document.querySelector('[data-testid="send-button"]').onclick = async e => {
              e.target.remove();
              document.body.insertAdjacentHTML('beforeend', '<button data-testid="stop-button">Stop</button>');
              document.querySelector('[data-testid="stop-button"]').onclick = async () => {
                await fetch('/fixture-stop'); document.querySelector('[data-testid="stop-button"]')?.remove();
              };
              await fetch('/backend-api/f/conversation', {method:'POST', headers:{'Content-Type':'application/json'},
                body:JSON.stringify({messages:[{author:{role:'user'},content:{content_type:'text',parts:['editor text']}}]})});
              box.textContent = '';
              history.replaceState(null, '', '/c/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee');
              __OVERRIDES__
              if (__FINISH__) setTimeout(() => {
                document.querySelector('[data-testid="stop-button"]')?.remove();
                const turn = document.createElement('div'); turn.dataset.testid = 'conversation-turn-1';
                const answer = document.createElement('div'); answer.dataset.messageAuthorRole = 'assistant'; answer.dataset.messageId = 'answer';
                answer.textContent = 'JARVIS_ACT {"name":"Read","arguments":{"file_path":"README.md"}}'; turn.append(answer);
                const copy = document.createElement('button'); copy.dataset.testid = 'copy-turn-action-button'; turn.append(copy);
                document.body.append(turn);
              }, 5500);
            };
            </script>
            """;
    }
}
