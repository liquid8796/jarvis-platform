using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Panels;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// Exercises the pane's real UI entry points against an isolated Electron process.
/// Only a loopback server and throwaway HTML files are loaded; no provider runs.
/// </summary>
[Collection("Native window tests")]
public sealed class BrowserNavigationNativeTests
{
    [ElectronFact]
    public Task OpeningALocalPreviewWhileAnEarlierUiNavigationLoadsDoesNotLeaveAnUnobservedFault() =>
        RunOnDispatcherAsync(async () =>
        {
            await using var fixture = await PaneFixture.CreateAsync();
            await using var server = new HeldFirstResponseServer();
            var unobserved = new ConcurrentQueue<Exception>();
            void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
            {
                if (args.Exception.ToString().Contains("ERR_ABORTED", StringComparison.Ordinal))
                {
                    unobserved.Enqueue(args.Exception);
                    args.SetObserved();
                }
            }

            TaskScheduler.UnobservedTaskException += OnUnobserved;
            try
            {
                fixture.Panel.OpenUrl(server.Url);
                await server.FirstRequest.WaitAsync(fixture.Token);
                var fileUrl = await fixture.CreatePageAsync("Current local preview");
                await fixture.Panel.NavigateTabAsync(fixture.Tab, fileUrl);
                await fixture.AssertDocumentAsync(fileUrl, "Current local preview");
                server.ReleaseFirst();

                // The original defect only surfaced when the dropped Task was
                // finalized, well after the replacement page had already loaded.
                await DrainAndCollectAsync();
                Assert.Empty(unobserved);
                Assert.Empty(fixture.ToastText.Text);
                Assert.False(fixture.Tab.LastNavigationDownloaded);
                Assert.False(fixture.Tab.PinnedFilePreview);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }
        });

    [ElectronFact]
    public Task OverlappingRequestsForTheSameUrlSupersedeOnlyTheOlderNavigation() =>
        RunOnDispatcherAsync(async () =>
        {
            await using var fixture = await PaneFixture.CreateAsync();
            await using var server = new HeldFirstResponseServer();
            var first = fixture.Panel.NavigateTabAsync(fixture.Tab, server.Url);
            await server.FirstRequest.WaitAsync(fixture.Token);
            await fixture.Panel.NavigateTabAsync(fixture.Tab, server.Url);
            await Assert.ThrowsAsync<BrowserNavigationSupersededException>(() => first);
            await fixture.AssertDocumentAsync(server.Url, "Replacement document");

            // Releasing the abandoned HTTP response must not replace the
            // document or disturb the completed navigation's metadata.
            server.ReleaseFirst();
            await Task.Delay(75, fixture.Token);
            await fixture.AssertDocumentAsync(server.Url, "Replacement document");
            Assert.False(fixture.Tab.PinnedFilePreview);
            Assert.False(fixture.Tab.LastNavigationDownloaded);
            Assert.Empty(fixture.ToastText.Text);
            Assert.Equal(2, server.DocumentRequests);
        });

    [ElectronFact]
    public Task AMissingLocalFileReportsInsideThePaneAndRemainsAnErrorForAwaitedNavigation() =>
        RunOnDispatcherAsync(async () =>
        {
            await using var fixture = await PaneFixture.CreateAsync();
            var missing = new Uri(Path.Combine(fixture.Root, "missing.html")).AbsoluteUri;
            fixture.Panel.OpenUrl(missing);
            await WaitUntilAsync(() => fixture.ToastText.Text.Length > 0, fixture.Token);
            Assert.Contains("ERR_FILE_NOT_FOUND", fixture.ToastText.Text, StringComparison.Ordinal);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Panel.NavigateTabAsync(fixture.Tab, missing));
            Assert.Contains("ERR_FILE_NOT_FOUND", error.Message, StringComparison.Ordinal);
            Assert.False(fixture.Tab.LastNavigationDownloaded);
        });

    private static async Task RunOnDispatcherAsync(Func<Task> exercise)
    {
        Dispatcher? dispatcher = null;
        WpfTestThread.Run(() => dispatcher = Dispatcher.CurrentDispatcher);
        await dispatcher!.InvokeAsync(exercise).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static async Task DrainAndCollectAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(75);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(20, token);
    }

    private sealed class PaneFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(75));
        private readonly ElectronPaneHost _host;
        private readonly ElectronPaneSession _session;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "jarvis-browser-navigation", Guid.NewGuid().ToString("N"));
        public BrowserPanel Panel { get; } = new();
        public BrowserPanel.PaneTab Tab => Panel.ActiveTab!;
        public TextBlock ToastText => (TextBlock)Panel.FindName("ToastText");
        public CancellationToken Token => _deadline.Token;

        private PaneFixture()
        {
            Directory.CreateDirectory(Root);
            _host = new ElectronPaneHost(new ElectronRuntime(), ElectronPaneHost.DefaultAppDirectory,
                Path.Combine(Root, "engine"), ["disable-background-networking", "no-proxy-server"]);
            _session = new ElectronPaneSession(_host, Dispatcher.CurrentDispatcher, ownsHost: false);
        }

        public static async Task<PaneFixture> CreateAsync()
        {
            var fixture = new PaneFixture();
            try
            {
                await fixture._session.EnsureHostWindowAsync(false, cancellationToken: fixture.Token, offscreen: true);
                fixture.Tab.EngineTabId = await fixture._session.CreateTabAsync(true, cancellationToken: fixture.Token);
                // The only seam is the already-created native session: navigation,
                // its task lifetime, request ordering, and local toast are real.
                fixture.Tab.Owner.Session = fixture._session;
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<string> CreatePageAsync(string title)
        {
            var file = Path.Combine(Root, "preview.html");
            await File.WriteAllTextAsync(file, $"<!doctype html><title>{title}</title><h1>Ready</h1>", Token);
            return new Uri(file).AbsoluteUri;
        }

        public async Task AssertDocumentAsync(string url, string title)
        {
            var cdp = _session.Cdp(Tab.EngineTabId!);
            var actualUrl = await BrowserPaneCdp.EvaluateAsync(cdp, "location.href", false, cancellationToken: Token);
            var actualTitle = await BrowserPaneCdp.EvaluateAsync(cdp, "document.title", false, cancellationToken: Token);
            Assert.Equal(url, actualUrl!.GetValue<string>());
            Assert.Equal(title, actualTitle!.GetValue<string>());
            Assert.Equal(url, Tab.Url);
        }

        public async ValueTask DisposeAsync()
        {
            Tab.Owner.Session = null;
            Tab.EngineTabId = null;
            Panel.DisposeTabs();
            await _session.DisposeAsync();
            await _host.DisposeAsync();
            _deadline.Dispose();
        }
    }

    private sealed class HeldFirstResponseServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _accept;
        private int _documentRequests;
        public Task FirstRequest => _firstRequest.Task;
        public int DocumentRequests => Volatile.Read(ref _documentRequests);
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/document";

        public HeldFirstResponseServer()
        {
            _listener.Start();
            _accept = AcceptAsync();
        }

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

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
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                try
                {
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_stop.Token);
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                    var document = request?.StartsWith("GET /document ", StringComparison.Ordinal) == true;
                    if (document && Interlocked.Increment(ref _documentRequests) == 1)
                    {
                        _firstRequest.TrySetResult();
                        await _releaseFirst.Task.WaitAsync(_stop.Token);
                    }

                    var body = Encoding.UTF8.GetBytes(document
                        ? "<!doctype html><title>Replacement document</title><h1>Ready</h1>"
                        : "");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
                catch (IOException) { } // The superseded request closes its socket.
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
