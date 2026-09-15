using System.IO;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

/// <summary>The three sections the Directory offers, in the reference's order.</summary>
public enum DirectorySection
{
    Skills,
    Connectors,
    Plugins,
}

/// <summary>The reference's Sort-by values.</summary>
public enum DirectorySort
{
    MostPopular,
    NameAsc,
    RecentlyUpdated,
}

/// <summary>The reference's Status facet.</summary>
public enum DirectoryStatus
{
    All,
    Installed,
    NotInstalled,
}

/// <summary>One offer the Directory lists.</summary>
public sealed record DirectoryItem(
    DirectorySection Section,
    string Id,
    string Title,
    string? Description,
    string? Author,
    string Source,
    string? Category,
    bool Installed,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> SkillNames,
    string? Directory = null,
    string? Url = null)
{
    /// <summary>The reference's metadata line: which skill of this item the query hit.</summary>
    public string? MatchedSkill { get; init; }

    /// <summary>The reference's other metadata line: the field the query hit.</summary>
    public string? MatchedOn { get; init; }
}

/// <summary>One section of a directory search, with the total behind its preview.</summary>
public sealed record DirectorySearchSection(
    DirectorySection Section, IReadOnlyList<DirectoryItem> Entries, int Total);

/// <summary>
/// The Directory (c99ed1520): the Browse tab of Skills, Connectors and Plugins,
/// its facets and sort, and the unified search across all three. The reference
/// serves it from the claude.ai catalog; the offers here are local — the packs
/// this build ships, the plugins the configured git marketplaces carry, and the
/// public MCP registry — which is why <see cref="DirectorySources"/> assembles
/// them and this class only decides what is shown.
/// </summary>
public static class CustomizeDirectory
{
    /// <summary>The reference shows five per section and offers "See all" past that.</summary>
    public const int SectionPreview = 5;

    public static string SectionLabel(DirectorySection section) => section switch
    {
        DirectorySection.Connectors => "Connectors",
        DirectorySection.Plugins => "Plugins",
        _ => "Skills",
    };

    /// <summary>The reference's per-section "See all {count, number} {things}".</summary>
    public static string SeeAllLabel(DirectorySection section, int total) => section switch
    {
        DirectorySection.Connectors => $"See all {total:N0} connectors",
        DirectorySection.Plugins => $"See all {total:N0} plugins",
        _ => $"See all {total:N0} skills",
    };

    /// <summary>The reference's placeholder per section, and the unified one.</summary>
    public static string SearchPlaceholder(DirectorySection? section) => section switch
    {
        DirectorySection.Skills => "Search skills…",
        DirectorySection.Connectors => "Search connectors…",
        DirectorySection.Plugins => "Search plugins…",
        _ => "Search skills, connectors, and plugins…",
    };

    public static string SortLabel(DirectorySort sort) => sort switch
    {
        DirectorySort.NameAsc => "Name A-Z",
        DirectorySort.RecentlyUpdated => "Recently updated",
        _ => "Most popular",
    };

    public static string StatusLabel(DirectoryStatus status) => status switch
    {
        DirectoryStatus.Installed => "Installed",
        DirectoryStatus.NotInstalled => "Not installed",
        _ => "All",
    };

    /// <summary>The reference's Status facet.</summary>
    public static IReadOnlyList<DirectoryItem> ByStatus(
        IEnumerable<DirectoryItem> items, DirectoryStatus status) => status switch
    {
        DirectoryStatus.Installed => [.. items.Where(i => i.Installed)],
        DirectoryStatus.NotInstalled => [.. items.Where(i => !i.Installed)],
        _ => [.. items],
    };

