using System.Runtime.CompilerServices;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class SessionSummaryLifecycleTests
{
    [NativeUiFact]
    public async Task Hiding_the_summary_cancels_the_actual_provider_and_stops_refresh_until_shown_again()
    {
        Task? work = null;
        WpfTestThread.Run(() => work = ExerciseAsync());
        await work!.WaitAsync(TimeSpan.FromSeconds(20));

        static async Task ExerciseAsync()
        {
            var paths = ProfilePaths.Create("summary-lifecycle-test-" + Guid.NewGuid().ToString("N"));
            var provider = new ControlledProvider();
            using var services = new AppServices(paths, headless: true, decorateProviders: _ => new ProviderRegistry([provider]));
            services.Settings.Current.CustomModels.Add(new("summary-fixture", "summary-model", "Summary fixture", 200000));
            services.Settings.Current.DefaultModelId = "summary-model";
            var vm = new ChatViewModel(services, isCodeSurface: true);
            try
            {
                vm.NewSession(System.IO.Path.GetTempPath());
                vm.Session.Messages.Add(ChatMessage.FromUserText("The parser is fixed; validation is pending."));
                var store = new SessionSummaryStore(paths.Root, vm.Session.Id);
                vm.SetLiveSummaryVisible(true);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                vm.SetLiveSummaryVisible(false);
                await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Yield();
                Assert.Null(store.Load());
                await vm.RefreshLiveSummaryAsync(force: true);
                Assert.Equal(1, provider.Calls);

                provider.CompleteImmediately = true;
                vm.SetLiveSummaryVisible(true);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (store.Load() is null && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.NotNull(store.Load());
                Assert.Equal(2, provider.Calls);
                Assert.Equal("Validation is pending.", Assert.Single(vm.Transcript.OfType<SessionSummaryItem>()).Markdown);
                vm.SetLiveSummaryVisible(false);
                await vm.RefreshLiveSummaryAsync(force: true);
                Assert.Equal(2, provider.Calls);
            }
            finally { vm.Discard(); }
        }
    }

    private sealed class ControlledProvider : ILlmProvider
    {
        public string Id => "summary-fixture";
        public string DisplayName => "Summary fixture";
        public bool RequiresApiKey => false;
        public bool CompleteImmediately { get; set; }
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Calls++; Started.TrySetResult();
            if (!CompleteImmediately)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { if (token.IsCancellationRequested) Cancelled.TrySetResult(); }
            }
            yield return new TextDeltaEvent("Validation is pending.");
            yield return new ResponseCompletedEvent(false, new Usage(20, 7), StopReasons.EndTurn);
        }
    }
}
