using JarvisCode.Core.Models;

namespace JarvisCode.Core.Agent;

/// <summary>What a microcompact pass did, so the caller can retry the request.</summary>
public sealed record MicrocompactResult(
    IReadOnlyList<ChatMessage> Messages,
    long TokensSaved,
    IReadOnlySet<string> ClearedIds,
    IReadOnlyDictionary<string, string> ClearedContent);

/// <summary>
/// The reference's keep-recent microcompact (CLI 2.1.251's <c>lqn</c>/<c>Pan</c>,
/// logged as "[KEEP-RECENT MC]"): keep the last <see cref="KeepRecent"/> results
/// of the tools that produce bulk output and clear every older one in place.
/// The reference reaches it only from the context-hint reject path, and only
/// when the pass would free at least <see cref="MinTokensSaved"/> tokens.
/// </summary>
public static class Microcompactor
{
    /// <summary>Results of the most recent N clearable calls are never cleared (the reference's k).</summary>
    public const int KeepRecent = 5;

    /// <summary>Below this estimate the pass is not worth running (Ian).</summary>
    public const int MinTokensSaved = 20_000;

    /// <summary>Tokens attributed to an embedded image or document (kBn).</summary>
    public const int MediaTokenEstimate = 2_000;

    /// <summary>What a cleared result says (jse).</summary>
    public const string ClearedPlaceholder = "[Old tool result content cleared]";

    /// <summary>Opening tag of a result whose body was written to disk instead (Vte/bBn).</summary>
    public const string PersistedPrefix = "<persisted-output>";

    /// <summary>Closing tag of that wrapper (Qfn).</summary>
    public const string PersistedSuffix = "</persisted-output>";

    /// <summary>
    /// The tools whose results the reference is willing to clear (its wBn set):
    /// the bulk-output family, spelled with this registry's names, which are
    /// the reference's own.
    /// </summary>
    public static readonly IReadOnlySet<string> ClearableTools =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Read", "Bash", "PowerShell", "Grep", "Glob", "WebSearch", "WebFetch", "Edit", "Write",
        };

    /// <summary>The reference's persisted-output stub for a result written to <paramref name="path"/>.</summary>
    public static string PersistedStub(string path) =>
        $"{PersistedPrefix}Tool result saved to: {path}\n\nUse Read to view{PersistedSuffix}";

    /// <summary>The reference's <c>EBn</c>: a result that has already been cleared or persisted.</summary>
    public static bool IsCleared(string content) =>
        content == ClearedPlaceholder || content.StartsWith(PersistedPrefix, StringComparison.Ordinal);

    /// <summary>The reference's <c>TBn</c>: chars/4 for text, a flat figure per image.</summary>
    public static long EstimateResultTokens(ToolResultBlock result) =>
        (result.Content.Length + 3) / 4 +
        (long)(result.Images?.Count ?? 0) * MediaTokenEstimate;

    /// <summary>
    /// The reference's <c>Pan</c>: the ids to clear, the ids to keep, what
    /// clearing them would save, and the result blocks that carry them.
    /// </summary>
    public static (HashSet<string> ClearSet, HashSet<string> KeepSet, long TokensSaved,
        List<ToolResultBlock> Candidates) Plan(IReadOnlyList<ChatMessage> messages, int keepRecent = KeepRecent)
    {
        var ordered = new List<string>();
        foreach (var message in messages)
        {
            if (message.Role != Role.Assistant)
                continue;
            foreach (var block in message.Content)
            {
                if (block is ToolCallBlock call && ClearableTools.Contains(call.Name))
                    ordered.Add(call.Id);
            }
        }

        int keep = Math.Max(1, keepRecent);
        var keepSet = new HashSet<string>(ordered.Skip(Math.Max(0, ordered.Count - keep)), StringComparer.Ordinal);
        var clearSet = new HashSet<string>(ordered.Where(id => !keepSet.Contains(id)), StringComparer.Ordinal);
        if (clearSet.Count == 0)
            return (clearSet, keepSet, 0, []);

        long saved = 0;
        var candidates = new List<ToolResultBlock>();
        foreach (var message in messages)
        {
            if (message.Role != Role.User)
                continue;
            foreach (var block in message.Content)
            {
                if (block is not ToolResultBlock result ||
                    !clearSet.Contains(result.ToolCallId) ||
                    IsCleared(result.Content))
                {
                    continue;
                }

                saved += EstimateResultTokens(result);
                candidates.Add(result);
            }
        }

        return (clearSet, keepSet, saved, candidates);
    }

    /// <summary>
    /// The reference's <c>lqn</c>: plan the pass, refuse it below the minimum
    /// saving, then rewrite the cleared results in place. <paramref name="persist"/>
    /// mirrors the reference's optional writer - a result whose body it stores
    /// gets the persisted-output stub, anything else the plain placeholder, and
    /// a result carrying images always gets the placeholder.
    /// </summary>
    public static MicrocompactResult? Run(
        IList<ChatMessage> messages,
        int keepRecent = KeepRecent,
        Func<ToolResultBlock, string?>? persist = null)
    {
        var (_, _, saved, candidates) = Plan([.. messages], keepRecent);
        if (saved < MinTokensSaved)
            return null;

        var clearedIds = new HashSet<string>(candidates.Select(c => c.ToolCallId), StringComparer.Ordinal);
        var clearedContent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            string? stored = candidate.Content.Length > 0 ? persist?.Invoke(candidate) : null;
            clearedContent[candidate.ToolCallId] = stored ?? ClearedPlaceholder;
        }

        Apply(messages, clearedIds, clearedContent);
        return new MicrocompactResult([.. messages], saved, clearedIds, clearedContent);
    }

    /// <summary>The reference's <c>_dt</c>: swap the cleared results' bodies in place.</summary>
    private static void Apply(
        IList<ChatMessage> messages,
        IReadOnlySet<string> clearedIds,
        IReadOnlyDictionary<string, string> clearedContent)
    {
        if (clearedIds.Count == 0)
            return;

        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != Role.User)
                continue;

            var blocks = new List<ContentBlock>(message.Content.Count);
            bool changed = false;
            foreach (var block in message.Content)
            {
                if (block is not ToolResultBlock result || !clearedIds.Contains(result.ToolCallId))
                {
                    blocks.Add(block);
                    continue;
                }

                // A result carrying media is always reduced to the placeholder:
                // the reference never points such a result at a stored copy.
                string replacement = result.Images is { Count: > 0 }
                    ? ClearedPlaceholder
                    : clearedContent.GetValueOrDefault(result.ToolCallId, ClearedPlaceholder);
                if (result.Content == replacement && result.Images is null or { Count: 0 })
                {
                    blocks.Add(block);
                    continue;
                }

                blocks.Add(result with { Content = replacement, Images = null });
                changed = true;
            }

            if (changed)
                messages[i] = message with { Content = blocks };
        }
    }
}
