using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Mcp;

/// <summary>Shared JSON-RPC result parsing for the MCP transports.</summary>
public static class McpResultParsing
{
    public static IReadOnlyList<McpToolDescriptor> ParseTools(JsonNode result, string serverName)
    {
        var tools = new List<McpToolDescriptor>();
        foreach (var entry in (result["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Tools.JsonArgs.GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var schema = entry["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
            tools.Add(new McpToolDescriptor(
                name,
                Tools.JsonArgs.GetString(entry, "description") ?? $"Tool from the {serverName} MCP server",
                (JsonObject)schema.DeepClone())
            {
                AlwaysLoad = McpToolDescriptor.ReadAlwaysLoad(entry),
                SearchHint = McpToolDescriptor.ReadSearchHint(entry),
                MaxResultSizeChars = McpToolDescriptor.ReadMaxResultSizeChars(entry),
                RequiresUserInteraction = McpToolDescriptor.ReadRequiresUserInteraction(entry),
            });
        }

        return tools;
    }

    public static IReadOnlyList<McpResourceDescriptor> ParseResources(JsonNode result)
    {
        var resources = new List<McpResourceDescriptor>();
        foreach (var entry in (result["resources"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var uri = Tools.JsonArgs.GetString(entry, "uri");
            if (string.IsNullOrWhiteSpace(uri))
                continue;
            resources.Add(new McpResourceDescriptor(
                uri,
                Tools.JsonArgs.GetString(entry, "name") ?? uri,
                Tools.JsonArgs.GetString(entry, "description"),
                Tools.JsonArgs.GetString(entry, "mimeType")));
        }

        return resources;
    }

    public static string ParseResourceText(JsonNode result)
    {
        var text = new StringBuilder();
        foreach (var entry in (result["contents"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Tools.JsonArgs.GetString(entry, "text") is { } part)
                text.AppendLine(part);
            else if (entry.ContainsKey("blob"))
                text.AppendLine($"[binary content ({Tools.JsonArgs.GetString(entry, "mimeType") ?? "unknown type"}) omitted]");
        }

        return text.ToString().TrimEnd();
    }

    public static IReadOnlyList<McpPromptDescriptor> ParsePrompts(JsonNode result)
    {
        var prompts = new List<McpPromptDescriptor>();
        foreach (var entry in (result["prompts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Tools.JsonArgs.GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var arguments = new List<McpPromptArgument>();
            foreach (var arg in (entry["arguments"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Tools.JsonArgs.GetString(arg, "name") is { Length: > 0 } argName)
                    arguments.Add(new McpPromptArgument(
                        argName,
                        Tools.JsonArgs.GetString(arg, "description"),
                        arg["required"]?.GetValue<bool>() ?? false));
            }

            prompts.Add(new McpPromptDescriptor(name, Tools.JsonArgs.GetString(entry, "description"), arguments));
        }

        return prompts;
    }

    public static string ParsePromptText(JsonNode result)
    {
        var text = new StringBuilder();
        foreach (var message in (result["messages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var content = message["content"];
            string? part = content switch
            {
                JsonObject block when Tools.JsonArgs.GetString(block, "type") == "text" =>
                    Tools.JsonArgs.GetString(block, "text"),
                JsonValue value when value.TryGetValue(out string? plain) => plain,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(part))
                continue;
            if (text.Length > 0)
                text.AppendLine().AppendLine();
            text.Append(part.Trim());
        }

        return text.ToString();
    }

    public static McpCallResult ParseCallResult(JsonNode result)
    {
        var text = new StringBuilder();
        var images = new List<Models.ImageBlock>();
        foreach (var block in (result["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Tools.JsonArgs.GetString(block, "type") == "text")
                text.AppendLine(Tools.JsonArgs.GetString(block, "text") ?? "");
            else if (Tools.JsonArgs.GetString(block, "type") == "image" &&
                Tools.JsonArgs.GetString(block, "mimeType") is { } media &&
                Tools.JsonArgs.GetString(block, "data") is { } data)
                images.Add(new Models.ImageBlock(media, data));
            else if (Tools.JsonArgs.GetString(block, "type") == "resource" && block["resource"] is JsonObject resource)
                text.AppendLine(resource["text"]?.ToString() ?? resource.ToJsonString());
            else
                text.AppendLine($"[{Tools.JsonArgs.GetString(block, "type") ?? "unknown"} content omitted]");
        }

        bool isError = result["isError"]?.GetValue<bool>() ?? false;
        var structured = result["structuredContent"]?.DeepClone();
        if (structured is not null) text.AppendLine(structured.ToJsonString());
        return new McpCallResult(text.ToString().TrimEnd(), isError)
        { Images = images.Count == 0 ? null : images, StructuredContent = structured };
    }
}
