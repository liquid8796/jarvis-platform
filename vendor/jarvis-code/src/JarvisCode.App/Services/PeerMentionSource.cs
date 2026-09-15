using System.IO;
using JarvisCode.App.Composition;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Agent;

namespace JarvisCode.App.Services;

/// <summary>
/// The peers a composer mention can name: this session's teammates and the
/// other Code sessions on this machine. The reference builds the same candidate
/// set from its team context plus its session list (its cloud and bridge rows
/// have no counterpart here).
/// </summary>
public static class PeerMentionSource
{
    /// <summary>Where a candidate runs, in the reference's label style.</summary>
    public const string InThisSession = "teammate in this session";

    public const string OnThisMachine = "this machine";

    /// <summary>Longest candidate list an ambiguous mention prints, as the reference caps it.</summary>
    public const int MaxListedCandidates = 3;

    /// <summary>
    /// Collects the addressable peers. Teammates come first: they are this
    /// session's own agents, and a bare name should reach them before it reaches
    /// a stranger's session title.
    /// </summary>
    public static async Task<IReadOnlyList<PeerCandidate>> CollectAsync(
        AppServices services, ChatViewModel viewModel)
    {
        var candidates = new List<PeerCandidate>();
        foreach (var name in viewModel.Workers.RunningNames())
            candidates.Add(new PeerCandidate(name, InThisSession));

        if (viewModel.Teams.Read(viewModel.Session.Id) is { } team)
        {
            foreach (var member in team.Members)
            {
                if (!candidates.Any(c => string.Equals(
                        TeamNames.Canonical(c.Token), TeamNames.Canonical(member.Name), StringComparison.Ordinal)))
                {
                    candidates.Add(new PeerCandidate(member.Name, InThisSession));
                }
            }
        }

        try
        {
            var sessions = (await services.Sessions.ListAsync())
                .Where(s => s.Id != viewModel.Session.Id)
                .OrderByDescending(static s => s.UpdatedAt)
                .Take(20);
            foreach (var session in sessions)
            {
                if (!string.IsNullOrWhiteSpace(session.Title) && PeerMentions.IsAddressable(session.Title))
                    candidates.Add(new PeerCandidate(session.Title, OnThisMachine));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A session list we cannot read simply offers no candidates.
        }

        return candidates;
    }

    /// <summary>
    /// Turns the mentions in a typed message into the reference's peer_mention
    /// attachments: one line per mention, resolved or ambiguous. Returns an
    /// empty list when nothing was mentioned or nothing matched.
    /// </summary>
    public static IReadOnlyList<string> Attachments(
        string text, IReadOnlyList<PeerCandidate> candidates, IReadOnlyList<string>? attachedPaths = null)
    {
        // A mention the composer already resolved to a file belongs to that
        // file, as the reference resolves file mentions before peer ones.
        var files = attachedPaths is null
            ? []
            : attachedPaths
                .SelectMany(path => new[] { path, Path.GetFileName(path) })
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(TeamNames.Canonical)
                .ToHashSet(StringComparer.Ordinal);

        var attachments = new List<string>();
        foreach (var mention in PeerMentions.Parse(text))
        {
            var key = TeamNames.Canonical(mention.Name);
            if (mention.Ref is null && files.Contains(key))
                continue;
            var matches = candidates
                .Where(c => string.Equals(TeamNames.Canonical(c.Token), key, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
                continue;
            attachments.Add(matches.Count == 1
                ? PeerMentions.Resolved(mention.Display, matches[0])
                : PeerMentions.Ambiguous(
                    mention.Display, matches.Take(MaxListedCandidates).ToList(), matches.Count));
        }

        return attachments;
    }
}
