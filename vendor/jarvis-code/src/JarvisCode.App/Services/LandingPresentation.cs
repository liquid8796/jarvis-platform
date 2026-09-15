namespace JarvisCode.App.Services;

/// <summary>
/// The new-session landing row of the reference's Code surface (desktop
/// 1.40609.1.0, `fA`/`pA`/`ce` in the ccd chunk): a folder chip whose menu lists
/// recent folders under "Recent" with a hover ×, a branch picker and a worktree
/// switch joined into one split pill while the folder is a git repository, one chip
/// per additional folder, and a FolderPlus button. The strings and the branch order
/// live here so they are unit-testable; the chips are drawn in ChatSurface.
/// </summary>
public static class LandingPresentation
{
    public const string WorkingDirectoryTooltip = "Working directory";
    public const string SelectFolder = "Select folder…";
    public const string RecentGroupLabel = "Recent";
    public const string RemoveFromRecent = "Remove from recent";
    public const string OpenFolder = "Open folder…";
    public const string SelectFolderDialogTitle = "Select folder for local session";
    public const string AddFolderDialogTitle = "Add folder to session";
    public const string AddAnotherFolder = "Add another folder";
    public const string BranchTooltip = "Branch to start from";
    public const string BranchPlaceholder = "—";
    public const string SearchBranches = "Search branches…";
    public const string NoBranchesMatch = "No branches match.";
    public const string WorktreeLabel = "worktree";
    public const string WorktreeTooltip = "Work in an isolated copy of the repository";

    /// <summary>
    /// The branches the reference lists first, in this order, before everything else
    /// alphabetical (`de` in the launch chunk).
    /// </summary>
    public static IReadOnlyList<string> WellKnownBranches { get; } = WellKnown;

    private static readonly string[] WellKnown =
        ["main", "master", "staging", "develop", "production", "prod", "development"];

    /// <summary>
    /// The reference's branch order: an exact match of the query first, then the
    /// repository's default branch, then the well-known names in their fixed order,
    /// then the rest alphabetically. A default branch missing from the list is added
    /// unless the query rules it out.
    /// </summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> branches, string? defaultBranch, string query = "")
    {
        var q = query.Trim().ToLowerInvariant();
        var all = branches.ToList();
        var filtered = q.Length == 0 ? all : all.Where(b => b.ToLowerInvariant().Contains(q)).ToList();
        if (defaultBranch is { Length: > 0 } && !all.Contains(defaultBranch) &&
            (q.Length == 0 || defaultBranch.ToLowerInvariant().Contains(q)))
        {
            filtered.Insert(0, defaultBranch);
        }

        filtered.Sort((a, b) =>
        {
            var la = a.ToLowerInvariant();
            var lb = b.ToLowerInvariant();
            if (q.Length > 0)
            {
                var ea = la == q;
                var eb = lb == q;
                if (ea && !eb)
                {
                    return -1;
                }

                if (eb && !ea)
                {
                    return 1;
                }
            }

            if (defaultBranch is not null)
            {
                if (a == defaultBranch)
                {
                    return -1;
                }

                if (b == defaultBranch)
                {
                    return 1;
                }
            }

            var ra = Array.IndexOf(WellKnown, la);
            var rb = Array.IndexOf(WellKnown, lb);
            var ka = ra >= 0;
            var kb = rb >= 0;
            if (ka && !kb)
            {
                return -1;
            }

            if (kb && !ka)
            {
                return 1;
            }

            if (ka && kb)
            {
                return ra - rb;
            }

            return string.Compare(a, b, StringComparison.CurrentCulture);
        });
        return filtered;
    }

    /// <summary>The folder chip's label: the folder's name, or the reference's placeholder.</summary>
    public static string FolderLabel(string? folder) =>
        string.IsNullOrEmpty(folder) ? SelectFolder : System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : folder;

    /// <summary>
    /// A recent folder's row: its name, and its parent path as the description when
    /// another recent folder shares the name (the reference disambiguates only then).
    /// </summary>
    public static IReadOnlyList<(string Path, string Name, string? Description)> RecentRows(IEnumerable<string> recents)
    {
        var list = recents.ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in list)
        {
            var name = FolderLabel(path);
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return [.. list.Select(path =>
        {
            var name = FolderLabel(path);
            var parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            return (path, name, counts[name] > 1 ? parent : null);
        })];
    }
}
