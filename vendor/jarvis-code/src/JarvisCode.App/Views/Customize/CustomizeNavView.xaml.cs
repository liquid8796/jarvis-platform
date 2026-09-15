using System.Windows;
using System.Windows.Controls;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// The second-level nav the Customize entry swaps in, naming the reference's
/// four sections — Skills, Connectors, Plugins and Memory — over the installed
/// personal plugins and the organization folder.
/// </summary>
public partial class CustomizeNavView : UserControl
{
    public event EventHandler? BackRequested;
    public event EventHandler? SkillsRequested;
    public event EventHandler? ConnectorsRequested;
    public event EventHandler? PluginsRequested;
    public event EventHandler? MemoryRequested;
    public event EventHandler? OrgPluginsRequested;
    public event EventHandler? AddPluginRequested;
    public event EventHandler<string>? PluginSelected;

    public CustomizeNavView()
    {
        InitializeComponent();
    }

    public void ShowPlugins(IReadOnlyList<PluginInfo> plugins)
    {
        PluginsHost.Children.Clear();
        if (plugins.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No plugins installed yet.",
                FontSize = 11.5,
                Margin = new Thickness(12, 2, 12, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            PluginsHost.Children.Add(empty);
            return;
        }

        foreach (var plugin in plugins)
        {
            var icon = new TextBlock
            {
                Text = "",
                FontSize = 13,
                Width = 22,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");

            var label = new TextBlock
            {
                Text = ThemingDisplayName(plugin.Name),
                FontSize = 13.5,
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(label);

            var row = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            row.SetResourceReference(StyleProperty, "RowButton");
            row.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, $"Plugin: {label.Text}");
            var captured = plugin.Name;
            row.Click += (_, _) => PluginSelected?.Invoke(this, captured);
            PluginsHost.Children.Add(row);
        }
    }

    /// <summary>Hides the "will appear here" line once a mount folder is set.</summary>
    public void ShowOrganizationFolder(bool configured) =>
        OrgPluginsEmpty.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"typescript-lsp" → "Typescript lsp", matching the reference nav casing.</summary>
    public static string ThemingDisplayName(string name)
    {
        var words = name.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void OnSkillsClick(object sender, RoutedEventArgs e) => SkillsRequested?.Invoke(this, EventArgs.Empty);
    private void OnConnectorsClick(object sender, RoutedEventArgs e) => ConnectorsRequested?.Invoke(this, EventArgs.Empty);
    private void OnPluginsClick(object sender, RoutedEventArgs e) => PluginsRequested?.Invoke(this, EventArgs.Empty);
    private void OnMemoryClick(object sender, RoutedEventArgs e) => MemoryRequested?.Invoke(this, EventArgs.Empty);
    private void OnOrgPluginsClick(object sender, RoutedEventArgs e) => OrgPluginsRequested?.Invoke(this, EventArgs.Empty);
    private void OnAddPluginClick(object sender, RoutedEventArgs e) => AddPluginRequested?.Invoke(this, EventArgs.Empty);
}
