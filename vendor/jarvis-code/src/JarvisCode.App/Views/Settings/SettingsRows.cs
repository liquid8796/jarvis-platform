using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// The reference settings page's own building blocks, ported from the desktop's
/// shared settings components (ion-dist <c>shared-6-D8hZtQZb.js</c>): a
/// <c>SettingsSection</c> (its <c>ym</c> — an h3 title, an optional footnote and an
/// action slot, then the rows with dividers between non-row children), a
/// <c>SettingsRow</c> (its <c>hm</c> — body-size label and muted description on the
/// left, the control right-aligned and vertically centred, an optional "below"
/// block under both, and an indent for a dependent row), the nested card (its
/// <c>_m</c>) and the segmented control (its <c>iU</c>). Sizes follow the CDS
/// tokens the classes name: body 14, footnote 12, heading 16, md 12, lg 16, xl 24.
/// </summary>
internal static class SettingsRows
{
    private const double BodySize = 14;
    private const double FootnoteSize = 12;
    private const double HeadingSize = 16;
    private const double GapMd = 12;
    private const double GapLg = 16;
    private const double GapXl = 24;

    /// <summary>The reference's <c>ym</c>: a titled section whose children stack with the row padding.</summary>
    public static StackPanel Section(string? title, string? description = null, FrameworkElement? action = null)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, GapXl) };
        if (title is not null || description is not null || action is not null)
        {
            var header = new Grid { Margin = new Thickness(0, 0, 0, GapMd) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            if (title is not null)
            {
                var heading = new TextBlock
                {
                    Text = title,
                    FontSize = HeadingSize,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
                text.Children.Add(heading);
            }

            if (description is not null)
            {
                var footnote = new TextBlock
                {
                    Text = description,
                    FontSize = FootnoteSize,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                footnote.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
                text.Children.Add(footnote);
            }

            header.Children.Add(text);
            if (action is not null)
            {
                action.VerticalAlignment = VerticalAlignment.Top;
                action.Margin = new Thickness(GapLg, 0, 0, 0);
                Grid.SetColumn(action, 1);
                header.Children.Add(action);
            }

            section.Children.Add(header);
        }

        return section;
    }

    /// <summary>
    /// The reference's <c>hm</c>: label + description left, control right, an
    /// optional block underneath, and a left inset for a row that depends on the
    /// one above it.
    /// </summary>
    /// <summary>The same row with a rendered description block (a sentence carrying links).</summary>
    public static FrameworkElement RowWithProse(
        string? label,
        TextBlock description,
        FrameworkElement? control,
        FrameworkElement? below = null,
        bool indent = false,
        FrameworkElement? labelAdornment = null)
        => Row(label, null, control, below, indent, labelAdornment, description);

    public static FrameworkElement Row(
        string? label,
        string? description,
        FrameworkElement? control,
        FrameworkElement? below = null,
        bool indent = false,
        FrameworkElement? labelAdornment = null,
        TextBlock? descriptionBlock = null)
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (label is not null)
        {
            var labelBlock = new TextBlock { Text = label, FontSize = BodySize, TextWrapping = TextWrapping.Wrap };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            if (labelAdornment is not null)
            {
                var adorned = new StackPanel { Orientation = Orientation.Horizontal };
                adorned.Children.Add(labelBlock);
                labelAdornment.Margin = new Thickness(6, 0, 0, 0);
                labelAdornment.VerticalAlignment = VerticalAlignment.Center;
                adorned.Children.Add(labelAdornment);
                text.Children.Add(adorned);
            }
            else
            {
                text.Children.Add(labelBlock);
            }
        }

        if (descriptionBlock is not null)
        {
            descriptionBlock.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(descriptionBlock);
        }
        else if (description is not null)
        {
            var desc = new TextBlock
            {
                Text = description,
                FontSize = BodySize,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            text.Children.Add(desc);
        }

        line.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(GapLg, 0, 0, 0);
            Grid.SetColumn(control, 1);
            line.Children.Add(control);
        }

        FrameworkElement body = line;
        if (below is not null)
        {
            var stack = new StackPanel();
            stack.Children.Add(line);
            below.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(below);
            body = stack;
        }

        body.Margin = new Thickness(indent ? GapLg : 0, GapMd, 0, GapMd);
        System.Windows.Automation.AutomationProperties.SetName(body, label ?? "");
        return body;
    }

    /// <summary>A block that is not a row inside a section (the reference pads those the same way).</summary>
    public static FrameworkElement Block(FrameworkElement content)
    {
        content.Margin = new Thickness(0, GapMd, 0, GapMd);
        return content;
    }

    /// <summary>The reference's <c>_m</c>: a nested card with a ring, holding rows of its own.</summary>
    public static Border Nest(UIElement child, string padding = "md")
    {
        var border = new Border
        {
            Child = child,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = padding == "sm" ? new Thickness(GapMd, 8, GapMd, 8) : new Thickness(16, 0, 16, 0),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return border;
    }

    /// <summary>Muted footnote text, as the reference's <c>text-footnote text-secondary</c>.</summary>
    public static TextBlock Footnote(string text)
    {
        var block = new TextBlock { Text = text, FontSize = FootnoteSize, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        return block;
    }

    /// <summary>Muted body text, as the reference's <c>text-body text-muted</c>.</summary>
    public static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, FontSize = BodySize, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        return block;
    }

    /// <summary>Danger-coloured body text (the reference's <c>text-danger text-body</c>).</summary>
    public static TextBlock Danger(string text)
    {
        var block = new TextBlock { Text = text, FontSize = BodySize, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Danger100Brush");
        return block;
    }

    /// <summary>A neutral badge (the reference's <c>Badge variant="neutral"</c>), as beside "Computer use · Beta".</summary>
    public static Border Badge(string text, string? foregroundKey = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Padding = new Thickness(6, 1, 6, 2),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey ?? "Text300Brush");
        var chip = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
        return chip;
    }

    /// <summary>
    /// The reference's segmented control (<c>iU</c>, contained variant): a rounded
    /// track holding one radio segment per option, the checked one carrying the
    /// thumb. Control height is the CDS control height (32).
    /// </summary>
    public static FrameworkElement Segmented(
        IReadOnlyList<(string Value, string Label)> segments,
        string value,
        Action<string> onChange,
        string? accessibleName = null,
        Func<string, bool>? isDisabled = null)
    {
        var track = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(1),
            Height = 32,
        };
        track.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        track.Child = panel;
        if (accessibleName is not null)
        {
            System.Windows.Automation.AutomationProperties.SetName(track, accessibleName);
        }

        var buttons = new List<(string Value, RadioButton Button)>();
        var group = "seg-" + Guid.NewGuid().ToString("N");
        foreach (var (segValue, label) in segments)
        {
            var button = new RadioButton
            {
                Content = label,
                GroupName = group,
                IsChecked = segValue == value,
                IsEnabled = isDisabled?.Invoke(segValue) != true,
                Padding = new Thickness(12, 0, 12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            var captured = segValue;
            button.Checked += (_, _) =>
            {
                if (captured != value)
                {
                    value = captured;
                    onChange(captured);
                }
            };
            buttons.Add((segValue, button));
            panel.Children.Add(button);
        }

        return track;
    }

    /// <summary>
    /// The reference's select (<c>Y</c> in <c>mode:"select"</c>): a menu of items with an
    /// optional description line, aligned to the row's end. Rendered as a ComboBox
    /// whose rows carry both lines.
    /// </summary>
    public static ComboBox Select(
        IReadOnlyList<(string Value, string Label, string? Description)> items,
        string value,
        Action<string> onChange,
        double width = 160,
        string? accessibleName = null)
    {
        var combo = new ComboBox { Width = width };
        foreach (var item in items)
        {
            var row = new StackPanel { Tag = item.Value };
            row.Children.Add(new TextBlock { Text = item.Label, FontSize = 13 });
            if (item.Description is not null)
            {
                var desc = new TextBlock { Text = item.Description, FontSize = 11.5 };
                desc.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
                row.Children.Add(desc);
            }

            combo.Items.Add(new ComboBoxItem { Content = row, Tag = item.Value });
        }

        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value)
                             ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (accessibleName is not null)
        {
            System.Windows.Automation.AutomationProperties.SetName(combo, accessibleName);
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string picked } && picked != value)
            {
                value = picked;
                onChange(picked);
            }
        };
        return combo;
    }

    /// <summary>A secondary (ghost) button sized like the reference's <c>size="sm"</c>.</summary>
    public static Button SecondaryButton(string label, Action onClick)
    {
        var button = SettingsUi.GhostButton(label, onClick);
        button.Padding = new Thickness(10, 4, 10, 4);
        return button;
    }

    /// <summary>The reference's primary/brand button.</summary>
    public static Button PrimaryButton(string label, Action onClick)
    {
        var button = SettingsUi.PrimaryButton(label, onClick);
        button.Padding = new Thickness(10, 4, 10, 4);
        return button;
    }

    /// <summary>A text input at the reference's row width (<c>w-[220px]</c> unless told otherwise).</summary>
    public static TextBox Input(string text, double width = 220, string? placeholder = null, string? accessibleName = null)
    {
        var box = SettingsUi.Input(text, width);
        if (placeholder is not null)
        {
            box.ToolTip = placeholder;
            Controls.PlaceholderText.SetText(box, placeholder);
        }

        if (accessibleName is not null)
        {
            System.Windows.Automation.AutomationProperties.SetName(box, accessibleName);
        }

        return box;
    }

    /// <summary>
    /// A text box with its placeholder drawn inside it while it is empty, which is
    /// how the reference's inputs show theirs (<c>placeholder</c> on its Input). The
    /// field draws it from its own padding and font, so it lands where the caret does.
    /// </summary>
    public static FrameworkElement WithPlaceholder(TextBox box, string placeholder)
    {
        Controls.PlaceholderText.SetText(box, placeholder);
        return box;
    }

    /// <summary>A disabled switch carrying the reason as its tooltip (the reference's <c>disabledReason</c>).</summary>
    public static CheckBox DisabledSwitch(bool isChecked, string reason)
    {
        var box = SettingsUi.Switch(isChecked, _ => { });
        box.IsEnabled = false;
        box.ToolTip = reason;
        return box;
    }

    /// <summary>A link-styled text run that runs an action.</summary>
    public static TextBlock Link(string text, Action onClick, double size = BodySize)
    {
        var block = new TextBlock
        {
            FontSize = size,
            Cursor = System.Windows.Input.Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        block.Text = text;
        block.MouseLeftButtonUp += (_, _) => onClick();
        return block;
    }
}
