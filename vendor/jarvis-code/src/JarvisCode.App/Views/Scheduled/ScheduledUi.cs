using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace JarvisCode.App.Views.Scheduled;

/// <summary>
/// The pieces the three Scheduled views are built from. The reference draws
/// this page out of a handful of primitives — a section heading over a bordered
/// card, a labelled field, a pill chip, a muted footnote — so they live here
/// once instead of being rebuilt per view.
/// </summary>
internal static class ScheduledUi
{
    public const double CardRadius = 12;

    public static TextBlock Heading(string text) => Text(text, 22, FontWeights.SemiBold, "Text100Brush");

    /// <summary>A section title: "History", "Instructions", "Always allowed".</summary>
    public static TextBlock SectionHeading(string text)
    {
        var block = Text(text, 13, FontWeights.Medium, "Text400Brush");
        block.Margin = new Thickness(0, 0, 0, 8);
        return block;
    }

    public static TextBlock Footnote(string text)
    {
        var block = Text(text, 11.5, FontWeights.Normal, "Text500Brush");
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    public static TextBlock Text(string text, double size, FontWeight weight, string brush)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    public static Border Card(UIElement child, Thickness? padding = null, Thickness? margin = null)
    {
        var card = new Border
        {
            Child = child,
            CornerRadius = new CornerRadius(CardRadius),
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(14, 12, 14, 12),
            Margin = margin ?? new Thickness(0, 0, 0, 10),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return card;
    }

    /// <summary>
    /// A status pill. The reference tints it by status — success for Active,
    /// danger for Auto-disabled, neutral for the rest — so the colour carries
    /// the same information as the word for a reader scanning the list.
    /// </summary>
    public static Border Chip(string text, string foreground = "Text400Brush", string? border = null)
    {
        var label = Text(text, 11, FontWeights.Medium, foreground);
        var chip = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chip.SetResourceReference(Border.BorderBrushProperty, border ?? "BorderSoftBrush");
        return chip;
    }

    /// <summary>A form field: its label, the box, and the error line under it.</summary>
    public static TextBox Field(
        Panel parent,
        string label,
        string? placeholder = null,
        bool multiline = false,
        bool required = false)
    {
        var caption = Text(label + (required ? " *" : ""), 12, FontWeights.Normal, "Text400Brush");
        caption.Margin = new Thickness(2, 12, 0, 4);
        parent.Children.Add(caption);

        var box = new TextBox();
        box.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        box.SetValue(AutomationProperties.NameProperty, label);
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.MinHeight = 96;
            box.TextWrapping = TextWrapping.Wrap;
            box.VerticalContentAlignment = VerticalAlignment.Top;
        }

        if (placeholder is not { Length: > 0 })
        {
            parent.Children.Add(box);
            return box;
        }

        // The reference shows each field's example inside the box; WPF has no
        // placeholder, so the field's own template draws one. It also rides
        // AutomationProperties so it is not lost to a reader that never sees it.
        box.SetValue(AutomationProperties.HelpTextProperty, placeholder);
        Controls.PlaceholderText.SetText(box, placeholder);
        parent.Children.Add(box);
        return box;
    }

    /// <summary>A search box with its hint drawn inside it by the field's own template.</summary>
    public static Grid SearchBox(string placeholder, string accessibleName, out TextBox box)
    {
        var host = new Grid();
        box = new TextBox { MinWidth = 200 };
        box.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        box.SetValue(AutomationProperties.NameProperty, accessibleName);
        Controls.PlaceholderText.SetText(box, placeholder);
        host.Children.Add(box);
        return host;
    }

    /// <summary>The red line a field shows when it will not save.</summary>
    public static TextBlock ErrorLine(Panel parent)
    {
        var error = Text("", 11.5, FontWeights.Normal, "Danger100Brush");
        error.Margin = new Thickness(2, 4, 0, 0);
        error.TextWrapping = TextWrapping.Wrap;
        error.Visibility = Visibility.Collapsed;
        parent.Children.Add(error);
        return error;
    }

    public static void SetError(TextBlock line, string? message)
    {
        line.Text = message ?? "";
        line.Visibility = message is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    public static Button Button(string content, string style = "GhostButton")
    {
        var button = new Button { Content = content, Padding = new Thickness(11, 5, 11, 5) };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        return button;
    }

    /// <summary>An icon-only action, named for a screen reader as the reference names it.</summary>
    public static Button IconButton(string glyph, string accessibleName)
    {
        var button = new Button();
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        button.SetValue(AutomationProperties.NameProperty, accessibleName);
        button.ToolTip = accessibleName;
        var text = new TextBlock { Text = glyph, FontSize = 13 };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
        button.Content = text;
        return button;
    }

    /// <summary>A row of controls with a consistent gap.</summary>
    public static StackPanel Row(double spacing = 8, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement element && i > 0)
            {
                element.Margin = new Thickness(spacing, 0, 0, 0);
            }

            panel.Children.Add(children[i]);
        }

        return panel;
    }

    /// <summary>The muted "·" the reference puts between a status and its schedule.</summary>
    public static TextBlock Separator()
    {
        var dot = Text("·", 12, FontWeights.Normal, "Text500Brush");
        dot.Margin = new Thickness(6, 0, 6, 0);
        dot.VerticalAlignment = VerticalAlignment.Center;
        return dot;
    }

    /// <summary>The theme's accent, for the one place a status needs a colour of its own.</summary>
    public static Brush Resource(FrameworkElement scope, string key) =>
        scope.TryFindResource(key) as Brush ?? Brushes.Gray;
}
