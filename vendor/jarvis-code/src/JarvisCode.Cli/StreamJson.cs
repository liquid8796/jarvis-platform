using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Hooks;

namespace JarvisCode.Cli;

/// <summary>
/// The print mode's JSON shapes, matching the reference CLI's stream-json /
/// json output line for line (system init → assistant/user messages → result;
/// field names measured against 2.1.251). A session ledger supplies measured
/// usage and list-price cost; an unpriced model reports null for its cost.
/// </summary>
internal sealed class StreamJson(TextWriter output, string sessionId)
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private readonly object _writeLock = new();

    public static string Version =>
        typeof(StreamJson).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public void EmitInit(
        string cwd,
        IEnumerable<string> tools,
        IEnumerable<(string Name, string Status)> mcpServers,
        string model,
        string permissionMode,
        string apiKeySource,
        IEnumerable<string> agents,
        IEnumerable<string> skills,
        string outputStyle = "default",
        IEnumerable<string>? commands = null,
        IEnumerable<(string Name, string Path)>? plugins = null)
    {
        var line = new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["cwd"] = cwd,
            ["session_id"] = sessionId,
            ["tools"] = new JsonArray([.. tools.Select(t => JsonValue.Create(t))]),
            ["mcp_servers"] = new JsonArray([.. mcpServers.Select(s =>
                (JsonNode)new JsonObject { ["name"] = s.Name, ["status"] = s.Status })]),
            ["model"] = model,
            ["permissionMode"] = permissionMode,
            ["slash_commands"] = new JsonArray([.. (commands ?? []).Select(command => (JsonNode?)JsonValue.Create(command))]),
            ["apiKeySource"] = apiKeySource,
            ["claude_code_version"] = Version,
            ["output_style"] = outputStyle,
            ["agents"] = new JsonArray([.. agents.Select(a => JsonValue.Create(a))]),
            ["skills"] = new JsonArray([.. skills.Select(s => JsonValue.Create(s))]),
            ["plugins"] = new JsonArray([.. (plugins ?? []).Select(plugin => (JsonNode)new JsonObject
            { ["name"] = plugin.Name, ["path"] = plugin.Path })]),
            ["uuid"] = Guid.NewGuid().ToString(),
        };
        WriteLine(line);
    }

    public void EmitAssistant(ChatMessage message, string model, string? parentToolUseId = null) =>
        WriteLine(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["id"] = $"msg_{Guid.NewGuid():N}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = ContentArray(message),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
            },
            ["parent_tool_use_id"] = parentToolUseId,
            ["session_id"] = sessionId,
            ["uuid"] = Guid.NewGuid().ToString(),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        });

    public void EmitToolResult(string callId, string content, bool isError, string? parentToolUseId = null,
        IReadOnlyList<ImageBlock>? images = null) =>
        WriteLine(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = callId,
                    ["content"] = images is { Count: > 0 }
                        ? ContentArray(new ChatMessage(Role.User, [new TextBlock(content), .. images])) : JsonValue.Create(content),
                    ["is_error"] = isError,
                }),
            },
            ["parent_tool_use_id"] = parentToolUseId,
            ["session_id"] = sessionId,
            ["uuid"] = Guid.NewGuid().ToString(),
        });

    public JsonObject BuildResult(
        bool isError,
        string subtype,
        string result,
        long durationMs,
        long durationApiMs,
        int numTurns,
        Usage usage,
        string modelId,
        string providerId,
        int contextWindow,
        int permissionDenials,
        string terminalReason,
        CliUsageSnapshot? accounting = null,
        JsonNode? structuredOutput = null,
        bool hasStructuredOutput = false)
    {
        var usageNode = new JsonObject
        {
            ["input_tokens"] = usage.InputTokens,
            ["cache_creation_input_tokens"] = usage.CacheCreationInputTokens,
            ["cache_read_input_tokens"] = usage.CacheReadInputTokens,
            ["output_tokens"] = usage.OutputTokens,
        };
        if (usage.IsEstimated) usageNode["is_estimated"] = true;
        var resultNode = new JsonObject
        {
            ["is_error"] = isError,
            ["duration_api_ms"] = durationApiMs,
            ["num_turns"] = numTurns,
            ["stop_reason"] = isError ? null : "end_turn",
            ["session_id"] = sessionId,
            ["total_cost_usd"] = accounting?.CostUsd,
            ["usage"] = usageNode,
            ["modelUsage"] = new JsonObject
            {
                [modelId] = new JsonObject
                {
                    ["inputTokens"] = usage.InputTokens,
                    ["outputTokens"] = usage.OutputTokens,
                    ["cacheReadInputTokens"] = usage.CacheReadInputTokens,
                    ["cacheCreationInputTokens"] = usage.CacheCreationInputTokens,
                    ["webSearchRequests"] = 0,
                    ["costUSD"] = accounting?.CostUsd,
                    ["contextWindow"] = contextWindow,
                    ["provider"] = providerId,
                },
            },
            ["permission_denials"] = new JsonArray(),
            ["terminal_reason"] = terminalReason,
            ["subtype"] = subtype,
            ["api_error_status"] = null,
            ["result"] = result,
            ["errors"] = isError ? new JsonArray(result) : new JsonArray(),
            ["type"] = "result",
            ["duration_ms"] = durationMs,
            ["uuid"] = Guid.NewGuid().ToString(),
            ["permission_denial_count"] = permissionDenials,
        };
        if (usage.IsEstimated) resultNode["modelUsage"]![modelId]!["isEstimated"] = true;
        if (accounting is not null)
        {
            var models = new JsonObject();
            foreach (var row in accounting.Models)
            {
                var key = accounting.Models.Count(x => x.Model.ModelId == row.Model.ModelId) > 1
                    ? row.Model.ProviderId + "/" + row.Model.ModelId : row.Model.ModelId;
                models[key] = new JsonObject
                {
                    ["inputTokens"] = row.Usage.InputTokens,
                    ["outputTokens"] = row.Usage.OutputTokens,
                    ["cacheReadInputTokens"] = row.Usage.CacheReadInputTokens,
                    ["cacheCreationInputTokens"] = row.Usage.CacheCreationInputTokens,
                    ["webSearchRequests"] = row.WebSearchRequests,
                    ["costUSD"] = row.CostUsd,
                    ["contextWindow"] = row.Model.MaxContextTokens,
                    ["provider"] = row.Model.ProviderId,
                };
                if (row.Usage.IsEstimated) models[key]!["isEstimated"] = true;
            }
            resultNode["modelUsage"] = models;
            resultNode["cost_source"] = usage.IsEstimated ? "unavailable_estimated_browser_usage" : "reported_tokens_and_configured_list_prices";
            resultNode["cost_is_estimate"] = true;
            resultNode["unreported_api_calls"] = accounting.UnreportedCalls;
        }
        if (hasStructuredOutput)
            resultNode["structured_output"] = structuredOutput?.DeepClone();
        return resultNode;
    }

    public void WriteLine(JsonObject line)
    {
        lock (_writeLock)
        {
            output.WriteLine(line.ToJsonString(Compact));
            output.Flush();
        }
    }

    public void EmitUser(CliInputMessage input) => WriteLine(new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = ContentArray(input.Message) },
        ["parent_tool_use_id"] = null,
        ["session_id"] = sessionId,
        ["uuid"] = input.Uuid,
        ["isReplay"] = true,
    });

    public void EmitPreview(string text, string? parentToolUseId = null) => WriteLine(new JsonObject
    {
        ["type"] = "system", ["subtype"] = "answer_preview", ["text"] = text,
        ["transient"] = true, ["session_id"] = sessionId, ["parent_tool_use_id"] = parentToolUseId,
        ["uuid"] = Guid.NewGuid().ToString(),
    });

    public void EmitPartial(JsonObject evt, string? parentToolUseId = null) => WriteLine(new JsonObject
    {
        ["type"] = "stream_event", ["event"] = evt,
        ["session_id"] = sessionId, ["parent_tool_use_id"] = parentToolUseId,
        ["uuid"] = Guid.NewGuid().ToString(),
    });

    public void EmitHook(HookLifecycleEvent hook) => WriteLine(new JsonObject
    {
        ["type"] = "system", ["subtype"] = "hook_" + hook.Phase,
        ["hook_id"] = hook.HookId, ["hook_name"] = hook.Name, ["hook_event"] = hook.Event.ToString(),
        ["stdout"] = hook.Stdout, ["stderr"] = hook.Stderr,
        ["output"] = hook.Stdout + hook.Stderr, ["exit_code"] = hook.ExitCode,
        ["outcome"] = hook.Phase == "started" ? null : hook.ExitCode == 0 ? "success" : "error",
        ["session_id"] = sessionId, ["uuid"] = Guid.NewGuid().ToString(),
    });

    public void EmitTask(Core.Agent.WorkerInfo worker, bool started) => WriteLine(new JsonObject
    {
        ["type"] = "system", ["subtype"] = started ? "task_started" : "task_notification",
        ["task_id"] = worker.Id, ["task_type"] = "local_agent", ["tool_use_id"] = worker.ToolUseId,
        ["description"] = worker.PromptPreview, ["summary"] = worker.ResultText ?? worker.PromptPreview,
        ["status"] = started ? "running" : worker.Status switch
        { Core.Agent.WorkerStatus.Completed => "completed", Core.Agent.WorkerStatus.Killed => "stopped", _ => "failed" },
        ["output_file"] = "", ["session_id"] = sessionId, ["uuid"] = Guid.NewGuid().ToString(),
    });

    internal static JsonArray ContentArray(ChatMessage message)
    {
        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    var textNode = new JsonObject { ["type"] = "text", ["text"] = text.Text };
                    if (text.Citations is { Count: > 0 })
                        textNode["citations"] = new JsonArray([.. text.Citations.Select(c => JsonNode.Parse(c.RawJson))]);
                    content.Add(textNode);
                    break;
                case ThinkingBlock thinking:
                    content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = thinking.Thinking,
                        ["signature"] = thinking.Signature });
                    break;
                case ImageBlock image:
                    content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject
                    { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = image.Base64Data } });
                    break;
                case RawProviderBlock raw:
                    content.Add(JsonNode.Parse(raw.RawJson));
                    break;
                case ToolResultBlock toolResult:
                    content.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolResult.ToolCallId,
                        ["content"] = toolResult.Content, ["is_error"] = toolResult.IsError });
                    break;
                case ToolCallBlock call:
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = JsonNode.Parse(call.ArgumentsJson ?? "{}"),
                    });
                    break;
            }
        }

        return content;
    }
}
