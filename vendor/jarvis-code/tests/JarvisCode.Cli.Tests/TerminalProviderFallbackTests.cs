using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli.Tests;

[Collection("repl")]
public sealed class TerminalProviderFallbackTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "jarvis-terminal-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldDirectory = Environment.CurrentDirectory;
    private readonly string? _oldProfile = Environment.GetEnvironmentVariable("JARVISCODE_PROFILE");
    private readonly string _profile = "test-terminal-fallback-" + Guid.NewGuid().ToString("N");
    private readonly TextWriter _oldOutput = Console.Out;
    private readonly TextWriter _oldError = Console.Error;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public TerminalProviderFallbackTests()
    {
        Directory.CreateDirectory(_workspace);
        Environment.CurrentDirectory = _workspace;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _profile);
        Console.SetOut(_output);
        Console.SetError(_error);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Terminal_failure_never_replays_or_discards_completed_local_tools(bool configuredFallback, bool overflowText)
    {
        var provider = new FixtureProvider(Path.Combine(_workspace, "local-result.txt"), canRetry: false,
            failure: overflowText ? "terminal fixture failure: quoted 429; prompt is too long: 200001 tokens > 200000 maximum" : "terminal fixture failure");
        var options = Options(configuredFallback ? "fallback-model" : null);

        var exit = await PrintRunner.RunAsync(options, default,
            decorateProviders: _ => new ProviderRegistry([provider]));

        Assert.Equal(1, exit);
        Assert.Equal(new[] { "primary-model", "primary-model" }, provider.Requests.Select(request => request.ModelId));
        Assert.DoesNotContain("Falling back", _error.ToString());
        Assert.Contains("terminal fixture failure", _output.ToString());
        Assert.Equal("executed exactly once", File.ReadAllText(provider.FilePath));
        var resultPassedToFailure = Assert.Single(provider.Requests[1].Messages.SelectMany(message => message.Content)
            .OfType<ToolResultBlock>());
        Assert.False(resultPassedToFailure.IsError);

        var stored = await StoredSession();
        Assert.Equal("primary-model", stored.ModelId);
        var persistedResult = Assert.Single(stored.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal(resultPassedToFailure, persistedResult);
        Assert.Single(stored.Messages.SelectMany(message => message.Content).OfType<ToolCallBlock>());
        Assert.Single(_output.ToString().Split('\n').Where(line => line.Contains("\"type\":\"user\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Ordinary_retryable_API_failure_still_uses_configured_fallback()
    {
        var provider = new FixtureProvider(Path.Combine(_workspace, "local-result.txt"), canRetry: true, useTool: false);

        var exit = await PrintRunner.RunAsync(Options("fallback-model"), default,
            decorateProviders: _ => new ProviderRegistry([provider]));

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "primary-model", "fallback-model" }, provider.Requests.Select(request => request.ModelId));
        Assert.Contains("Falling back to fallback-model", _error.ToString());
        Assert.Contains("fallback completed", _output.ToString());
        Assert.Equal("fallback-model", (await StoredSession()).ModelId);
    }

    private CliOptions Options(string? fallback) => new()
    {
        Prompt = "Write the local fixture, then validate it.", Print = true, OutputFormat = "stream-json",
        Model = "primary-model",
        Bare = true, SettingSources = "", DangerouslySkipPermissions = true, PromptSuggestions = false,
        HasToolsFilter = true, Tools = ["Write"], FallbackModel = fallback,
        Settings = """
            {"DefaultModelId":"primary-model","CustomModels":[
              {"ProviderId":"fixture","ModelId":"primary-model","DisplayName":"Primary","MaxContextTokens":200000},
              {"ProviderId":"fixture","ModelId":"fallback-model","DisplayName":"Fallback","MaxContextTokens":200000}]}
            """,
    };

    private async Task<Session> StoredSession()
    {
        var store = new JsonSessionStore(Path.Combine(ProfilePaths.Create(_profile).SessionsDirectory, "code"));
        var summary = Assert.Single(await store.ListAsync());
        return Assert.IsType<Session>(await store.LoadAsync(summary.Id));
    }

    private sealed class FixtureProvider(string filePath, bool canRetry, bool useTool = true,
        string failure = "terminal fixture failure") : ILlmProvider
    {
        public string Id => "fixture";
        public string DisplayName => "Fixture";
        public bool RequiresApiKey => false;
        public string FilePath => filePath;
        public List<LlmRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ModelId == "fallback-model")
            {
                yield return new TextDeltaEvent("fallback completed");
                yield return new ResponseCompletedEvent(false, new Usage(10, 2), "end_turn");
                yield break;
            }
            if (!useTool || Requests.Count > 1)
                throw new ProviderException(failure) { CanRetry = canRetry };
            yield return new ToolCallStartedEvent(0, "local-write-1", "Write");
            yield return new ToolCallArgumentsDeltaEvent(0, new JsonObject
            { ["file_path"] = filePath, ["content"] = "executed exactly once" }.ToJsonString());
            yield return new ResponseCompletedEvent(true, new Usage(10, 2), "tool_use");
        }
    }

    public void Dispose()
    {
        Console.SetOut(_oldOutput);
        Console.SetError(_oldError);
        Environment.CurrentDirectory = _oldDirectory;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _oldProfile);
        var workspace = Path.GetFullPath(_workspace);
        Assert.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "jarvis-terminal-fallback-"), workspace,
            StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        var profile = Path.GetFullPath(ProfilePaths.Create(_profile).Root);
        Assert.Equal("JarvisCode-" + _profile, Path.GetFileName(profile));
        if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        _output.Dispose();
        _error.Dispose();
    }
}
