using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JarvisCode.App.Composition;
using JarvisCode.App.Theming;

namespace JarvisCode.App.Views;

/// <summary>
/// The theme gallery: stock + user + gaming + community palettes as cards with
/// swatch dots (dark row over light row). Clicking or Enter applies live.
/// </summary>
public partial class ThemePickerWindow : Window
{
    private static readonly string[] SwatchTokens =
    [
        "--bg-000", "--text-000", "--accent-brand", "--accent-pro-100",
        "--danger-100", "--warning-100", "--success-100",
    ];

    private readonly AppServices _services;
    private readonly List<(ThemeCatalogEntry? Entry, Button Card)> _cards = [];

    public ThemePickerWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        BuildSections("");
        PreviewKeyDown += OnKey;
    }

    private void BuildSections(string filter)
    {
        SectionsHost.Children.Clear();
        _cards.Clear();

        var entries = _services.Theme.Catalog.All
            .Where(e => filter.Length == 0 || e.Theme.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        e.Theme.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filter.Length == 0 || "default stock".Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            AddSection("Stock", [null]);
        }

        AddSection("Your themes", entries.Where(static e => e.Section == ThemeSection.Custom).Cast<ThemeCatalogEntry?>().ToList());
        AddSection("Gaming", entries.Where(static e => e.Section == ThemeSection.Gaming).Cast<ThemeCatalogEntry?>().ToList());
        AddSection("Common", entries.Where(static e => e.Section == ThemeSection.Common).Cast<ThemeCatalogEntry?>().ToList());
    }

    private void AddSection(string title, IReadOnlyList<ThemeCatalogEntry?> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var header = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 14, 0, 8),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        SectionsHost.Children.Add(header);

        var grid = new UniformGridPanel();
        foreach (var entry in entries)
        {
            var card = BuildCard(entry);
            _cards.Add((entry, card));
            grid.Children.Add(card);
        }

        SectionsHost.Children.Add(grid);
    }

    private Button BuildCard(ThemeCatalogEntry? entry)
    {
        var isActive = entry is null
            ? _services.Theme.ActiveTheme.Key.Length == 0
            : string.Equals(_services.Theme.ActiveTheme.Key, entry.Theme.Key, StringComparison.OrdinalIgnoreCase);

        var theme = entry?.Theme ?? StockPalette.Theme;

        var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        var name = new TextBlock
        {
            Text = entry is null ? "Default" : theme.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        stack.Children.Add(name);
        stack.Children.Add(BuildSwatchRow(theme, dark: true));
        stack.Children.Add(BuildSwatchRow(theme, dark: false));

        var card = new Button
        {
            Content = stack,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand,
            Tag = entry?.Theme.Key ?? "",
        };
        card.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, $"Theme: {name.Text}");
        card.Template = BuildCardTemplate(isActive);
        card.Click += (_, _) => Apply(entry?.Theme.Key ?? "");
        return card;
    }

    private static ControlTemplate BuildCardTemplate(bool isActive)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Root");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(isActive ? 2 : 1));
        border.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        border.SetResourceReference(Border.BorderBrushProperty, isActive ? "AccentBrandBrush" : "BorderSoftBrush");
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter { Property = Border.BorderBrushProperty, TargetName = "Root", Value = new DynamicResourceExtension("BorderStrongBrush") });
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter { Property = Border.BorderBrushProperty, TargetName = "Root", Value = new DynamicResourceExtension("Accent100Brush") });
        if (!isActive)
        {
            template.Triggers.Add(hover);
        }

        template.Triggers.Add(focus);
        return template;
    }

    private StackPanel BuildSwatchRow(ThemeDefinition theme, bool dark)
    {
        var tokens = ThemeService.ResolveTokens(theme, dark);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 7, 0, 0),
        };

        var rowBg = tokens.TryGetValue("--bg-100", out var bgValue) && CssColor.TryParse(bgValue, out var bg)
            ? bg
            : Colors.Transparent;
        var container = new Border
        {
            Background = new SolidColorBrush(rowBg),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 5, 7, 5),
        };

        var dots = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var token in SwatchTokens)
        {
            if (tokens.TryGetValue(token, out var value) && CssColor.TryParse(value, out var color))
            {
                dots.Children.Add(new Ellipse
                {
                    Width = 13,
                    Height = 13,
                    Margin = new Thickness(0, 0, 5, 0),
                    Fill = new SolidColorBrush(color),
                });
            }
        }

        container.Child = dots;
        row.Children.Add(container);
        return row;
    }

    private void Apply(string themeKey)
    {
        var ui = _services.UiSettings.Current;
        ui.ActiveTheme = themeKey;
        _services.UiSettings.Save();
        _services.Theme.Apply(themeKey, ui.ThemeMode);
        BuildSections(FilterBox.Text.Trim());
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) =>
        BuildSections(FilterBox.Text.Trim());

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.T &&
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Oem2 && !FilterBox.IsKeyboardFocused)
        {
            // "/" focuses the filter, as in the reference picker.
            e.Handled = true;
            FilterBox.Focus();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Simple wrap grid: as many 236px-min columns as fit, equal widths.</summary>
public sealed class UniformGridPanel : Panel
{
    private const double MinColumnWidth = 236;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, (int)(availableSize.Width / MinColumnWidth));
        var columnWidth = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width / columns;
        double rowHeight = 0, totalHeight = 0;
        var index = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            index++;
            if (index % columns == 0 || index == InternalChildren.Count)
            {
                totalHeight += rowHeight;
                rowHeight = 0;
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? columns * columnWidth : availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, (int)(finalSize.Width / MinColumnWidth));
        var columnWidth = finalSize.Width / columns;
        double y = 0, rowHeight = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var column = i % columns;
            if (column == 0 && i > 0)
            {
                y += rowHeight;
                rowHeight = 0;
            }

            child.Arrange(new Rect(column * columnWidth, y, columnWidth, child.DesiredSize.Height));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        return finalSize;
    }
}
