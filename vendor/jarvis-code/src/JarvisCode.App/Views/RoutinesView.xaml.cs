using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Scheduled;
using JarvisCode.Core.Routines;

namespace JarvisCode.App.Views;

/// <summary>Which of the page's three views is on screen.</summary>
internal enum ScheduledPageMode
{
    List,
    Editor,
    Detail,
}

/// <summary>
/// The "Scheduled" page: one list over the routines this app schedules and the
/// SKILL.md tasks the <c>scheduled-tasks</c> MCP server writes, with the
/// reference's editor and detail views behind it.
///
/// Ported from the reference desktop 1.40609.1.0: the list route in
/// <c>ccd3f68fe-BCipNHOa.js</c>, the local routine form in
/// <c>c0243d234-BOJof1xz.js</c>, the local routine detail in
/// <c>cfc18e0f4-DP8WK7zq.js</c>, and the page chrome in
/// <c>c9e1458cd-BHGZMMuT.js</c>. The strings are its own, from the routines
/// entity table in <c>shared-7-pSrlloWM.js</c>.
/// </summary>
public partial class RoutinesView : UserControl
{
    private RoutineRunner? _runner;
    private Func<IReadOnlyList<string>>? _modelIds;
    private UiSettings? _uiSettings;
    private Action? _saveUiSettings;

    private ScheduledPageMode _mode = ScheduledPageMode.List;
    private string _query = "";
    private ScheduledStatus? _statusFilter;
    private ScheduledSort _sort = ScheduledSort.NextRun;
    private string? _detailId;
    private Routine? _editing;
    private TextBox? _searchBox;
    private IReadOnlyList<ScheduledItem>? _items;

    public event EventHandler? BackRequested;

    /// <summary>Asks the shell to open a new Code session on a prompt ("Create with Jarvis").</summary>
    public event EventHandler<string>? SetupRequested;

    /// <summary>Asks the shell to open a session the History list names.</summary>
    public event EventHandler<string>? SessionRequested;

    public RoutinesView()
    {
        InitializeComponent();
    }

    public void Initialize(
        RoutineRunner runner,
        Func<IReadOnlyList<string>> modelIds,
        UiSettings? uiSettings = null,
        Action? saveUiSettings = null)
    {
        _runner = runner;
        _modelIds = modelIds;
        _uiSettings = uiSettings;
        _saveUiSettings = saveUiSettings;
        _sort = uiSettings?.ScheduledSort == "name" ? ScheduledSort.Name : ScheduledSort.NextRun;
        Render();
    }

    /// <summary>Opens the editor straight away — the <c>--open=scheduled:editor</c> pose.</summary>
    public void ShowEditor(Routine? routine = null)
    {
        _editing = routine;
        _mode = ScheduledPageMode.Editor;
        Render();
    }

    public void ShowDetail(string routineId)
    {
        _detailId = routineId;
        _historyShown = HistoryPage;
        _mode = ScheduledPageMode.Detail;
        Render();
    }

    public void ShowList()
    {
        _mode = ScheduledPageMode.List;
        _editing = null;
        _detailId = null;
        Render();
    }

    public void Render()
    {
        if (_runner is null)
        {
            return;
        }

        _items = null;
        Host.Children.Clear();
        Scroller.ScrollToTop();
        switch (_mode)
        {
            case ScheduledPageMode.Editor:
                RenderEditor();
                break;
            case ScheduledPageMode.Detail when Items().FirstOrDefault(i => i.Id == _detailId) is { } item:
                RenderDetail(item);
                break;
            case ScheduledPageMode.Detail:
                // The routine went away while its page was open (deleted in
                // another window, or its file removed) — the reference answers
                // with its not-found state rather than an empty page.
                RenderNotFound();
                break;
            default:
                RenderList();
                break;
        }
    }

    // ---- the model behind all three views ----------------------------------

