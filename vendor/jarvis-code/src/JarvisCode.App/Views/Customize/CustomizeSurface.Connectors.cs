using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.Core.Mcp;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// Customize › Connectors, at the shape of the reference's page (c40525e86):
/// Yours over Browse, a status filter, a Connector / Type / Status table, the
/// Connect · Disconnect · Reconnect actions, a Needs attention section, and the
/// two-step Add custom connector form.
/// </summary>
public partial class CustomizeSurface
{
    private string _connectorQuery = "";
    private ConnectorStatusFilter _connectorFilter = ConnectorStatusFilter.All;
    private bool _connectorsBrowseTab;
    private StackPanel? _connectorListHost;
    private readonly HashSet<string> _connecting = new(StringComparer.OrdinalIgnoreCase);

    public void ShowConnectors()
    {
        if (_services is null)
            return;
        _connectorsBrowseTab = false;
        RenderConnectors();
    }

    public bool ShowConnectorSuggestion(string connectorId)
    {
        var row = ConnectorRows().FirstOrDefault(row =>
            string.Equals(row.Name, connectorId, StringComparison.OrdinalIgnoreCase));
        if (row is null) return false;
        _connectorQuery = row.Name;
        _connectorFilter = ConnectorStatusFilter.All;
        _connectorsBrowseTab = false;
        RenderConnectors();
        return true;
    }

    /// <summary>The Browse tab, which is the Directory scoped to connectors.</summary>
    public void ShowConnectorDirectory()
    {
        if (_services is null)
            return;
        _connectorsBrowseTab = true;
        RenderConnectors();
    }

    private IReadOnlyList<ConnectorRow> ConnectorRows()
    {
        var paths = Services.Paths;
        var ui = Services.UiSettings.Current;
        var plugins = PluginLibrary.LoadActive(paths, Cwd, ui);
        var all = McpConfig.Load(Cwd, paths.UserMcpFile,
            JarvisCode.App.Services.DesktopExtensions.ConfigFiles(paths, plugins.McpFiles));
        var userServers = new HashSet<string>(
            McpConfig.LoadSingleFile(paths.UserMcpFile).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        // Which plugin, if any, contributes each server: its .mcp.json is the file
        // the server was read from, and the plugin's folder is that file's parent.
        var byPlugin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins.Installed)
        {
            if (!plugin.HasMcp)
                continue;
            var file = System.IO.Path.Combine(plugin.Directory, ".mcp.json");
            foreach (var server in McpConfig.LoadSingleFile(file))
                byPlugin[server.Name] = plugin.Name;
        }

        var counts = Services.Mcp.ConnectedToolCounts;
        var failures = Services.Mcp.LastFailures;
        return [.. all.Select(config => ConnectorListPresentation.ToRow(
            config, counts, failures,
            userServers.Contains(config.Name),
            byPlugin.TryGetValue(config.Name, out var plugin) ? plugin : null,
            _connecting.Contains(config.Name)))];
    }

    private void RenderConnectors()
    {
        var page = NewPage();
        page.Children.Add(CustomizeUi.PageHeader(
            "Connectors",
            "Search connectors",
            query => { _connectorQuery = query; RenderConnectorList(); },
            _connectorQuery,
            CustomizeUi.Primary("Add custom connector", AddCustomConnector)));

        page.Children.Add(CustomizeUi.Toolbar(
            CustomizeUi.Tabs("Connectors",
            [
                ("Yours", !_connectorsBrowseTab, () => { _connectorsBrowseTab = false; RenderConnectors(); }),
                ("Browse", _connectorsBrowseTab, () => { _connectorsBrowseTab = true; RenderConnectors(); }),
            ]),
            _connectorsBrowseTab ? null : CustomizeUi.Picker(
                "Filter by status",
                new[]
                {
                    (ConnectorStatusFilter.All, "All"),
                    (ConnectorStatusFilter.Connected, "Connected"),
                    (ConnectorStatusFilter.NotConnected, "Not connected"),
                },
                _connectorFilter,
                value => { _connectorFilter = value; RenderConnectors(); },
                _connectorFilter != ConnectorStatusFilter.All),
            CustomizeUi.Ghost("Reconnect all", () => { RefreshMcpNow(); RenderConnectors(); })));

        _connectorListHost = new StackPanel();
        page.Children.Add(_connectorListHost);
        RenderConnectorList();
        Show(page);
    }