    /// <summary>The Source facet: "All sources", or one named source.</summary>
    public static IReadOnlyList<DirectoryItem> BySource(IEnumerable<DirectoryItem> items, string? source) =>
        source is null ? [.. items]
        : [.. items.Where(i => string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The Category facet.</summary>
    public static IReadOnlyList<DirectoryItem> ByCategory(IEnumerable<DirectoryItem> items, string? category) =>
        category is null ? [.. items]
        : [.. items.Where(i => string.Equals(i.Category, category, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// The search, which is also what fills the metadata line: a hit on one of
    /// the item's skills says so, a hit on a field other than the title says
    /// which, and a title hit says nothing.
    /// </summary>
    public static IReadOnlyList<DirectoryItem> Search(IEnumerable<DirectoryItem> items, string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0)
            return [.. items];

        var found = new List<DirectoryItem>();
        foreach (var item in items)
        {
            if (item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(item with { MatchedSkill = null, MatchedOn = null });
                continue;
            }
            var skill = item.SkillNames.FirstOrDefault(s => s.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (skill is not null)
            {
                found.Add(item with { MatchedSkill = skill, MatchedOn = null });
                continue;
            }
            if (item.Description is { Length: > 0 } description &&
                description.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(item with { MatchedSkill = null, MatchedOn = description });
                continue;
            }
            if (item.Author is { Length: > 0 } author &&
                author.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(item with { MatchedSkill = null, MatchedOn = author });
            }
        }
        return found;
    }

    /// <summary>
    /// The sort. "Most popular" has no popularity number to read here, so it
    /// falls back to the shape a catalog with no counts has: installed offers
    /// first, then by name — which is the order the reference's own list takes
    /// when every count is equal.
    /// </summary>
    public static IReadOnlyList<DirectoryItem> Sort(IEnumerable<DirectoryItem> items, DirectorySort sort) => sort switch
    {
        DirectorySort.NameAsc =>
            [.. items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)],
        DirectorySort.RecentlyUpdated =>
            [.. items
                .OrderByDescending(i => i.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)],
        _ =>
            [.. items
                .OrderByDescending(i => i.Installed)
                .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)],
    };

    /// <summary>The unified search: every section, five entries each, with its total.</summary>
    public static IReadOnlyList<DirectorySearchSection> SearchAll(IEnumerable<DirectoryItem> items, string query)
    {
        var matched = Search(items, query);
        var sections = new List<DirectorySearchSection>();
        foreach (var section in new[]
                 { DirectorySection.Skills, DirectorySection.Connectors, DirectorySection.Plugins })
        {
            var ofSection = Sort(matched.Where(i => i.Section == section), DirectorySort.MostPopular);
            sections.Add(new DirectorySearchSection(
                section, [.. ofSection.Take(SectionPreview)], ofSection.Count));
        }
        return sections;
    }

    /// <summary>"{n} result" / "{n} results" — the count above the sections.</summary>
    public static string ResultCount(int count) => count == 1 ? "1 result" : $"{count:N0} results";

    /// <summary>The sources a set of offers came from, for the Source facet.</summary>
    public static IReadOnlyList<string> Sources(IEnumerable<DirectoryItem> items) =>
        [.. items.Select(i => i.Source).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The categories a set of offers declares, for the Category facet.</summary>
    public static IReadOnlyList<string> Categories(IEnumerable<DirectoryItem> items) =>
        [.. items.Select(i => i.Category).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)];
}

/// <summary>
/// Where the Directory's offers come from in this build: the skill packs it
/// ships, the plugins the configured git marketplaces carry, and the connectors
/// already configured. The reference reads its claude.ai catalog instead, which
/// is declared as a deliberate difference rather than faked.
/// </summary>
public static class DirectorySources
{
    /// <summary>The skills this build ships and the ones marketplace plugins carry.</summary>
    public static IReadOnlyList<DirectoryItem> Skills(
        ProfilePaths paths, string cwd, IReadOnlyCollection<string> installedSkillNames)
    {
        var items = new List<DirectoryItem>();
        foreach (var skill in SkillPacks.All)
        {
            var pack = skill.Name.Contains(':') ? skill.Name[..skill.Name.IndexOf(':')] : "built-in";
            items.Add(new DirectoryItem(
                DirectorySection.Skills, skill.Name, skill.Name, skill.Description, skill.Author,
                pack, null, Installed: true, UpdatedAt: null, SkillNames: [skill.Name],
                Directory: skill.Directory));
        }
        foreach (var marketplace in PluginMarketplaces.List(paths.Root))
        {
            foreach (var plugin in PluginMarketplaces.Plugins(marketplace))
            {
                foreach (var skill in SkillNamesIn(plugin))
                {
                    var qualified = $"{plugin.Name}:{skill}";
                    items.Add(new DirectoryItem(
                        DirectorySection.Skills, qualified, qualified, plugin.Description, null,
                        marketplace.Name, null,
                        installedSkillNames.Contains(qualified, StringComparer.OrdinalIgnoreCase),
                        MarketplaceSync.LastUpdated(marketplace.Directory), [skill],
                        Directory: plugin.Directory));
                }
            }
        }
        return [.. items.DistinctBy(i => i.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The plugins the configured marketplaces offer.</summary>
    public static IReadOnlyList<DirectoryItem> PluginOffers(
        ProfilePaths paths, IReadOnlyCollection<string> installedPluginNames)
    {
        var items = new List<DirectoryItem>();
        foreach (var marketplace in PluginMarketplaces.List(paths.Root))
        {
            var updated = MarketplaceSync.LastUpdated(marketplace.Directory);
            foreach (var plugin in PluginMarketplaces.Plugins(marketplace))
            {
                var manifest = PluginManifests.Read(plugin.Directory);
                items.Add(new DirectoryItem(
                    DirectorySection.Plugins, plugin.Name, manifest?.Name ?? plugin.Name,
                    plugin.Description ?? manifest?.Description, manifest?.Author,
                    marketplace.Name, manifest?.Category,
                    installedPluginNames.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase),
                    updated, SkillNamesIn(plugin), Directory: plugin.Directory));
            }
        }
        return [.. items.DistinctBy(i => i.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The skill names a marketplace plugin would contribute if it were installed.</summary>
    public static IReadOnlyList<string> SkillNamesIn(MarketplacePlugin plugin)
    {
        try
        {
            var directory = Path.Combine(plugin.Directory, "skills");
            if (!System.IO.Directory.Exists(directory))
                return [];
            return
            [
                .. System.IO.Directory.GetDirectories(directory).Select(Path.GetFileName).OfType<string>(),
                .. System.IO.Directory.GetFiles(directory, "*.md")
                    .Select(Path.GetFileNameWithoutExtension).OfType<string>(),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The connectors already configured, which is the only local catalog of them.</summary>
    public static IReadOnlyList<DirectoryItem> Connectors(IEnumerable<ConnectorRow> rows) =>
        [.. rows.Select(row => new DirectoryItem(
            DirectorySection.Connectors, row.Name, row.Name, row.Endpoint, null,
            ConnectorListPresentation.KindLabel(row.Kind), null,
            row.Status == ConnectorStatus.Connected, null, [], Url: row.Config.Url))];
}
