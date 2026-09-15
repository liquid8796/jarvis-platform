using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Settings;

public partial class SettingsDialog : UserControl
{
    private sealed record NavItem(string Id, string Group, string Label, string Icon, Func<FrameworkElement> Builder);

    /// <summary>
    /// The nav's groups and sections, which the command palette turns into one
    /// "Settings → {group} → {section}" row each, the way the reference builds
    /// those rows from its own settings navigation.
    /// </summary>
    public static IReadOnlyList<(string Group, string Label)> NavSections { get; } =
    [
        ("Settings", "General"),
        ("Settings", "Providers"),
        ("Settings", "Permissions"),
        ("Settings", "Usage"),
        ("Settings", "Jarvis Code"),
        ("Settings", "Import & export"),
        ("Extra", "Themes"),
        ("Extra", "Features"),
        ("Extra", "Fingerprints"),
        ("Desktop app", "General"),
        ("Desktop app", "Extensions"),
        ("Desktop app", "Developer"),
        ("Desktop app", "Debug"),
    ];


    private readonly AppServices _services;
    private readonly List<(NavItem Item, Button Button)> _navButtons = [];
    private readonly List<NavItem> _items;

    public event EventHandler? CloseRequested;
    public event EventHandler? ThemePickerRequested;
    public event EventHandler? PluginsRequested;

    public SettingsDialog(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var panels = new SettingsPanels(services, () => ThemePickerRequested?.Invoke(this, EventArgs.Empty));

        // The reference's own nav, in its order: General, Usage, Claude Code and
        // Import & export under "Settings" (ion-dist shared-16's `z`), then General,
        // Extensions and Developer under "Desktop app" — with this build's own
        // Providers, Permissions, Extra group and Debug pane joined where they fit.
        _items =
        [
            new("general", "Settings", "General", "", panels.BuildGeneral),
            new("providers", "Settings", "Providers", "", panels.BuildProviders),
            new("permissions", "Settings", "Permissions", "", panels.BuildPermissions),
            new("usage", "Settings", "Usage", "", panels.BuildUsage),
            new("claude-code", "Settings", "Jarvis Code", "",
                () => panels.BuildJarvisCode(() => PluginsRequested?.Invoke(this, EventArgs.Empty))),
            new("import", "Settings", "Import & export", "", panels.BuildImportExport),
            new("themes", "Extra", "Themes", "", panels.BuildThemes),
            new("features", "Extra", "Features", "", panels.BuildFeatures),
            new("fingerprints", "Extra", "Fingerprints", "", panels.BuildFingerprints),
            new("desktop", "Desktop app", "General", "",
                () => panels.BuildDesktop(SelectById)),
            new("desktop/extensions", "Desktop app", "Extensions", "", panels.BuildExtensions),
            new("desktop/developer", "Desktop app", "Developer", "", panels.BuildDeveloper),
            new("desktop/debug", "Desktop app", "Debug", "", panels.BuildDebug),
        ];

        BuildNav(_items);
        Select(_items[0]);

        Loaded += (_, _) => Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                NavSearch.Focus();
                NavSearch.SelectAll();
                return;
            }

            if (e.Key != Key.Escape)
            {
                return;
            }

            e.Handled = true;

