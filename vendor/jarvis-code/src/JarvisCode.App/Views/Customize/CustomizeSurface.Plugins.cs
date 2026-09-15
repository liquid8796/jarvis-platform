using System.IO;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.Core.Customization;
using Path = System.IO.Path;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// Customize › Plugins, at the shape of the reference's tabbed list
/// (c76f00e40): "Your plugins" over "Discover", the Show and Sort facets, the
/// Needs attention section, the Add plugin menu, and the detail page a row
/// opens with its Contents, its meta strip and its enable switch.
/// </summary>
public partial class CustomizeSurface
{
    private string _pluginQuery = "";
    private PluginSort _pluginSort = PluginSort.Updated;
    private PluginShow _pluginShow = PluginShow.All;
    private bool _pluginsBrowseTab;
    private StackPanel? _pluginListHost;

    public void ShowPlugins(string? focusPlugin = null)
    {
        if (_services is null)
            return;
        _pluginsBrowseTab = false;
        if (focusPlugin is { Length: > 0 } &&
            PluginRows().FirstOrDefault(r =>
                string.Equals(r.Name, focusPlugin, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            ShowPluginDetail(row);
            return;
        }
        RenderPlugins();
    }

    /// <summary>The Discover tab, which is the Directory scoped to plugins.</summary>
    public void ShowPluginDirectory()
    {
        if (_services is null)
            return;
        _pluginsBrowseTab = true;
        RenderPlugins();
    }

    private IReadOnlyList<PluginRow> PluginRows()
    {
        var now = DateTimeOffset.Now;
        var ui = Services.UiSettings.Current;
        var content = AllPlugins();
        var installed = new Dictionary<string, InstalledPluginRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, _) in PluginRoots.For(Services.Paths, Cwd))
        {
            foreach (var (name, record) in new InstalledPluginsIndex(root).All())
                installed[name] = record;
        }
        return [.. content.Installed.Select(plugin =>
            PluginListPresentation.ToRow(plugin, content.Skills, ui, installed, now))];
    }

    private void RenderPlugins()
    {
        var page = NewPage();
        page.Children.Add(CustomizeUi.PageHeader(
            "Plugins",
            "Search skills and plugins",
            query => { _pluginQuery = query; RenderPluginList(); },
            _pluginQuery,
            AddPluginButton()));

        page.Children.Add(CustomizeUi.Toolbar(
            CustomizeUi.Tabs("Plugins",
            [
                ("Your plugins", !_pluginsBrowseTab, () => { _pluginsBrowseTab = false; RenderPlugins(); }),
                ("Discover", _pluginsBrowseTab, () => { _pluginsBrowseTab = true; RenderPlugins(); }),
            ]),
            _pluginsBrowseTab ? null : CustomizeUi.Picker(
                "Show", PluginListPresentation.ShowOptions, _pluginShow,
                value => { _pluginShow = value; RenderPlugins(); }, _pluginShow != PluginShow.All),
            _pluginsBrowseTab ? null : CustomizeUi.Picker(
                "Sort by",
                PluginListPresentation.SortOptions(PluginRows().Any(r => r.OwnUses90d is not null)),
                _pluginSort, value => { _pluginSort = value; RenderPlugins(); }, _pluginSort != PluginSort.Updated),
            _pluginsBrowseTab || (_pluginShow == PluginShow.All && _pluginSort == PluginSort.Updated)
                ? null
                : CustomizeUi.ResetLink(() =>
                {
                    _pluginShow = PluginShow.All;
                    _pluginSort = PluginSort.Updated;
                    RenderPlugins();
                })));

        _pluginListHost = new StackPanel();
        page.Children.Add(_pluginListHost);
        RenderPluginList();
        Show(page);
    }

