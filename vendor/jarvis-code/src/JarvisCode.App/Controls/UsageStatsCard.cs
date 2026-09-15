using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JarvisCode.App.Theming;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Controls;

/// <summary>
/// The home-screen usage dashboard: Overview/Models tabs, an All/30d/7d range
/// filter, eight stat tiles, the contribution-style activity heatmap, and the
/// fun-fact caption — Claude Code Desktop's stats card.
/// </summary>
public sealed class UsageStatsCard : ContentControl
{
    /// <summary>≈ tokens in the novel Dune; the reference card's yardstick.</summary>
    public const long DuneTokens = 250_000;

    private readonly Func<UsageStatsData> _loadData;
    private readonly Func<int> _sessionCount;
    private string _tab = "Overview";
    private int? _windowDays;

    public UsageStatsCard(Func<UsageStatsData> loadData, Func<int> sessionCount)
    {
        _loadData = loadData;
        _sessionCount = sessionCount;
        Focusable = false;
        Rebuild();
    }

    public static string FunFact(long totalTokens)
    {
        if (totalTokens <= 0)
        {
            return "Start a session and your usage shows up here.";
        }

        var ratio = (double)totalTokens / DuneTokens;
        return ratio >= 1
            ? $"You've used ~{Math.Round(ratio):N0}× more tokens than Dune."
            : $"You're at ~{ratio * 100:0}% of the tokens in Dune.";
    }

    private void Rebuild()
    {
        var summary = UsageStats.Summarize(_loadData(), DateOnly.FromDateTime(DateTime.Now), _windowDays);

        var panel = new StackPanel();

        // Top row: Overview | Models tabs left, All 30d 7d range right.
        var top = new Grid { Margin = new Thickness(12, 10, 12, 6) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var tab in new[] { "Overview", "Models" })
        {
            tabs.Children.Add(SegmentButton(tab, _tab == tab, () => { _tab = tab; Rebuild(); }));
        }

        top.Children.Add(tabs);
        var ranges = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, days) in new (string, int?)[] { ("All", null), ("30d", 30), ("7d", 7) })
        {
            var captured = days;
            ranges.Children.Add(SegmentButton(label, _windowDays == days, () => { _windowDays = captured; Rebuild(); }));
        }

        Grid.SetColumn(ranges, 1);
        top.Children.Add(ranges);
        panel.Children.Add(top);

        if (_tab == "Overview")
        {
            panel.Children.Add(BuildTiles(summary));
            var heatmap = BuildHeatmap(summary.Heatmap);
            heatmap.Margin = new Thickness(12, 8, 12, 4);
            panel.Children.Add(heatmap);

            var funFact = new TextBlock
            {
                Text = FunFact(summary.TotalTokens),
                FontSize = 11.5,
                Margin = new Thickness(12, 4, 12, 12),
            };
            funFact.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            panel.Children.Add(funFact);
        }
        else
        {
            panel.Children.Add(BuildModels(summary));
        }

        var card = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        Content = card;
    }

    private UIElement BuildTiles(UsageStatsSummary summary)
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(8, 0, 8, 0) };
        foreach (var (label, value) in new (string, string)[]
                 {
                     ("Sessions", _sessionCount().ToString("N0")),
                     ("Messages", summary.TotalMessages.ToString("N0")),
                     ("Total tokens", FormatTokens(summary.TotalTokens)),
                     ("Active days", summary.ActiveDays.ToString()),
                     ("Current streak", $"{summary.CurrentStreak}d"),
                     ("Longest streak", $"{summary.LongestStreak}d"),
                     ("Peak hour", summary.TotalMessages > 0 ? FormatHour(summary.PeakHour) : "—"),
                     ("Favorite model", ShortModel(summary.FavoriteModel)),
                 })
        {
            var tile = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            var labelBlock = new TextBlock { Text = label, FontSize = 11 };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            tile.Children.Add(labelBlock);
            var valueBlock = new TextBlock
            {
                Text = value,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            valueBlock.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            tile.Children.Add(valueBlock);

            var cell = new Border
            {
                Child = tile,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4, 4, 4, 4),
            };
            cell.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
            cell.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
            grid.Children.Add(cell);
        }

        return grid;
    }

    private UIElement BuildModels(UsageStatsSummary summary)
    {
        var panel = new StackPanel { Margin = new Thickness(12, 2, 12, 12) };
        if (summary.ByModel.Count == 0)
        {
            var empty = new TextBlock { Text = "No model usage yet.", FontSize = 12 };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            panel.Children.Add(empty);
            return panel;
        }

        var max = summary.ByModel.Max(static m => m.Tokens);
        foreach (var model in summary.ByModel.OrderByDescending(static m => m.Tokens).Take(10))
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = ShortModel(model.ModelId), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
            row.Children.Add(name);

            var barHost = new Border { Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            barHost.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
            var fraction = max > 0 ? (double)model.Tokens / max : 0;
            var bar = new Border
            {
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
            };
            bar.SetResourceReference(Border.BackgroundProperty, "AccentBrandBrush");
            barHost.SizeChanged += (_, _) => bar.Width = Math.Max(2, barHost.ActualWidth * fraction);
            barHost.Child = bar;
            Grid.SetColumn(barHost, 1);
            row.Children.Add(barHost);

            var stats = new TextBlock
            {
                Text = $"{model.Messages:N0} msgs · {FormatTokens(model.Tokens)}",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stats.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            Grid.SetColumn(stats, 2);
            row.Children.Add(stats);
            panel.Children.Add(row);
        }

        return panel;
    }

    /// <summary>GitHub-contribution-style day grid; shared with the settings Usage panel.</summary>
    public static FrameworkElement BuildHeatmap(IReadOnlyList<HeatmapCell> cells)
    {
        const double size = 11, gap = 3;
        var weeks = (cells.Count + 6) / 7;
        var canvas = new Canvas
        {
            Width = weeks * (size + gap),
            Height = 7 * (size + gap),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var accent = Application.Current.Resources["AccentBrandColor"] is Color c ? c : Colors.Coral;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = size,
                Height = size,
                RadiusX = 2.5,
                RadiusY = 2.5,
                ToolTip = $"{cell.Date:yyyy-MM-dd}: {cell.Messages} messages",
            };
            if (cell.Level <= 0)
            {
                rect.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "HoverOverlayBrush");
            }
            else
            {
                var alpha = cell.Level switch { 1 => 0.30, 2 => 0.55, 3 => 0.80, _ => 1.0 };
                rect.Fill = new SolidColorBrush(CssColor.WithAlpha(accent, alpha));
            }

            Canvas.SetLeft(rect, i / 7 * (size + gap));
            Canvas.SetTop(rect, i % 7 * (size + gap));
            canvas.Children.Add(rect);
        }

        return canvas;
    }

    private Button SegmentButton(string label, bool active, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 11.5,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 2, 0),
            BorderThickness = new Thickness(0),
        };
        button.SetResourceReference(StyleProperty, "RowButton");
        if (active)
        {
            button.SetResourceReference(BackgroundProperty, "SelectedOverlayBrush");
            button.SetResourceReference(ForegroundProperty, "Text100Brush");
            button.FontWeight = FontWeights.SemiBold;
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    private static string FormatHour(int hour) => hour switch
    {
        0 => "12 AM",
        < 12 => $"{hour} AM",
        12 => "12 PM",
        _ => $"{hour - 12} PM",
    };

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString(),
    };

    private static string ShortModel(string? modelId)
        => string.IsNullOrEmpty(modelId) ? "—" : modelId.Length > 20 ? modelId[..20] + "…" : modelId;
}