            // Escape backs out of the query first and the dialog second.
            if (NavSearch.IsKeyboardFocusWithin && NavSearch.Text.Length > 0)
            {
                NavSearch.Clear();
                return;
            }

            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        Focusable = true;
    }

    private void BuildNav(List<NavItem> items)
    {
        NavHost.Children.Clear();
        _navButtons.Clear();

        string? currentGroup = null;
        foreach (var item in items)
        {
            if (item.Group != currentGroup)
            {
                currentGroup = item.Group;
                if (item.Group == "Extra")
                {
                    var rainbow = SettingsUi.RainbowLabel("Extra");
                    rainbow.Margin = new Thickness(8, 12, 8, 4);
                    NavHost.Children.Add(rainbow);
                }
                else
                {
                    var header = new TextBlock
                    {
                        Text = item.Group,
                        FontSize = 11.5,
                        Margin = new Thickness(8, 12, 8, 4),
                    };
                    header.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
                    NavHost.Children.Add(header);
                }
            }

            var icon = new TextBlock
            {
                Text = item.Icon,
                FontSize = 14,
                Width = 20,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");

            var label = new TextBlock
            {
                Text = item.Label,
                FontSize = 13,
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(label);

            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 6, 8, 6),
            };
            button.SetResourceReference(StyleProperty, "RowButton");
            button.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, $"{item.Group}: {item.Label}");
            button.Click += (_, _) => Select(item);

            NavHost.Children.Add(button);
            _navButtons.Add((item, button));
        }
    }

    private void Select(NavItem item)
    {
        foreach (var (navItem, button) in _navButtons)
        {
            if (ReferenceEquals(navItem, item))
            {
                button.SetResourceReference(BackgroundProperty, "SelectedOverlayBrush");
                button.FontWeight = FontWeights.Medium;
            }
            else
            {
                button.ClearValue(BackgroundProperty);
                button.FontWeight = FontWeights.Normal;
            }
        }

        PanelHost.Content = item.Builder();
    }

    /// <summary>Opens a page by the label the nav shows, which is how one page links to another.</summary>
    private void SelectById(string label)
    {
        var match = _items.FirstOrDefault(i => string.Equals(i.Label, label, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            Select(match);
        }
    }

    /// <summary>The sections the search runs over, in nav order.</summary>
    private IReadOnlyList<SettingsSearchSection> SearchSections() =>
        [.. _items.Select(i => new SettingsSearchSection(
            i.Id,
            i.Group,
            i.Label,
            SettingsSearchIndex.SectionKeywords.TryGetValue(i.Id, out var keywords) ? keywords : []))];

    /// <summary>
    /// The reference's settings search: a query ranks a section by its name first
    /// and by the rows it carries second, and the results list the section with the
    /// matching rows beneath it. The row index is the reference's own shape — one
    /// <c>{section, label}</c> per row (ion-dist <c>ca25db325-CwyIN-8d.js</c>) — and
    /// the matcher is its <c>xe</c>, ported in <see cref="SettingsSearchIndex"/>.
    /// </summary>
    private void OnNavSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = NavSearch.Text.Trim();
        if (query.Length == 0)
        {
            SearchResults.Visibility = Visibility.Collapsed;
            NavHost.Visibility = Visibility.Visible;
            foreach (var (_, button) in _navButtons)
            {
                button.Visibility = Visibility.Visible;
            }

            return;
        }

        NavHost.Visibility = Visibility.Collapsed;
        SearchResults.Visibility = Visibility.Visible;
        SearchResults.Children.Clear();

        var hits = SettingsSearchIndex.Search(SearchSections(), SettingsSearchIndex.Rows, query);
        if (hits.Count == 0)
        {
            SearchResults.Children.Add(SettingsRows.Muted("No matches."));
            return;
        }

        var header = SettingsRows.Footnote($"Matching “{query}” across all sections.");
        header.Margin = new Thickness(8, 0, 8, 8);
        SearchResults.Children.Add(header);

        foreach (var hit in hits)
        {
            var section = hit.Section;
            var content = new StackPanel();
            var label = new TextBlock
            {
                Text = hit.Kind == "hit" && hit.Via is null ? section.Name : hit.Via ?? section.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            content.Children.Add(label);
            if (hit.Kind == "more")
            {
                label.Text = $"+{hit.MoreCount} more";
            }
            else if (hit.Via is not null)
            {
                var via = SettingsRows.Footnote($"configured in {section.Name}");
                content.Children.Add(via);
            }

            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 6, 8, 6),
            };
            button.SetResourceReference(StyleProperty, "RowButton");
            var target = _items.FirstOrDefault(i => i.Id == section.Id);
            button.Click += (_, _) =>
            {
                if (target is not null)
                {
                    NavSearch.Clear();
                    Select(target);
                }
            };
            SearchResults.Children.Add(button);
        }
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Re-opens on a specific panel (e.g. jump straight to Themes).</summary>
    public void SelectPanel(string group, string label)
    {
        var match = _navButtons.FirstOrDefault(p =>
            string.Equals(p.Item.Group, group, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Item.Label, label, StringComparison.OrdinalIgnoreCase));
        if (match.Item is not null)
        {
            Select(match.Item);
        }
    }
}
