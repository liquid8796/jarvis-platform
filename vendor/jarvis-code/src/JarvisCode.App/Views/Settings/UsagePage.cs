using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JarvisCode.App.Composition;
using JarvisCode.App.Theming;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// One day of the Usage page's chart: the tokens each surface contributed.
/// Pure data so the shaping is testable without a window.
/// </summary>
public sealed record UsageDay(DateOnly Date, long ChatTokens, long CodeTokens)
{
    public long Total => ChatTokens + CodeTokens;
}

/// <summary>
/// The Usage page's own arithmetic, ported from the reference's Usage settings
/// (ion-dist <c>c71860c77-BgBWVi5D.js</c>): its three time ranges (its <c>H</c> —
/// 7, 30 and 90 days), its per-surface totals, and the rule that decides whether
/// the Cost metric can be picked at all (its <c>C</c>: no turn in range carries a
/// cost estimate, so the segment is disabled and the metric falls back to tokens).
/// </summary>
public static class UsageReport
{
    /// <summary>The reference's <c>H</c>.</summary>
    public static readonly IReadOnlyList<int> Ranges = [7, 30, 90];

    /// <summary>
    /// The reference's bucketing: a range of 7 or 30 days is charted a day per bar,
    /// 90 a week per bar (its <c>bucketDays</c>).
    /// </summary>
    public static int BucketDays(int windowDays) => windowDays > 30 ? 7 : 1;

    /// <summary>The days in range, oldest first, with a zero row for every day that recorded nothing.</summary>
    public static IReadOnlyList<UsageDay> Days(UsageStatsData data, DateOnly today, int windowDays)
    {
        var days = new List<UsageDay>(windowDays);
        for (var offset = windowDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            if (data.Days.TryGetValue(UsageStatsData.KeyFor(date), out var day))
            {
                var chat = day.TokensBySurface.GetValueOrDefault("chat");
                var code = day.TokensBySurface.GetValueOrDefault("code");
                // A day recorded before the split has its whole total under neither
                // surface; charting it as Code keeps the bar honest about the tokens.
                if (chat == 0 && code == 0)
                {
                    code = day.Tokens;
                }

                days.Add(new UsageDay(date, chat, code));
            }
            else
            {
                days.Add(new UsageDay(date, 0, 0));
            }
        }

        return days;
    }

    /// <summary>The bars the chart draws: consecutive days folded into buckets of <see cref="BucketDays"/>.</summary>
    public static IReadOnlyList<UsageDay> Bars(IReadOnlyList<UsageDay> days, int bucketDays)
    {
        if (bucketDays <= 1)
        {
            return days;
        }

        var bars = new List<UsageDay>();
        for (var i = 0; i < days.Count; i += bucketDays)
        {
            var slice = days.Skip(i).Take(bucketDays).ToList();
            bars.Add(new UsageDay(slice[0].Date, slice.Sum(d => d.ChatTokens), slice.Sum(d => d.CodeTokens)));
        }

        return bars;
    }

    /// <summary>The reference's compact token format (<c>notation:"compact"</c>, one fraction digit).</summary>
    public static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{value / 1_000_000.0:0.#}M",
        >= 1_000 => $"{value / 1_000.0:0.#}K",
        _ => value.ToString("N0"),
    };
}

/// <summary>
/// Settings › Usage — the reference desktop's Usage page (<c>c71860c77-BgBWVi5D.js</c>,
/// its <c>O</c>): the description, the Time range control, a tile per surface, the
/// Chart metric control and the daily bar chart, plus this build's own activity
/// heatmap and per-model breakdown beneath.
///
/// The Cost half of the page is present and switched off, which is what the
/// reference itself renders when no turn in range carries a cost estimate: its
/// <c>C</c> disables the Cost segment with the reason
/// "No turns in this time range have a cost estimate" and forces the metric back
/// to tokens. This engine never prices a turn — <c>costUSD</c> is written per turn
/// by the reference's own runtime and no price table ships in its bundle — so that
/// branch is the only one this build can be in.
/// </summary>
internal sealed class UsagePage(AppServices services)
{
    public const string PageTitle = "Usage";

    private int _windowDays = 30;

