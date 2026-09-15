using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;

namespace JarvisCode.Cli.Tests;

[Collection("repl")]
public sealed class ChatGptCliPreviewTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "jarvis-preview-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldDirectory = Environment.CurrentDirectory;
    private readonly string? _oldProfile = Environment.GetEnvironmentVariable("JARVISCODE_PROFILE");
    private readonly string _profile = "test-preview-" + Guid.NewGuid().ToString("N");
    private string? _profileRoot;

    public ChatGptCliPreviewTests()
    {
        Directory.CreateDirectory(_workspace);
        Environment.CurrentDirectory = _workspace;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _profile);
    }

    private static CliOptions Options => new()
    {
        Prompt = "fixture prompt", Print = true, Bare = true, SafeMode = true,
        NoSessionPersistence = true, SettingSources = "", HasToolsFilter = true, Tools = [],
        Settings = """{"DefaultModelId":"fixture-model","CustomModels":[{"ProviderId":"fixture","ModelId":"fixture-model","DisplayName":"Fixture","MaxContextTokens":200000}]}""",
    };

    private CliServices Services(ILlmProvider provider)
    {
        var services = CliServices.Create(Options, _ => new ProviderRegistry([provider]));
        _profileRoot = services.App.Paths.Root;
        return services;
    }

    [Fact]
    public async Task Runner_delivers_replaceable_previews_and_clears_them_before_authoritative_text()
    {
        using var services = Services(new PreviewProvider());
        await services.InitializeAsync(Options, _workspace, default);
        var session = await services.ResolveSessionAsync(Options, _workspace, default);
        var sequence = new List<string>();
        var runner = new CliTurnRunner(services, Options, new UiPermissionGate { WorkingDirectory = _workspace });
        var result = await runner.RunAsync(session, Options.Prompt, new CliTurnEvents
        {
            TextPreview = text => sequence.Add("preview:" + text),
            TextDelta = text => sequence.Add("text:" + text),
        }, default);

        Assert.Equal(new[] { "preview:first draft", "preview:replaced draft", "preview:", "text:verified answer" }, sequence);
        Assert.Equal(TurnEndReason.Completed, result.Reason);
        Assert.Equal("verified answer", result.FinalText);
        var serialized = JsonSerializer.Serialize(session);
        Assert.DoesNotContain("first draft", serialized);
        Assert.DoesNotContain("replaced draft", serialized);
        Assert.Equal("verified answer", Assert.Single(session.Messages.Where(message => message.Role == Role.Assistant)).GetText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_turns_clear_the_preview_without_persisting_an_answer(bool cancel)
    {
        using var services = Services(new PreviewProvider(fail: !cancel));
        await services.InitializeAsync(Options, _workspace, default);
        var session = await services.ResolveSessionAsync(Options, _workspace, default);
        var previews = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var runner = new CliTurnRunner(services, Options, new UiPermissionGate { WorkingDirectory = _workspace });
        var result = await runner.RunAsync(session, Options.Prompt, new CliTurnEvents
        {
            TextPreview = text =>
            {
                previews.Add(text);
                if (cancel && text == "replaced draft") cancellation.Cancel();
            },
        }, cancellation.Token);

        Assert.Equal(cancel ? TurnEndReason.Cancelled : TurnEndReason.Error, result.Reason);
        Assert.Equal(new[] { "first draft", "replaced draft", "" }, previews);
        Assert.Empty(result.FinalText);
        Assert.Empty(session.Messages.Where(message => message.Role == Role.Assistant));
    }

    [Fact]
    public void Preview_replacement_and_clear_affect_only_the_current_live_frame()
    {
        var state = new ReplViewState { Text = "my next prompt", Offset = 4, AnswerPreview = "old draft" };
        var first = ReplView.Render(state, Ansi.Plain, 60);
        var replacement = ReplView.Render(state with { AnswerPreview = "new draft" }, Ansi.Plain, 60);
        var cleared = ReplView.Render(state with { AnswerPreview = "" }, Ansi.Plain, 60);

        Assert.Contains("old draft", first.Lines);
        Assert.Contains("new draft", replacement.Lines);
        Assert.DoesNotContain("old draft", replacement.Lines);
        Assert.DoesNotContain(cleared.Lines, line => line.Contains("draft", StringComparison.Ordinal));
        Assert.DoesNotContain("Answer preview", cleared.Lines);
        Assert.Contains(cleared.Lines, line => line.Contains("my next prompt", StringComparison.Ordinal));
        Assert.NotNull(cleared.Caret);
        Assert.Equal("my next prompt", state.Text);
    }

    [Fact]
    public void Long_preview_keeps_the_latest_lines_visible_and_cannot_inject_terminal_controls()
    {
        var text = string.Join('\n', Enumerable.Range(0, 20).Select(index => "line " + index)) + "\n\x1b[2Jfinal\a";
        var lines = ReplView.PreviewLines(text, columns: 30, maximumRows: 3);
        Assert.Equal(new[] { "line 18", "line 19", "final" }, lines);
        Assert.All(lines, line => Assert.DoesNotContain(line, char.IsControl));
        var frame = ReplView.Render(new ReplViewState
        {
            Text = "", Offset = 0, AnswerPreview = "hidden draft", Dialog = ["Permission required"],
        }, Ansi.Plain, 60);
        Assert.DoesNotContain("hidden draft", frame.Lines);
        Assert.Contains("Permission required", frame.Lines);
    }

    [Fact]
    public void Cumulative_usage_is_counted_once_and_a_new_turn_starts_a_new_accumulator()
    {
        var turn = new TurnUsageAccumulator();
        var session = Usage.Zero;
        session = session.Add(turn.Observe(new Usage(10, 2, 3, 4)));
        session = session.Add(turn.Observe(new Usage(25, 5, 6, 8) { IsEstimated = true }));
        session = session.Add(turn.Observe(new Usage(25, 5, 6, 8) { IsEstimated = true }));
        session = session.Add(turn.Observe(new Usage(10, 2, 3, 4)));
        Assert.Equal(new Usage(25, 5, 6, 8) { IsEstimated = true }, session);

        // No terminal callback is needed to account completed calls: a later failure or cancel
        // leaves the values above intact. A new turn then adds its own first cumulative report.
        session = session.Add(new TurnUsageAccumulator().Observe(new Usage(10, 2, 3, 4)));
        Assert.Equal(new Usage(35, 7, 9, 12) { IsEstimated = true }, session);
        Assert.Equal(Usage.Zero, new TurnUsageAccumulator().Observe(Usage.Zero));
    }

    [Fact]
    public void Both_partial_usage_and_per_model_result_rows_retain_estimate_provenance()
    {
        using var writer = new StringWriter();
        var output = new StreamJson(writer, "session");
        var partial = new PartialJsonMessage(output);
        var request = new LlmRequest { ModelId = "browser-model", SystemPrompt = "", Messages = [] };
        partial.Begin(request);
        var usage = new Usage(10, 2) { IsEstimated = true };
        partial.Observe(new TextDeltaEvent("answer"));
        partial.Observe(new ResponseCompletedEvent(false, usage));
        var rows = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.True(Assert.Single(rows, row => row["event"]?["type"]?.GetValue<string>() == "message_delta")
            ["event"]!["usage"]!["is_estimated"]!.GetValue<bool>());

        var model = new ModelInfo("browser", "browser-model", "Browser", 128_000);
        var accounting = new CliUsageSnapshot(usage, null, 1, 2, [new CliModelUsage(model, usage, null, 1, 0)]);
        foreach (var ledger in new CliUsageSnapshot?[] { null, accounting })
        {
            var result = output.BuildResult(false, "success", "answer", 1, 1, 1, usage,
                model.ModelId, model.ProviderId, model.MaxContextTokens, 0, "completed", ledger);
            Assert.True(result["modelUsage"]![model.ModelId]!["isEstimated"]!.GetValue<bool>());
        }
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _oldDirectory;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _oldProfile);
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        if (_profileRoot is not null && Directory.Exists(_profileRoot)) Directory.Delete(_profileRoot, recursive: true);
    }

    private sealed class PreviewProvider(bool fail = false) : ILlmProvider
    {
        public string Id => "fixture";
        public string DisplayName => "Preview fixture";
        public bool RequiresApiKey => false;
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new TextPreviewEvent("first draft");
            yield return new TextPreviewEvent("replaced draft");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (fail) throw new ProviderException("fixture failed");
            yield return new TextDeltaEvent("verified answer");
            yield return new ResponseCompletedEvent(false, new Usage(10, 2) { IsEstimated = true }, StopReasons.EndTurn);
        }
    }
}
