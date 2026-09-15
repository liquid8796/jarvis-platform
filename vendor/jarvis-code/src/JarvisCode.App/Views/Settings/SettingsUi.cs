using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace JarvisCode.App.Views.Settings;

/// <summary>Small builders that keep every settings panel visually consistent.</summary>
internal static class SettingsUi
{
    public static TextBlock PanelTitle(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 6, 0, 2),
    };

    public static TextBlock PanelSubtitle(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        return block;
    }

    public static TextBlock SectionHeader(string text)
    {
        var block = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 16, 0, 6),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return block;
    }

    /// <summary>Row: bold name + wrapped description on the left, a control on the right.</summary>
    public static Grid Row(string title, string? description, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var titleBlock = new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.Medium };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        text.Children.Add(titleBlock);
        if (!string.IsNullOrEmpty(description))
        {
            var desc = new TextBlock
            {
                Text = description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            text.Children.Add(desc);
        }

        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    public static CheckBox Switch(bool isChecked, Action<bool> onChanged)
    {
        var box = new CheckBox { IsChecked = isChecked };
        box.SetResourceReference(FrameworkElement.StyleProperty, "SwitchToggle");
        box.Checked += (_, _) => onChanged(true);
        box.Unchecked += (_, _) => onChanged(false);
        return box;
    }

    public static TextBox Input(string text, double width = 220)
    {
        var box = new TextBox { Text = text, Width = width };
        box.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        return box;
    }

    /// <summary>
    /// Idle gap after the last keystroke before an edit is committed. Long enough that a
    /// save is not run per keystroke — a save rewrites settings.json and resets the API-key
    /// rotation — short enough that it lands before the user moves on.
    /// </summary>
    public const int AutoSaveIdleMilliseconds = 500;

    /// <summary>
    /// Commits a text setting as it is edited: after a typing pause, when focus leaves the
    /// box, and when the panel is torn down. The teardown hook is the safety net — the
    /// settings dialog is an overlay that Escape and the scrim drop outright, and picking
    /// another panel discards the box, so a pause still counting down must not go with it.
    /// <paramref name="commit"/> receives the current text and whether the commit is final;
    /// a final commit may clamp the value and write it back into the box.
    /// </summary>
    public static void AutoSave(TextBox box, Action<string, bool> commit)
    {
        var idle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoSaveIdleMilliseconds) };
        var committed = box.Text;

        void Commit(bool final)
        {
            idle.Stop();
            if (!final && string.Equals(box.Text, committed, StringComparison.Ordinal))
            {
                return;
            }

            committed = box.Text;
            commit(committed, final);
            // A final commit may normalize the text, which restarts the timer through TextChanged.
            committed = box.Text;
            if (final)
            {
                idle.Stop();
            }
        }

        idle.Tick += (_, _) => Commit(false);
        box.TextChanged += (_, _) =>
        {
            idle.Stop();
            idle.Start();
        };
        box.LostFocus += (_, _) => Commit(true);
        box.Unloaded += (_, _) => Commit(true);
    }

    public static ComboBox Combo(IEnumerable<string> options, string selected, Action<string> onChanged, double width = 180)
    {
        var combo = new ComboBox { Width = width };
        foreach (var option in options)
        {
            combo.Items.Add(option);
        }

        combo.SelectedItem = combo.Items.Contains(selected) ? selected : (combo.Items.Count > 0 ? combo.Items[0] : null);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string value)
            {
                onChanged(value);
            }
        };
        return combo;
    }

    public static Border Card(UIElement child)
    {
        var border = new Border
        {
            Child = child,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 4, 0, 4),
        };
        border.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return border;
    }

    /// <summary>The small grey label that rides beside a name (matches the Customize cards).</summary>
    public static TextBlock ChipText(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        return block;
    }

    public static Border Chip(TextBlock label)
    {
        var chip = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
        return chip;
    }

    /// <summary>Horizontal breathing room between buttons that sit in one row.</summary>
    public static Button Spaced(Button button)
    {
        button.Margin = new Thickness(6, 0, 0, 0);
        return button;
    }

    /// <summary>
    /// A destructive button that asks first: the first click swaps in the confirmation
    /// label, a second click within <see cref="ConfirmWindowSeconds"/> runs the action, and
    /// letting the window lapse puts the original label back. A dialog would be heavier than
    /// the action deserves, but doing it unasked would be worse.
    /// </summary>
    public static Button ConfirmButton(string label, string confirmLabel, Action onConfirmed)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");

        var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ConfirmWindowSeconds) };
        void Disarm()
        {
            reset.Stop();
            button.Content = label;
            button.ClearValue(Control.ForegroundProperty);
        }

        reset.Tick += (_, _) => Disarm();
        button.Click += (_, _) =>
        {
            if (!reset.IsEnabled)
            {
                button.Content = confirmLabel;
                button.SetResourceReference(Control.ForegroundProperty, "Danger100Brush");
                reset.Start();
                return;
            }

            Disarm();
            onConfirmed();
        };
        button.Unloaded += (_, _) => reset.Stop();
        return button;
    }

    /// <summary>How long a <see cref="ConfirmButton"/> stays armed after the first click.</summary>
    public const int ConfirmWindowSeconds = 5;

    public static Button GhostButton(string label, Action onClick)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button PrimaryButton(string label, Action onClick)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) => onClick();
        return button;
    }

    public static TextBlock Caption(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return block;
    }

    /// <summary>The rainbow "Extra" group label from the reference build.</summary>
    public static TextBlock RainbowLabel(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
        };
        block.Foreground = new LinearGradientBrush(
        [
            new GradientStop((Color)ColorConverter.ConvertFromString("#ff5f6d"), 0.00),
            new GradientStop((Color)ColorConverter.ConvertFromString("#ffa751"), 0.17),
            new GradientStop((Color)ColorConverter.ConvertFromString("#f7e14b"), 0.33),
            new GradientStop((Color)ColorConverter.ConvertFromString("#47d178"), 0.50),
            new GradientStop((Color)ColorConverter.ConvertFromString("#4aa8ff"), 0.67),
            new GradientStop((Color)ColorConverter.ConvertFromString("#a56cf0"), 0.83),
            new GradientStop((Color)ColorConverter.ConvertFromString("#ff5f6d"), 1.00),
        ], new Point(0, 0), new Point(1, 0));
        return block;
    }
}
