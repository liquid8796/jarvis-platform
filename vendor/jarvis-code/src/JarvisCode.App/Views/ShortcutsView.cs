using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// "Keyboard shortcuts": the reference desktop's Code-surface sheet
/// (<c>qI</c> in <c>shared-16-DFDNRrwQ.js</c>) — three sections, one row per
/// binding with the description on the left and its caps on the right, and
/// alternative chords joined by "or".
/// </summary>
public sealed class ShortcutsView : UserControl
{
    public event EventHandler? CloseRequested;

    public ShortcutsView()
    {
        Focusable = true;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
        backdrop.MouseLeftButtonDown += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var body = new StackPanel { Margin = new Thickness(20, 14, 20, 18) };
        body.Children.Add(BuildHeader());

        foreach (var section in ShortcutSheet.Sections)
        {
            var title = new TextBlock
            {
                Text = section.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                // The reference's section heading sits mt-5 with py-2 of its own.
                Margin = new Thickness(0, 20, 0, 8),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            body.Children.Add(title);

            for (var i = 0; i < section.Rows.Count; i++)
            {
                body.Children.Add(BuildRow(section.Rows[i], last: i == section.Rows.Count - 1));
            }
        }

        var card = new Border
        {
            Child = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            Width = 560,
            MaxHeight = 620,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 4,
                Opacity = 0.32,
                Color = Colors.Black,
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;

        var root = new Grid();
        root.Children.Add(backdrop);
        root.Children.Add(card);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    private Grid BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock { Text = ShortcutSheet.Title, FontSize = 16, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        header.Children.Add(title);

        var close = new Button
        {
            Content = new TextBlock { Text = "\uE8BB", FontSize = 10 },
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.SetResourceReference(StyleProperty, "IconButton");
        close.SetResourceReference(FontFamilyProperty, "IconFontFamily");
        close.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Close keyboard shortcuts");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        return header;
    }

    private static FrameworkElement BuildRow(ShortcutRow row, bool last)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var description = new TextBlock
        {
            Text = row.Description,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        grid.Children.Add(description);

        var chords = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < row.Chords.Count; i++)
        {
            if (i > 0)
            {
                var separator = new TextBlock
                {
                    Text = ShortcutSheet.ChordSeparator,
                    FontSize = 11.5,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                separator.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
                chords.Children.Add(separator);
            }

            chords.Children.Add(KeyCaps.Build(ShortcutSheet.Keys(row.Chords[i])));
        }

        Grid.SetColumn(chords, 1);
        grid.Children.Add(chords);

        // Every row but the last of a section carries a hairline under it.
        var wrapper = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, last ? 0 : 0.5) };
        wrapper.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return wrapper;
    }
}
