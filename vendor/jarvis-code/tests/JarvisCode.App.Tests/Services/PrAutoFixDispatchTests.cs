using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;

namespace JarvisCode.App.Tests.Services;

public sealed class PrAutoFixDispatchTests
{
    [Fact]
    public void Genuine_monitor_event_wakes_the_owning_session_and_turns_off_cleanly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var paths = ProfilePaths.Create("pr-dispatch-test-" + Guid.NewGuid().ToString("N"));
            AppServices? services = null;
            ChatViewModel? vm = null;
            var finished = false;
            var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            deadline.Tick += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            deadline.Start();
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var provider = new FakeProvider();
                    services = new AppServices(paths, headless: true, decorateProviders: _ => new ProviderRegistry([provider]));
                    services.Settings.Current.CustomModels.Add(new("fixture", "fixture-model", "Fixture", 200000));
                    services.Settings.Current.DefaultModelId = "fixture-model";
                    vm = new ChatViewModel(services, isCodeSurface: true);
                    vm.NewSession(System.IO.Path.GetTempPath());
                    vm.Session.Messages.Add(ChatMessage.FromUserText("Fix this project's failing tests."));
                    var binding = new PrAutoFixBinding(7, "https://github.com/example/project/pull/7", vm.Session.WorkingDirectory, "feature") { AutoFix = true };
                    vm.PrSnapshotReader = (_, _) => Task.FromResult<JsonObject?>(new JsonObject
                    {
                        ["url"] = binding.Url, ["state"] = "OPEN", ["headRefName"] = "feature", ["localBranch"] = "feature",
                        ["headRefOid"] = new string('a', 40), ["mergeable"] = "MERGEABLE",
                        ["statusCheckRollup"] = new JsonArray(new JsonObject { ["name"] = "tests", ["conclusion"] = "FAILURE", ["detailsUrl"] = "run-1" }),
                    });
                    vm.ConfigurePrAutoFix(binding);
                    var request = await provider.Request.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    Assert.Contains(PrAutoFixPrompts.Enabled, request.SystemPrompt);
                    Assert.Contains(request.Messages, message => message.IsMeta && message.GetText().StartsWith("<ci-monitor-event>", StringComparison.Ordinal));
                    Assert.DoesNotContain(request.Messages, message => !message.IsMeta && message.GetText().StartsWith("<ci-monitor-event>", StringComparison.Ordinal));
                    vm.ConfigurePrAutoFix(binding with { AutoFix = false });
                    Assert.False(vm.Gate.PrAutoFixActive);
                    Assert.False(vm.HasPrAutoFixMonitor);
                    Assert.False(services.UiSettings.Current.SessionPrAutoFix[vm.Session.Id].AutoFix);
                    finished = true;
                }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
            deadline.Stop();
            vm?.Discard();
            services?.Dispose();
            if (!finished && failure is null) failure = new TimeoutException("The monitor did not dispatch a model turn.");
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeProvider : ILlmProvider
    {
        public string Id => "fixture";
        public string DisplayName => "Fixture";
        public bool RequiresApiKey => false;
        public TaskCompletionSource<LlmRequest> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Request.TrySetResult(request);
            await Task.Yield();
            yield return new TextDeltaEvent("Monitor event received.");
            yield return new ResponseCompletedEvent(false, Usage.Zero, StopReasons.EndTurn);
        }
    }
}