    /// <summary>
    /// Every row the page shows: the routines, then the scheduled tasks. The
    /// reference merges the two the same way and renders one list over both.
    /// </summary>
    internal IReadOnlyList<ScheduledItem> Items()
    {
        if (_runner is null)
        {
            return [];
        }

        // Each row's run count is a file read, and the list is rebuilt on every
        // keystroke of the search box, so one render reads the stores once.
        if (_items is { } cached)
        {
            return cached;
        }

        var items = new List<ScheduledItem>();
        var now = DateTimeOffset.Now;
        foreach (var routine in _runner.Store.Load())
        {
            items.Add(ToItem(routine, _runner.Runs.RunCount(routine.Id), NextRunOf(routine, now)));
        }

        foreach (var task in _runner.ScheduledTasks.Load())
        {
            var jitter = ScheduledTaskJitter.SecondsFor(task.TaskId, task.CronExpression);
            items.Add(new ScheduledItem
            {
                Id = task.TaskId,
                Description = task.Description,
                Prompt = task.Prompt,
                Enabled = task.Enabled,
                CronExpression = task.CronExpression,
                FireAt = task.FireAt,
                LastRunAt = task.LastRunAt,
                JitterSeconds = jitter,
                IsScheduledTask = true,
                NotifyOnCompletion = task.NotifyOnCompletion,
                RunCount = _runner.Runs.RunCount(task.TaskId),
                NextRunAt = task.NextRunAt(now)?.AddSeconds(jitter),
            });
        }

        _items = items;
        return items;
    }

    internal static ScheduledItem ToItem(Routine routine, int runCount, DateTimeOffset? nextRunAt) => new()
    {
        Id = routine.Id,
        Name = routine.Name,
        Description = routine.Description,
        Prompt = routine.Instruction,
        Enabled = routine.Enabled,
        CronExpression = routine.CronExpression,
        FireAt = routine.FireAt,
        LastRunAt = routine.LastRunAt is { } last ? new DateTimeOffset(last) : null,
        EndedReason = routine.EndedReason,
        SuspensionReason = routine.SuspensionReason,
        WorkingDirectory = routine.WorkingDirectory,
        ModelId = routine.ModelId,
        PermissionModeName = routine.PermissionModeName,
        UseWorktree = routine.UseWorktree,
        SourceBranch = routine.SourceBranch,
        NotifyOnCompletion = routine.NotifyOnCompletion,
        ChromePermissionMode = routine.ChromePermissionMode,
        ChromeAllowedDomains = routine.ChromeAllowedDomains,
        ApprovedPermissions = routine.ApprovedPermissions,
        RunCount = runCount,
        NextRunAt = nextRunAt,
    };

    /// <summary>
    /// When a routine next fires, from the real evaluator — a preset, a
    /// hand-written cron, or one of the built-in schedules.
    /// </summary>
    private static DateTimeOffset? NextRunOf(Routine routine, DateTimeOffset now)
    {
        if (!routine.Enabled)
        {
            return null;
        }

        if (routine.FireAt is { } fireAt)
        {
            return fireAt > now ? fireAt : null;
        }

        if (routine.CronExpression is { Length: > 0 } cron)
        {
            return CronSchedule.TryParse(cron)?.NextAfter(now.LocalDateTime) is { } next
                ? new DateTimeOffset(next)
                : null;
        }

        return RoutineScheduler.NextSlot(routine, now.LocalDateTime) is { } slot
            ? new DateTimeOffset(slot)
            : null;
    }

    internal Routine? FindRoutine(string id) =>
        _runner?.Store.Load().FirstOrDefault(r => r.Id == id);

    // ---- list --------------------------------------------------------------

