namespace JarvisCode.App.Services;

/// <summary>One choice of a filter submenu: the stored key and the label the row shows.</summary>
public sealed record FilterOption(string Key, string Label);

/// <summary>
/// A submenu of the filter popover: its label, the choices, the chosen key, and
/// whether the value is "active" (shown in accent) because it is not the default.
/// </summary>
public sealed record FilterSection(
    string Label,
    IReadOnlyList<FilterOption> Options,
    string Value,
    bool Active = false,
    string? SeparatorBefore = null,
    bool Disabled = false)
{
    /// <summary>The trailing text on the submenu trigger: the chosen option's label.</summary>
    public string ValueLabel => Options.FirstOrDefault(o => o.Key == Value)?.Label ?? Value;
}

/// <summary>A checkbox row of the popover (Show empty groups, Show PR status).</summary>
public sealed record FilterToggle(string Key, string Label, bool Checked);

/// <summary>The whole popover, section by section in the reference's order.</summary>
public sealed record FilterMenu(
    IReadOnlyList<FilterSection> StatusSections,
    IReadOnlyList<FilterSection> GroupSortSections,
    IReadOnlyList<FilterToggle> Toggles,
    bool ShowClearFilters);

/// <summary>The sidebar's filter state, as the reference stores it per mode.</summary>
public sealed record SidebarFilterState
{
    public string GroupBy { get; init; } = SidebarFilterModel.DefaultGroupBy;
    public string SortBy { get; init; } = SidebarFilterModel.DefaultSortBy;
    public string Status { get; init; } = SidebarFilterModel.DefaultStatus;
    public bool ShowEmptyFolders { get; init; }
    public bool ShowPrStatus { get; init; } = true;

    /// <summary>
    /// The Last-activity window the State grouping filters by — the reference's
    /// <c>stateActivityDays</c>, 7 by default and 0 for "All".
    /// </summary>
    public int StateActivityDays { get; init; } = SidebarFilterModel.DefaultStateActivityDays;
}

/// <summary>
/// The reference sidebar's filter icon and popover for the code mode (desktop
/// 1.40609.1.0, the popover at `shared-18`@172200 with its option lists at 168400
/// and its submenu row `zS`): a Status submenu, a rule, Group by and
/// Sort by submenus, a rule, two checkbox rows, and Clear filters while a filter is
/// active. Each submenu trigger carries the chosen value as trailing footnote text,
/// accent-coloured when it is not the default. The defaults are the reference's own:
/// its `sortByByMode[mode] ?? "recency"` and its `xg(mode, isDesktop)`, which answers
/// "project" for the code sidebar on the desktop. Its Environment section is
/// cloud-only and is declared rather than built; its Last activity submenu is local
/// and is offered on exactly the condition the reference offers it — while the
/// grouping is State (re-measured against 1.44121.2.0, `shared-19-BctYnjt1.js`@243500
/// with the option list `[0,1,3,7,30]` at `shared-2-dTgTvujb.js`@94866 and the
/// default 7 at `shared-17-DsNaDSP_.js`@305200). Pure so the option lists, the
/// defaults and the "active" rule are unit-testable.
/// </summary>
public static class SidebarFilterModel
{
    public const string DefaultGroupBy = "project";
    public const string DefaultSortBy = "recency";
    public const string DefaultStatus = "active";

    /// <summary>The reference's own default window for the State grouping.</summary>
    public const int DefaultStateActivityDays = 7;

    /// <summary>
    /// The reference's <c>$l</c>. Zero is its "All" and is rendered last, after the
    /// day windows, which is what its <c>[...$l.filter(e =&gt; e !== 0), 0]</c> does.
    /// </summary>
    public static IReadOnlyList<int> StateActivityDayChoices { get; } = [0, 1, 3, 7, 30];

    /// <summary>The Last-activity submenu's rows, in the reference's order.</summary>
    public static IReadOnlyList<FilterOption> StateActivityOptions { get; } =
    [
        .. StateActivityDayChoices.Where(static d => d != 0).Select(static d => new FilterOption($"{d}", $"{d}d")),
        new("0", "All"),
    ];

    public static IReadOnlyList<FilterOption> GroupByOptions { get; } =
    [
        new("date", "Date"),
        new("project", "Folder"),
        new("state", "State"),
        new("custom", "Custom groups"),
        new("none", "None"),
    ];

