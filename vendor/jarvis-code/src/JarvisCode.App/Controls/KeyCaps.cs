using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JarvisCode.App.Controls;

/// <summary>
/// One cap per key, the way the reference draws a chord everywhere it shows one
/// (its <c>hs</c> component in <c>c2771e1f6-Czf-iSjS.js</c>): a rounded 4px box
/// per key, 20×20 at its small size and 28×28 otherwise, with 4px between them
/// at the small size and 6px otherwise.
/// </summary>
public static class KeyCaps
{
    public static StackPanel Build(IReadOnlyList<string> keys, bool small = true, bool outlined = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < keys.Count; i++)
        {
            var cap = new Border
            {
                MinWidth = small ? 20 : 28,
                Height = small ? 20 : 28,
                Padding = new Thickness(small ? 4 : 8, 0, small ? 4 : 8, 0),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(i == 0 ? 0 : (small ? 4 : 6), 0, 0, 0),
            };

            if (outlined)
            {
                // The palette draws its caps as outlines over the row rather
                // than as filled chips.
                cap.Background = Brushes.Transparent;
                cap.BorderThickness = new Thickness(1);
                cap.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
            }
            else
            {
                cap.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");
            }

            var text = new TextBlock
            {
                Text = keys[i],
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            cap.Child = text;
            panel.Children.Add(cap);
        }

        return panel;
    }
}
