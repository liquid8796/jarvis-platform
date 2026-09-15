using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.Core.Routines;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The Runs pane, ported from the reference's SN/uN/mN/hN/vN (ion-dist chunk
/// c360a9e1c-DUoNQd2W.js at the "Not a scheduled run" literal): the routine's own header —
/// its name, a Local badge and the schedule under it, with a Details link on the right —
/// then a Running and a Completed section of run rows, each row a status mark, the run's
/// relative time and the status word.
/// </summary>
public sealed class RunHistoryPanel : UserControl
{
    /// <summary>The reference's empty-state body when the session was not started on a schedule.</summary>
    private const string NotScheduledBody =
        "This session wasn’t started from a routine. Open a session that a routine started to see its run history here.";

    private readonly StackPanel _content = new();
    private readonly ContentControl _host = new() { Focusable = false };

    public RunHistoryPanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PanePrimitives.HeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(PanePrimitives.Title(SidePanes.Title(SidePanes.RunHistory)));

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 4, 14, 14),
            Content = _host,
        };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);
        Content = grid;
    }

    /// <summary>Sessions whose turn is live right now, so their row reads Running.</summary>
    public IReadOnlyCollection<string> RunningSessionIds { get; set; } = [];

    /// <summary>Opens another run's session.</summary>
    public event EventHandler<string>? RunSelected;

    /// <summary>The Details link: the reference opens the routine; here that is the Scheduled page.</summary>
    public event EventHandler? DetailsRequested;

    public void Load(AppServices services, Session session)
    {
        _content.Children.Clear();
        if (session.RoutineId is not { Length: > 0 } routineId)
        {
            _host.Content = PanePrimitives.EmptyState(
                "ClockGlyph",
                "Not a scheduled run",
                NotScheduledBody);
            return;
        }

        _host.Content = _content;
        var routine = new RoutineStore(services.Paths.RoutinesFile).Load()
            .FirstOrDefault(r => r.Id == routineId);
        _content.Children.Add(BuildHeader(
            routine?.Name ?? session.RoutineName ?? routineId,
            routine is null ? "" : RoutineScheduler.Describe(routine)));

        IReadOnlyList<SessionSummary> summaries;
        try
        {
            summaries = services.Sessions.ListAsync().GetAwaiter().GetResult();
        }
        catch (System.IO.IOException)
        {
            _content.Children.Add(PanePrimitives.EmptyState(
                "ClockGlyph",
                "Couldn’t load run history",
                "Something went wrong fetching this routine’s runs. Close and reopen the pane to retry."));
            return;
        }

        var failed = new HashSet<string>(StringComparer.Ordinal);
        var rows = RunHistoryPresentation.Build(
            summaries, routineId, session.Id, RunningSessionIds, failed, DateTimeOffset.Now);
        if (rows.Count == 0)
        {
            var empty = PanePrimitives.Muted("No runs yet");
            empty.TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(0, 10, 0, 0);
            _content.Children.Add(empty);
            return;
        }

        AddSection("Running", rows.Where(r => r.Status == RunStatus.Running).ToList());
        AddSection("Completed", rows.Where(r => r.Status != RunStatus.Running).ToList());
    }

    private FrameworkElement BuildHeader(string name, string schedule)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        titleRow.Children.Add(title);

        var badge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "SelectedOverlayBrush");
        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal };
        var laptop = PanePrimitives.Glyph("LaptopGlyph", 11, "Text400Brush");
        laptop.Margin = new Thickness(0, 0, 4, 0);
        badgeRow.Children.Add(laptop);
        var badgeText = new TextBlock { Text = "Local", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        badgeRow.Children.Add(badgeText);
        badge.Child = badgeRow;
        titleRow.Children.Add(badge);
        left.Children.Add(titleRow);

        if (schedule.Length > 0)
        {
            var text = PanePrimitives.Muted(schedule, 11.5);
            text.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(text);
        }

        grid.Children.Add(left);

        var details = new TextBlock
        {
            Text = "Details",
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
        };
        details.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        details.MouseLeftButtonUp += (_, _) => DetailsRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(details, "Details");
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);
        return grid;
    }

    private void AddSection(string heading, IReadOnlyList<RunRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = PanePrimitives.Muted(heading, 11.5);
        title.Margin = new Thickness(0, 0, 0, 6);
        section.Children.Add(title);
        foreach (var row in rows)
        {
            section.Children.Add(BuildRow(row));
        }

        _content.Children.Add(section);
    }

    private FrameworkElement BuildRow(RunRow row)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
        };
        border.SetResourceReference(Border.BackgroundProperty,
            row.IsCurrent ? "SelectedOverlayBrush" : "Bg100Brush");
        border.MouseLeftButtonUp += (_, _) => RunSelected?.Invoke(this, row.SessionId);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (glyph, brush) = row.Status switch
        {
            RunStatus.Failed => ("XCircleGlyph", "Danger100Brush"),
            RunStatus.Running => ("ClockGlyph", "AccentBrandBrush"),
            _ => ("CheckCircleGlyph", "Text400Brush"),
        };
        var mark = PanePrimitives.Glyph(glyph, 12, brush);
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(mark);

        var text = new StackPanel();
        var label = new TextBlock { Text = row.Label, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        text.Children.Add(label);
        var status = PanePrimitives.Muted(RunHistoryPresentation.StatusLabel(row.Status), 11.5);
        text.Children.Add(status);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var caret = PanePrimitives.Glyph("CaretRightGlyph", 12, "Text500Brush");
        caret.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(caret, 2);
        grid.Children.Add(caret);

        border.Child = grid;
        AutomationProperties.SetName(border, $"{row.Label} — {RunHistoryPresentation.StatusLabel(row.Status)}");
        return border;
    }
}