    public static IReadOnlyList<FilterOption> SortByOptions { get; } =
    [
        new("alpha", "Name"),
        new("created", "Date created"),
        new("recency", "Last activity"),
    ];

    public static IReadOnlyList<FilterOption> StatusOptions { get; } =
    [
        new("active", "Active"),
        new("archived", "Archived"),
        new("all", "All"),
    ];

    /// <summary>The filter icon's accessible name (the reference's `8JsNlucP/1`).</summary>
    public const string TriggerName = "Filter sessions";

    /// <summary>The icon's tooltip: it says so while a filter is on.</summary>
    public static string TriggerLabel(SidebarFilterState state) =>
        IsFilterActive(state) ? "Filter (active)" : "Filter";

    /// <summary>
    /// The reference's `ON`: the code mode counts as filtered when the status is not
    /// Active, when the State grouping's activity window is not its default seven
    /// days, or when the folder grouping shows empty folders. (Its environment arm is
    /// cloud-only and does not exist here.)
    /// </summary>
    public static bool IsFilterActive(SidebarFilterState state) =>
        state.Status != DefaultStatus ||
        (state.GroupBy == "state" && state.StateActivityDays != DefaultStateActivityDays) ||
        (state.GroupBy == "project" && state.ShowEmptyFolders);

    public static FilterMenu Build(SidebarFilterState state)
    {
        var status = new FilterSection("Status", StatusOptions, state.Status, Active: state.Status != DefaultStatus);
        var top = new List<FilterSection> { status };
        if (state.GroupBy == "state")
        {
            top.Add(new FilterSection("Last activity", StateActivityOptions, $"{state.StateActivityDays}",
                Active: state.StateActivityDays != DefaultStateActivityDays));
        }

        var groupBy = new FilterSection("Group by", GroupByOptions, state.GroupBy, SeparatorBefore: "none");
        var sortBy = new FilterSection("Sort by", SortByOptions, state.SortBy);
        var toggles = new List<FilterToggle>();
        if (state.GroupBy == "project")
        {
            toggles.Add(new FilterToggle("showEmptyFolders", "Show empty groups", state.ShowEmptyFolders));
        }

        toggles.Add(new FilterToggle("showPrStatus", "Show PR status", state.ShowPrStatus));
        return new FilterMenu(top, [groupBy, sortBy], toggles, IsFilterActive(state));
    }

    /// <summary>"Clear filters": the reference's `Ne` resets status, environments, activity days and empty folders.</summary>
    public static SidebarFilterState ClearFilters(SidebarFilterState state) =>
        state with
        {
            Status = DefaultStatus,
            ShowEmptyFolders = false,
            StateActivityDays = DefaultStateActivityDays,
        };

    /// <summary>The stored Last-activity window, clamped to a choice the menu offers.</summary>
    public static int NormalizeStateActivityDays(int value) =>
        StateActivityDayChoices.Contains(value) ? value : DefaultStateActivityDays;

    public static string NormalizeGroupBy(string? value) =>
        GroupByOptions.Any(o => o.Key == value) ? value! : DefaultGroupBy;

    public static string NormalizeSortBy(string? value) =>
        SortByOptions.Any(o => o.Key == value) ? value! : DefaultSortBy;

    public static string NormalizeStatus(string? value) =>
        StatusOptions.Any(o => o.Key == value) ? value! : DefaultStatus;

    /// <summary>
    /// Orders sessions by the chosen sort: Name (ordinal, case-insensitive), Date
    /// created (newest first, from the session id's own timestamp prefix when the
    /// summary has no creation date) or Last activity (newest first).
    /// </summary>
    public static IEnumerable<T> Sort<T>(
        IEnumerable<T> sessions,
        string sortBy,
        Func<T, string> title,
        Func<T, DateTimeOffset> updatedAt,
        Func<T, DateTimeOffset> createdAt) =>
        sortBy switch
        {
            "alpha" => sessions.OrderBy(title, StringComparer.OrdinalIgnoreCase),
            "created" => sessions.OrderByDescending(createdAt),
            _ => sessions.OrderByDescending(updatedAt),
        };

    /// <summary>The Status filter's verdict on one session.</summary>
    public static bool PassesStatus(string status, bool isArchived) =>
        status switch
        {
            "archived" => isArchived,
            "all" => true,
            _ => !isArchived,
        };
}
