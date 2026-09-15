using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// The pieces every Customize page is built from, so the four pages share one
/// look: the page header with its search box and actions, the tab strip, the
/// section header with its count, the list row, the facet buttons and the empty
/// states. Geometry follows the reference's own: a 32px control height, an 8px
/// rhythm, rows separated by a hairline rather than boxed as cards.
/// </summary>
internal static class CustomizeUi
{
    /// <summary>The reference's --cds-h-control: every button, box and facet is this tall.</summary>
    public const double ControlHeight = 32;

    /// <summary>The gap between a page's sections.</summary>
    public const double SectionGap = 20;

    public static TextBlock Text(
        string text, double size, string brush, bool semibold = false, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            // Trimming stays on when the text wraps: a clamped block then ends its
            // last visible line with an ellipsis instead of slicing the next one.
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>The page title, its search box and the buttons on its right.</summary>
    public static Grid PageHeader(
        string title,
        string? searchPlaceholder,
        Action<string>? onSearch,
        string searchValue,
        params FrameworkElement[] actions)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var heading = Text(title, 22, "Text100Brush", semibold: true);
        heading.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(heading);

        if (searchPlaceholder is not null && onSearch is not null)
        {
            var search = SearchBox(searchPlaceholder, searchValue, onSearch);
            search.Margin = new Thickness(16, 0, 16, 0);
            search.MaxWidth = 320;
            search.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(search, 1);
            grid.Children.Add(search);
        }

        if (actions.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var action in actions)
            {
                action.Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(action);
            }
            Grid.SetColumn(row, 2);
            grid.Children.Add(row);
        }
        return grid;
    }

    /// <summary>A search box with the reference's placeholder behaviour and 200-character cap.</summary>
    public static FrameworkElement SearchBox(
        string placeholder, string value, Action<string> onChanged, int maxLength = 200)
    {
        var box = new TextBox { Text = value, MaxLength = maxLength, MinWidth = 200, Height = ControlHeight };
        box.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        AutomationProperties.SetName(box, placeholder);
        Controls.PlaceholderText.SetText(box, placeholder);
        box.TextChanged += (_, _) => onChanged(box.Text);
        return box;
    }

    /// <summary>The tab strip over a list: one pill per tab, the selected one raised.</summary>
    public static FrameworkElement Tabs(
        string automationName, IReadOnlyList<(string Label, bool Selected, Action Select)> tabs)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (label, selected, select) in tabs)
        {
            var button = new Button
            {
                Content = Text(label, 12.5, selected ? "Text100Brush" : "Text400Brush", semibold: selected),
                Padding = new Thickness(14, 4, 14, 5),
                Height = ControlHeight - 6,
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
            if (selected)
                button.SetResourceReference(Control.BackgroundProperty, "Bg000Brush");
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => select();
            strip.Children.Add(button);
        }

        var shell = new Border
        {
            Child = strip,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        shell.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        AutomationProperties.SetName(shell, automationName);
        return shell;
    }

    /// <summary>The toolbar under the tabs: the tab strip left, the facet buttons right.</summary>
    public static Grid Toolbar(FrameworkElement? left, params FrameworkElement?[] right)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (left is not null)
            grid.Children.Add(left);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var element in right)
        {
            if (element is null)
                continue;
            element.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(element);
        }
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return grid;
    }

    /// <summary>A drop-down that picks one of a list — the Filter by / Sort by / Show controls.</summary>
    public static FrameworkElement Picker<T>(
        string label,
        IReadOnlyList<(T Value, string Label)> options,
        T selected,
        Action<T> onPick,
        bool held = false)
    {
        var current = options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, selected)).Label
            ?? options[0].Label;
        var button = new Button
        {
            Content = Text($"{label}: {current}", 12.5, held ? "Text100Brush" : "Text400Brush"),
            Padding = new Thickness(10, 0, 10, 0),
            Height = ControlHeight,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        AutomationProperties.SetName(button, label);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (value, text) in options)
        {
            var item = new MenuItem
            {
                Header = text,
                IsChecked = EqualityComparer<T>.Default.Equals(value, selected),
                IsCheckable = true,
            };
            var captured = value;
            item.Click += (_, _) => onPick(captured);
            menu.Items.Add(item);
        }
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    /// <summary>The "Reset filters and sort" link the reference offers beside a held facet.</summary>
    public static FrameworkElement ResetLink(Action onReset)
    {
        var button = new Button
        {
            Content = Text("Reset filters and sort", 12.5, "AccentBrandBrush"),
            Padding = new Thickness(8, 0, 8, 0),
            Height = ControlHeight,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        AutomationProperties.SetName(button, "Reset filters and sort");
        button.Click += (_, _) => onReset();
        return button;
    }

    /// <summary>A section header with the reference's count beside it.</summary>
    public static FrameworkElement SectionHeader(string label, int count, bool attention = false)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, SectionGap, 0, 6),
        };
        row.Children.Add(Text(label, 12.5, attention ? "Warning100Brush" : "Text400Brush", semibold: true));
        if (count > 0)
        {
            var badge = Text(count.ToString(), 11.5, "Text500Brush");
            badge.Margin = new Thickness(8, 0, 0, 0);
            badge.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(badge);
        }
        return row;
    }

    /// <summary>A small chip beside a name: "Disabled", "built-in", a scope suffix.</summary>
    public static Border Chip(string text, string? background = null, string? foreground = null)
    {
        var block = Text(text, 10.5, foreground ?? "Text400Brush");
        block.Margin = new Thickness(6, 1, 6, 2);
        var chip = new Border
        {
            Child = block,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        chip.SetResourceReference(Border.BackgroundProperty, background ?? "HoverOverlayBrush");
        return chip;
    }

    /// <summary>
    /// One list row: a clickable body on the left and controls on the right,
    /// under a hairline. Clicking the body opens the row's detail, which is how
    /// the reference's list behaves.
    /// </summary>
    public static FrameworkElement ListRow(
        FrameworkElement body, FrameworkElement? controls, Action? onOpen, string automationName)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (onOpen is not null)
        {
            var open = new Button
            {
                Content = body,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 10, 8, 10),
            };
            open.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
            AutomationProperties.SetName(open, automationName);
            open.Click += (_, _) => onOpen();
            grid.Children.Add(open);
        }
        else
        {
            body.Margin = new Thickness(8, 10, 8, 10);
            grid.Children.Add(body);
        }

        if (controls is not null)
        {
            controls.VerticalAlignment = VerticalAlignment.Center;
            controls.Margin = new Thickness(12, 0, 4, 0);
            Grid.SetColumn(controls, 1);
            grid.Children.Add(controls);
        }

        var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
        Grid.SetColumnSpan(rule, 2);
        grid.Children.Add(rule);
        return grid;
    }

    /// <summary>The name line of a row: the name, then any chips.</summary>
    public static StackPanel NameLine(string name, params FrameworkElement?[] chips)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var block = Text(name, 14, "Text100Brush", semibold: true);
        block.MaxWidth = 420;
        row.Children.Add(block);
        foreach (var chip in chips)
        {
            if (chip is not null)
                row.Children.Add(chip);
        }
        return row;
    }

    /// <summary>The two lines under a name: description, then the meta line.</summary>
    public static StackPanel Body(string name, string? description, string? meta, params FrameworkElement?[] chips)
    {
        var stack = new StackPanel();
        stack.Children.Add(NameLine(name, chips));
        if (!string.IsNullOrWhiteSpace(description))
        {
            // One truncated line, the way the reference's list row truncates:
            // wrapping under a height cap leaves a short description sitting in a
            // two-line box, so every row would be a different height for nothing.
            var block = Text(OneLine(description), 12.5, "Text400Brush");
            block.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(block);
        }
        if (!string.IsNullOrWhiteSpace(meta))
        {
            var block = Text(meta, 11.5, "Text500Brush");
            block.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(block);
        }
        return stack;
    }

    /// <summary>
    /// Flattens a description onto one line. A frontmatter description may span
    /// several, and an unwrapped block still breaks on the newlines it carries.
    /// </summary>
    public static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A page's empty state: a headline, a line under it, and one action.</summary>
    public static FrameworkElement EmptyState(
        string headline, string description, string? actionLabel = null, Action? onAction = null)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 48, 0, 24),
            MaxWidth = 420,
        };
        var title = Text(headline, 15, "Text100Brush", semibold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.TextTrimming = TextTrimming.None;
        title.TextAlignment = TextAlignment.Center;
        title.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(title);

        var body = Text(description, 13, "Text400Brush", wrap: true);
        body.TextAlignment = TextAlignment.Center;
        body.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.TextTrimming = TextTrimming.None;
        body.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(body);

        if (actionLabel is not null && onAction is not null)
        {
            var button = new Button
            {
                Content = actionLabel,
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Height = ControlHeight,
                Padding = new Thickness(14, 0, 14, 0),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            button.Click += (_, _) => onAction();
            panel.Children.Add(button);
        }
        return panel;
    }

    /// <summary>A one-line notice: the list found nothing, or something failed to load.</summary>
    public static FrameworkElement Notice(string text, bool danger = false)
    {
        var block = Text(text, 12.5, danger ? "Danger100Brush" : "Text500Brush", wrap: true);
        block.TextTrimming = TextTrimming.None;
        block.Margin = new Thickness(2, 14, 0, 0);
        return block;
    }

    /// <summary>A ghost button at the toolbar's control height.</summary>
    public static Button Ghost(string label, Action onClick, string? automationName = null)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 0, 12, 0),
            Height = ControlHeight,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        AutomationProperties.SetName(button, automationName ?? label);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A primary button at the toolbar's control height.</summary>
    public static Button Primary(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(14, 0, 14, 0),
            Height = ControlHeight,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A danger-styled button, for a remove that is about to happen.</summary>
    public static Button Danger(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 0, 12, 0),
            Height = ControlHeight,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "DangerButton");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>The ⋮ button that opens a row's menu.</summary>
    public static FrameworkElement Kebab(string automationName, IReadOnlyList<(string Label, bool Danger, Action Run)> items)
    {
        var button = new Button { Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        button.Content = Glyph("KebabGlyph", filled: true);
        AutomationProperties.SetName(button, automationName);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (label, danger, run) in items)
        {
            var item = new MenuItem { Header = label };
            if (danger)
                item.SetResourceReference(FrameworkElement.StyleProperty, "DangerMenuItem");
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    /// <summary>One of the icon geometries, stroked (or filled) from the button's own foreground.</summary>
    public static Path Glyph(string geometryKey, bool filled = false)
    {
        var path = new Path { VerticalAlignment = VerticalAlignment.Center };
        path.SetResourceReference(FrameworkElement.StyleProperty, filled ? "PanelGlyphFilled" : "PanelGlyph");
        path.SetResourceReference(Path.DataProperty, geometryKey);
        return path;
    }

    /// <summary>A labelled switch, the shape Settings rows already use.</summary>
    public static CheckBox Switch(bool isChecked, Action<bool> onChanged, string automationName)
    {
        var box = new CheckBox { IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center };
        box.SetResourceReference(FrameworkElement.StyleProperty, "SwitchToggle");
        AutomationProperties.SetName(box, automationName);
        box.Checked += (_, _) => onChanged(true);
        box.Unchecked += (_, _) => onChanged(false);
        return box;
    }

    /// <summary>A status dot: filled when connected, faint otherwise.</summary>
    public static Ellipse Dot(string brush)
    {
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        dot.SetResourceReference(Shape.FillProperty, brush);
        return dot;
    }

    /// <summary>A back row above a detail page: "‹ {label}".</summary>
    public static FrameworkElement BackRow(string label, Action onBack)
    {
        var button = new Button
        {
            Padding = new Thickness(6, 2, 10, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Glyph("ArrowLeftGlyph"));
        var text = Text(label, 12.5, "Text400Brush");
        text.Margin = new Thickness(6, 0, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        button.Content = row;
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onBack();
        return button;
    }

    /// <summary>A "Label / value" cell of a detail page's meta strip.</summary>
    public static FrameworkElement MetaCell(string label, string value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 32, 10) };
        stack.Children.Add(Text(label, 11, "Text500Brush"));
        var block = Text(value, 12.5, "Text200Brush");
        block.MaxWidth = 260;
        stack.Children.Add(block);
        return stack;
    }

    /// <summary>A labelled field in a form: the label, the box, and an optional hint or error.</summary>
    public static TextBox Field(
        Panel host, string label, string value, string? hint = null, bool multiline = false)
    {
        var labelBlock = Text(label, 12, "Text400Brush");
        labelBlock.Margin = new Thickness(2, 10, 0, 4);
        host.Children.Add(labelBlock);

        var box = new TextBox { Text = value };
        box.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        AutomationProperties.SetName(box, label);
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.MinHeight = 120;
        }
        else
        {
            box.Height = ControlHeight;
        }
        host.Children.Add(box);

        if (!string.IsNullOrEmpty(hint))
        {
            var hintBlock = Text(hint, 11, "Text500Brush", wrap: true);
            hintBlock.Margin = new Thickness(2, 4, 0, 0);
            host.Children.Add(hintBlock);
        }
        return box;
    }

    /// <summary>An inline error under a field; hidden until it has something to say.</summary>
    public static TextBlock FieldError(Panel host)
    {
        var block = Text("", 11.5, "Danger100Brush", wrap: true);
        block.Margin = new Thickness(2, 4, 0, 0);
        block.Visibility = Visibility.Collapsed;
        host.Children.Add(block);
        return block;
    }

    public static void SetError(TextBlock block, string? message)
    {
        block.Text = message ?? "";
        block.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Copies text, swallowing the clipboard failures Windows sometimes raises.</summary>
    public static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing sensible to do.
        }
    }

    /// <summary>Focus the first box of a form once it is on screen.</summary>
    public static void FocusWhenLoaded(Control control) =>
        control.Loaded += (_, _) => control.Dispatcher.BeginInvoke(new Action(() =>
        {
            control.Focus();
            Keyboard.Focus(control);
        }), System.Windows.Threading.DispatcherPriority.Input);
}