    private void RenderPluginList()
    {
        if (_pluginListHost is null)
            return;
        _pluginListHost.Children.Clear();

        if (_pluginsBrowseTab)
        {
            RenderDirectory(_pluginListHost, DirectorySection.Plugins, _pluginQuery);
            return;
        }

        var all = PluginRows();
        if (all.Count == 0)
        {
            _pluginListHost.Children.Add(CustomizeUi.EmptyState(
                "Add your first plugins",
                "Give Jarvis role-level expertise with plugins. Add them from Discover, or create your own.",
                "Discover",
                () => { _pluginsBrowseTab = true; RenderPlugins(); }));
            RenderProjectScopeNotice(_pluginListHost);
            return;
        }

        var matched = PluginListPresentation.Show(
            PluginListPresentation.Search(all, _pluginQuery), _pluginShow);
        var sort = PluginListPresentation.EffectiveSort(
            _pluginSort, matched.Any(r => r.OwnUses90d is not null));
        var sections = PluginListPresentation.Split(matched, row => !row.Enabled);

        if (matched.Count == 0)
        {
            _pluginListHost.Children.Add(CustomizeUi.Notice(
                _pluginQuery.Trim().Length > 0 ? "No plugins match your search" : "No plugins match your filters."));
            if (_pluginQuery.Trim().Length > 0)
            {
                _pluginListHost.Children.Add(CustomizeUi.Notice(
                    "Try a different term, or browse the Directory for plugins to add."));
            }
            return;
        }

        void AddRows(IReadOnlyList<PluginRow> rows)
        {
            foreach (var row in rows.OrderBy(r => r, Comparer<PluginRow>.Create((a, b) =>
                         PluginListPresentation.Compare(sort, a, b))))
            {
                _pluginListHost.Children.Add(PluginListRow(row));
            }
        }

        if (sections.Attention.Count > 0)
        {
            _pluginListHost.Children.Add(
                CustomizeUi.SectionHeader("Needs attention", sections.Attention.Count, attention: true));
            AddRows(sections.Attention);
        }
        AddRows(sections.Main);
        RenderProjectScopeNotice(_pluginListHost);
        RenderMarketplaces(_pluginListHost);
    }

    /// <summary>
    /// The reference asks for a folder before it will show project-scoped
    /// plugins; a Code session here always has one, so the line only appears
    /// when the session is not in a project.
    /// </summary>
    private void RenderProjectScopeNotice(Panel host)
    {
        if (_workingDirectory?.Invoke() is { Length: > 0 })
            return;
        host.Children.Add(CustomizeUi.Notice(
            "Select a folder to see project-scoped plugins and install plugins at the project level."));
    }

    private FrameworkElement PluginListRow(PluginRow row)
    {
        var chips = new List<FrameworkElement?>();
        if (!row.Enabled)
            chips.Add(CustomizeUi.Chip("Disabled"));
        if (row.Suffix is { Length: > 0 } suffix)
            chips.Add(CustomizeUi.Chip(suffix));

        var parts = new List<string>();
        if (row.Plugin.CommandCount > 0)
            parts.Add($"{row.Plugin.CommandCount} command{(row.Plugin.CommandCount == 1 ? "" : "s")}");
        if (row.Plugin.AgentCount > 0)
            parts.Add($"{row.Plugin.AgentCount} agent{(row.Plugin.AgentCount == 1 ? "" : "s")}");
        if (row.Plugin.SkillCount > 0)
            parts.Add(PluginListPresentation.SkillCountLabel(row.Plugin.SkillCount));
        if (row.Plugin.HasHooks)
            parts.Add("hooks");
        if (row.Plugin.HasMcp)
            parts.Add("connectors");

        var meta = new List<string>();
        if (row.Author is { Length: > 0 } author)
            meta.Add($"by {author}");
        if (parts.Count > 0)
            meta.Add(string.Join(" · ", parts));

        var body = CustomizeUi.Body(
            row.DisplayName, row.Description, meta.Count > 0 ? string.Join(" · ", meta) : null, [.. chips]);

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(CustomizeUi.Switch(
            row.Enabled, enabled => SetPluginEnabled(row, enabled), $"Enable plugin {row.DisplayName}"));
        controls.Children.Add(PluginKebab(row));
        return CustomizeUi.ListRow(body, controls, () => ShowPluginDetail(row), row.DisplayName);
    }

    private void SetPluginEnabled(PluginRow row, bool enabled)
    {
        PluginLibrary.SetEnabled(Services.UiSettings, row.Plugin, enabled);
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        SkillsWereChanged();
        RenderPluginList();
    }

    private FrameworkElement PluginKebab(PluginRow row) =>
        CustomizeUi.Kebab($"More options for {row.DisplayName}",
        [
            ("Check for updates", false, () => CheckPluginForUpdates(row)),
            ("Open folder", false, () => OpenInShell(row.Plugin.Directory)),
            ("Remove", true, () => RemovePlugin(row)),
        ]);

