using JarvisCode.Core.Models;
using JarvisCode.Core.Sessions;
using JarvisCode.Core.Settings;

namespace JarvisCode.Core.Tests.Persistence;

public sealed class StoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SessionStore_RoundTripsAllContentBlockTypes()
    {
        var store = new JsonSessionStore(Path.Combine(_temp.Path, "sessions"));
        var session = Session.CreateNew(_temp.Path);
        session.Title = "Round trip";
        session.AdditionalDirectories = [@"D:\other-source"];
        session.Messages =
        [
            ChatMessage.FromUserText("do something"),
            new ChatMessage(Role.Assistant,
            [
                new ThinkingBlock("weighing it up", "sig") { DurationSeconds = 12.5 },
                new TextBlock("working on it"),
                new ToolCallBlock("call-1", "PowerShell", """{"command":"dir"}"""),
            ]),
            new ChatMessage(Role.User, [new ToolResultBlock("call-1", "PowerShell", "output", IsError: false)]),
        ];

        await store.SaveAsync(session);
        var loaded = await store.LoadAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Round trip", loaded.Title);
        Assert.Equal([@"D:\other-source"], loaded.AdditionalDirectories);
        Assert.Equal(3, loaded.Messages.Count);
        var thinking = Assert.IsType<ThinkingBlock>(loaded.Messages[1].Content[0]);
        Assert.Equal("weighing it up", thinking.Thinking);
        Assert.Equal("sig", thinking.Signature);
        // The UI stamps how long the block took; without it a reopened session
        // cannot say what its thinking header said before.
        Assert.Equal(12.5, thinking.DurationSeconds);
        var toolCall = Assert.IsType<ToolCallBlock>(loaded.Messages[1].Content[2]);
        Assert.Equal("PowerShell", toolCall.Name);
        var toolResult = Assert.IsType<ToolResultBlock>(loaded.Messages[2].Content.Single());
        Assert.Equal("call-1", toolResult.ToolCallId);
    }

    [Fact]
    public async Task SessionStore_ListsNewestFirstAndSurvivesCorruptFiles()
    {
        var directory = Path.Combine(_temp.Path, "sessions");
        var store = new JsonSessionStore(directory);
        var older = Session.CreateNew(_temp.Path);
        older.UpdatedAt = DateTimeOffset.Now.AddHours(-1);
        var newer = Session.CreateNew(_temp.Path);
        await store.SaveAsync(older);
        await store.SaveAsync(newer);
        File.WriteAllText(Path.Combine(directory, "corrupt.json"), "{not json");

        var list = await store.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal(newer.Id, list[0].Id);
        Assert.Equal(_temp.Path, list[0].WorkingDirectory);
    }

    [Fact]
    public async Task SessionStore_Delete_RemovesFile()
    {
        var store = new JsonSessionStore(Path.Combine(_temp.Path, "sessions"));
        var session = Session.CreateNew(_temp.Path);
        await store.SaveAsync(session);

        await store.DeleteAsync(session.Id);

        Assert.Null(await store.LoadAsync(session.Id));
    }

    [Fact]
    public void SettingsStore_RoundTripsAndProtectsKeyLists()
    {
        var path = Path.Combine(_temp.Path, "settings.json");
        var protector = new ReversingProtector();
        var store = new JsonSettingsStore(path, protector);

        var settings = new AppSettings
        {
            DefaultModelId = "claude-sonnet-5",
            OllamaBaseUrl = "http://box:11434",
            NvidiaBaseUrl = "http://nim.box:8000/v1",
            LlmApiBaseUrl = "https://relay.box/v1",
            LlmApiProtocolName = "OpenAI",
            SearxngBaseUrl = "http://searx.box:8080",
            PermissionModeName = "AcceptEdits",
            ThinkingEffortName = "Medium",
            ChatGptProjectName = "Jarvis",
            ChatGptProjectId = "g-p-123",
            ChatGptRotateAfterMessages = 250,
            AutoCompactWindow = 500_000,
            BedrockRegion = "eu-central-1",
            BedrockAccessKeyId = "AKIAEXAMPLE",
            VertexProjectId = "my-gcp-project",
            VertexRegion = "europe-west1",
            Language = "japanese",
        };
        settings.ApiKeySets["anthropic"] = ["sk-first", "sk-second"];
        settings.CustomModels.Add(new ModelInfo("ollama", "qwen3:14b", "Qwen3 14B", 32_000));
        settings.ModelContextOverrides["claude-opus-5"] = 1_000_000;
        store.Save(settings);

        // No key may be stored in plaintext.
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("sk-first", raw);
        Assert.DoesNotContain("sk-second", raw);

        var loaded = store.Load();
        Assert.Equal(["sk-first", "sk-second"], loaded.GetApiKeys("anthropic"));
        Assert.Equal("sk-first", loaded.GetApiKey("anthropic"));
        Assert.Equal("http://box:11434", loaded.OllamaBaseUrl);
        Assert.Equal("http://searx.box:8080", loaded.SearxngBaseUrl);
        Assert.Equal("http://nim.box:8000/v1", loaded.NvidiaBaseUrl);
        Assert.Equal("https://relay.box/v1", loaded.LlmApiBaseUrl);
        Assert.Equal("OpenAI", loaded.LlmApiProtocolName);
        Assert.Equal("Jarvis", loaded.ChatGptProjectName);
        Assert.Equal("g-p-123", loaded.ChatGptProjectId);
        Assert.Equal(250, loaded.ChatGptRotateAfterMessages);
        Assert.Equal("AcceptEdits", loaded.PermissionModeName);
        Assert.Equal("Medium", loaded.ThinkingEffortName);
        Assert.Equal(500_000, loaded.AutoCompactWindow);
        Assert.Equal("eu-central-1", loaded.BedrockRegion);
        Assert.Equal("AKIAEXAMPLE", loaded.BedrockAccessKeyId);
        Assert.Equal("my-gcp-project", loaded.VertexProjectId);
        Assert.Equal("europe-west1", loaded.VertexRegion);
        Assert.Equal("japanese", loaded.Language);
        Assert.Single(loaded.CustomModels);
        Assert.Equal(1_000_000, loaded.ModelContextOverrides["claude-opus-5"]);
        // Deserialization would hand back a case-sensitive dictionary; ids are matched loosely.
        Assert.Equal(1_000_000, loaded.ModelContextOverrides["CLAUDE-OPUS-5"]);
    }

    [Fact]
    public void SettingsStore_MigratesLegacySingleKeyFiles()
    {
        var path = Path.Combine(_temp.Path, "settings.json");
        var protector = new ReversingProtector();
        // A file written by an older version: single protected key per provider.
        File.WriteAllText(path, $$"""
            {
              "ApiKeys": { "anthropic": "{{protector.Protect("sk-legacy")}}" },
              "DefaultModelId": "claude-sonnet-5"
            }
            """);

        var loaded = new JsonSettingsStore(path, protector).Load();

        Assert.Equal(["sk-legacy"], loaded.GetApiKeys("anthropic"));
        Assert.Empty(loaded.ApiKeys);
    }

    [Fact]
    public void SettingsStore_CorruptFile_FallsBackToDefaults()
    {
        var path = Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, "{broken");
        var store = new JsonSettingsStore(path, new PassThroughSecretProtector());

        var loaded = store.Load();

        Assert.Empty(loaded.ApiKeys);
        Assert.Null(loaded.DefaultModelId);
    }

    /// <summary>Trivially reversible "encryption" so tests can assert protection happened.</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plaintext) => new([.. plaintext.Reverse()]);
        public string? Unprotect(string ciphertext) => new([.. ciphertext.Reverse()]);
    }
}
