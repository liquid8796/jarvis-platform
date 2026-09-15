using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;

namespace JarvisCode.Cli.Tests;

[Collection("repl")]
public sealed class CliTerminalWorkflowTests
{
    [Fact]
    public void Transcript_scroll_and_diff_detail_navigation_are_bounded()
    {
        var document = new ScrollableDocument("Transcript", Enumerable.Range(0, 100).Select(index => "row " + index).ToArray());
        document.Scroll("scroll:pageDown", 20); Assert.Equal(17, document.Offset);
        document.Scroll("scroll:bottom", 20); Assert.Equal(83, document.Offset);
        document.Scroll("scroll:lineDown", 20); Assert.Equal(83, document.Offset);
        document.Scroll("scroll:top", 20); Assert.Equal(0, document.Offset);
        var diff = new DiffViewer([new("Working tree", [new("one", ["-old", "+new"]), new("two", ["+second"])]),
            new("Turn 1", [new("original", ["+turn edit"])])]);
        Assert.True(diff.Handle("diff:nextFile", 20)); Assert.Equal(1, diff.FileIndex);
        diff.Handle("diff:viewDetails", 20); Assert.True(diff.Details);
        Assert.True(diff.Handle("diff:dismiss", 20)); Assert.False(diff.Details);
        diff.Handle("diff:nextSource", 20); Assert.Equal(1, diff.SourceIndex); Assert.Equal(0, diff.FileIndex);
        Assert.False(diff.Handle("diff:dismiss", 20));
    }

    [Fact]
    public async Task Switching_tabs_keeps_the_running_turn_in_its_own_conversation()
    {
        var oldDirectory = Environment.CurrentDirectory;
        var oldProfile = Environment.GetEnvironmentVariable("JARVISCODE_PROFILE");
        var workspace = Path.Combine(Path.GetTempPath(), "jarvis-tabs-" + Guid.NewGuid().ToString("N"));
        var profile = "test-tabs-" + Guid.NewGuid().ToString("N");
        var profileRoot = "";
        Directory.CreateDirectory(workspace);
        Environment.CurrentDirectory = workspace;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", profile);
        var provider = new HeldProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var options = new CliOptions { Prompt = "", Bare = true, NoSessionPersistence = true, Settings =
                """{"DefaultModelId":"fixture","CustomModels":[{"ProviderId":"fixture","ModelId":"fixture","DisplayName":"Fixture","MaxContextTokens":200000}]}""" };
            using var services = CliServices.Create(options, _ => new ProviderRegistry([provider]));
            profileRoot = services.App.Paths.Root;
            services.App.UiSettings.Current.TrustedWorkspaces.Add(workspace);
            var console = new LiveConsole();
            using var repl = new InteractiveRepl(services, options, console);
            var running = repl.RunAsync(timeout.Token);
            console.SendText("first prompt"); console.Send(new("enter"));
            await provider.FirstStarted.Task.WaitAsync(timeout.Token);
            var firstId = repl.ActiveSessionId;
            console.SendText("queued one"); console.Send(new("enter"));
            console.SendText("queued two"); console.Send(new("enter"));
            console.Send(new("n", Alt: true));
            await Until(() => repl.OpenSessions.Count == 2, timeout.Token);
            var secondId = repl.ActiveSessionId;
            Assert.NotEqual(firstId, secondId);
            console.SendText("second prompt"); console.Send(new("enter"));
            await Until(() => repl.OpenSessions.Single(session => session.Id == secondId).Messages.Any(message => message.GetText() == "second answer"), timeout.Token);
            provider.ReleaseFirst.TrySetResult();
            await Until(() => repl.OpenSessions.Single(session => session.Id == firstId).Messages.Any(message => message.GetText() == "first answer"), timeout.Token);
            await Until(() => provider.Requests.Count == 4, timeout.Token);
            var requests = provider.Requests.ToArray();
            Assert.Contains("queued one", requests[2].Messages.Last(message => message.Role == Role.User && !message.HarnessSystemTurn).GetText());
            Assert.Contains("queued two", requests[3].Messages.Last(message => message.Role == Role.User && !message.HarnessSystemTurn).GetText());
            Assert.DoesNotContain(repl.OpenSessions.Single(session => session.Id == secondId).Messages,
                message => message.GetText() == "first answer");
            Assert.Equal(secondId, repl.ActiveSessionId);
            console.Send(new("1", Alt: true));
            await Until(() => repl.ActiveSessionId == firstId, timeout.Token);
            console.Send(new("o", Ctrl: true));
            await Until(() => console.Output.Contains("Transcript · Ctrl+E", StringComparison.Ordinal), timeout.Token);
            console.Send(new("escape"));
            console.Close();
            Assert.Equal(0, await running);
            Assert.Contains("[1 first prompt]", console.Output);
        }
        finally
        {
            provider.ReleaseFirst.TrySetResult();
            Environment.CurrentDirectory = oldDirectory;
            Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", oldProfile);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, true);
        }
    }

    private static async Task Until(Func<bool> condition, CancellationToken cancellationToken)
    { while (!condition()) await Task.Delay(20, cancellationToken); }

    private sealed class HeldProvider : ILlmProvider
    {
        public string Id => "fixture";
        public string DisplayName => "Fixture";
        public bool RequiresApiKey => false;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<LlmRequest> Requests { get; } = new();
        private int _calls;
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request with { Messages = request.Messages.ToArray() });
            var first = Interlocked.Increment(ref _calls) == 1;
            if (first) { FirstStarted.TrySetResult(); await ReleaseFirst.Task.WaitAsync(cancellationToken); }
            yield return new TextDeltaEvent(first ? "first answer" : "second answer");
            yield return new ResponseCompletedEvent(false, new Usage(10, 2), "end_turn");
        }
    }

    private sealed class LiveConsole : IConsole
    {
        private readonly Channel<KeyPress> _keys = Channel.CreateUnbounded<KeyPress>();
        private readonly StringBuilder _output = new();
        public int Width => 100;
        public int Height => 30;
        public bool IsInteractive => true;
        public bool SupportsAnsi => false;
        public event Action? Resized { add { } remove { } }
        public string Output { get { lock (_output) return Repl.Render.Ansi.Strip(_output.ToString()); } }
        public void Write(string text) { lock (_output) _output.Append(text); }
        public void Send(KeyPress key) => _keys.Writer.TryWrite(key);
        public void SendText(string text) { foreach (var character in text) Send(KeyPress.Typed(character.ToString())); }
        public void Close() => _keys.Writer.TryComplete();
        public async ValueTask<KeyPress?> ReadKeyAsync(CancellationToken cancellationToken)
        {
            try { return await _keys.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }
    }
}