    public FrameworkElement Build()
    {
        var page = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var body = new StackPanel();
        void Render()
        {
            body.Children.Clear();
            body.Children.Add(BuildBody());
        }

        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var description = SettingsRows.Muted("Token usage on this device across Chat and Code.");
        description.MaxWidth = 420;
        header.Children.Add(description);
        var range = SettingsRows.Segmented(
            [.. UsageReport.Ranges.Select(d => (d.ToString(), $"{d}d"))],
            _windowDays.ToString(),
            value =>
            {
                _windowDays = int.Parse(value);
                Render();
            },
            accessibleName: "Time range");
        Grid.SetColumn(range, 1);
        header.Children.Add(range);
        page.Children.Add(header);

        Render();
        page.Children.Add(body);
        return page;
    }

    private FrameworkElement BuildBody()
    {
        var data = services.UsageStats.Load();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var days = UsageReport.Days(data, today, _windowDays);
        var stack = new StackPanel();

        if (days.All(d => d.Total == 0))
        {
            var empty = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var headline = new TextBlock { Text = "No usage yet", FontSize = 15, FontWeight = FontWeights.SemiBold };
            headline.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            empty.Children.Add(headline);
            empty.Children.Add(SettingsRows.Muted("Once you use Chat or Code on this device, token usage will show up here."));
            stack.Children.Add(empty);
            return stack;
        }

        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (label, tokens) in new (string, long)[]
                 {
                     ("Chat", days.Sum(d => d.ChatTokens)),
                     ("Code", days.Sum(d => d.CodeTokens)),
                     ("Total", days.Sum(d => d.Total)),
                 })
        {
            var tile = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var name = new TextBlock { Text = label, FontSize = 12 };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            tile.Children.Add(name);
            var value = new TextBlock { Text = UsageReport.Compact(tokens), FontSize = 20, FontWeight = FontWeights.SemiBold };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            tile.Children.Add(value);
            var note = new TextBlock { Text = "input + output tokens", FontSize = 11.5 };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            tile.Children.Add(note);
            tiles.Children.Add(SettingsUi.Card(tile));
        }

        stack.Children.Add(tiles);

        var metric = SettingsRows.Segmented(
            [("tokens", "Tokens"), ("cost", "Cost")],
            "tokens",
            _ => { },
            accessibleName: "Chart metric",
            isDisabled: value => value == "cost");
        metric.HorizontalAlignment = HorizontalAlignment.Left;
        metric.ToolTip = "No turns in this time range have a cost estimate";
        metric.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(metric);

        stack.Children.Add(BuildChart(UsageReport.Bars(days, UsageReport.BucketDays(_windowDays))));

        var summary = UsageStats.Summarize(data, today);
        stack.Children.Add(SettingsUi.SectionHeader("Activity"));
        stack.Children.Add(BuildHeatmap(summary.Heatmap));

        if (summary.ByModel.Count > 0)
        {
            stack.Children.Add(SettingsUi.SectionHeader("By model"));
            foreach (var model in summary.ByModel.OrderByDescending(static m => m.Tokens).Take(8))
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock { Text = model.ModelId, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
                row.Children.Add(name);
                var stats = new TextBlock
                {
                    Text = $"{model.Messages:N0} messages · {UsageReport.Compact(model.Tokens)} tokens",
                    FontSize = 12,
                };
                stats.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
                Grid.SetColumn(stats, 1);
                row.Children.Add(stats);
                stack.Children.Add(row);
            }
        }

        return stack;
    }

    private static FrameworkElement BuildChart(IReadOnlyList<UsageDay> bars)
    {
        const double height = 140;
        var max = Math.Max(1, bars.Max(b => b.Total));
        var grid = new UniformGrid { Columns = bars.Count, Height = height, Margin = new Thickness(0, 0, 0, 12) };
        var accent = Application.Current.Resources["AccentBrandColor"] is Color c ? c : Colors.Coral;

        foreach (var bar in bars)
        {
            var column = new Grid { Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Bottom };
            var stackPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            var chat = new System.Windows.Shapes.Rectangle
            {
                Height = bar.ChatTokens / (double)max * height,
                Fill = new SolidColorBrush(CssColor.WithAlpha(accent, 0.45)),
            };
            var code = new System.Windows.Shapes.Rectangle
            {
                Height = bar.CodeTokens / (double)max * height,
                Fill = new SolidColorBrush(accent),
            };
            stackPanel.Children.Add(chat);
            stackPanel.Children.Add(code);
            column.Children.Add(stackPanel);
            column.ToolTip = $"{bar.Date:yyyy-MM-dd}: {UsageReport.Compact(bar.Total)} tokens";
            grid.Children.Add(column);
        }

        return grid;
    }

    private static FrameworkElement BuildHeatmap(IReadOnlyList<HeatmapCell> cells)
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
}
