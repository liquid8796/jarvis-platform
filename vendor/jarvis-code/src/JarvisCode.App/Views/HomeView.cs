using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The Code home view's action center — the reference's `LM` (desktop 1.40609.1.0,
/// ccd chunk `c11959232`@86400): three sections in its order, Sessions, Projects and
/// Pull requests, each a header with a spacer, an optional "Mark all read" and a
/// "Show N more" toggle, over a list of rows at `gap-g3`. Which rows appear and how
/// many fit is <see cref="HomeViewPresentation"/>'s; this draws them.
///
/// Deliberate deltas: the reference's Projects section lists claude.ai projects,
/// which this build has no layer for, so it lists the folders sessions have run in;
/// and its rows carry a cloud "Move to cloud" menu this port has no destination for.
/// </summary>
public sealed class HomeView : StackPanel
{
    /// <summary>The reference's `gap-[40px]` between sections and `gap-g3` between rows.</summary>
    private const double SectionGap = 40;

    private const double RowGap = 6;

    public HomeView()
    {
        Margin = new Thickness(0, 24, 0, 56);
    }

    /// <summary>A row was activated: open that session.</summary>
    public event EventHandler<string>? SessionActivated;

    /// <summary>The row's dismiss button: the reference's per-row X.</summary>
    public event EventHandler<string>? SessionDismissed;

    /// <summary>"Mark all read".</summary>
    public event EventHandler? MarkAllRead;

    /// <summary>A pull-request row was activated: open its PR.</summary>
    public event EventHandler<HomePrRow>? PrActivated;

    /// <summary>A folder row was activated: start a session there.</summary>
    public event EventHandler<string>? FolderActivated;

    private bool _sessionsExpanded;
    private bool _foldersExpanded;
    private bool _prsExpanded;

    private IReadOnlyList<HomeSessionRow> _sessions = [];
    private IReadOnlyList<string> _folders = [];
    private IReadOnlyList<HomePrRow> _prs = [];
    private bool _hasClearableUnreads;
    private double _windowHeight = 900;

    /// <summary>True when every section is empty — the reference's `landingClear`.</summary>
    public bool IsClear => _sessions.Count == 0 && _folders.Count == 0 && _prs.Count == 0;

    public void Show(
        IReadOnlyList<HomeSessionRow> sessions,
        IReadOnlyList<string> folders,
        IReadOnlyList<HomePrRow> prs,
        bool hasClearableUnreads,
        double windowHeight)
    {
        _sessions = sessions;
        _folders = folders;
        _prs = prs;
        _hasClearableUnreads = hasClearableUnreads;
        _windowHeight = windowHeight;
        Render();
    }

    private void Render()
    {
        Children.Clear();
        var budget = HomeViewPresentation.RowBudget(_windowHeight);
        var limit = HomeViewPresentation.SessionLimit(_windowHeight);

        if (_sessions.Count > 0)
        {
            Children.Add(Section(
                HomeViewPresentation.SessionsTitle,
                _sessions.Count,
                limit,
                _sessionsExpanded,
                value =>
                {
                    _sessionsExpanded = value;
                    Render();
                },
                _hasClearableUnreads ? () => MarkAllRead?.Invoke(this, EventArgs.Empty) : null,
                (_sessionsExpanded ? _sessions : _sessions.Take(limit).ToList()).Select(SessionRow)));
        }

        if (_folders.Count > 0)
        {
            Children.Add(Section(
                HomeViewPresentation.ProjectsTitle,
                _folders.Count,
                limit,
                _foldersExpanded,
                value =>
                {
                    _foldersExpanded = value;
                    Render();
                },
                null,
                (_foldersExpanded ? _folders : _folders.Take(limit).ToList()).Select(FolderRow)));
        }

        if (_prs.Count > 0)
        {
            Children.Add(Section(
                HomeViewPresentation.PullRequestsTitle,
                _prs.Count,
                budget,
                _prsExpanded,
                value =>
                {
                    _prsExpanded = value;
                    Render();
                },
                null,
                (_prsExpanded ? _prs : _prs.Take(budget).ToList()).Select(PrRow)));
        }
    }

    private static FrameworkElement Section(
        string title,
        int total,
        int limit,
        bool expanded,
        Action<bool> setExpanded,
        Action? markAllRead,
        IEnumerable<FrameworkElement> rows)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, SectionGap) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        header.Children.Add(label);

        if (markAllRead is not null)
        {
            var button = GhostButton(HomeViewPresentation.MarkAllRead);
            button.Click += (_, _) => markAllRead();
            Grid.SetColumn(button, 1);
            header.Children.Add(button);
        }

        if (total > limit)
        {
            var toggle = GhostButton(expanded
                ? HomeViewPresentation.ShowLess
                : HomeViewPresentation.ShowMore(total - limit));
            toggle.Click += (_, _) => setExpanded(!expanded);
            Grid.SetColumn(toggle, 2);
            header.Children.Add(toggle);
        }

        panel.Children.Add(header);
        foreach (var row in rows)
        {
            row.Margin = new Thickness(0, RowGap, 0, 0);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static Button GhostButton(string text)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12 },
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.SetResourceReference(StyleProperty, "GhostButton");
        return button;
    }

    private FrameworkElement SessionRow(HomeSessionRow row)
    {
        var card = RowCard(out var grid);
        grid.Children.Add(Pill(HomeViewPresentation.KindLabel(row.Kind), HomeViewPresentation.KindBrushKey(row.Kind)));

        var title = RowTitle(row.Title);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        if (row.Session.Summary is { Length: > 0 } summary)
        {
            var subtitle = Muted(summary, 13);
            subtitle.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(subtitle, 2);
            grid.Children.Add(subtitle);
        }

        if (row.Session.RepoName is { Length: > 0 } repo)
        {
            var meta = Muted(repo, 12);
            meta.MaxWidth = 180;
            Grid.SetColumn(meta, 3);
            grid.Children.Add(meta);
        }

        var time = Muted(HomeViewPresentation.NarrowRelative(row.Session.UpdatedAt, DateTimeOffset.Now), 12);
        time.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(time, 4);
        grid.Children.Add(time);

        var dismiss = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Opacity = 0,
            ToolTip = HomeViewPresentation.Dismiss,
            Content = new TextBlock { Text = "✕", FontSize = 11 },
        };
        dismiss.SetResourceReference(StyleProperty, "IconButton");
        dismiss.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            HomeViewPresentation.DismissSession);
        dismiss.Click += (_, e) =>
        {
            e.Handled = true;
            SessionDismissed?.Invoke(this, row.Session.Id);
        };
        Grid.SetColumn(dismiss, 5);
        grid.Children.Add(dismiss);

        card.MouseEnter += (_, _) => dismiss.Opacity = 1;
        card.MouseLeave += (_, _) => dismiss.Opacity = 0;
        card.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            HomeViewPresentation.OpenSession(row.Title));
        card.MouseLeftButtonUp += (_, _) => SessionActivated?.Invoke(this, row.Session.Id);
        return card;
    }

    private FrameworkElement PrRow(HomePrRow row)
    {
        var card = RowCard(out var grid);
        grid.Children.Add(Pill(row.Pill.Label, HomeViewPresentation.ToneBrushKey(row.Pill.Tone)));

        var title = RowTitle(row.Title);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var number = Muted($"#{row.Entry.Pr.Number}", 12);
        number.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(number, 2);
        grid.Children.Add(number);

        var repo = Muted(row.Entry.RepoSlug, 12);
        repo.MaxWidth = 180;
        Grid.SetColumn(repo, 3);
        grid.Children.Add(repo);

        var time = Muted(HomeViewPresentation.NarrowRelative(row.Entry.UpdatedAt, DateTimeOffset.Now), 12);
        time.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(time, 4);
        grid.Children.Add(time);

        card.MouseLeftButtonUp += (_, _) => PrActivated?.Invoke(this, row);
        return card;
    }

    private FrameworkElement FolderRow(string folder)
    {
        var card = RowCard(out var grid);
        grid.Children.Add(Pill(null, "Text500Brush"));

        var title = RowTitle(SidebarPresentation.FolderName(folder));
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var path = Muted(folder, 12);
        path.MaxWidth = 240;
        Grid.SetColumn(path, 3);
        grid.Children.Add(path);

        card.ToolTip = folder;
        card.MouseLeftButtonUp += (_, _) => FolderActivated?.Invoke(this, folder);
        return card;
    }

    /// <summary>The reference's row card: `px-p4 py-p6 rounded-r6 bg-t1` with a hover tint.</summary>
    private static Border RowCard(out Grid grid)
    {
        grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var card = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8, 8, 8),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");
        return card;
    }

    /// <summary>The reference's kind pill: a 5px dot in a 16px column, then the label.</summary>
    private static FrameworkElement Pill(string? label, string brushKey)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var slot = new Grid { Width = 16 };
        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetResourceReference(Shape.FillProperty, brushKey);
        slot.Children.Add(dot);
        panel.Children.Add(slot);

        if (label is { Length: > 0 })
        {
            var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            panel.Children.Add(text);
        }

        return panel;
    }

    private static TextBlock RowTitle(string text)
    {
        var title = new TextBlock
        {
            Text = text,
            FontSize = 13.5,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        return title;
    }

    private static TextBlock Muted(string text, double size)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return block;
    }
}
