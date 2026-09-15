using System.IO;
using System.Runtime.CompilerServices;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;

namespace JarvisCode.App.Tests.Services;

public sealed class SessionSummaryAndSidebarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-summary-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Summary_calls_the_given_provider_once_then_reuses_persisted_source_provenance()
    {
        var provider = new SummaryProvider();
        var store = new SessionSummaryStore(_root, "session");
        ChatMessage[] messages = [ChatMessage.FromUserText("Repair the widget."),
            new ChatMessage(Role.Assistant, [new TextBlock("The parser was changed; tests have not run.")])];
        var result = await store.GenerateAsync(provider, "chosen-model", messages, default);
        Assert.Equal("metered-test", result.ProviderId);
        Assert.Equal("chosen-model", result.ModelId);
        Assert.Equal(2, result.SourceMessages);
        Assert.Contains("Pending validation", result.Markdown);
        Assert.Equal(7, result.Usage.OutputTokens);
        Assert.Single(provider.Calls);
        Assert.Empty(provider.Calls[0].Tools);
        Assert.Equal(1536, provider.Calls[0].MaxOutputTokens);
        Assert.Contains("tests have not run", provider.Calls[0].Messages[0].GetText());
        Assert.Equal(result, await store.GenerateAsync(provider, "chosen-model", messages, default));
        Assert.Single(provider.Calls);
        await store.GenerateAsync(provider, "chosen-model", messages, default, force: true);
        Assert.Equal(2, provider.Calls.Count);
    }

    [Fact]
    public async Task Cancelled_or_unfinished_summary_never_replaces_a_saved_result()
    {
        var store = new SessionSummaryStore(_root, "session");
        var saved = await store.GenerateAsync(new SummaryProvider(), "model", [ChatMessage.FromUserText("First")], default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GenerateAsync(new SummaryProvider(), "model",
            [ChatMessage.FromUserText("Changed")], cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GenerateAsync(new SummaryProvider { Finish = false }, "model",
            [ChatMessage.FromUserText("Changed")], default));
        Assert.Equal(saved, store.Load());
    }

    [Fact]
    public void Row_window_holds_visible_identities_until_interaction_finishes()
    {
        var rows = Enumerable.Range(0, 25).Select(i => Row("old-" + i)).ToArray();
        var prior = rows.Take(20).Select(r => r.Id).ToArray();
        var changed = new[] { Row("new") }.Concat(rows).ToArray();
        var held = StableSidebarWindow.Select(changed, prior, 20, true);
        Assert.Equal(prior, held.Select(r => r.Id));
        Assert.Equal("new", StableSidebarWindow.Select(changed, prior, 20, false)[0].Id);
        Assert.Equal(21, StableSidebarWindow.Select(changed, prior, 21, true).Count);
        var sections = SidebarPresentation.Build(rows, new SidebarFilterState(), [], "", _ => false, DateTimeOffset.Now);
        var pinned = Assert.Single(sections, s => s.Pinned);
        Assert.Empty(pinned.Rows); Assert.Equal("Nothing pinned yet", pinned.EmptyBody);
        var empty = SidebarPresentation.Build([], new SidebarFilterState(), [], "", _ => false, DateTimeOffset.Now);
        Assert.Equal("No active sessions", Assert.Single(empty, s => s.Key == SidebarPresentation.RecentsKey).EmptyBody);
    }

    private static SidebarSessionInput Row(string id) => new(id, id, "C:\\project", DateTimeOffset.Now, DateTimeOffset.Now);
    private sealed class SummaryProvider : ILlmProvider
    {
        public string Id => "metered-test";
        public string DisplayName => "Metered test provider";
        public bool Finish { get; init; } = true;
        public List<LlmRequest> Calls { get; } = [];
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(request); await Task.Yield();
            yield return new TextDeltaEvent("Changed parser. Pending validation: run the tests.");
            if (Finish) yield return new ResponseCompletedEvent(false, new Usage(20, 7), "end_turn");
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
