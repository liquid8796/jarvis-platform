using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.Composition;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's <c>advisor</c> tool. Its own is server-side — a captured
/// request declares it as <c>{"type":"advisor_20260301","name":"advisor",
/// "model":"…"}</c> with no schema, and the API forwards the conversation to
/// the reviewer model itself — so this is a re-derivation of the contract its
/// prompt block states rather than a copy of its machinery: no parameters, the
/// whole conversation forwarded, a stronger model answering.
///
/// The block that documents it is <see cref="AdvisorPrompt"/>; the two are
/// gated together, so the model is never told about a tool it does not have.
/// </summary>
public sealed class AdvisorTool(AppServices services, Func<string?> advisorModelId) : ITool
{
    public const string ToolName = "advisor";

    private const string RolePrompt =
        "You are an advisor consulted by another AI coding agent at a key decision point. You are shown its " +
        "whole conversation so far: the task, every tool call it has made, every result it has seen. Give " +
        "your best judgment: weigh the approach it is taking (or a better one it missed), name what it has " +
        "got wrong, and be decisive. Keep the answer tight. Plain text, no preamble.";

    /// <summary>
    /// The reference declares no description for it — the API supplies the tool
    /// and the prompt block is its documentation. This one is registered as an
    /// ordinary local tool, so it needs a line; it says what the block says.
    /// </summary>
    public string Description =>
        "Consult a stronger reviewer model. Takes no parameters: your entire conversation history is " +
        "forwarded automatically.";

    public string Name => ToolName;

    /// <summary>No parameters, which is what the prompt block promises.</summary>
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) => "Consulted the advisor";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var modelId = advisorModelId();
        var model = modelId is null ? null : ModelCatalog.Find(services.Settings.Models, modelId);
        if (model is null)
        {
            return ToolResult.Error("No advisor model is configured (/advisor <model id> sets one).");
        }

        ILlmProvider provider;
        try
        {
            provider = services.Providers.Get(model.ProviderId);
        }
        catch (ProviderException ex)
        {
            return ToolResult.Error($"The advisor model is unavailable: {ex.Message}");
        }

        if (!ProviderCapabilities.For(provider).SupportsToolFreeInference)
            return ToolResult.Error(
                "The configured advisor cannot guarantee tool-free review. Choose an API advisor model; " +
                "the conversation was not sent to a browser assistant.");

        var conversation = context.ConversationSnapshot?.Invoke() ?? [];
        if (conversation.Count == 0)
        {
            return ToolResult.Error("There is no conversation to forward to the advisor yet.");
        }

        var turn = new AgentTurnContext
        {
            Provider = provider,
            ModelId = model.ModelId,
            SystemPrompt = RolePrompt,
            Messages = [.. AdvisorConversation.Forwardable(conversation)],
            Tools = new ToolRegistry([]),
            PermissionGate = PromptlessGate,
            ToolContext = new ToolExecutionContext { WorkingDirectory = context.WorkingDirectory },
            MaxIterations = 1,
        };

        string answer = "";
        await foreach (var evt in services.Orchestrator.RunTurnAsync(turn, cancellationToken))
        {
            if (evt is AssistantMessageCompleted completed && completed.Message.GetText() is { Length: > 0 } text)
            {
                answer = text;
            }
            else if (evt is TurnCompleted { Reason: TurnEndReason.Error } failed)
            {
                return ToolResult.Error($"The advisor call failed: {failed.Detail ?? "unknown error"}");
            }
        }

        return answer.Length > 0
            ? ToolResult.Success($"Advisor ({model.DisplayName}):\n{answer}")
            : ToolResult.Error("The advisor returned no answer.");
    }

    /// <summary>The advisor runs toolless, so the gate is never consulted; deny-all keeps it honest.</summary>
    private static readonly UiPermissionGate PromptlessGate = new() { PromptAsync = null };
}

/// <summary>
/// Shaping the parent conversation into something a second model will accept.
/// The advisor runs with no tools, so a forwarded <c>tool_use</c> would name a
/// tool that is not in its request; the transcript is therefore flattened to
/// plain text and handed over as one user turn, which is also what keeps the
/// advisor from trying to continue the parent's work instead of judging it.
/// </summary>
internal static class AdvisorConversation
{
    /// <summary>Its own budget; a whole session can outrun any context window.</summary>
    internal const int MaxCharacters = 120_000;

    /// <summary>How much of one tool result is worth showing the advisor.</summary>
    private const int MaxResultCharacters = 2_000;

    /// <summary>
    /// One turn as the advisor sees it. The prompt block promises it sees "every
    /// tool call you've made, every result you've seen", so the calls and their
    /// results are rendered rather than dropped — a long result is cut, since
    /// the advice is about the shape of the work, not its output in full.
    /// </summary>
    private static string Render(ChatMessage message)
    {
        var parts = new List<string>();

        // VisibleText answers for the whole message — it strips the harness
        // blocks by their recorded counts — so it is asked once, not per block.
        if (SystemReminders.VisibleText(message) is { Length: > 0 } visible)
        {
            parts.Add(visible.Trim());
        }

        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ToolCallBlock call:
                    parts.Add($"-> {call.Name}({Clip(call.ArgumentsJson, 600)})");
                    break;
                case ToolResultBlock result:
                    var label = result.IsError ? "error" : "result";
                    parts.Add($"<- {result.ToolName} {label}: {Clip(result.Content, MaxResultCharacters)}");
                    break;
            }
        }

        // A message whose only text was a harness reminder renders as nothing,
        // which is right: the advisor is shown the session, not its plumbing.
        return string.Join("\n", parts.Where(static p => p.Length > 0)).Trim();
    }

    private static string Clip(string value, int limit)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit] + $"… [+{trimmed.Length - limit} chars]";
    }

    internal static IReadOnlyList<ChatMessage> Forwardable(IReadOnlyList<ChatMessage> conversation)
    {
        var builder = new StringBuilder();
        builder.Append("Here is the agent's conversation so far.\n\n<conversation>\n");

        var rendered = new List<string>();
        foreach (var message in conversation)
        {
            var turn = Render(message);
            if (turn.Length > 0)
            {
                rendered.Add($"[{message.Role}]\n{turn}");
            }
        }

        // Oldest first is what the reference forwards, but a budget has to drop
        // something: the newest turns are the ones the advice is about, so the
        // cut is taken off the front and said out loud.
        var kept = new List<string>();
        var total = 0;
        for (var i = rendered.Count - 1; i >= 0; i--)
        {
            total += rendered[i].Length + 2;
            if (total > MaxCharacters && kept.Count > 0)
            {
                kept.Insert(0, "[earlier turns omitted for length]");
                break;
            }

            kept.Insert(0, rendered[i]);
        }

        builder.Append(string.Join("\n\n", kept)).Append("\n</conversation>\n\n");
        builder.Append(
            "Advise the agent: is the approach right, what is it missing, and what should it do next?");
        return [ChatMessage.FromUserText(builder.ToString())];
    }
}