    private void RenderList()
    {
        var all = Items();
        var now = DateTimeOffset.Now;

        Host.Children.Add(BuildListHeader(all.Count));

        // "Local routines only run while your computer is awake and online." —
        // the one thing a reader has to know before trusting an unattended run.
        var banner = ScheduledUi.Card(
            ScheduledUi.Footnote("Local routines only run while your computer is awake and online."),
            new Thickness(14, 10, 14, 10),
            new Thickness(0, 0, 0, 14));
        Host.Children.Add(banner);

        var filtered = ScheduledTaskPresentation.Sort(
            ScheduledTaskPresentation.Filter(all, _query, _statusFilter), _sort, now);

        if (all.Count == 0)
        {
            Host.Children.Add(EmptyState());
            Host.Children.Add(BuildTemplates());
            return;
        }

        if (filtered.Count == 0)
        {
            var message = ScheduledTaskPresentation.EmptyResultMessage(
                _query.Trim().Length > 0, _statusFilter is not null);
            var empty = ScheduledUi.Footnote(message);
            empty.Margin = new Thickness(2, 16, 0, 0);
            Host.Children.Add(empty);
            return;
        }

        foreach (var item in filtered)
        {
            Host.Children.Add(BuildRow(item, now));
        }

        // The reference offers the template gallery again while the list is
        // still short, and only when nothing is filtering it.
        if (_query.Trim().Length == 0 && _statusFilter is null && all.Count <= 5)
        {
            Host.Children.Add(BuildTemplates());
        }
    }

    private FrameworkElement BuildListHeader(int count)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = ScheduledUi.IconButton("", "Back");
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var title = ScheduledUi.Heading("Scheduled");
        title.Margin = new Thickness(10, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(ScheduledUi.Row(0, back, title));

        var newTask = new Button { Content = "New task" };
        newTask.SetResourceReference(StyleProperty, "PrimaryButton");
        newTask.HorizontalAlignment = HorizontalAlignment.Right;
        newTask.ContextMenu = BuildNewTaskMenu();
        newTask.Click += (_, _) =>
        {
            newTask.ContextMenu.PlacementTarget = newTask;
            newTask.ContextMenu.IsOpen = true;
        };
        Grid.SetColumn(newTask, 1);
        top.Children.Add(newTask);
        header.Children.Add(top);

        var subtitle = ScheduledUi.Footnote(
            "Create templated routines that can be kicked off on schedule, by API, or webhook.");
        subtitle.Margin = new Thickness(2, 8, 0, 14);
        header.Children.Add(subtitle);

        header.Children.Add(BuildListControls(count));
        return header;
    }

    private ContextMenu BuildNewTaskMenu()
    {
        var menu = new ContextMenu();
        var withJarvis = new MenuItem { Header = "Create with Jarvis" };
        withJarvis.Click += (_, _) => SetupRequested?.Invoke(
            this,
            "Help me set up a routine. Ask me what it should do, how often it should run, and when, then create it.");
        menu.Items.Add(withJarvis);

        var manual = new MenuItem { Header = "Set up manually" };
        manual.Click += (_, _) => ShowEditor();
        menu.Items.Add(manual);
        return menu;
    }

    private FrameworkElement BuildListControls(int count)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchHost = ScheduledUi.SearchBox("Search routines…", "Search routines", out var search);
        search.Text = _query;
        search.TextChanged += (_, _) =>
        {
            _query = search.Text;
            var caret = search.CaretIndex;
            Render();
            // The list is rebuilt on every keystroke, so the box the render just
            // made has to take the caret back, or typing would jump to the start
            // of the query.
            if (_searchBox is { } refreshed)
            {
                refreshed.Focus();
                refreshed.CaretIndex = Math.Min(caret, refreshed.Text.Length);
            }
        };
        _searchBox = search;
        row.Children.Add(searchHost);