    private void RenderConnectorList()
    {
        if (_connectorListHost is null)
            return;
        _connectorListHost.Children.Clear();

        if (_connectorsBrowseTab)
        {
            RenderDirectory(_connectorListHost, DirectorySection.Connectors, _connectorQuery);
            return;
        }

        _connectorListHost.Children.Add(JarvisBrowserRow());

        var all = ConnectorRows();
        if (all.Count == 0)
        {
            _connectorListHost.Children.Add(CustomizeUi.EmptyState(
                "Add your first connectors",
                "Connectors let Jarvis work with your team’s tools and data.",
                "Add custom connector",
                AddCustomConnector));
            return;
        }

        var matched = ConnectorListPresentation.Filter(
            ConnectorListPresentation.Search(all, _connectorQuery), _connectorFilter);
        if (matched.Count == 0)
        {
            _connectorListHost.Children.Add(CustomizeUi.Notice(
                ConnectorListPresentation.EmptyLabel(_connectorFilter, _connectorQuery.Trim().Length > 0)));
            return;
        }

        var (attention, main) = ConnectorListPresentation.Split(matched);
        if (attention.Count > 0)
        {
            _connectorListHost.Children.Add(
                CustomizeUi.SectionHeader("Needs attention", attention.Count, attention: true));
            foreach (var row in attention)
                _connectorListHost.Children.Add(ConnectorListRow(row));
        }
        foreach (var row in main)
            _connectorListHost.Children.Add(ConnectorListRow(row));

        _connectorListHost.Children.Add(CustomizeUi.Notice(
            $"User connectors live in {Services.Paths.UserMcpFile}; project connectors in " +
            $"{InstallScopes.ProjectDirectoryName}/mcp.json."));
    }

    private FrameworkElement ConnectorListRow(ConnectorRow row)
    {
        var connected = row.Status == ConnectorStatus.Connected;
        var meta = new List<string>
        {
            ConnectorListPresentation.KindLabel(row.Kind),
            connected
                ? ConnectorListPresentation.ToolsLabel(row.ToolCount)
                : ConnectorListPresentation.StatusLabel(row.Status),
        };
        if (ConnectorListPresentation.ProvidedBy(row) is { } provided)
            meta.Add(provided);

        var body = new StackPanel();
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(CustomizeUi.Dot(connected ? "Success100Brush"
            : row.NeedsAttention ? "Warning100Brush" : "Text500Brush"));
        nameRow.Children.Add(CustomizeUi.Text(row.Name, 14, "Text100Brush", semibold: true));
        body.Children.Add(nameRow);

        var endpoint = CustomizeUi.Text(row.Endpoint, 11.5, "Text400Brush");
        endpoint.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");
        endpoint.Margin = new Thickness(18, 3, 0, 0);
        endpoint.MaxWidth = 460;
        body.Children.Add(endpoint);

        var metaBlock = CustomizeUi.Text(string.Join(" · ", meta), 11.5, "Text500Brush");
        metaBlock.Margin = new Thickness(18, 4, 0, 0);
        body.Children.Add(metaBlock);

        if (row.FailureMessage is { Length: > 0 } failure)
        {
            var block = CustomizeUi.Text(failure, 11.5, "Danger100Brush", wrap: true);
            block.Margin = new Thickness(18, 4, 12, 0);
            body.Children.Add(block);
        }

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(CustomizeUi.Ghost(
            ConnectorListPresentation.ActionLabel(row.Status), () => ConnectorAction(row), $"Connect {row.Name}"));
        if (row.IsRemovable)
        {
            controls.Children.Add(CustomizeUi.Kebab($"More options for {row.Name}",
            [
                ("Reconnect", false, () => { RefreshMcpNow(); RenderConnectors(); }),
                ("Remove", true, () => RemoveConnector(row)),
            ]));
        }
        return CustomizeUi.ListRow(body, controls, null, row.Name);
    }

