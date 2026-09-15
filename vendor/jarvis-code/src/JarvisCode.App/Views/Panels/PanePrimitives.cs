using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The pieces every new side pane draws: the pane header (36px, the title at the left and
/// the pane's own trailing actions beside the shared chrome) and the reference's empty
/// state — an icon over a title over a body, centred.
/// </summary>
internal static class PanePrimitives
{
    /// <summary>The reference's pane header row height, shared by every panel here.</summary>
    public const double HeaderHeight = 36;

    /// <summary>
    /// How much of a pane header the tile's shared chrome occupies: four 26px actions and
    /// the strip's own 6px margin. A pane's own header actions sit to the left of it.
    /// </summary>
    public const double ChromeWidth = 110;

    /// <summary>The reference's prose column: markdown in a pane is capped at 68ch.</summary>
    public const double ProseWidth = 524;

    public static TextBlock Title(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(12, 0, 0, 0) };
        block.SetResourceReference(FrameworkElement.StyleProperty, "PanelTitleText");
        return block;
    }

    public static TextBlock Muted(string text, double size = 12)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return block;
    }

    public static Path Glyph(string key, double size = 14, string brush = "Text400Brush")
    {
        var path = new Path { Width = size, Height = size, Stretch = Stretch.Uniform };
        path.SetResourceReference(Path.DataProperty, key);
        path.SetResourceReference(Shape.StrokeProperty, brush);
        path.StrokeThickness = 1.8;
        path.StrokeStartLineCap = PenLineCap.Round;
        path.StrokeEndLineCap = PenLineCap.Round;
        path.StrokeLineJoin = PenLineJoin.Round;
        return path;
    }

    public static Button IconButton(string glyph, string label, Action click)
    {
        var button = new Button { Content = Glyph(glyph), ToolTip = label };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PanelActionButton");
        button.SetValue(AutomationProperties.NameProperty, label);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The reference's nm: a glyph, a title and a body, centred in the pane.</summary>
    public static FrameworkElement EmptyState(string glyph, string title, string body)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 300,
        };
        var icon = Glyph(glyph, 22, "Text500Brush");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(icon);

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        stack.Children.Add(heading);

        var text = Muted(body);
        text.TextAlignment = TextAlignment.Center;
        text.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(text);
        return stack;
    }

    /// <summary>A section heading — the reference's uppercase caption over a pane section.</summary>
    public static TextBlock SectionHeading(string text)
    {
        var block = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 6),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return block;
    }
}
