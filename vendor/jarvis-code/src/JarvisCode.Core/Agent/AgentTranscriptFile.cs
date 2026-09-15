using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;

namespace JarvisCode.Core.Agent;

/// <summary>
/// The JSONL transcript a background agent writes as it runs — the reference's
/// <c>output_file</c> for an async Agent call, which its launch result names and
/// tells the model not to Read (the whole transcript would overflow the
/// context). One JSON object per line: the agent's completed messages, its tool
/// calls and results, and its end.
/// </summary>
public static class AgentTranscriptFile
{
    /// <summary>A file name safe on this platform, built from a call or agent id.</summary>
    public static string SafeName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return name.Length == 0 ? "agent" : name;
    }

    /// <summary>Appends one event as a line; a file that cannot be written is left alone.</summary>
    public static void Append(string path, AgentEvent agentEvent)
    {
        var line = Describe(agentEvent);
        if (line is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line.ToJsonString() + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the transcript is a diagnostic, not the agent's result.
        }
    }

    /// <summary>The line for an event, or null for the streaming deltas that would only repeat the message.</summary>
    public static JsonObject? Describe(AgentEvent agentEvent)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("o");
        return agentEvent switch
        {
            AssistantMessageCompleted completed => new JsonObject
            {
                ["type"] = "assistant",
                ["timestamp"] = stamp,
                ["message"] = MessageJson(completed.Message),
            },
            ToolExecutionStarted started => new JsonObject
            {
                ["type"] = "tool_use",
                ["timestamp"] = stamp,
                ["id"] = started.CallId,
                ["name"] = started.ToolName,
            },
            ToolExecutionCompleted done => new JsonObject
            {
                ["type"] = "tool_result",
                ["timestamp"] = stamp,
                ["tool_use_id"] = done.CallId,
                ["name"] = done.ToolName,
                ["is_error"] = done.IsError,
                ["content"] = done.Result,
            },
            ToolExecutionDenied denied => new JsonObject
            {
                ["type"] = "tool_result",
                ["timestamp"] = stamp,
                ["tool_use_id"] = denied.CallId,
                ["name"] = denied.ToolName,
                ["is_error"] = true,
                ["content"] = "denied",
            },
            TurnCompleted ended => new JsonObject
            {
                ["type"] = "turn_completed",
                ["timestamp"] = stamp,
                ["reason"] = ended.Reason.ToString(),
                ["detail"] = ended.Detail,
            },
            _ => null,
        };
    }

    private static JsonObject MessageJson(ChatMessage message)
    {
        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ThinkingBlock thinking:
                    content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = thinking.Thinking });
                    break;
                case ToolCallBlock call:
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = TryParse(call.ArgumentsJson),
                    });
                    break;
            }
        }

        return new JsonObject { ["role"] = message.Role.ToString().ToLowerInvariant(), ["content"] = content };
    }

    private static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
