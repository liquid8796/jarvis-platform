using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Services;

public sealed record CliImportResult(int Imported, int SkippedExisting, int SkippedEmpty, int Failed);

/// <summary>
/// "Import Claude Code CLI sessions…": reads the transcripts the CLI (and the
/// reference desktop app) keep under ~/.claude/projects — one JSONL per session —
/// and converts each into an engine session, keeping the conversation history:
/// user prompts, assistant text and thinking, tool calls and their results.
/// Already-imported sessions are recognised by id and skipped, so the import can
/// run again any time.
/// </summary>
public static class CliSessionImporter
{
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private const int MaxCharsPerBlock = 30_000;

    /// <summary>
    /// Imports one CLI transcript by its uuid and returns the session id it landed
    /// under, or null when this machine has no such transcript. This is what a
    /// jarvis://resume link opens, where the reference's own resume deep link
    /// imports a single session rather than sweeping the folder.
    /// </summary>
    public static async Task<string?> ImportOneAsync(
        JsonSessionStore store, string uuid, string? root = null)
    {
        root ??= DefaultRoot;
        var id = "cli-" + uuid;
        if ((await store.ListAsync()).Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return id;
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var project in Directory.GetDirectories(root))
        {
            var file = Path.Combine(project, uuid + ".jsonl");
            if (!File.Exists(file))
            {
                continue;
            }

            var session = Convert(file, id);
            if (session is null || session.Messages.Count == 0)
            {
                return null;
            }

            await store.SaveAsync(session);
            return id;
        }

        return null;
    }

    public static async Task<CliImportResult> ImportAsync(JsonSessionStore store, string? root = null)
    {
        root ??= DefaultRoot;
        int imported = 0, skippedExisting = 0, skippedEmpty = 0, failed = 0;
        if (!Directory.Exists(root))
        {
            return new CliImportResult(0, 0, 0, 0);
        }

        var existing = (await store.ListAsync())
            .Select(static s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var project in Directory.GetDirectories(root))
        {
            // Top level only: subagent journals live in deeper folders and are not sessions.
            foreach (var file in Directory.GetFiles(project, "*.jsonl"))
            {
                var uuid = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(uuid, out _))
                {
                    continue;
                }

                var id = "cli-" + uuid;
                if (existing.Contains(id))
                {
                    skippedExisting++;
                    continue;
                }

                try
                {
                    var session = Convert(file, id);
                    if (session is null || session.Messages.Count == 0)
                    {
                        skippedEmpty++;
                        continue;
                    }

                    await store.SaveAsync(session);
                    imported++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
                {
                    failed++;
                }
            }
        }

        return new CliImportResult(imported, skippedExisting, skippedEmpty, failed);
    }

    private static Session? Convert(string file, string id)
    {
        string title = "";
        string cwd = "";
        DateTimeOffset created = default;
        DateTimeOffset updated = default;
        var messages = new List<ChatMessage>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        Role? lastRole = null;

        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject? entry;
            try
            {
                entry = JsonNode.Parse(line) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            switch (entry["type"]?.GetValue<string>())
            {
                case "custom-title" when entry["customTitle"]?.GetValue<string>() is { Length: > 0 } custom:
                    title = custom;
                    break;

                case "summary" when title.Length == 0 &&
                                    entry["summary"]?.GetValue<string>() is { Length: > 0 } summary:
                    title = summary;
                    break;

                case "user" or "assistant":
                    if (entry["isSidechain"]?.GetValue<bool>() == true ||
                        entry["message"] is not JsonObject message)
                    {
                        break;
                    }

                    if (entry["cwd"]?.GetValue<string>() is { Length: > 0 } entryCwd)
                    {
                        cwd = entryCwd;
                    }

                    if (entry["timestamp"]?.GetValue<string>() is { } stamp &&
                        DateTimeOffset.TryParse(stamp, out var at))
                    {
                        if (created == default)
                        {
                            created = at;
                        }

                        updated = at;
                    }

                    var role = message["role"]?.GetValue<string>() == "assistant" ? Role.Assistant : Role.User;
                    var blocks = ConvertBlocks(message["content"], toolNames);
                    if (blocks.Count == 0)
                    {
                        break;
                    }

                    // The CLI writes one entry per API message; providers need strict
                    // role alternation, so consecutive same-role entries merge.
                    if (lastRole == role && messages.Count > 0)
                    {
                        messages[^1] = messages[^1] with { Content = [.. messages[^1].Content, .. blocks] };
                    }
                    else
                    {
                        messages.Add(new ChatMessage(role, blocks));
                        lastRole = role;
                    }

                    break;
            }
        }

        if (messages.Count == 0)
        {
            return null;
        }

        if (title.Length == 0)
        {
            var firstText = messages
                .Where(static m => m.Role == Role.User)
                .SelectMany(static m => m.Content.OfType<TextBlock>())
                .Select(static b => b.Text.ReplaceLineEndings(" ").Trim())
                .FirstOrDefault(static t => t.Length > 0) ?? "Imported CLI session";
            title = firstText.Length <= 48 ? firstText : firstText[..48].TrimEnd() + "…";
        }

        return new Session
        {
            Id = id,
            Title = title,
            CreatedAt = created == default ? DateTimeOffset.Now : created,
            UpdatedAt = updated == default ? DateTimeOffset.Now : updated,
            WorkingDirectory = cwd,
            Messages = messages,
        };
    }

    private static List<ContentBlock> ConvertBlocks(JsonNode? content, Dictionary<string, string> toolNames)
    {
        var blocks = new List<ContentBlock>();
        switch (content)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text.Trim().Length > 0)
                {
                    blocks.Add(new TextBlock(Cap(text)));
                }

                break;

            case JsonArray array:
                foreach (var node in array.OfType<JsonObject>())
                {
                    switch (node["type"]?.GetValue<string>())
                    {
                        case "text" when node["text"]?.GetValue<string>() is { } text && text.Trim().Length > 0:
                            blocks.Add(new TextBlock(Cap(text)));
                            break;

                        case "thinking" when node["thinking"]?.GetValue<string>() is { Length: > 0 } thinking:
                            blocks.Add(new ThinkingBlock(Cap(thinking), node["signature"]?.GetValue<string>()));
                            break;

                        case "tool_use" when node["id"]?.GetValue<string>() is { } callId:
                            var name = node["name"]?.GetValue<string>() ?? "tool";
                            toolNames[callId] = name;
                            blocks.Add(new ToolCallBlock(callId, name, node["input"]?.ToJsonString() ?? "{}"));
                            break;

                        case "tool_result" when node["tool_use_id"]?.GetValue<string>() is { } resultId:
                            blocks.Add(new ToolResultBlock(
                                resultId,
                                toolNames.GetValueOrDefault(resultId, "tool"),
                                Cap(FlattenResult(node["content"])),
                                node["is_error"]?.GetValue<bool>() ?? false));
                            break;
                    }
                }

                break;
        }

        return blocks;
    }

    /// <summary>Tool results arrive as a string or an array of text/image parts; text survives.</summary>
    private static string FlattenResult(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray array => string.Join(
            "\n",
            array.OfType<JsonObject>()
                .Where(static p => p["type"]?.GetValue<string>() == "text")
                .Select(static p => p["text"]?.GetValue<string>() ?? "")
                .Where(static t => t.Length > 0)),
        _ => "",
    };

    private static string Cap(string text)
        => text.Length <= MaxCharsPerBlock ? text : text[..MaxCharsPerBlock] + "\n… [truncated on import]";
}
