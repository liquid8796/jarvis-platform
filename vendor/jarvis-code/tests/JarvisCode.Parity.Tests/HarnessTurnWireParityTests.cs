using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers;
using JarvisCode.Providers.Anthropic;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The shape of a post-tool-result request, against what the reference actually
/// sent for the same eight configurations.
///
/// The fixture was recorded by driving the installed CLI at a local listener
/// with a dummy key: a scripted reply answers the first call with a
/// <c>tool_use</c>, the CLI runs a read-only tool, and the follow-up request is
/// the record. What it pins is the half no text comparison can reach — that a
/// harness turn goes out under <c>role: "system"</c> with <c>content</c> as a
/// bare string rather than the block array the two ordinary roles use, that
/// several sections riding one turn are joined by a blank line, that the turn is
/// the last element of <c>messages</c>, and that a model without the
/// mid-conversation system role receives no such turn at all however the
/// reminder text was configured.
/// </summary>
public sealed class HarnessTurnWireParityTests
{
    private const string RosterTurn = "Available agent types for the Agent tool:\n- claude: a test roster.";

    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var c in Fixture.Cases)
            {
                data.Add(c.Case, c.Model);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Our_post_tool_result_request_matches_the_recorded_shape(string caseName, string model)
    {
        var recorded = Fixture.Cases.Single(c => c.Case == caseName && c.Model == model);
        var body = BuildRequest(model, recorded.Env);
        var messages = body["messages"]!.AsArray();

        var ours = messages.Select(Describe).ToList();
        var theirs = recorded.Messages.Select(m => $"{m.Role}:{m.Content}").ToList();
        Assert.Equal(theirs, ours);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_reminder_text_lands_where_the_reference_put_it(string caseName, string model)
    {
        var recorded = Fixture.Cases.Single(c => c.Case == caseName && c.Model == model);
        var expected = recorded.Messages
            .Where(m => m.Role == "system" && m.Text is { } t && !t.StartsWith('<'))
            .Select(m => m.Text!)
            .ToList();

        var messages = BuildRequest(model, recorded.Env)["messages"]!.AsArray();
        var actual = messages
            .Where(m => m!["role"]!.GetValue<string>() == "system")
            .Select(m => m!["content"])
            .OfType<JsonValue>()
            .Select(v => v.GetValue<string>())
            .Where(t => t != RosterTurn)
            .ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The gate is absolute, which is the claim worth a test of its own: a model
    /// without the mid-conversation system role gets no reminder even with the
    /// text configured, and the roster it would have carried folds onto the user
    /// turn instead.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-opus-4-5", false)]
    public void Only_a_mid_conversation_system_model_carries_a_reminder(string model, bool carries)
    {
        var env = new Dictionary<string, string> { ["CLAUDE_CODE_TOASTY_THIMBLE"] = "ZZGATEZZ" };
        var messages = BuildRequest(model, env)["messages"]!.AsArray();
        var reminders = messages
            .Where(m => m!["role"]!.GetValue<string>() == "system")
            .Select(m => m!["content"]?.ToString() ?? string.Empty)
            .Where(t => t.Contains("ZZGATEZZ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(carries ? 1 : 0, reminders.Count);
    }

    /// <summary>
    /// Builds the request this app would send after a batch of tool results, by
    /// the route it really takes: the conversation, the reminders the harness
    /// composes for it, and the provider's own serializer.
    /// </summary>
    private static JsonObject BuildRequest(string model, IReadOnlyDictionary<string, string> env)
    {
        var conversation = new List<ChatMessage>
        {
            ChatMessage.FromUserText("list the markdown files here"),
            new(Role.User, [new TextBlock(RosterTurn)]) { HarnessSystemTurn = true },
            new(Role.Assistant, [new ToolCallBlock("toolu_probe1", "Glob", "{\"pattern\":\"*.md\"}")]),
            new(Role.User, [new ToolResultBlock("toolu_probe1", "Glob", "alpha.md\nbeta.md", IsError: false)]),
        };

        var options = OptionsFrom(model, env);
        var reminders = ToolResultReminders.Compose(conversation, options);
        if (reminders.Count > 0)
        {
            conversation.Add(new ChatMessage(Role.User, [.. reminders.Select(r => new TextBlock(r))])
            {
                HarnessSystemTurn = true,
            });
        }

        var provider = new AnthropicProvider(new HttpClient(), new NoKeys());
        return provider.PreviewRequest(new LlmRequest
        {
            ModelId = model,
            SystemPrompt = "system",
            Messages = conversation,
        }).Body;
    }

    /// <summary>
    /// The recorded run's configuration, as options. Each case supplied the text
    /// through one of the two slots — by environment variable, or by the
    /// <c>client_data</c> map the reference resolves through the same
    /// <c>zHo</c> path — and both arrive here as the resolved text.
    /// </summary>
    private static ToolResultReminders.Options OptionsFrom(
        string model, IReadOnlyDictionary<string, string> env)
    {
        string? Slot(string variable, string clientDataKey) =>
            env.TryGetValue(variable, out var fromEnv) ? fromEnv
            : env.TryGetValue(clientDataKey, out var fromData) ? fromData
            : null;

        return new ToolResultReminders.Options(
            SystemTurnModel: HarnessTurnComposer.UsesSystemTurn(model),
            ModelOwnsText: false)
        {
            BatchingText = Slot(
                ToolResultReminders.BatchingTextVariable, "client_data.tengu_toasty_thimble"),
            SecondaryText = Slot(
                ToolResultReminders.SecondaryTextVariable, "client_data.tengu_gentle_parasol"),
            SilentTurnEnabled = false,
        };
    }

    private static string Describe(JsonNode? message)
    {
        var role = message!["role"]!.GetValue<string>();
        var kind = message["content"] is JsonValue ? "string" : "array";
        return $"{role}:{kind}";
    }

    private static readonly HarnessTurnFixture Fixture = Load();

    private static HarnessTurnFixture Load()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Captures", "HarnessTurn", "reminder-wire-cli-2.1.257.json");
        return JsonSerializer.Deserialize<HarnessTurnFixture>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed class NoKeys : IApiKeySource
    {
        public string? GetKey(string providerId) => "not-a-real-key";
    }
}

internal sealed class HarnessTurnFixture
{
    public string CapturedFrom { get; set; } = string.Empty;
    public List<HarnessTurnCase> Cases { get; set; } = [];
}

internal sealed class HarnessTurnCase
{
    public string Case { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public Dictionary<string, string> Env { get; set; } = [];
    public List<HarnessTurnMessage> Messages { get; set; } = [];
}

internal sealed class HarnessTurnMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Text { get; set; }
}