    private void CheckPluginForUpdates(PluginRow row)
    {
        var offered = MarketplaceOffer(row.Name);
        var (available, version) = MarketplaceSync.UpdateFor(row.Installed, offered);
        if (!available)
        {
            Warn("On latest version");
            return;
        }
        if (!Confirm("Update", $"Update to {version}", "Update", row.DisplayName) || offered is null)
            return;
        try
        {
            PluginLibrary.Install(
                offered.Directory, row.Plugin.Root, row.Name,
                new InstalledPluginRecord(row.Installed?.Marketplace, version, DateTimeOffset.Now, offered.Directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex.Message);
            return;
        }
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        SkillsWereChanged();
        RenderPlugins();
    }

    private MarketplacePlugin? MarketplaceOffer(string name)
    {
        foreach (var marketplace in PluginMarketplaces.List(Services.Paths.Root))
        {
            var offered = PluginMarketplaces.Plugins(marketplace)
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (offered is not null)
                return offered;
        }
        return null;
    }

    private void RemovePlugin(PluginRow row)
    {
        if (!Confirm("Remove plugin?", "This removes the plugin from this device.", "Remove", row.DisplayName))
            return;
        try
        {
            PluginLibrary.Uninstall(row.Plugin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex.Message);
            return;
        }
        Services.UiSettings.Current.DisabledPlugins.Remove(PluginLibrary.Key(row.Plugin));
        Services.UiSettings.Save();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        SkillsWereChanged();
        RenderPlugins();
    }

    // ---------- add plugin ----------

    private FrameworkElement AddPluginButton()
    {
        var button = CustomizeUi.Primary("Add plugin", () => { });
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var upload = new MenuItem { Header = "Upload plugin" };
        upload.Click += (_, _) => UploadPlugin();
        menu.Items.Add(upload);
        var folder = new MenuItem { Header = "Upload local plugin" };
        folder.Click += (_, _) => InstallPluginFromFolder();
        menu.Items.Add(folder);
        var marketplace = new MenuItem { Header = "Add marketplace" };
        marketplace.Click += (_, _) => AddMarketplacePrompt();
        menu.Items.Add(marketplace);
        var withJarvis = new MenuItem { Header = "Create with Jarvis" };
        withJarvis.Click += (_, _) => CodeSessionRequested?.Invoke(this, "Create a plugin with me.");
        menu.Items.Add(withJarvis);
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    /// <summary>The scope an install writes to; a session without a project has only the user root.</summary>
    private InstallScope? AskInstallScope(string what)
    {
        if (_workingDirectory?.Invoke() is not { Length: > 0 })
            return InstallScope.User;
        var choice = ScopeDialog.Ask(Window.GetWindow(this), what, Services.Paths.PluginsDirectory);
        return choice;
    }

    private void UploadPlugin()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Upload a plugin",
            Filter = "Plugin archives (*.zip;*.plugin)|*.zip;*.plugin",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var preview = PluginLibrary.Preview(dialog.FileName);
        if (preview is null)
        {
            Warn(PluginLibrary.ManifestRule);
            return;
        }
        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                "Upload a plugin",
                "Add a plugin to your workspace from a .zip or .plugin archive.",
                "Upload",
                preview.Name ?? Path.GetFileNameWithoutExtension(dialog.FileName),
                "Make sure you trust a plugin before installing or using it. Uploaded plugins are not controlled by Anthropic, and Anthropic cannot verify that they will work as intended. See each plugin’s homepage for more information."))
            return;

