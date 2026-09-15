using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.Core.Mcp;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// Settings › Desktop app › Developer — the reference's Developer page
/// (ion-dist <c>c71860c77-Cjeeh0xx.js</c>, its <c>Z</c>): the "Local MCP servers"
/// section (<c>7+U8x5o7v9</c>) with its description (<c>h2kOgf50w3</c>), the
/// two-pane table — a list of server names on the left, and on the right the
/// server's status chip, "Command" (<c>urCd4k/cE0</c>), "Arguments"
/// (<c>pgaCSv2/6H</c>), "Error" (<c>zCIK9K8J4a</c>), a "View logs" button
/// (<c>zvT1bjOqfj</c>), a delete button asking the reference's own question
/// (<c>E4wAMW5Ily</c>) and, behind "Advanced options" (<c>CZwl8X2D85</c>),
/// "Environment variables" (<c>4qP7MjrQfC</c>) — the "Edit config" action
/// (<c>Vvus2ifAny</c>), the "No servers added" empty state (<c>TrS+kwadjI</c>) and
/// the "Developer docs" link (<c>J1rj3Exw6V</c>). A server an extension installed
/// shows the reference's note (<c>HC0OdPtRvg</c>) and cannot be deleted here.
///
/// "Use Built-in Node.js for MCP" (<c>TlT44L+Q3V</c>) is not offered: it switches
/// the reference between the Node runtime it bundles and whatever is on PATH, and
/// this build bundles no runtime for it to choose.
/// </summary>
internal sealed class DeveloperPage(AppServices services)
{
    public const string PageTitle = "Developer";

    private readonly List<McpServerConfig> _servers = [];
    private readonly HashSet<string> _fromExtensions = new(StringComparer.OrdinalIgnoreCase);
    private string? _selected;
    private StackPanel? _tableHost;

    public FrameworkElement Build()
    {
        var page = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var section = SettingsRows.Section(
            "Local MCP servers",
            "Add and manage MCP servers that you’re working on. ");
        _tableHost = new StackPanel();
        section.Children.Add(_tableHost);
        page.Children.Add(section);

        Reload();
        page.Children.Add(BuildAppInfo());
        return page;
    }