        var actions = ScheduledUi.Row(8, BuildFilterButton(), BuildSortButton(), BuildCountLabel(count));
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private FrameworkElement BuildCountLabel(int count)
    {
        var label = ScheduledUi.Footnote(ScheduledTaskPresentation.RoutineCountLabel(count));
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    private FrameworkElement BuildFilterButton()
    {
        var active = _statusFilter is not null;
        var button = ScheduledUi.Button(active
            ? $"Filter · {ScheduledTaskPresentation.StatusLabel(_statusFilter!.Value)}"
            : "Filter");
        button.SetValue(AutomationProperties.NameProperty, active ? "Filter routines, 1 filter active" : "Filter routines");

        var menu = new ContextMenu();
        AddFilterItem(menu, "All", null);
        foreach (var status in new[]
                 {
                     ScheduledStatus.Active, ScheduledStatus.Paused,
                     ScheduledStatus.AutoDisabled, ScheduledStatus.OnHold,
                 })
        {
            AddFilterItem(menu, ScheduledTaskPresentation.StatusLabel(status), status);
        }

        button.ContextMenu = menu;
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    private void AddFilterItem(ContextMenu menu, string label, ScheduledStatus? status)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _statusFilter == status };
        item.Click += (_, _) =>
        {
            _statusFilter = status;
            Render();
        };
        menu.Items.Add(item);
    }