    /// <summary>Connect signs in when the server wants it, otherwise reconnects.</summary>
    private void ConnectorAction(ConnectorRow row)
    {
        if (row.Status == ConnectorStatus.Connected)
        {
            DisconnectConnector(row);
            return;
        }
        if (row.Config.IsRemote &&
            (row.Status == ConnectorStatus.NeedsAuthentication || row.Status == ConnectorStatus.NotConnected))
        {
            SignIn(row);
            return;
        }
        RefreshMcpNow();
        RenderConnectors();
    }

    /// <summary>
    /// Disconnecting a remote connector drops the stored grant, which is what
    /// makes the next Connect ask the authorization server again; a local one has
    /// nothing to drop and simply stops at the next refresh.
    /// </summary>
    private void DisconnectConnector(ConnectorRow row)
    {
        if (row.Config.IsRemote)
            Services.McpTokens.Remove(row.Config);
        RefreshMcpNow();
        RenderConnectors();
    }

    private void SignIn(ConnectorRow row)
    {
        var dialog = new ConnectorSignInDialog(row.Config, Services.Http, Services.McpTokens)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true)
            RefreshMcpNow();
        RenderConnectors();
    }

    private void RemoveConnector(ConnectorRow row)
    {
        if (!Confirm("Remove connector?", "This removes the connector from this device.", "Remove", row.Name))
            return;
        var remaining = McpConfig.LoadSingleFile(Services.Paths.UserMcpFile)
            .Where(s => !string.Equals(s.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        McpConfig.SaveUserServers(Services.Paths.UserMcpFile, remaining);
        if (row.Config.IsRemote)
            Services.McpTokens.Remove(row.Config);
        RefreshMcpNow();
        RenderConnectors();
    }

    private void AddCustomConnector()
    {
        var existing = McpConfig.Load(Cwd, Services.Paths.UserMcpFile).Select(s => s.Name).ToList();
        var dialog = new CustomConnectorDialog(existing) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } config)
            return;

        var servers = McpConfig.LoadSingleFile(Services.Paths.UserMcpFile)
            .Where(s => !string.Equals(s.Name, config.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        servers.Add(config);
        McpConfig.SaveUserServers(Services.Paths.UserMcpFile, servers);
        RefreshMcpNow();
        RenderConnectors();
    }

    /// <summary>The browser extension, which is a connector the user installs rather than configures.</summary>
    private FrameworkElement JarvisBrowserRow()
    {
        var bridge = Services.Browser;
        var connected = bridge.IsConnected;

        var body = new StackPanel();
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(CustomizeUi.Dot(connected ? "Success100Brush" : "Text500Brush"));
        nameRow.Children.Add(CustomizeUi.Text("Jarvis Browser", 14, "Text100Brush", semibold: true));
        nameRow.Children.Add(CustomizeUi.Chip("extension"));
        body.Children.Add(nameRow);

        var status = CustomizeUi.Text(
            connected
                ? "Connected — the agent can list tabs, navigate, read pages, click, and type."
                : "Not connected. Install once: open your browser's extensions page, turn on Developer mode, " +
                  "choose \"Load unpacked\" and select the folder below, then restart the browser.",
            12, "Text400Brush", wrap: true);
        status.Margin = new Thickness(18, 3, 12, 0);
        body.Children.Add(status);

        var path = CustomizeUi.Text(JarvisBrowserSetup.ExtensionDirectory(Services.Paths), 11, "Text500Brush");
        path.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");
        path.Margin = new Thickness(18, 4, 0, 0);
        path.MaxWidth = 460;
        body.Children.Add(path);

        var open = CustomizeUi.Ghost("Open folder", () =>
        {
            JarvisBrowserSetup.EnsureInstalled(Services.Paths);
            OpenInShell(JarvisBrowserSetup.ExtensionDirectory(Services.Paths));
        });
        return CustomizeUi.ListRow(body, open, null, "Jarvis Browser");
    }
}