    private void Reload()
    {
        _servers.Clear();
        _fromExtensions.Clear();

        var userFile = services.Paths.UserMcpFile;
        _servers.AddRange(McpConfig.LoadSingleFile(userFile));

        var extensionsFile = DesktopExtensions.McpFile(services.Paths);
        foreach (var server in McpConfig.LoadSingleFile(extensionsFile))
        {
            _fromExtensions.Add(server.Name);
            if (!_servers.Any(s => s.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _servers.Add(server);
            }
        }

        _selected ??= _servers.FirstOrDefault()?.Name;
        if (_selected is not null && !_servers.Any(s => s.Name.Equals(_selected, StringComparison.OrdinalIgnoreCase)))
        {
            _selected = _servers.FirstOrDefault()?.Name;
        }

        Render();
    }

    private void Render()
    {
        if (_tableHost is null)
        {
            return;
        }

        _tableHost.Children.Clear();
        if (_servers.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            var caption = SettingsRows.Muted("No servers added");
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(caption);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
            };
            buttons.Children.Add(SettingsRows.SecondaryButton("Edit config", EditConfig));
            var docs = SettingsRows.SecondaryButton("Developer docs",
                () => SettingsProse.Open("https://modelcontextprotocol.io/quickstart"));
            docs.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(docs);
            empty.Children.Add(buttons);
            _tableHost.Children.Add(empty);
            return;
        }

        var edit = SettingsRows.SecondaryButton("Edit config", EditConfig);
        edit.HorizontalAlignment = HorizontalAlignment.Left;
        edit.Margin = new Thickness(0, 8, 0, 12);
        _tableHost.Children.Add(edit);

        var frame = new Grid { MinHeight = 240 };
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(192) });
        frame.ColumnDefinitions.Add(new ColumnDefinition());

        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var server in _servers)
        {
            var name = server.Name;
            var button = new Button
            {
                Content = name,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(6, 2, 6, 2),
                Padding = new Thickness(10, 6, 10, 6),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
            if (string.Equals(name, _selected, StringComparison.OrdinalIgnoreCase))
            {
                button.SetResourceReference(Control.BackgroundProperty, "SelectedOverlayBrush");
                button.FontWeight = FontWeights.Medium;
            }

            button.Click += (_, _) =>
            {
                _selected = name;
                Render();
            };
            list.Children.Add(button);
        }

        var listPane = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = list } };
        listPane.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        frame.Children.Add(listPane);

        var detail = BuildDetail(_servers.FirstOrDefault(s =>
            string.Equals(s.Name, _selected, StringComparison.OrdinalIgnoreCase)));
        Grid.SetColumn(detail, 1);
        frame.Children.Add(detail);

        var card = new Border
        {
            Child = frame,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
        };
        card.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        _tableHost.Children.Add(card);
    }

    private FrameworkElement BuildDetail(McpServerConfig? server)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
        if (server is null)
        {
            return panel;
        }

        var status = services.Mcp.DescribeStatus()
            .FirstOrDefault(line => line.StartsWith(server.Name + ":", StringComparison.OrdinalIgnoreCase));
        var connected = status is not null && status.Contains("connected", StringComparison.OrdinalIgnoreCase);
        var failed = status is not null && status.Contains("failed", StringComparison.OrdinalIgnoreCase);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = server.Name, FontSize = 16, FontWeight = FontWeights.Medium };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        titleRow.Children.Add(title);
        var chip = SettingsRows.Badge(connected ? "running" : failed ? "failed" : "stopped");
        chip.Margin = new Thickness(8, 0, 0, 0);
        titleRow.Children.Add(chip);
        header.Children.Add(titleRow);

        if (!_fromExtensions.Contains(server.Name))
        {
            var delete = SettingsRows.SecondaryButton("Delete", () => Delete(server));
            Grid.SetColumn(delete, 1);
            header.Children.Add(delete);
        }

        panel.Children.Add(header);

        if (_fromExtensions.Contains(server.Name))
        {
            var managed = SettingsRows.Muted("This server is managed by an extension");
            managed.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(managed);
        }

        panel.Children.Add(Field("Command", server.IsRemote ? server.Url ?? "" : server.Command));
        if (server.Args.Count > 0)
        {
            panel.Children.Add(Field("Arguments", string.Join(' ', server.Args)));
        }

        if (failed && status is not null)
        {
            var label = new TextBlock { Text = "Error", FontSize = 13, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 16, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Danger100Brush");
            panel.Children.Add(label);
            panel.Children.Add(SettingsRows.Danger(status));
        }

        var logs = SettingsRows.SecondaryButton("View logs", () => ViewLogs(server.Name));
        logs.HorizontalAlignment = HorizontalAlignment.Left;
        logs.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(logs);

        if (server.Env.Count > 0)
        {
            var advanced = new Expander
            {
                Header = "Advanced options",
                Margin = new Thickness(0, 16, 0, 0),
                Content = Field("Environment variables",
                    string.Join(Environment.NewLine, server.Env.Select(e => $"{e.Key}={e.Value}"))),
            };
            panel.Children.Add(advanced);
        }

        return panel;
    }

    private static FrameworkElement Field(string label, string value)
    {
        var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var heading = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.Medium };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        block.Children.Add(heading);
        var text = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        block.Children.Add(text);
        return block;
    }

    private void Delete(McpServerConfig server)
    {
        if (MessageBox.Show(
                $"Are you sure you want to remove the MCP server “{server.Name}”?",
                PageTitle,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        var remaining = McpConfig.LoadSingleFile(services.Paths.UserMcpFile)
            .Where(s => !s.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase));
        McpConfig.SaveUserServers(services.Paths.UserMcpFile, remaining);
        _selected = null;
        Reload();
    }

    private void EditConfig()
    {
        try
        {
            var file = services.Paths.UserMcpFile;
            if (!File.Exists(file))
            {
                McpConfig.SaveUserServers(file, []);
            }

            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // No editor association is not actionable here.
        }
    }

    private void ViewLogs(string serverName)
    {
        try
        {
            var directory = services.Paths.LogsDirectory;
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"mcp-{DesktopExtensions.Sanitize(serverName)}.log");
            Process.Start(new ProcessStartInfo("explorer.exe",
                File.Exists(file) ? $"/select,\"{file}\"" : $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // Explorer failing to open is not actionable here.
        }
    }

    private FrameworkElement BuildAppInfo()
    {
        var section = SettingsRows.Section("This app");
        var version = typeof(DeveloperPage).Assembly.GetName().Version?.ToString(3) ?? "dev";
        section.Children.Add(SettingsRows.Block(SettingsUi.Caption(
            $"Jarvis Code {version} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")}")));

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, path) in new (string, string)[]
                 {
                     ("Open app data", services.Paths.Root),
                     ("Open sessions", services.Paths.SessionsDirectory),
                     ("Open themes folder", services.Paths.ThemesDirectory),
                     ("Open extensions folder", DesktopExtensions.Root(services.Paths)),
                 })
        {
            var button = SettingsUi.GhostButton(label, () =>
            {
                try
                {
                    Directory.CreateDirectory(path);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                {
                    // Explorer failing to open is not actionable here.
                }
            });
            button.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(button);
        }

        section.Children.Add(SettingsRows.Block(buttons));
        return section;
    }
}