    private FrameworkElement BuildSortButton()
    {
        var label = _sort == ScheduledSort.Name ? "Name" : "Next run";
        var button = ScheduledUi.Button($"Sort by {label}");
        button.SetValue(AutomationProperties.NameProperty, "Sort routines");

        var menu = new ContextMenu();
        AddSortItem(menu, "Next run", ScheduledSort.NextRun);
        AddSortItem(menu, "Name", ScheduledSort.Name);
        button.ContextMenu = menu;
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    private void AddSortItem(ContextMenu menu, string label, ScheduledSort sort)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _sort == sort };
        item.Click += (_, _) =>
        {
            _sort = sort;
            if (_uiSettings is not null)
            {
                _uiSettings.ScheduledSort = sort == ScheduledSort.Name ? "name" : "nextRun";
                _saveUiSettings?.Invoke();
            }

            Render();
        };
        menu.Items.Add(item);
    }

    private FrameworkElement EmptyState()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 24, 0, 24) };
        var text = ScheduledUi.Text("No routines yet.", 13.5, FontWeights.Normal, "Text400Brush");
        text.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(text);

        var create = new Button { Content = "New routine", Margin = new Thickness(0, 12, 0, 0) };
        create.SetResourceReference(StyleProperty, "PrimaryButton");
        create.HorizontalAlignment = HorizontalAlignment.Center;
        create.Click += (_, _) => ShowEditor();
        panel.Children.Add(create);
        return panel;
    }

    private FrameworkElement BuildRow(ScheduledItem item, DateTimeOffset now)
    {
        var status = ScheduledTaskPresentation.Status(item);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        var name = ScheduledUi.Text(
            ScheduledTaskPresentation.DisplayName(item), 14, FontWeights.SemiBold, "Text100Brush");
        name.VerticalAlignment = VerticalAlignment.Center;
        nameRow.Children.Add(name);
        var badge = ScheduledUi.Chip("Only on this computer");
        badge.Margin = new Thickness(8, 0, 0, 0);
        nameRow.Children.Add(badge);
        text.Children.Add(nameRow);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        meta.Children.Add(StatusChip(status));
        var schedule = ScheduledTaskPresentation.Describe(item, now) ?? "Manual only";
        meta.Children.Add(ScheduledUi.Separator());
        var scheduleText = ScheduledUi.Text(schedule, 12, FontWeights.Normal, "Text400Brush");
        scheduleText.VerticalAlignment = VerticalAlignment.Center;
        meta.Children.Add(scheduleText);
        if (item.RunCount > 0)
        {
            meta.Children.Add(ScheduledUi.Separator());
            var runs = ScheduledUi.Text(
                ScheduledTaskPresentation.RunCountLabel(item.RunCount), 12, FontWeights.Normal, "Text500Brush");
            runs.VerticalAlignment = VerticalAlignment.Center;
            meta.Children.Add(runs);
        }

        text.Children.Add(meta);

        if (ScheduledTaskPresentation.DescriptionPreview(item) is { } preview)
        {
            var description = ScheduledUi.Text(preview, 12, FontWeights.Normal, "Text500Brush");
            description.TextWrapping = TextWrapping.Wrap;
            description.TextTrimming = TextTrimming.CharacterEllipsis;
            description.MaxHeight = 34;
            description.Margin = new Thickness(0, 5, 0, 0);
            text.Children.Add(description);
        }

        if (ScheduledTaskPresentation.NextRunText(item, now) is { } next)
        {
            var nextRun = ScheduledUi.Footnote(
                item.FireAt is not null ? $"Runs at: {next}" : $"Next run: {next}");
            nextRun.Margin = new Thickness(0, 5, 0, 0);
            text.Children.Add(nextRun);
        }

        grid.Children.Add(text);

        var open = ScheduledUi.Button("Open");
        open.SetValue(AutomationProperties.NameProperty, $"Open routine {ScheduledTaskPresentation.DisplayName(item)}");
        open.VerticalAlignment = VerticalAlignment.Center;
        open.Click += (_, _) => ShowDetail(item.Id);
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);

        return ScheduledUi.Card(grid);
    }

    internal static Border StatusChip(ScheduledStatus status)
    {
        var label = ScheduledTaskPresentation.StatusLabel(status);
        return status switch
        {
            ScheduledStatus.Active => ScheduledUi.Chip(label, "Success100Brush", "Success100Brush"),
            ScheduledStatus.AutoDisabled => ScheduledUi.Chip(label, "Danger100Brush", "Danger100Brush"),
            _ => ScheduledUi.Chip(label),
        };
    }

    private FrameworkElement BuildTemplates()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        panel.Children.Add(ScheduledUi.SectionHeading(ScheduledTemplates.Heading));

        foreach (var template in ScheduledTemplates.All)
        {
            // Laid out like a routine row rather than as one big button: the
            // reference's whole card is clickable, but this app's ghost button
            // centres its content, and a card that stretches with a named
            // action beside it keeps one pattern on the page and stays
            // reachable from the keyboard.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var content = new StackPanel();
            content.Children.Add(ScheduledUi.Text(template.Title, 13, FontWeights.SemiBold, "Text100Brush"));
            var description = ScheduledUi.Text(template.Description, 12, FontWeights.Normal, "Text400Brush");
            description.TextWrapping = TextWrapping.Wrap;
            description.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(description);

            var item = new ScheduledItem { Id = template.Id, CronExpression = template.Cron, Enabled = true };
            if (ScheduledTaskPresentation.Describe(item, DateTimeOffset.Now) is { } schedule)
            {
                var when = ScheduledUi.Footnote(schedule);
                when.Margin = new Thickness(0, 5, 0, 0);
                content.Children.Add(when);
            }

            grid.Children.Add(content);

            var use = ScheduledUi.Button("Set up");
            use.VerticalAlignment = VerticalAlignment.Center;
            use.Margin = new Thickness(12, 0, 0, 0);
            use.SetValue(AutomationProperties.NameProperty, $"Set up {template.Title}");
            use.Click += (_, _) => SetupRequested?.Invoke(this, template.Prompt);
            Grid.SetColumn(use, 1);
            grid.Children.Add(use);

            panel.Children.Add(ScheduledUi.Card(grid, margin: new Thickness(0, 0, 0, 8)));
        }

        return panel;
    }

    private void RenderNotFound()
    {
        Host.Children.Add(BuildDetailHeader("Routines", null));
        var message = ScheduledUi.Text(
            "This routine couldn’t be found.", 13.5, FontWeights.Normal, "Text400Brush");
        message.Margin = new Thickness(2, 18, 0, 12);
        Host.Children.Add(message);
    }

    /// <summary>The back-to-the-list chrome the editor and the detail view share.</summary>
    internal FrameworkElement BuildDetailHeader(string title, string? subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var back = ScheduledUi.Button("Routines");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Margin = new Thickness(-6, 0, 0, 10);
        back.Click += (_, _) => ShowList();
        panel.Children.Add(back);
        panel.Children.Add(ScheduledUi.Heading(title));
        if (subtitle is { Length: > 0 })
        {
            var text = ScheduledUi.Footnote(subtitle);
            text.Margin = new Thickness(2, 6, 0, 0);
            panel.Children.Add(text);
        }

        return panel;
    }
}
