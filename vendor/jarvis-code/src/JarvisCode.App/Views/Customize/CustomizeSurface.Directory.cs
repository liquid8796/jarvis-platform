using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// The Directory (c99ed1520): the Browse tab each page opens on, its Category /
/// Status / Source facets and Sort by, and the search across all three sections
/// with the reference's five-per-section preview and "See all {n}". What it
/// offers is local — the packs this build ships, the plugins the configured git
/// marketplaces carry, the connectors already configured — because there is no
/// claude.ai catalog behind this app.
/// </summary>
public partial class CustomizeSurface
{
    private DirectorySort _directorySort = DirectorySort.MostPopular;
    private DirectoryStatus _directoryStatus = DirectoryStatus.All;
    private string? _directorySource;
    private string? _directoryCategory;

    /// <summary>Everything the Directory can offer, across the three sections.</summary>
    private IReadOnlyList<DirectoryItem> DirectoryItems()
    {
        var skills = SkillCatalog.LoadAll(Cwd, Services.Paths);
        var installedSkills = skills.Select(s => s.Name).ToList();
        var installedPlugins = AllPlugins().Installed.Select(p => p.Name).ToList();
        return
        [
            .. DirectorySources.Skills(Services.Paths, Cwd, installedSkills),
            .. DirectorySources.Connectors(ConnectorRows()),
            .. DirectorySources.PluginOffers(Services.Paths, installedPlugins),
        ];
    }

    /// <summary>
    /// The Browse tab a page shows in place of its own list: this section's
    /// offers when nothing is typed, and the reference's cross-section search
    /// results when something is.
    /// </summary>
    private void RenderDirectory(Panel host, DirectorySection section, string query)
    {
        var all = DirectoryItems();

        if (query.Trim().Length > 0)
        {
            RenderDirectorySearch(host, all, query);
            return;
        }

        var items = all.Where(i => i.Section == section).ToList();
        host.Children.Add(DirectoryFacets(items, section));

        var filtered = CustomizeDirectory.Sort(
            CustomizeDirectory.ByCategory(
                CustomizeDirectory.BySource(
                    CustomizeDirectory.ByStatus(items, _directoryStatus), _directorySource),
                _directoryCategory),
            _directorySort);

        if (filtered.Count == 0)
        {
            host.Children.Add(CustomizeUi.Notice(
                items.Count == 0 ? "Nothing to show here yet." : "No results match your search."));
            return;
        }
        foreach (var item in filtered)
            host.Children.Add(DirectoryRow(item));
    }

    private FrameworkElement DirectoryFacets(IReadOnlyList<DirectoryItem> items, DirectorySection section)
    {
        var sources = new List<(string? Value, string Label)> { (null, "All sources") };
        sources.AddRange(CustomizeDirectory.Sources(items).Select(s => ((string?)s, s)));

        var categories = new List<(string? Value, string Label)> { (null, "All") };
        categories.AddRange(CustomizeDirectory.Categories(items).Select(c => ((string?)c, c)));

        return CustomizeUi.Toolbar(
            null,
            categories.Count > 1
                ? CustomizeUi.Picker("Category", categories, _directoryCategory,
                    value => { _directoryCategory = value; RenderDirectoryTab(section); }, _directoryCategory is not null)
                : null,
            CustomizeUi.Picker(
                "Status",
                new[]
                {
                    (DirectoryStatus.All, "All"),
                    (DirectoryStatus.Installed, "Installed"),
                    (DirectoryStatus.NotInstalled, "Not installed"),
                },
                _directoryStatus,
                value => { _directoryStatus = value; RenderDirectoryTab(section); },
                _directoryStatus != DirectoryStatus.All),
            sources.Count > 1
                ? CustomizeUi.Picker("Source", sources, _directorySource,
                    value => { _directorySource = value; RenderDirectoryTab(section); }, _directorySource is not null)
                : null,
            CustomizeUi.Picker(
                "Sort by",
                new[]
                {
                    (DirectorySort.MostPopular, "Most popular"),
                    (DirectorySort.NameAsc, "Name A-Z"),
                    (DirectorySort.RecentlyUpdated, "Recently updated"),
                },
                _directorySort,
                value => { _directorySort = value; RenderDirectoryTab(section); },
                _directorySort != DirectorySort.MostPopular));
    }

    /// <summary>Re-renders whichever page's Browse tab is on screen.</summary>
    private void RenderDirectoryTab(DirectorySection section)
    {
        switch (section)
        {
            case DirectorySection.Skills:
                RenderSkillList();
                break;
            case DirectorySection.Connectors:
                RenderConnectorList();
                break;
            default:
                RenderPluginList();
                break;
        }
    }

