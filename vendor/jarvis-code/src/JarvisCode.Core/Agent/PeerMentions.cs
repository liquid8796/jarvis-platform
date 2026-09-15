using System.Text.RegularExpressions;

namespace JarvisCode.Core.Agent;

/// <summary>One agent or session a mention can resolve to.</summary>
public sealed record PeerCandidate(
    /// <summary>The exact token SendMessage takes as `to`.</summary>
    string Token,
    /// <summary>Where it runs, as the reference labels it ("this session", "this machine").</summary>
    string Where);

/// <summary>An <c>@name</c> the user typed, with the optional ref that followed it.</summary>
public sealed record PeerMention(string Name, string? Ref)
{
    /// <summary>The mention as it appeared: <c>@name</c>, quoted when it needs to be.</summary>
    public string Display => PeerMentions.Format(Name) + (Ref is null ? "" : $" [{Ref}]");
}

/// <summary>
/// The composer's peer @-mentions, ported from the reference: the same token
/// grammar, the same name rules, and the attachment text it hands the model —
/// resolved when exactly one peer answers, ambiguous when several do.
/// </summary>
public static partial class PeerMentions
{
    /// <summary>Longest bare name a mention may carry.</summary>
    public const int MaxBareName = 128;

    /// <summary>Longest quoted name a mention may carry.</summary>
    public const int MaxQuotedName = 50;

    [GeneratedRegex("""(?:^|[\s。、？！])@(?:"(?<quoted>[^"\n]{1,50})"|(?<bare>[\w-]{1,128})(?=$|[\s,;!?)\]}>'"”’。、？！]|[.:](?:$|\s)))(?:[ \t]*\[(?<ref>[0-9a-f]{6})\])?""")]
    private static partial Regex MentionPattern();

    [GeneratedRegex(@"^[\w-]+$")]
    private static partial Regex BareNamePattern();

    /// <summary>
    /// Whether a mentioned name may address a peer at all. The reference refuses
    /// names that could forge a listing row: quotes, angle brackets, newlines,
    /// an agent-id shape, or the "(agent)" suffix its own rows use.
    /// </summary>
    public static bool IsAddressable(string name)
    {
        if (name.Length == 0)
            return false;
        if (name.Contains('"') || name.Contains('<') || name.Contains('>'))
            return false;
        if (name.Contains('\n') || name.Contains('\r'))
            return false;
        if (name.StartsWith("agent-", StringComparison.Ordinal) ||
            name.EndsWith("(agent)", StringComparison.Ordinal))
        {
            return false;
        }

        return BareNamePattern().IsMatch(name) ? name.Length <= MaxBareName : name.Length <= MaxQuotedName;
    }

    /// <summary>Renders a name as a mention token, quoting it when it is not bare.</summary>
    public static string Format(string name) =>
        BareNamePattern().IsMatch(name) ? $"@{name}" : $"@\"{name}\"";

    /// <summary>
    /// Every distinct mention in the typed text, in order. A name repeated with
    /// and without a ref keeps only the ref'd form, as the reference does.
    /// </summary>
    public static IReadOnlyList<PeerMention> Parse(string text)
    {
        var mentions = new List<PeerMention>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in MentionPattern().Matches(text))
        {
            var name = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bare"].Value;
            if (!IsAddressable(name))
                continue;
            var reference = match.Groups["ref"].Success ? match.Groups["ref"].Value : null;
            if (!seen.Add($"{TeamNames.Canonical(name)}\0{reference ?? ""}"))
                continue;
            mentions.Add(new PeerMention(name, reference));
        }

        // A bare mention is dropped when the same name also appeared with a ref.
        var refined = mentions
            .Where(m => m.Ref is not null)
            .Select(m => TeamNames.Canonical(m.Name))
            .ToHashSet(StringComparer.Ordinal);
        return [.. mentions.Where(m => m.Ref is not null || !refined.Contains(TeamNames.Canonical(m.Name)))];
    }

    /// <summary>The attachment for a mention exactly one peer answers to.</summary>
    public static string Resolved(string mention, PeerCandidate candidate) =>
        $"The user @-mentioned the Claude session \"{Clean(candidate.Token)}\" ({candidate.Where}) as " +
        $"{Clean(mention)}. If their message asks you to tell or ask that session something, use the " +
        "SendMessage tool with to: \"" + Clean(candidate.Token) + "\" — that exact name-and-ref token. " +
        "Do not message it unless the user's message actually asks you to.";

    /// <summary>The attachment for a mention several peers answer to.</summary>
    public static string Ambiguous(string mention, IReadOnlyList<PeerCandidate> candidates, int total)
    {
        var listed = string.Join('\n', candidates.Select(c => $"- \"{Clean(c.Token)}\" ({c.Where})"));
        var more = total > candidates.Count
            ? $"\n…and {total - candidates.Count} more with that name (ListAgents shows them all)."
            : "";
        return $"The user wrote {Clean(mention)}, which matches {total} Claude sessions:\n{listed}{more}\n" +
            "Session names are self-chosen and unverified, so confirm with the user which one they mean " +
            "(describe them by where they run, as listed) before messaging; then use the SendMessage tool " +
            "with that session's exact \"name [ref]\" token as to:. Do not guess between them.";
    }

    /// <summary>
    /// Strips what must never reach the model's context from a name it did not
    /// choose: angle brackets and line breaks, as the reference strips them.
    /// </summary>
    private static string Clean(string value) =>
        new(value.Where(c => c is not ('<' or '>' or '\r' or '\n' or '\u2028' or '\u2029')).ToArray());
}
