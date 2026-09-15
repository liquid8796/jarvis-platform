using System.Text.Json.Nodes;
using JarvisCode.Core.Models;

namespace JarvisCode.Core.Utilities;

public sealed record TridentResult(
    IReadOnlyList<ChatMessage> Messages,
    int SupersededResults,
    int CollapsedMessages,
    int CharsSaved)
{
    public bool ChangedAnything => SupersededResults > 0 || CollapsedMessages > 0;
}

/// <summary>
/// Zero-LLM-cost context pruning (ported from claw-code's Trident pipeline), run
/// before compaction:
/// stage 1 stubs out file-tool results that a later write/edit to the same path
/// made stale (the content is provably outdated, so nothing factual is lost);
/// stage 2 collapses long runs of short chatty messages into one marker.
/// Only user-side result content and whole chatty messages are touched — assistant
/// blocks (thinking signatures, tool-call arguments) are never modified, so provider
/// replay stays valid.
/// </summary>
public static class TridentPruner
{
    private static readonly HashSet<string> FileTools = ["Read", "Write", "Edit"];
    private static readonly HashSet<string> MutatingFileTools = ["Write", "Edit"];

    private const int ChattyMaxChars = 200;
    private const int ChattyMinRun = 4;

    public static TridentResult Prune(IReadOnlyList<ChatMessage> messages, int preserveRecent = 6)
    {
        var (afterSupersede, superseded, savedBySupersede) = SupersedeStaleFileResults(messages);
        var (afterCollapse, collapsed, savedByCollapse) = CollapseChattyRuns(afterSupersede, preserveRecent);
        return new TridentResult(afterCollapse, superseded, collapsed, savedBySupersede + savedByCollapse);
    }

    // ---------------- Stage 1: supersede stale file results ----------------

    private sealed record FileCall(string ToolName, string PathKey, string DisplayPath, int MessageIndex);

    private static (IReadOnlyList<ChatMessage> Messages, int Superseded, int CharsSaved) SupersedeStaleFileResults(
        IReadOnlyList<ChatMessage> messages)
    {
        // Providers like Ollama reuse tool-call ids across turns, so results must
        // match the nth call with that id, not a single dictionary entry.
        var callsById = new Dictionary<string, Queue<FileCall>>(StringComparer.Ordinal);
        var lastMutationIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < messages.Count; i++)
        {
            foreach (var call in messages[i].Content.OfType<ToolCallBlock>())
            {
                if (!FileTools.Contains(call.Name))
                    continue;
                var path = ExtractFilePath(call.ArgumentsJson);
                if (path is null)
                    continue;
                var key = NormalizePathKey(path);
                if (!callsById.TryGetValue(call.Id, out var queue))
                    callsById[call.Id] = queue = new Queue<FileCall>();
                queue.Enqueue(new FileCall(call.Name, key, path, i));
                if (MutatingFileTools.Contains(call.Name))
                    lastMutationIndex[key] = i;
            }
        }

        if (lastMutationIndex.Count == 0)
            return (messages, 0, 0);

        int superseded = 0;
        int saved = 0;
        List<ChatMessage>? rewritten = null;
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            List<ContentBlock>? newBlocks = null;
            for (int b = 0; b < message.Content.Count; b++)
            {
                if (message.Content[b] is not ToolResultBlock result)
                    continue;
                if (!callsById.TryGetValue(result.ToolCallId, out var queue) || queue.Count == 0)
                    continue;
                // Dequeue unconditionally so reused ids stay aligned with the nth call.
                var call = queue.Dequeue();
                if (result.IsError)
                    continue;
                if (!lastMutationIndex.TryGetValue(call.PathKey, out int mutationIndex) ||
                    call.MessageIndex >= mutationIndex)
                {
                    continue;
                }
                var stub = $"[Superseded: this {call.ToolName} result for {call.DisplayPath} is stale — " +
                           "the file was modified later in the conversation. Re-read it if needed.]";
                if (result.Content.Length <= stub.Length)
                    continue;
                newBlocks ??= [.. message.Content];
                newBlocks[b] = result with { Content = stub };
                superseded++;
                saved += result.Content.Length - stub.Length;
            }
            if (newBlocks is not null)
            {
                rewritten ??= [.. messages];
                rewritten[i] = message with { Content = newBlocks };
            }
        }
        return (rewritten ?? messages, superseded, saved);
    }

    private static string? ExtractFilePath(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;
        try
        {
            var path = (JsonNode.Parse(argumentsJson) as JsonObject)?["file_path"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Comparable key without filesystem access. Relative-vs-absolute references to
    /// the same file get different keys and are simply not pruned — the safe miss.
    /// </summary>
    private static string NormalizePathKey(string path) =>
        path.Trim().Replace('\\', '/').ToLowerInvariant();

    // ---------------- Stage 2: collapse chatty runs ----------------

    private static (IReadOnlyList<ChatMessage> Messages, int Collapsed, int CharsSaved) CollapseChattyRuns(
        IReadOnlyList<ChatMessage> messages, int preserveRecent)
    {
        int collapsibleEnd = messages.Count - Math.Max(0, preserveRecent);
        if (collapsibleEnd < ChattyMinRun)
            return (messages, 0, 0);

        var result = new List<ChatMessage>(messages.Count);
        int collapsed = 0;
        int saved = 0;
        int i = 0;
        while (i < messages.Count)
        {
            if (i >= collapsibleEnd || !IsChatty(messages[i]))
            {
                result.Add(messages[i]);
                i++;
                continue;
            }
            int runEnd = i;
            while (runEnd < collapsibleEnd && IsChatty(messages[runEnd]))
                runEnd++;
            int runLength = runEnd - i;
            if (runLength < ChattyMinRun)
            {
                for (; i < runEnd; i++)
                    result.Add(messages[i]);
                continue;
            }
            var marker = $"[Collapsed conversation: {runLength} short exchanges omitted to save context.]";
            result.Add(ChatMessage.FromUserText(marker));
            collapsed += runLength;
            saved += messages.Skip(i).Take(runLength).Sum(m => m.GetText().Length) - marker.Length;
            i = runEnd;
        }
        return collapsed > 0 ? (result, collapsed, Math.Max(0, saved)) : (messages, 0, 0);
    }

    private static bool IsChatty(ChatMessage message) =>
        message.Content.All(block => block is TextBlock) &&
        message.GetText().Length < ChattyMaxChars;
}
