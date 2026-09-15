using System.IO;
using JarvisCode.Core.Settings;
using JarvisCode.Host;

namespace JarvisCode.App.Tests.Services;

public sealed class ChatGptConversationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-chatgpt-state-" + Guid.NewGuid().ToString("N"));

    private static ChatGptChatEntry Entry(string scope, string conversation) => new()
    {
        ScopeKey = scope,
        Key = "history-" + conversation,
        ToolContractHash = "complete-contract",
        ConversationId = conversation,
        SentMessages = 1,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task IndependentStoreInstancesPreserveEachOthersScopes()
    {
        var desktop = new ChatGptConversationStore(_root);
        var cli = new ChatGptConversationStore(_root);
        await using (await desktop.AcquireScopeAsync("desktop-session", CancellationToken.None))
            desktop.Save("desktop-session", Entry("desktop-session", "remote-desktop"));
        await using (await cli.AcquireScopeAsync("cli-session", CancellationToken.None))
            cli.Save("cli-session", Entry("cli-session", "remote-cli"));

        Assert.Equal("remote-desktop", cli.Read("desktop-session")!.ConversationId);
        Assert.Equal("remote-cli", desktop.Read("cli-session")!.ConversationId);
        Assert.Equal(2, Directory.GetFiles(_root, "*.json").Length);
    }

    [Fact]
    public async Task TheSameScopeWaitsForTheOtherInstancesLease()
    {
        var desktop = new ChatGptConversationStore(_root);
        var cli = new ChatGptConversationStore(_root);
        var first = await desktop.AcquireScopeAsync("same-session", CancellationToken.None);
        var waiting = cli.AcquireScopeAsync("same-session", CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        await first!.DisposeAsync();

        await using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task CancellingAWaitingLeaseDoesNotReleaseTheCurrentOwner()
    {
        var desktop = new ChatGptConversationStore(_root);
        var cli = new ChatGptConversationStore(_root);
        var first = await desktop.AcquireScopeAsync("same-session", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = cli.AcquireScopeAsync("same-session", cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        using var nextCancellation = new CancellationTokenSource();
        var stillWaiting = cli.AcquireScopeAsync("same-session", nextCancellation.Token).AsTask();
        Assert.False(stillWaiting.IsCompleted);
        await first!.DisposeAsync();
        await using var next = await stillWaiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task AnInvalidationTombstonePreventsLegacyStateFromReturning()
    {
        var desktop = new ChatGptConversationStore(_root);
        var legacy = new[] { Entry("session", "old-remote") };
        Assert.Equal("old-remote", desktop.Read("session", legacy)!.ConversationId);
        await using (await desktop.AcquireScopeAsync("session", CancellationToken.None))
            desktop.Save("session", null);

        var cliWithOldSettings = new ChatGptConversationStore(_root);
        Assert.Null(cliWithOldSettings.Read("session", legacy));
        Assert.Single(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public async Task DamagedStateDoesNotFallBackToAnOldSettingsSnapshot()
    {
        var store = new ChatGptConversationStore(_root);
        var legacy = new[] { Entry("session", "old-remote") };
        await using var lease = await store.AcquireScopeAsync("session", CancellationToken.None);
        store.Save("session", Entry("session", "current-remote"));
        File.WriteAllText(Assert.Single(Directory.GetFiles(_root, "*.json")), "{truncated");

        Assert.Null(store.Read("session", legacy));
    }

    [Fact]
    public async Task ScopeNamesCannotEscapeTheDedicatedDirectory()
    {
        var store = new ChatGptConversationStore(_root);
        const string scope = "../../other/settings.json";
        await using var lease = await store.AcquireScopeAsync(scope, CancellationToken.None);
        store.Save(scope, Entry(scope, "remote"));

        var path = Assert.Single(Directory.GetFiles(_root, "*.json"));
        Assert.Equal(Path.GetFullPath(_root), Path.GetDirectoryName(path));
        Assert.Equal("remote", store.Read(scope)!.ConversationId);
    }

    [Fact]
    public async Task ProjectPinsAreIsolatedByAccountAndName()
    {
        var desktop = new ChatGptConversationStore(_root);
        var cli = new ChatGptConversationStore(_root);
        await using (await desktop.AcquireScopeAsync("account-one:Jarvis", CancellationToken.None))
            desktop.SaveProjectId("account-one:Jarvis", "g-p-one");
        Assert.Equal("", cli.ReadProjectId("account-two:Jarvis"));
        await using (await cli.AcquireScopeAsync("account-two:Jarvis", CancellationToken.None))
            cli.SaveProjectId("account-two:Jarvis", "g-p-two");

        Assert.Equal("g-p-one", cli.ReadProjectId("account-one:Jarvis"));
        Assert.Equal("g-p-two", desktop.ReadProjectId("account-two:Jarvis"));
    }

    [Fact]
    public async Task RuntimeConversationAndProjectWritesDoNotSaveApplicationSettings()
    {
        var desktopSettings = new CountingSettingsStore();
        var cliSettings = new CountingSettingsStore();
        var desktop = new SettingsService(desktopSettings, browserStateRoot: _root);
        var cli = new SettingsService(cliSettings, browserStateRoot: _root);
        await using (await desktop.AcquireChatGptScopeAsync("desktop", CancellationToken.None))
            desktop.SaveChatGptChat("desktop", Entry("desktop", "remote-desktop"));
        await using (await cli.AcquireChatGptScopeAsync("cli", CancellationToken.None))
            cli.SaveChatGptChat("cli", Entry("cli", "remote-cli"));
        await using (await desktop.AcquireChatGptScopeAsync("account:project", CancellationToken.None))
            desktop.SaveChatGptProjectId("account:project", "g-p-one");

        Assert.Equal(0, desktopSettings.Saves);
        Assert.Equal(0, cliSettings.Saves);
        Assert.Equal("remote-desktop", cli.ReadChatGptChat("desktop")!.ConversationId);
        Assert.Equal("remote-cli", desktop.ReadChatGptChat("cli")!.ConversationId);
        Assert.Equal("g-p-one", cli.GetChatGptProjectId("account:project"));
    }

    private sealed class CountingSettingsStore : ISettingsStore
    {
        public int Saves { get; private set; }
        public AppSettings Load() => new();
        public void Save(AppSettings settings) => Saves++;
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(resolved).StartsWith("jarvis-chatgpt-state-", StringComparison.Ordinal)
            && Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }
}