        if (AskInstallScope("plugin") is not { } scope)
            return;
        var root = InstallScopes.PluginsRoot(scope, Services.Paths, Cwd);
        var outcome = PluginLibrary.Upload(dialog.FileName, root, overwrite: false);
        if (outcome.Kind == PluginUploadOutcome.Conflict)
        {
            if (!Confirm("Replace plugin?", "A plugin with that name is already installed.", "Upload", outcome.PluginName))
                return;
            outcome = PluginLibrary.Upload(dialog.FileName, root, overwrite: true);
        }
        if (outcome.Kind != PluginUploadOutcome.Ok)
        {
            Warn(outcome.Message ?? "Plugin couldn’t be installed. Try again.");
            return;
        }
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        SkillsWereChanged();
        ShowPlugins(outcome.PluginName);
    }

    public void InstallPluginFromFolder()
    {
        if (_services is null)
            return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Upload local plugin" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var source = dialog.FolderName;
        var name = Path.GetFileName(source.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name))
            return;
        if (AskInstallScope("plugin") is not { } scope)
            return;

        var root = InstallScopes.PluginsRoot(scope, Services.Paths, Cwd);
        if (Directory.Exists(Path.Combine(root, name)) &&
            !Confirm("Replace plugin?", "A plugin with that name is already installed.", "Upload", name))
            return;
        try
        {
            PluginLibrary.Install(
                source, root, name, new InstalledPluginRecord(null, null, DateTimeOffset.Now, source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex.Message);
            return;
        }
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        SkillsWereChanged();
        ShowPlugins(name);
    }

    // ---------- marketplaces ----------

    private void AddMarketplacePrompt()
    {
        if (InputDialog.Prompt(Window.GetWindow(this), "Add marketplace", "", "Add") is not { } url)
            return;
        var appRoot = Services.Paths.Root;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var (_, error) = await Task.Run(() => PluginMarketplaces.Add(appRoot, url));
            if (error is not null)
            {
                Warn(MarketplaceSync.FailureReason(error));
                return;
            }
            RenderPlugins();
        });
    }

    private void RenderMarketplaces(Panel host)
    {
        var marketplaces = PluginMarketplaces.List(Services.Paths.Root);
        if (marketplaces.Count == 0)
            return;
        var installed = new HashSet<string>(
            AllPlugins().Installed.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var marketplace in marketplaces)
        {
            host.Children.Add(CustomizeUi.SectionHeader(marketplace.Name, 0));

            var meta = new List<string>();
            if (MarketplaceSync.LastUpdated(marketplace.Directory) is { } updated)
                meta.Add($"Last updated: {ViewModels.TranscriptTime.Relative(DateTimeOffset.Now - updated)}");
            if (MarketplaceSync.SyncedCommit(marketplace.Directory) is { } sha)
                meta.Add($"Synced commit: {sha}");

            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(CustomizeUi.Ghost(
                "Refresh marketplace", () => RefreshMarketplace(marketplace)));
            controls.Children.Add(CustomizeUi.Kebab($"Marketplace options",
            [
                ("Open folder", false, () => OpenInShell(marketplace.Directory)),
                ("Remove", true, () => RemoveMarketplace(marketplace, installed)),
            ]));

            host.Children.Add(CustomizeUi.ListRow(
                CustomizeUi.Body(
                    marketplace.Name, marketplace.Url, meta.Count > 0 ? string.Join(" · ", meta) : null),
                controls, null, marketplace.Name));

            var offered = PluginMarketplaces.Plugins(marketplace);
            if (offered.Count == 0)
            {
                host.Children.Add(CustomizeUi.Notice("When you add plugins to your marketplace, they will appear here."));
                continue;
            }
            foreach (var plugin in offered)
            {
                var already = installed.Contains(plugin.Name);
                var install = already
                    ? CustomizeUi.Ghost("Installed", () => { })
                    : CustomizeUi.Ghost("Install", () => InstallFromMarketplace(marketplace, plugin));
                install.IsEnabled = !already;
                host.Children.Add(CustomizeUi.ListRow(
                    CustomizeUi.Body(plugin.Name, plugin.Description, null), install, null, plugin.Name));
            }
        }
    }

    private void RefreshMarketplace(MarketplaceInfo marketplace)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var outcome = await Task.Run(() => MarketplaceSync.Refresh(marketplace));
            Warn(outcome.Message);
            RenderPlugins();
        });
    }

    private void RemoveMarketplace(MarketplaceInfo marketplace, IReadOnlyCollection<string> installed)
    {
        var fromHere = PluginMarketplaces.Plugins(marketplace)
            .Where(p => installed.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        var message = fromHere.Count > 0
            ? $"This will also uninstall {fromHere.Count} {(fromHere.Count == 1 ? "plugin" : "plugins")} " +
              "from this marketplace:"
            : "This will remove the marketplace from your list.";
        if (!Confirm("Remove marketplace?", message, "Remove",
                fromHere.Count > 0 ? string.Join("\n", fromHere) : marketplace.Name))
            return;

        foreach (var name in fromHere)
        {
            var plugin = AllPlugins().Installed
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (plugin is not null)
            {
                try
                {
                    PluginLibrary.Uninstall(plugin);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warn(ex.Message);
                }
            }
        }
        try
        {
            PluginMarketplaces.Remove(Services.Paths.Root, marketplace.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn("Failed to remove marketplace.");
            return;
        }
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        SkillsWereChanged();
        RenderPlugins();
    }

    private void InstallFromMarketplace(MarketplaceInfo marketplace, MarketplacePlugin plugin)
    {
        if (AskInstallScope("plugin") is not { } scope)
            return;
        var root = InstallScopes.PluginsRoot(scope, Services.Paths, Cwd);
        try
        {
            PluginLibrary.Install(
                plugin.Directory, root, plugin.Name,
                new InstalledPluginRecord(
                    marketplace.Name, MarketplaceSync.Version(plugin), DateTimeOffset.Now, plugin.Directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn("Plugin couldn’t be installed. Try again.");
            return;
        }
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        SkillsWereChanged();
        ShowPlugins(plugin.Name);
    }

    // ---------- detail ----------

    private void ShowPluginDetail(PluginRow row)
    {
        var page = NewPage();
        page.Children.Add(CustomizeUi.BackRow("Plugins", () => ShowPlugins()));

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(CustomizeUi.Text(row.DisplayName, 22, "Text100Brush", semibold: true));
        if (row.Description is { Length: > 0 } description)
        {
            var block = CustomizeUi.Text(description, 13, "Text400Brush", wrap: true);
            block.Margin = new Thickness(0, 6, 12, 0);
            title.Children.Add(block);
        }
        header.Children.Add(title);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(CustomizeUi.Ghost(
            row.Enabled ? "Disable plugin" : "Enable plugin", () =>
            {
                SetPluginEnabled(row, !row.Enabled);
                if (PluginRows().FirstOrDefault(r => r.Name == row.Name) is { } updated)
                    ShowPluginDetail(updated);
            }));
        var remove = CustomizeUi.Ghost("Remove", () => RemovePlugin(row));
        remove.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        page.Children.Add(header);

        var meta = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        meta.Children.Add(CustomizeUi.MetaCell("Source", row.Installed?.Marketplace is { Length: > 0 } market
            ? $"Marketplace ({market})"
            : "Uploaded from file"));
        if (row.Manifest?.Version is { Length: > 0 } version)
            meta.Children.Add(CustomizeUi.MetaCell("Version", version));
        if (InstallScopes.ScopeCell(row.Plugin.Scope) is { } scope)
            meta.Children.Add(CustomizeUi.MetaCell("Scope", scope));
        if (row.Author is { Length: > 0 } author)
            meta.Children.Add(CustomizeUi.MetaCell("Author", author));
        meta.Children.Add(CustomizeUi.MetaCell("Installation", row.Plugin.Directory));
        if (row.UpdatedAt is { } updatedAt)
            meta.Children.Add(CustomizeUi.MetaCell("Last updated", updatedAt.ToString("d")));
        page.Children.Add(meta);

        page.Children.Add(CustomizeUi.SectionHeader("Contents", 0));
        AddContents(page, "Skills", row.SkillNames);
        AddContents(page, "Agents", ContentNames(row.Plugin.Directory, "agents"));
        AddContents(page, "Commands", ContentNames(row.Plugin.Directory, "commands"));
        if (row.Plugin.HasHooks)
            AddContents(page, "Hooks", ["hooks.json"]);
        AddContents(page, "MCP servers", PluginServerNames(row.Plugin));
        AddMonitors(page, PluginMonitors.Load(row.Plugin.Directory));

        Show(page);
    }

    /// <summary>
    /// The Contents section's Monitors rows: each carries the reference's own two
    /// fields — "Runs" (the arm trigger) and the description — over the shell command
    /// it runs, or its "No command is declared for this monitor." line.
    /// </summary>
    private static void AddMonitors(Panel host, IReadOnlyList<PluginMonitor> monitors)
    {
        if (monitors.Count == 0)
            return;
        var header = CustomizeUi.Text(PluginMonitors.SectionHeading, 12.5, "Text400Brush", semibold: true);
        header.Margin = new Thickness(0, 14, 0, 4);
        host.Children.Add(header);
        foreach (var monitor in monitors)
        {
            var description = monitor.Description is { Length: > 0 } text
                ? $"{PluginMonitors.RunsLabel}: {PluginMonitors.RunsValue(monitor)} · {text}"
                : $"{PluginMonitors.RunsLabel}: {PluginMonitors.RunsValue(monitor)}";
            host.Children.Add(CustomizeUi.ListRow(
                CustomizeUi.Body(monitor.Name, description, null),
                null,
                null,
                monitor.Command is { Length: > 0 } command ? command : PluginMonitors.NoCommand));
        }
    }

    private static void AddContents(Panel host, string title, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return;
        var header = CustomizeUi.Text(title, 12.5, "Text400Brush", semibold: true);
        header.Margin = new Thickness(0, 14, 0, 4);
        host.Children.Add(header);
        foreach (var name in names)
        {
            host.Children.Add(CustomizeUi.ListRow(
                CustomizeUi.Body(name, null, null), null, null, name));
        }
    }

    private static IReadOnlyList<string> ContentNames(string pluginDirectory, string subdirectory)
    {
        try
        {
            var directory = Path.Combine(pluginDirectory, subdirectory);
            if (!Directory.Exists(directory))
                return [];
            return
            [
                .. Directory.GetFiles(directory, "*.md")
                    .Select(Path.GetFileNameWithoutExtension).OfType<string>()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> PluginServerNames(PluginInfo plugin)
    {
        if (!plugin.HasMcp)
            return [];
        var file = Path.Combine(plugin.Directory, ".mcp.json");
        return [.. JarvisCode.Core.Mcp.McpConfig.LoadSingleFile(file)
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }
}
