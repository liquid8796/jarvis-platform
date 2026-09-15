using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class SessionRuntimeIsolationTests
{
    [NativeUiFact]
    public async Task New_session_cancels_old_workers_and_drops_previously_dispatched_notifications()
    {
        Task? work = null;
        WpfTestThread.Run(() => work = ExerciseAsync());
        await work!.WaitAsync(TimeSpan.FromSeconds(15));

        static async Task ExerciseAsync()
        {
            var previousIsolation = Environment.GetEnvironmentVariable("JARVIS_PARITY_ISOLATED");
            Environment.SetEnvironmentVariable("JARVIS_PARITY_ISOLATED", "1");
            var paths = ProfilePaths.Create("session-isolation-test-" + Guid.NewGuid().ToString("N"));
            var provider = new ControlledProvider();
            using var services = new AppServices(paths, headless: true, decorateProviders: _ => new ProviderRegistry([provider]));
            services.Settings.Current.CustomModels.Add(new("runtime-fixture", "runtime-model", "Runtime fixture", 200000));
            services.Settings.Current.DefaultModelId = "runtime-model";
            var vm = new ChatViewModel(services, isCodeSurface: true);
            try
            {
                vm.NewSession(Path.GetTempPath());
                var oldSession = vm.Session.Id;
                var oldWorkers = vm.Workers;
                var oldTasks = vm.Tasks;
                var oldTeams = vm.Teams;
                vm.Gate.AdditionalDirectories = [Path.Combine(Path.GetTempPath(), "old-extra-directory")];
                var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var workerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                oldWorkers.Start("fixture", "old worker", new AgentTurnContext
                {
                    Provider = provider, ModelId = "runtime-model", SystemPrompt = "", Messages = [],
                    Tools = new ToolRegistry([]), PermissionGate = vm.Gate,
                    ToolContext = new ToolExecutionContext { WorkingDirectory = Path.GetTempPath() },
                }, async (_, token) =>
                {
                    workerStarted.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, token); return ToolResult.Success("not reached"); }
                    finally { if (token.IsCancellationRequested) workerCancelled.TrySetResult(); }
                });
                await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                // This queues a dispatcher callback while still on the original session.
                vm.DeliverTaskNotification("old-notice", "completed", "Old session notice", "old session payload");
                vm.NewSession(Path.GetTempPath());
                Assert.NotEqual(oldSession, vm.Session.Id);
                Assert.NotSame(oldWorkers, vm.Workers);
                Assert.NotSame(oldTasks, vm.Tasks);
                Assert.NotSame(oldTeams, vm.Teams);
                Assert.Empty(vm.Gate.AdditionalDirectories);
                await workerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Assert.Empty(vm.Session.Messages);
                Assert.Equal(0, provider.Calls);
            }
            finally
            {
                vm.Discard();
                Environment.SetEnvironmentVariable("JARVIS_PARITY_ISOLATED", previousIsolation);
            }
        }
    }

    private sealed class ControlledProvider : ILlmProvider
    {
        public string Id => "runtime-fixture";
        public string DisplayName => "Runtime fixture";
        public bool RequiresApiKey => false;
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Calls++; await Task.Yield();
            yield return new TextDeltaEvent("Unexpected notification dispatch.");
            yield return new ResponseCompletedEvent(false, Usage.Zero, StopReasons.EndTurn);
        }
    }
}
