using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// Exposes one MCP server tool as a regular agent tool. MCP tools have unknown
/// side effects, so they are never read-only and always pass the permission gate.
/// A server that elicits mid-call (elicitation/create) reaches the user through
/// the turn's AskUserQuestion UI; without one the elicitation is declined.
/// </summary>
public sealed class McpToolAdapter(IMcpClient client, McpToolDescriptor descriptor)
    : ITool, ISearchHintTool, IResultSizeTool
{
    public string ServerName => client.ServerName;

    public string SourceToolName => descriptor.Name;

    /// <summary>
    /// The server asked for this tool to stay in every request — the reference's
    /// <c>anthropic/alwaysLoad</c> annotation, which exempts it from deferral.
    /// </summary>
    public bool AlwaysLoad => descriptor.AlwaysLoad;

    /// <summary>The server's <c>anthropic/searchHint</c>, which tool search scores.</summary>
    public string? SearchHint => descriptor.SearchHint;

    /// <summary>The server's <c>anthropic/maxResultSizeChars</c>, which the persistence policy reads.</summary>
    public int? MaxResultSizeChars => descriptor.MaxResultSizeChars;

    /// <summary>The server's <c>anthropic/requiresUserInteraction</c>: never auto-backgrounded.</summary>
    public bool RequiresUserInteraction => descriptor.RequiresUserInteraction;

    public string Name { get; } = BuildToolName(client.ServerName, descriptor.Name);

    public string Description { get; } =
        $"[MCP: {client.ServerName}] {descriptor.Description}";

    public JsonObject InputSchema => descriptor.InputSchema;

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
    {
        var preview = arguments.ToJsonString();
        if (preview.Length > 60)
            preview = preview[..60] + "…";
        return $"{client.ServerName}:{descriptor.Name}({preview})";
    }

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        // Elicitations in flight for THIS call: a call blocked on a question the
        // user has not answered is not a slow call, and the reference keeps
        // waiting rather than backgrounding it out from under the question.
        int elicitations = 0;
        var elicit = context.AskUserAsync is null
            ? null
            : Wrap(BuildElicitationCallback(context), () => Interlocked.Increment(ref elicitations),
                   () => Interlocked.Decrement(ref elicitations));

        // A subagent's turn is not the user's turn: its tool calls have no tasks
        // pane to be moved into, so the reference leaves them on the turn. A tool
        // the server marked anthropic/requiresUserInteraction is not late either
        // — it is waiting on a person — so it never moves.
        var delay = context.IsSubagent || descriptor.RequiresUserInteraction
            ? 0
            : McpAutoBackground.DelayMs(client.ServerType, context.IsNonInteractiveSession);

        return McpAutoBackground.RunAsync(
            token => CallAsync(arguments, context, elicit, token),
            McpAutoBackground.Describe(client.ServerName, descriptor.Name),
            delay,
            context.BackgroundTasks,
            context.SessionId,
            () => Volatile.Read(ref elicitations) > 0,
            cancellationToken);
    }

    private async Task<ToolResult> CallAsync(
        JsonObject arguments,
        ToolExecutionContext context,
        McpElicitationCallback? elicit,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.CallToolAsync(descriptor.Name, arguments, cancellationToken, elicit);
            var text = result.Text.Length == 0 ? "(the tool returned no text content)" : result.Text;
            text = context.Truncate(text, "tool output");
            return new ToolResult(text, result.IsError, result.Images);
        }
        catch (McpException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    /// <summary>Counts an elicitation in and out around the real callback.</summary>
    private static McpElicitationCallback Wrap(
        McpElicitationCallback inner, Action entered, Action left) =>
        async (message, schema, cancellationToken) =>
        {
            entered();
            try
            {
                return await inner(message, schema, cancellationToken);
            }
            finally
            {
                left();
            }
        };

    /// <summary>
    /// Maps an elicitation onto the AskUserQuestion card: enum properties become
    /// options, everything else rides the free-text "Other". Dismissing the card
    /// cancels the elicitation.
    /// </summary>
    private McpElicitationCallback BuildElicitationCallback(ToolExecutionContext context) =>
        async (message, schema, cancellationToken) =>
        {
            context.FireHook?.Invoke(Hooks.HookEvent.Elicitation, new JsonObject
            {
                ["server"] = client.ServerName,
                ["message"] = message,
            });

            var properties = schema?["properties"] as JsonObject;
            var firstProperty = properties?.FirstOrDefault(p => p.Value is JsonObject) is { Key.Length: > 0 } first
                ? (first.Key, first.Value as JsonObject)
                : (null, null);

            var options = new List<UserQuestionOption>();
            if (firstProperty.Item2?["enum"] is JsonArray choices)
            {
                foreach (var choice in choices.OfType<JsonValue>())
                {
                    if (choice.TryGetValue(out string? label) && label is { Length: > 0 })
                        options.Add(new UserQuestionOption(label, ""));
                }
            }

            if (options.Count == 0)
            {
                options.Add(new UserQuestionOption("Accept", "Answer with the typed text (use Other for a value)"));
                options.Add(new UserQuestionOption("Decline", "Refuse this request"));
            }

            var question = new UserQuestion(
                message.Length > 0 ? message : $"The {client.ServerName} server needs input.",
                client.ServerName.Length > 12 ? client.ServerName[..12] : client.ServerName,
                options,
                MultiSelect: false);

            var answers = await context.AskUserAsync!([question], cancellationToken);
            var result = MapElicitationAnswer(answers, question.Question, firstProperty.Item1);
            context.FireHook?.Invoke(Hooks.HookEvent.ElicitationResult, new JsonObject
            {
                ["server"] = client.ServerName,
                ["action"] = result["action"]?.GetValue<string>() ?? "cancel",
            });
            return result;
        };

    internal static JsonObject MapElicitationAnswer(
        UserQuestionAnswers? answers, string questionText, string? propertyName)
    {
        if (answers is null)
            return new JsonObject { ["action"] = "cancel" };

        answers.Answers.TryGetValue(questionText, out var picked);
        var value = answers.FreeText is { Length: > 0 } typed ? typed : picked;
        if (string.Equals(picked, "Decline", StringComparison.OrdinalIgnoreCase) &&
            answers.FreeText is not { Length: > 0 })
            return new JsonObject { ["action"] = "decline" };

        var content = new JsonObject();
        if (propertyName is { Length: > 0 } && value is { Length: > 0 } &&
            !string.Equals(value, "Accept", StringComparison.OrdinalIgnoreCase))
            content[propertyName] = value;
        return new JsonObject { ["action"] = "accept", ["content"] = content };
    }

    /// <summary>
    /// The reference's <c>xc</c>: <c>mcp__{server}__{tool}</c> with both halves
    /// normalized by <see cref="McpBuiltInServers.Ln"/> and neither truncated.
    /// </summary>
    public static string BuildToolName(string serverName, string toolName) =>
        McpBuiltInServers.WireName(serverName, toolName);
}
