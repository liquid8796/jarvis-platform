using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using JarvisCode.App.Services;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Cli;

/// <summary>A lazy STA dispatcher for CLI browser sessions. API-only commands start no browser.</summary>
internal sealed class CliBrowserSessionHost(string profileRoot) : IDisposable
{
    private readonly object _sync = new();
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private bool _disposed;

    internal bool IsStarted => _thread is not null;

    internal void EnsureStarted()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null || ChatGptTransportRegistry.HasBrowserTransport) return;
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                    // Concurrent desktop/CLI processes cannot open the same Chromium profile.
                    ChatGptWebViewTransport.Register(dispatcher,
                        Path.Combine(profileRoot, "cli-browser", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    ready.SetResult(dispatcher);
                    Dispatcher.Run();
                }
                catch (Exception error) { ready.TrySetException(error); }
            }) { IsBackground = true, Name = "Jarvis CLI browser" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _dispatcher = ready.Task.GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_dispatcher is null) return;
            try { ChatGptWebViewTransport.ShutdownAsync().Wait(TimeSpan.FromSeconds(10)); }
            finally
            {
                ChatGptTransportRegistry.Reset();
                _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                _thread?.Join(TimeSpan.FromSeconds(5));
            }
        }
    }

    internal IProviderRegistry Wrap(IProviderRegistry registry) => new Registry(registry, this);

    private sealed class Registry(IProviderRegistry inner, CliBrowserSessionHost host) : IProviderRegistry
    {
        private ILlmProvider Wrap(ILlmProvider provider) => provider.Id == ChatGptWebProvider.ProviderId
            ? new Provider(provider, host) : provider;
        public IReadOnlyList<ILlmProvider> All => [.. inner.All.Select(Wrap)];
        public ILlmProvider Get(string providerId) => Wrap(inner.Get(providerId));
    }

    private sealed class Provider(ILlmProvider inner, CliBrowserSessionHost host) : ILlmProvider, IDecoratedProvider
    {
        public ILlmProvider InnerProvider => inner;
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public bool RequiresApiKey => inner.RequiresApiKey;
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            host.EnsureStarted();
            await foreach (var item in inner.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}
