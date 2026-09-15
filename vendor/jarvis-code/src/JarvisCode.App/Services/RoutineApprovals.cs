using JarvisCode.Core.Routines;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's per-task browser grant (its <c>chromePermissionMode</c>):
/// every site, or the sites the run was allowed one at a time.
/// </summary>
public static class ChromePermissionModes
{
    /// <summary>The reference's own spelling; every site is granted for the run.</summary>
    public const string SkipAllPermissionChecks = "skip_all_permission_checks";

    /// <summary>Its other value: the sites in the allow list, and nothing else.</summary>
    public const string FollowAPlan = "follow_a_plan";
}

/// <summary>
/// The reference desktop's per-scheduled-task permission store (app.asar
/// <c>index.chunk-DnlgCaT3.js</c>: <c>addApprovedPermissions</c>,
/// <c>removeApprovedPermission</c>, <c>shouldAutoApprovePermission</c>,
/// <c>updateChromePermissions</c>, <c>getChromePermissions</c> and
/// <c>clearChromePermissions</c>, with the detail page's rows in the ion-dist
/// chunk <c>cfc18e0f4-DP8WK7zq.js</c>).
///
/// Everything here is pure; <see cref="RoutineRunner"/> reads and writes the
/// routine, and <see cref="UiPermissionGate"/> turns the stored rules into the
/// session rule lines the gate already speaks.
/// </summary>
public static class RoutineApprovals
{
    // ---- the detail page's rows ----

    public const string SectionHeading = "Always allowed";

    public const string Empty = "Approvals you grant during a run appear here.";

    public const string BrowserRow = "Browser";

    public const string AllWebsites = "All websites";

    public const string RemoveApproval = "Remove approval";

    /// <summary>The reference's <c>{count, plural, one {# website} other {# websites}}</c>.</summary>
    public static string WebsiteCount(int count) => count == 1 ? "1 website" : $"{count} websites";

    /// <summary>The Browser row's trailing value: every site, or how many are stored.</summary>
    public static string BrowserValue(string? mode, IReadOnlyList<string> domains) =>
        mode == ChromePermissionModes.SkipAllPermissionChecks ? AllWebsites : WebsiteCount(domains.Count);

    /// <summary>A tool row's label: the tool, and its rule content in parentheses when it has one.</summary>
    public static string ToolLabel(string toolName, string? ruleContent) =>
        string.IsNullOrEmpty(ruleContent) ? toolName : $"{toolName} ({ruleContent})";

    // ---- the store ----

    /// <summary>The reference's identity for a stored rule: <c>{toolName}\0{ruleContent}</c>.</summary>
    public static string Key(string toolName, string? ruleContent) =>
        toolName + '\0' + (ruleContent ?? "");

    /// <summary>
    /// The reference's <c>v6n</c>: a bare tool-name approval is never stored for
    /// these, because it would grant the whole tool rather than one call.
    /// </summary>
    public static IReadOnlyList<string> BroadTools { get; } =
    [
        "Bash", "PowerShell", "Read", "Write", "Edit", "MultiEdit", "NotebookEdit", "Grep", "Glob", "WebFetch",
    ];

    /// <summary>The reference's <c>y6n</c>: a content-less rule on a broad tool.</summary>
    public static bool IsTooBroad(string toolName, string? ruleContent) =>
        string.IsNullOrEmpty(ruleContent) && BroadTools.Contains(toolName, StringComparer.Ordinal);

    /// <summary>
    /// The reference's <c>addApprovedPermissions</c>: append the rules that are not
    /// already stored and not too broad, keeping the existing order.
    /// </summary>
    public static IReadOnlyList<string> Add(
        IReadOnlyList<string> stored, IEnumerable<(string ToolName, string? RuleContent)> incoming)
    {
        var seen = new HashSet<string>(stored, StringComparer.Ordinal);
        var result = stored.ToList();
        foreach (var (toolName, ruleContent) in incoming)
        {
            var key = Key(toolName, ruleContent);
            if (seen.Contains(key) || IsTooBroad(toolName, ruleContent))
            {
                continue;
            }

            seen.Add(key);
            result.Add(key);
        }

        return result;
    }