    private void RenderDirectorySearch(Panel host, IReadOnlyList<DirectoryItem> all, string query)
    {
        var sections = CustomizeDirectory.SearchAll(all, query);
        var total = sections.Sum(s => s.Total);
        if (total == 0)
        {
            host.Children.Add(CustomizeUi.Notice("No results match your search."));
            return;
        }
        host.Children.Add(CustomizeUi.Notice(CustomizeDirectory.ResultCount(total)));

        foreach (var section in sections)
        {
            if (section.Total == 0)
                continue;
            var header = new Grid { Margin = new Thickness(0, CustomizeUi.SectionGap, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(CustomizeUi.Text(
                CustomizeDirectory.SectionLabel(section.Section), 12.5, "Text400Brush", semibold: true));
            if (section.Total > CustomizeDirectory.SectionPreview)
            {
                var seeAll = CustomizeUi.Ghost(
                    $"See all {section.Total:N0}",
                    () => OpenDirectorySection(section.Section),
                    CustomizeDirectory.SeeAllLabel(section.Section, section.Total));
                Grid.SetColumn(seeAll, 1);
                header.Children.Add(seeAll);
            }
            host.Children.Add(header);
            foreach (var item in section.Entries)
                host.Children.Add(DirectoryRow(item));
        }
    }

    /// <summary>"See all" moves to that section's Browse tab with the search cleared.</summary>
    private void OpenDirectorySection(DirectorySection section)
    {
        switch (section)
        {
            case DirectorySection.Skills:
                _skillQuery = "";
                ShowSkillDirectory();
                break;
            case DirectorySection.Connectors:
                _connectorQuery = "";
                ShowConnectorDirectory();
                break;
            default:
                _pluginQuery = "";
                ShowPluginDirectory();
                break;
        }
    }

    private FrameworkElement DirectoryRow(DirectoryItem item)
    {
        var meta = item.MatchedSkill is { Length: > 0 } skill
            ? $"Contains skill: {skill}"
            : item.MatchedOn is { Length: > 0 } matched
                ? $"Matched on “{Trim(matched)}”"
                : item.Author is { Length: > 0 } author ? $"by {author}" : item.Source;

        var chips = new List<FrameworkElement?>();
        if (item.Installed)
            chips.Add(CustomizeUi.Chip("Installed"));

        var body = CustomizeUi.Body(item.Title, item.Description, meta, [.. chips]);
        var controls = item.Installed || item.Section == DirectorySection.Connectors
            ? null
            : CustomizeUi.Ghost("Install", () => InstallFromDirectory(item), $"Install {item.Title}");
        return CustomizeUi.ListRow(body, controls, null, item.Title);
    }

    private static string Trim(string text) => text.Length <= 80 ? text : text[..77] + "…";

    private void InstallFromDirectory(DirectoryItem item)
    {
        if (item.Section == DirectorySection.Plugins)
        {
            var marketplace = PluginMarketplaces.List(Services.Paths.Root)
                .FirstOrDefault(m => string.Equals(m.Name, item.Source, StringComparison.OrdinalIgnoreCase));
            var offer = marketplace is null ? null : PluginMarketplaces.Plugins(marketplace)
                .FirstOrDefault(p => string.Equals(p.Name, item.Id, StringComparison.OrdinalIgnoreCase));
            if (marketplace is null || offer is null)
            {
                Warn("Plugin couldn’t be installed. Try again.");
                return;
            }
            InstallFromMarketplace(marketplace, offer);
            return;
        }

        if (item.Section == DirectorySection.Skills && item.Directory is { Length: > 0 } directory)
        {
            InstallSkillFromDirectory(item, directory);
        }
    }

    /// <summary>
    /// A skill installed from the Directory is copied into the scope the user
    /// picks, under its bare name — the pack prefix is how the shipped copy is
    /// addressed, not part of the folder the copy takes.
    /// </summary>
    private void InstallSkillFromDirectory(DirectoryItem item, string sourceDirectory)
    {
        if (AskInstallScope("skill") is not { } scope)
            return;
        var root = InstallScopes.SkillsRoot(scope, Services.Paths, Cwd);
        var name = item.Id.Contains(':') ? item.Id[(item.Id.LastIndexOf(':') + 1)..] : item.Id;
        if (SkillLibrary.Exists(root, name) &&
            !Confirm($"Replace {name}", "Upload a file to replace this skill’s contents.", "Save", name))
            return;
        try
        {
            System.IO.Directory.CreateDirectory(root);
            PluginLibrary.CopyDirectory(sourceDirectory, System.IO.Path.Combine(root, name));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Warn(ex.Message);
            return;
        }
        SkillsWereChanged();
        ShowSkills();
    }
}
