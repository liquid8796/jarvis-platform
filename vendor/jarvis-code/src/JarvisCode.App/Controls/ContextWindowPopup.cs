using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// The reference app's context-window popup: "Context window {used} / {cap}
/// ({pct}%)" over a segmented bar colored per category, expanding (chevron)
/// into the per-category breakdown — tokens, percent of the window, and the
/// count rows for MCP tools, memory files and custom agents. The reference's
/// plan-usage-limits section is account-bound and deliberately absent here.
/// </summary>
public sealed class ContextWindowPopup
{
    private static readonly Color[] SegmentColors =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6), // Messages — blue
        Color.FromRgb(0xF9, 0x73, 0x16), // System tools — orange
        Color.FromRgb(0x22, 0xC5, 0x5E), // MCP tools — green
        Color.FromRgb(0xEA, 0xB3, 0x08), // Skills — yellow
        Color.FromRgb(0xA8, 0x55, 0xF7), // Memory files — purple
        Color.FromRgb(0x9C, 0xA3, 0xAF), // System prompt — gray
        Color.FromRgb(0x6B, 0x72, 0x80), // Custom agents — darker gray
    ];

    private readonly Popup _popup;
    private bool _expanded;
    private ContextSnapshot? _snapshot;

    public ContextWindowPopup(UIElement placementTarget)
    {
        _popup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Custom,
            StaysOpen = false,
            AllowsTransparency = true,
            // Right-aligned above the pill, like the reference popup.
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                new CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 6),
                    PopupPrimaryAxis.Vertical),
            ],
        };
    }

    public void Show(ContextSnapshot snapshot)
    {
        _snapshot = snapshot;
        _popup.Child = Build();
        _popup.IsOpen = true;
    }

    private FrameworkElement Build()
    {
        var snapshot = _snapshot!;
        var stack = new StackPanel { Width = 340 };

        // Header: label · used/cap (pct) · chevron
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Text("Context window", 12.5, "Text300Brush");
        header.Children.Add(title);
        var amounts = new StackPanel { Orientation = Orientation.Horizontal };
        amounts.Children.Add(Text(
            $"{TurnStatusLine.FormatTokenCount(snapshot.UsedTokens)} / " +
            $"{TurnStatusLine.FormatTokenCount(snapshot.Cap)} ({snapshot.UsedPercent:0}%)",
            12.5, "Text100Brush", semibold: true));
        var chevron = Text(_expanded ? "⌄" : "›", 13, "Text400Brush");
        chevron.Margin = new Thickness(8, 0, 0, 0);
        amounts.Children.Add(chevron);
        Grid.SetColumn(amounts, 1);
        header.Children.Add(amounts);
        header.MouseLeftButtonUp += (_, _) =>
        {
            _expanded = !_expanded;
            _popup.Child = Build();
        };
        stack.Children.Add(header);

        if (snapshot.IsEstimated)
        {
            var estimateNote = Text("Estimated tokens. ChatGPT does not report exact usage; the context limit is configured locally.",
                11, "Text400Brush");
            estimateNote.TextWrapping = TextWrapping.Wrap;
            estimateNote.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(estimateNote);
        }

        stack.Children.Add(BuildSegmentBar(snapshot));

        if (_expanded)
        {
            var rows = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            int swatch = 0;
            foreach (var category in snapshot.Categories)
            {
                rows.Children.Add(BreakdownRow(
                    IsUnspent(category) ? UnspentColor : SegmentColors[swatch++ % SegmentColors.Length],
                    category.Label, category.Tokens,
                    snapshot.Cap <= 0 ? 0 : category.Tokens * 100.0 / snapshot.Cap));
            }

            var separator = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            separator.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
            rows.Children.Add(separator);

            // The reference details exactly these three by count.
            foreach (var category in snapshot.Categories.Where(c =>
                         c.Count >= 0 && c.Label is "MCP tools" or "MCP tools (deferred)"
                             or "Memory files" or "Custom agents"))
            {
                rows.Children.Add(CountRow(category.Label, category.Tokens, category.Count));
            }

            stack.Children.Add(rows);
        }

        var card = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        return card;
    }

    /// <summary>Free space and the compaction buffer are room, not spend, so both stay faint.</summary>
    private static bool IsUnspent(ContextCategory category) =>
        category.Label is Services.ContextBreakdown.FreeSpaceLabel
            or Services.ContextBreakdown.AutocompactBufferLabel
            or Services.ContextBreakdown.CompactBufferLabel;

    private static readonly Color UnspentColor = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

    /// <summary>The proportional segmented bar; free space trails as a faint slice.</summary>
    private static FrameworkElement BuildSegmentBar(ContextSnapshot snapshot)
    {
        var bar = new Grid { Height = 4 };
        int column = 0;

        void AddSegment(long tokens, Brush brush)
        {
            if (tokens <= 0)
            {
                return;
            }

            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tokens, GridUnitType.Star) });
            var segment = new Rectangle { Fill = brush, Margin = new Thickness(column == 0 ? 0 : 1, 0, 0, 0) };
            Grid.SetColumn(segment, column++);
            bar.Children.Add(segment);
        }

        // Deferred slices are listed but never drawn: they cost nothing until
        // tool_search fetches them, which is why the reference filters them out
        // of the grid and out of the totals alike.
        int swatch = 0;
        foreach (var category in snapshot.Categories.Where(static c => !c.IsDeferred))
        {
            AddSegment(
                category.Tokens,
                new SolidColorBrush(IsUnspent(category)
                    ? Color.FromArgb(0x28, 0x88, 0x88, 0x88)
                    : SegmentColors[swatch++ % SegmentColors.Length]));
        }

        if (snapshot.Cap <= 0)
        {
            AddSegment(1, new SolidColorBrush(Color.FromArgb(0x28, 0x88, 0x88, 0x88)));
        }

        return new Border
        {
            Child = bar,
            CornerRadius = new CornerRadius(2),
            Height = 4,
            SnapsToDevicePixels = true,
        };
    }

    private static FrameworkElement BreakdownRow(Color swatchColor, string label, long tokens, double percent)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        var swatch = new Rectangle
        {
            Width = 9,
            Height = 9,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(swatchColor),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(swatch);

        var name = Text(label, 12.5, "Text200Brush");
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var amount = Text(TurnStatusLine.FormatTokenCount(tokens), 12.5, "Text100Brush");
        amount.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(amount, 2);
        row.Children.Add(amount);

        var share = Text(percent >= 0.05 || tokens == 0 ? $"{percent:0.0}%" : "0.0%", 12.5, "Text500Brush");
        share.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(share, 3);
        row.Children.Add(share);
        return row;
    }

    private static FrameworkElement CountRow(string label, long tokens, int count)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        var name = Text($"›  {label}", 12.5, "Text300Brush");
        row.Children.Add(name);

        var amount = Text(TurnStatusLine.FormatTokenCount(tokens), 12.5, "Text200Brush");
        amount.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(amount, 1);
        row.Children.Add(amount);

        var counter = Text(count.ToString(), 12.5, "Text500Brush");
        counter.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(counter, 2);
        row.Children.Add(counter);
        return row;
    }

    private static TextBlock Text(string content, double size, string brushKey, bool semibold = false)
    {
        var text = new TextBlock
        {
            Text = content,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return text;
    }
}
