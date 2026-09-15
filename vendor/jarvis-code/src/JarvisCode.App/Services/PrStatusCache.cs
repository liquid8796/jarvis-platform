using System.Collections.Concurrent;

namespace JarvisCode.App.Services;

/// <summary>
/// One pull request per working directory, so the sidebar's PR badge, the home
/// view's Pull requests section and the PR bar all read the same answer instead of
/// each running `gh` for itself. The reference keeps the same shape — one entry per
/// repository and branch, not one per session — which is why its own list dedupes by
/// PR url rather than by session.
///
/// Nothing here blocks: a folder that has not been looked up yet reports no pull
/// request, and <see cref="RefreshAsync"/> fills it in off the UI thread.
/// </summary>
public sealed class PrStatusCache
{
    private readonly ConcurrentDictionary<string, PrInfo?> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _readAt = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshing;

    /// <summary>How long an answer stands before the next refresh re-reads it.</summary>
    public TimeSpan Freshness { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The cache changed; the sidebar and the home view repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>What is known about a folder right now, without asking `gh`.</summary>
    public PrInfo? For(string workingDirectory) =>
        workingDirectory.Length > 0 && _byFolder.TryGetValue(workingDirectory, out var pr) ? pr : null;

    /// <summary>Whether the folder has been looked up at all.</summary>
    public bool Knows(string workingDirectory) => _readAt.ContainsKey(workingDirectory);

    /// <summary>Records a lookup someone else already made (the PR bar's, typically).</summary>
    public void Record(string workingDirectory, PrInfo? pr)
    {
        if (workingDirectory.Length == 0)
        {
            return;
        }

        _byFolder[workingDirectory] = pr;
        _readAt[workingDirectory] = DateTimeOffset.UtcNow;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Looks up the folders that are stale, one `gh pr view` each, off the calling
    /// thread. One refresh at a time: a second call while one is in flight returns at
    /// once rather than queueing another round of subprocesses.
    /// </summary>
    public async Task RefreshAsync(IEnumerable<string> folders, int limit = 8)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var stale = Stale(folders, limit, DateTimeOffset.UtcNow);
            if (stale.Count == 0)
            {
                return;
            }

            var found = await Task.Run(() =>
            {
                var answers = new List<(string Folder, PrInfo? Pr)>();
                foreach (var folder in stale)
                {
                    answers.Add((folder, GitStatusProbe.ReadPullRequest(folder)));
                }

                return answers;
            });

            foreach (var (folder, pr) in found)
            {
                _byFolder[folder] = pr;
                _readAt[folder] = DateTimeOffset.UtcNow;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>Which of the folders need re-reading; separated out so the rule is testable.</summary>
    public IReadOnlyList<string> Stale(IEnumerable<string> folders, int limit, DateTimeOffset now)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        foreach (var folder in folders)
        {
            if (folder.Length == 0 || !seen.Add(folder))
            {
                continue;
            }

            if (_readAt.TryGetValue(folder, out var at) && now - at < Freshness)
            {
                continue;
            }

            stale.Add(folder);
            if (stale.Count >= limit)
            {
                break;
            }
        }

        return stale;
    }
}