    /// <summary>
    /// The reference's <c>removeApprovedPermission</c>: with no rule content every
    /// rule of that tool goes, otherwise only the one the key names.
    /// </summary>
    public static IReadOnlyList<string> Remove(
        IReadOnlyList<string> stored, string toolName, string? ruleContent)
    {
        if (string.IsNullOrEmpty(ruleContent))
        {
            return [.. stored.Where(k => ToolNameOf(k) != toolName)];
        }

        var key = Key(toolName, ruleContent);
        return [.. stored.Where(k => k != key)];
    }

    /// <summary>The tool half of a stored key.</summary>
    public static string ToolNameOf(string key)
    {
        var separator = key.IndexOf('\0');
        return separator < 0 ? key : key[..separator];
    }

    /// <summary>The rule-content half of a stored key, or null when it carries none.</summary>
    public static string? RuleContentOf(string key)
    {
        var separator = key.IndexOf('\0');
        if (separator < 0 || separator == key.Length - 1)
        {
            return null;
        }

        return key[(separator + 1)..];
    }

    /// <summary>
    /// The reference's sentinel prefixes: a browser or computer-use permission is
    /// a live card rather than a rule, so it is never auto-approved from the store.
    /// </summary>
    public static bool IsSentinel(string toolName) =>
        toolName.StartsWith("browser:", StringComparison.Ordinal) ||
        toolName.StartsWith("computer:", StringComparison.Ordinal);

    /// <summary>The directory-mount tool, which the reference keeps on userSelectedFolders instead.</summary>
    public const string DirectoryMountTool = "mcp__ccd_directory__request_directory";

    /// <summary>The scheduled-task management prefix, which the reference always asks about live.</summary>
    public const string ScheduledTaskToolPrefix = "mcp__scheduled-tasks__";

    /// <summary>
    /// The reference's <c>shouldAutoApprovePermission</c>, in its own order: nothing
    /// requested is never approved, the directory mount and the scheduled-task tools
    /// and the browser/computer sentinels always ask, and otherwise every requested
    /// rule must already be stored — either by its own key, or by a bare approval of
    /// its tool.
    /// </summary>
    public static bool ShouldAutoApprove(
        IReadOnlyList<string> stored,
        string toolName,
        IReadOnlyList<(string ToolName, string? RuleContent)> requested)
    {
        if (requested.Count == 0)
        {
            return false;
        }

        if (toolName == DirectoryMountTool ||
            toolName.StartsWith(ScheduledTaskToolPrefix, StringComparison.Ordinal) ||
            IsSentinel(toolName) ||
            requested.Any(r => IsSentinel(r.ToolName)))
        {
            return false;
        }

        var usable = stored.Where(k => !IsTooBroad(ToolNameOf(k), RuleContentOf(k))).ToList();
        var keys = new HashSet<string>(usable, StringComparer.Ordinal);
        var bare = new HashSet<string>(
            usable.Where(k => RuleContentOf(k) is null).Select(ToolNameOf), StringComparer.Ordinal);
        return requested.All(r => keys.Contains(Key(r.ToolName, r.RuleContent)) || bare.Contains(r.ToolName));
    }

    // ---- what the gate speaks ----

    /// <summary>
    /// The stored rules as the permission gate's own rule lines
    /// (<c>Tool</c> or <c>Tool(content)</c>), which is what
    /// <see cref="UiPermissionGate.AddSessionRuleLines"/> takes.
    /// </summary>
    public static IReadOnlyList<string> RuleLines(IReadOnlyList<string> stored) =>
        [.. stored.Select(k => ToolLabel(ToolNameOf(k), RuleContentOf(k)))];

    /// <summary>Whether the routine's browser grant admits <paramref name="origin"/>.</summary>
    public static bool AllowsOrigin(Routine routine, string origin) =>
        routine.ChromePermissionMode == ChromePermissionModes.SkipAllPermissionChecks ||
        routine.ChromeAllowedDomains.Contains(origin, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The reference's <c>updateChromePermissions</c>: an unchanged mode and list is
    /// a no-op, and an empty list clears the stored domains rather than storing one.
    /// </summary>
    public static bool UpdateChrome(Routine routine, string? mode, IReadOnlyList<string> domains)
    {
        var same = routine.ChromePermissionMode == mode &&
                   routine.ChromeAllowedDomains.Count == domains.Count &&
                   !routine.ChromeAllowedDomains.Where((d, i) => d != domains[i]).Any();
        if (same)
        {
            return false;
        }

        routine.ChromePermissionMode = mode;
        routine.ChromeAllowedDomains = domains.Count > 0 ? [.. domains] : [];
        return true;
    }
}
