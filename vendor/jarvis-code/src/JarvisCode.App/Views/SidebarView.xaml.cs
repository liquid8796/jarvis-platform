using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JarvisCode.App.Composition;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Panels;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Views;

public partial class SidebarView : UserControl
{
    /// <summary>The mosaic accepts the same format, so a row can be dropped on a pane column.</summary>
    private const string DragFormat = CodeWorkspace.SessionDragFormat;

    /// <summary>Side of the row's ⋮ button, which is square so its hover fill is too.</summary>
    private const double OverflowButtonSize = 20;

    public event EventHandler? NewSessionRequested;
    public event EventHandler? ScheduledRequested;
    public event EventHandler? CustomizeRequested;
    public event EventHandler<string>? FilterChanged;
    public event EventHandler<string>? SessionSelected;
    public event EventHandler<string>? SessionDeleteRequested;
    public event EventHandler<string>? SessionExportRequested;
    public event EventHandler<(string SessionId, string NewTitle)>? SessionRenameRequested;
    public event EventHandler<string>? SessionSplitRequested;

    /// <summary>A row was opened in its own window (Ctrl+click, a drag out, or the menu's New window).</summary>
    public event EventHandler<string>? SessionWindowRequested;

    /// <summary>"Go to routine" — the routine whose run started the picked session.</summary>
    public event EventHandler<string>? RoutineRequested;

    private AppServices? _services;
    private SessionGroupsStore? _groups;
    private ToastQueue? _toasts;
    private IReadOnlyList<SessionSummary> _sessions = [];
    private bool _groupByProject;
    private string? _activeSessionId;
    private IReadOnlyCollection<string> _runningSessionIds = [];
    private IReadOnlyCollection<string> _awaitingSessionIds = [];
    private Point _dragStart;
    private string? _dragCandidate;
    private string? _renamingSessionId;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private string? _selectionAnchor;

    public SidebarView()
    {
        InitializeComponent();
        GroupsHost.MouseLeave += (_, _) => { if (_groupByProject) Dispatcher.InvokeAsync(Render); };
        GroupsHost.LostKeyboardFocus += (_, _) => { if (_groupByProject && !GroupsHost.IsMouseOver) Dispatcher.InvokeAsync(Render); };
    }

    public string FilterText => SearchBox.Text;

    public void SetSurface(bool isCode)
    {
        NewChatLabel.Text = isCode ? "New session" : "New chat";
        ScheduledRow.Visibility = isCode ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            isCode ? SidebarPresentation.SearchRecents : "Filter sessions");
    }

    private void OnScheduledClick(object sender, RoutedEventArgs e)
        => ScheduledRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The run count beside the Scheduled row, the reference's secondary content
    /// for that entry. Hidden at zero: a row reading "0 runs" says less than the
    /// row alone.
    /// </summary>
    public void SetScheduledRunCount(int runs)
    {
        ScheduledRunCount.Text = Services.ScheduledTaskPresentation.RunCountLabel(runs);
        ScheduledRunCount.Visibility = runs > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The store holding user-created session groups (Code surface).</summary>
    public void SetGroupsStore(SessionGroupsStore store)
    {
        _groups = store;
    }

    /// <summary>Where a pin, an archive or a failure reports itself.</summary>
    public void SetToasts(ToastQueue toasts) => _toasts = toasts;

    /// <summary>
    /// The whole service graph, which the row menu needs for export, fork, deep links
    /// and a session opened in its own window.
    /// </summary>
    public void SetServices(AppServices services)
    {
        _services = services;
        _groups = services.SessionGroups;
        _toasts = services.Toasts;
        services.PullRequests.Changed += (_, _) => Dispatcher.Invoke(Render);
    }

    public void SetUser(string displayName, string subtitle)
    {
        UserName.Text = string.IsNullOrWhiteSpace(displayName) ? "Jarvis" : displayName;
        UserInitials.Text = UserName.Text.Length > 0 ? UserName.Text[..1].ToUpperInvariant() : "J";
        UserSubtitle.Text = subtitle;
        UserSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowSessions(
        IReadOnlyList<SessionSummary> sessions,
        bool groupByProject,
        string? activeSessionId,
        string? filter,
        IReadOnlyCollection<string>? runningSessionIds = null,
        IReadOnlyCollection<string>? awaitingSessionIds = null)
    {
        _sessions = sessions;
        _groupByProject = groupByProject;
        _activeSessionId = activeSessionId;
        _runningSessionIds = runningSessionIds ?? [];
        _awaitingSessionIds = awaitingSessionIds ?? [];
        Render();
    }

    // ---- filter state ----

    private SidebarFilterState FilterState
    {
        get
        {
            var settings = _services?.UiSettings.Current;
            return settings is null
                ? new SidebarFilterState()
                : new SidebarFilterState
                {
                    GroupBy = SidebarFilterModel.NormalizeGroupBy(settings.SidebarGroupBy),
                    SortBy = SidebarFilterModel.NormalizeSortBy(settings.SidebarSortBy),
                    Status = SidebarFilterModel.NormalizeStatus(settings.SidebarStatus),
                    ShowEmptyFolders = settings.SidebarShowEmptyFolders,
                    ShowPrStatus = settings.SidebarShowPrStatus,
                    StateActivityDays =
                        SidebarFilterModel.NormalizeStateActivityDays(settings.SidebarStateActivityDays),
                };
        }
    }

    private void SaveFilter(SidebarFilterState state)
    {
        if (_services is null)
        {
            return;
        }

        var settings = _services.UiSettings.Current;
        settings.SidebarGroupBy = state.GroupBy;
        settings.SidebarSortBy = state.SortBy;
        settings.SidebarStatus = state.Status;
        settings.SidebarShowEmptyFolders = state.ShowEmptyFolders;
        settings.SidebarShowPrStatus = state.ShowPrStatus;
        settings.SidebarStateActivityDays = state.StateActivityDays;
        _services.UiSettings.Save();
        Render();
    }

    /// <summary>
    /// The menu the sidebar last opened. A ContextMenu lives in a popup of its own
    /// rather than under this window's visual tree, so the self-test has no way to
    /// find one by walking; this is the seam it reads instead.
    /// </summary>
    internal ContextMenu? LastOpenedMenu { get; private set; }

    /// <summary>The filter popover, hung off the first section header's filter icon.</summary>
    private void OpenFilterMenu(FrameworkElement anchor)
    {
        var state = FilterState;
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 200,
        };

        var model = SidebarFilterModel.Build(state);
        foreach (var section in model.StatusSections)
        {
            menu.Items.Add(section.Label == "Last activity"
                ? FilterSubmenu(section, key => SaveFilter(
                    state with { StateActivityDays = int.TryParse(key, out var days) ? days : 0 }))
                : FilterSubmenu(section, key => SaveFilter(state with { Status = key })));
        }

        menu.Items.Add(new Separator());
        foreach (var section in model.GroupSortSections)
        {
            menu.Items.Add(section.Label == "Group by"
                ? FilterSubmenu(section, key => SaveFilter(state with { GroupBy = key }))
                : FilterSubmenu(section, key => SaveFilter(state with { SortBy = key })));
        }

        menu.Items.Add(new Separator());
        foreach (var toggle in model.Toggles)
        {
            var item = new MenuItem { Header = toggle.Label, IsCheckable = true, IsChecked = toggle.Checked };
            var key = toggle.Key;
            item.Click += (_, _) => SaveFilter(key == "showEmptyFolders"
                ? state with { ShowEmptyFolders = !state.ShowEmptyFolders }
                : state with { ShowPrStatus = !state.ShowPrStatus });
            menu.Items.Add(item);
        }

        if (model.ShowClearFilters)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear filters" };
            clear.Click += (_, _) => SaveFilter(SidebarFilterModel.ClearFilters(state));
            menu.Items.Add(clear);
        }

        LastOpenedMenu = menu;
        menu.IsOpen = true;
    }

    private static MenuItem FilterSubmenu(FilterSection section, Action<string> pick)
    {
        var root = new MenuItem { Header = section.Label, InputGestureText = section.ValueLabel };
        foreach (var option in section.Options)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                IsCheckable = true,
                IsChecked = option.Key == section.Value,
            };
            var key = option.Key;
            item.Click += (_, _) => pick(key);
            root.Items.Add(item);
        }

        return root;
    }

    // ---- Recents header menu ----

    private void OpenRecentsMenu(FrameworkElement anchor)
    {
        if (_groups is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 200,
        };

        if (_selected.Count > 0)
        {
            var context = new SessionMenuContext
            {
                MultiCount = _selected.Count,
                MultiUnarchive = _selected.All(id => _groups.IsArchived(id)),
                Groups = [.. _groups.Data.Groups.Select(g => (g.Id, g.Name))],
                MultiModels = BulkModelChoices(),
            };
            SessionMenuRenderer.Fill(menu.Items, SessionMenuModel.ForSidebarRow(context), InvokeMulti, menu);
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear selection" };
            clear.Click += (_, _) =>
            {
                _selected.Clear();
                Render();
            };
            menu.Items.Add(clear);
        }
        else
        {
            var selectAll = new MenuItem { Header = "Select all" };
            selectAll.Click += (_, _) =>
            {
                foreach (var session in _sessions)
                {
                    _selected.Add(session.Id);
                }

                Render();
            };
            menu.Items.Add(selectAll);
            var newGroup = new MenuItem { Header = SidebarPresentation.NewGroup };
            newGroup.Click += (_, _) => CreateGroupPrompt();
            menu.Items.Add(newGroup);
            menu.Items.Add(new Separator());
            menu.Items.Add(BulkOlderSubmenu());
        }

        LastOpenedMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// The reference's "Bulk actions for older sessions" submenu: archive or delete
    /// every unpinned session whose last activity is more than a week old, each behind
    /// its own confirm and each reporting the count it moved.
    /// </summary>
    private MenuItem BulkOlderSubmenu()
    {
        var root = new MenuItem { Header = SessionBulkActions.SubmenuLabel };
        var cutoff = DateTimeOffset.Now - SessionBulkActions.OlderThan;
        var older = _sessions
            .Where(s => s.UpdatedAt < cutoff && !(_groups?.IsPinned(s.Id) ?? false))
            .Select(static s => s.Id)
            .ToList();

        var archive = new MenuItem { Header = SessionBulkActions.ArchiveAll(older.Count) };
        archive.Click += (_, _) => RunBulkOlder(older, archive: true);
        root.Items.Add(archive);

        var delete = new MenuItem { Header = SessionBulkActions.DeleteAll(older.Count) };
        delete.Click += (_, _) => RunBulkOlder(older, archive: false);
        root.Items.Add(delete);

        if (older.Count == 0)
        {
            archive.IsEnabled = false;
            delete.IsEnabled = false;
        }

        return root;
    }

    private void RunBulkOlder(IReadOnlyList<string> ids, bool archive)
    {
        if (_groups is null || ids.Count == 0)
        {
            return;
        }

        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                archive ? SessionBulkActions.ArchiveTitle : SessionBulkActions.DeleteTitle,
                archive ? SessionBulkActions.ArchiveBody(ids.Count) : SessionBulkActions.DeleteBody(ids.Count),
                archive ? SessionBulkActions.ArchiveConfirm : SessionBulkActions.DeleteConfirm,
                focusCancel: true))
        {
            return;
        }

        foreach (var id in ids)
        {
            if (archive)
            {
                _groups.SetArchived(id, true);
            }
            else
            {
                SessionDeleteRequested?.Invoke(this, id);
            }
        }

        _toasts?.AddSuccess(archive
            ? SessionBulkActions.Archived(ids.Count)
            : SessionBulkActions.Deleted(ids.Count));
        Render();
    }

    /// <summary>
    /// The Output style submenu's rows for one session: the four built-in styles over
    /// a Default row, with the session's own choice checked. A session with no choice
    /// of its own runs at the account-wide style `/output-style` sets, which is what
    /// the Default row means here.
    /// </summary>
    private IReadOnlyList<SessionMenuChoice>? OutputStyleChoices(string sessionId)
    {
        var settings = _services?.UiSettings.Current;
        if (settings is null)
        {
            return null;
        }

        var current = settings.SessionOutputStyles.TryGetValue(sessionId, out var name) ? name : null;
        return
        [
            new SessionMenuChoice("", "Default", current is null),
            .. OutputStyles.Available(_sessions.FirstOrDefault(session => session.Id == sessionId)?.WorkingDirectory,
                _services!.Paths.Root).Select(style =>
                new SessionMenuChoice(style.Name, style.Name,
                    string.Equals(current, style.Name, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>The models the multi-select's "Set model for {n}" submenu offers.</summary>
    private IReadOnlyList<SessionMenuChoice>? BulkModelChoices()
    {
        var models = _services?.Settings.Models;
        return models is not { Count: > 0 }
            ? null
            : [.. models.Select(m => new SessionMenuChoice(m.ModelId, m.DisplayName, false))];
    }

    private void InvokeMulti(SessionMenuRow row)
    {
        if (_groups is null)
        {
            return;
        }

        var ids = _selected.ToList();
        switch (row.Action)
        {
            case SessionMenuAction.MultiMarkUnread:
                foreach (var id in ids)
                {
                    _groups.SetExplicitUnread(id, true);
                }

                break;
            case SessionMenuAction.MultiSetModel when _services is { } services:
                _ = SetModelForAsync(services, ids, row.Argument);
                return;
            case SessionMenuAction.MultiMoveToGroup:
                foreach (var id in ids)
                {
                    _groups.MoveSession(id, row.Argument);
                }

                break;
            case SessionMenuAction.MultiArchive:
                foreach (var id in ids)
                {
                    _groups.SetArchived(id, true);
                }

                break;
            case SessionMenuAction.MultiUnarchive:
                foreach (var id in ids)
                {
                    _groups.SetArchived(id, false);
                }

                break;
            case SessionMenuAction.MultiDelete:
                if (!ConfirmDialog.Ask(
                        Window.GetWindow(this),
                        "Delete selected?",
                        $"{ids.Count} sessions and their transcripts are removed from this computer. This can't be undone.",
                        "Delete"))
                {
                    return;
                }

                foreach (var id in ids)
                {
                    SessionDeleteRequested?.Invoke(this, id);
                }

                break;
            case SessionMenuAction.NewGroup:
                CreateGroupPrompt(ids);
                break;
        }

        _selected.Clear();
        Render();
    }

    /// <summary>
    /// "Set model for {n}": writes the chosen model onto each selected session, which
    /// is what the reference's own bulk row does.
    /// </summary>
    private async Task SetModelForAsync(AppServices services, IReadOnlyList<string> ids, string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return;
        }

        foreach (var id in ids)
        {
            if (await services.Sessions.LoadAsync(id) is { } session)
            {
                session.ModelId = modelId;
                await services.Sessions.SaveAsync(session);
            }
        }

        _selected.Clear();
        Render();
    }

    // ---- rendering ----

    private void Render()
    {
        GroupsHost.Items.Clear();
        var filter = SearchBox.Text.Trim();

        if (_groupByProject && _groups is not null)
        {
            RenderCodeList(filter);
            return;
        }

        var sessions = _sessions;
        if (filter.Length > 0)
        {
            sessions = [.. sessions.Where(s => s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        if (sessions.Count == 0)
        {
            GroupsHost.Items.Add(EmptyLabel(filter.Length > 0 ? SidebarPresentation.NoMatches : "No conversations yet"));
            return;
        }

        _renderedOrder.Clear();
        _jumpHints.Clear();
        foreach (var (name, bucket) in BuildDateBuckets(sessions))
        {
            GroupsHost.Items.Add(GroupHeaderLabel(name));
            var rows = bucket.Select(ToInput).ToList();
            var chatSection = new SidebarSection(name, name, rows, EmptyParents) { Collapsible = false };
            foreach (var row in rows)
            {
                GroupsHost.Items.Add(BuildSessionRow(row, chatSection));
            }
        }
    }

    /// <summary>The Chat list nests nothing, so its sections share one empty parent map.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyParents =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private SidebarSessionInput ToInput(SessionSummary summary)
    {
        var groups = _groups;
        var pr = _services?.PullRequests.For(summary.WorkingDirectory);
        return new SidebarSessionInput(
            summary.Id,
            summary.Title,
            summary.WorkingDirectory,
            CreatedAt(summary),
            summary.UpdatedAt)
        {
            IsArchived = groups?.IsArchived(summary.Id) ?? false,
            IsPinned = groups?.IsPinned(summary.Id) ?? false,
            IsUnread = groups?.IsUnread(summary.Id) ?? false,
            ExplicitUnread = groups?.IsExplicitUnread(summary.Id) ?? false,
            IsRunning = _runningSessionIds.Contains(summary.Id),
            NeedsInput = _awaitingSessionIds.Contains(summary.Id),
            HasCompleted = summary.MessageCount > 0,
            GroupId = groups?.GroupOf(summary.Id)?.Id,
            ColorKey = groups?.ColorOf(summary.Id),
            PrState = pr?.State ?? PrDisplayState.None,
            PrNumber = pr?.Number,
            RoutineId = summary.RoutineId,
            SideParentId = groups?.SpawnParentOf(summary.Id),
        };
    }

    /// <summary>
    /// A session id opens with the moment it was created ("yyyyMMdd-HHmmss"), so
    /// "Date created" can sort a list the summary carries no creation date for.
    /// </summary>
    internal static DateTimeOffset CreatedAt(SessionSummary summary)
    {
        if (summary.Id.Length >= 15 &&
            DateTimeOffset.TryParseExact(summary.Id[..15], "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var created))
        {
            return created;
        }

        return summary.UpdatedAt;
    }

    private FrameworkElement BuildDropPlaceholder(string? groupId)
    {
        var label = new TextBlock
        {
            Text = SidebarPresentation.DragOrMove,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        var zone = new Border
        {
            Child = label,
            AllowDrop = true,
            Padding = new Thickness(8, 9, 8, 9),
            Margin = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
        };
        HookDropTarget(zone, groupId, highlight: true);
        return zone;
    }

    /// <summary>
    /// A custom group header's own menu. Beside renaming and deleting the group, it
    /// carries the reference's Move up / Move down, which is the other half of its
    /// section reordering (its `onMoveSection`, source `header_menu`).
    /// </summary>
    private ContextMenu BuildGroupMenu(SidebarSection section)
    {
        var menu = new ContextMenu();
        var groupId = section.GroupId!;
        var groups = _groups!;
        var index = groups.Data.Groups.FindIndex(g => g.Id == groupId);

        var up = new MenuItem { Header = "Move up", IsEnabled = index > 0 };
        up.Click += (_, _) =>
        {
            if (groups.MoveGroup(groupId, -1))
            {
                Render();
            }
        };
        menu.Items.Add(up);

        var down = new MenuItem { Header = "Move down", IsEnabled = index >= 0 && index < groups.Data.Groups.Count - 1 };
        down.Click += (_, _) =>
        {
            if (groups.MoveGroup(groupId, 1))
            {
                Render();
            }
        };
        menu.Items.Add(down);
        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = SessionDialogs.RenameGroupTitle };
        rename.Click += (_, _) =>
        {
            if (GroupNameDialog.Prompt(
                    Window.GetWindow(this), SessionDialogs.RenameGroupTitle, section.Title) is { } newName)
            {
                groups.RenameGroup(groupId, newName);
                Render();
            }
        };
        menu.Items.Add(rename);

        var delete = new MenuItem { Header = SessionDialogs.DeleteGroupMenuItem };
        delete.Click += (_, _) =>
        {
            // Deleting a group ungroups its sessions, which is what the
            // reference spells out before it does it.
            var count = groups.Data.Groups.FirstOrDefault(g => g.Id == groupId)?.SessionIds.Count ?? 0;
            if (!ConfirmDialog.Ask(
                    Window.GetWindow(this),
                    SessionDialogs.DeleteGroupTitle,
                    SessionDialogs.DeleteGroupBody(section.Title, count),
                    SessionDialogs.Delete,
                    focusCancel: true))
            {
                return;
            }

            groups.DeleteGroup(groupId);
            Render();
        };
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());

        var create = new MenuItem { Header = SidebarPresentation.NewGroup };
        create.Click += (_, _) => CreateGroupPrompt();
        menu.Items.Add(create);
        return menu;
    }

    /// <summary>The pointer left the sidebar's own bounds, which is what makes a drag a "drag out".</summary>
    private bool DraggedOutside(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return point.X > ActualWidth || point.X < 0;
    }

    private void OnRowClick(SidebarSessionInput session)
    {
        var modifiers = Keyboard.Modifiers;
        if (_groupByProject && modifiers.HasFlag(ModifierKeys.Alt))
        {
            SessionSplitRequested?.Invoke(this, session.Id);
            return;
        }

        // The reference opens a session in its own window on cmd+click; Ctrl is its
        // Windows spelling.
        if (_groupByProject && modifiers.HasFlag(ModifierKeys.Control))
        {
            SessionWindowRequested?.Invoke(this, session.Id);
            return;
        }

        if (_groupByProject && modifiers.HasFlag(ModifierKeys.Shift))
        {
            ExtendSelection(session.Id);
            return;
        }

        _selected.Clear();
        _selectionAnchor = session.Id;
        SessionSelected?.Invoke(this, session.Id);
    }

    /// <summary>Shift+click selects the run between the anchor and the row, for the bulk actions.</summary>
    private void ExtendSelection(string sessionId)
    {
        var order = _sessions.Select(static s => s.Id).ToList();
        var anchor = _selectionAnchor ?? _activeSessionId ?? sessionId;
        var from = order.IndexOf(anchor);
        var to = order.IndexOf(sessionId);
        if (from < 0 || to < 0)
        {
            _selected.Add(sessionId);
            Render();
            return;
        }

        var (lo, hi) = from <= to ? (from, to) : (to, from);
        for (var i = lo; i <= hi; i++)
        {
            _selected.Add(order[i]);
        }

        Render();
    }

    // ---- inline rename ----

    private FrameworkElement BuildRenameRow(SidebarSessionInput session)
    {
        var editor = new InlineRenameBox(
            session.Title,
            name =>
            {
                if (name != session.Title)
                {
                    SessionRenameRequested?.Invoke(this, (session.Id, name));
                }
            },
            () =>
            {
                _renamingSessionId = null;
                Render();
            })
        {
            Margin = new Thickness(2, 1, 2, 1),
        };
        return editor;
    }

    private ContextMenu BuildRowMenu(SidebarSessionInput session, bool archived)
    {
        if (!_groupByProject)
        {
            return BuildChatRowMenu(session);
        }

        var groups = _groups;
        var context = new SessionMenuContext
        {
            IsArchived = archived,
            IsPinned = session.IsPinned,
            HasReadState = _groupByProject,
            IsUnread = session.IsUnread,
            IsAwaiting = session.NeedsInput,
            CanPin = _groupByProject,
            CanSplitView = _groupByProject,
            CanOpenInNewWindow = true,
            HasLocalPath = session.WorkingDirectory.Length > 0,
            HasPr = session.PrNumber is not null,
            HasRoutine = session.RoutineId is not null,
            HideMoveToGroup = !_groupByProject || groups is null,
            CurrentGroupId = session.GroupId,
            Groups = groups is null ? [] : [.. groups.Data.Groups.Select(g => (g.Id, g.Name))],
            Colors = _groupByProject ? SessionMenu.ColorChoices(session.ColorKey) : null,
            OutputStyles = _groupByProject ? OutputStyleChoices(session.Id) : null,
        };

        return SessionMenuRenderer.Build(SessionMenuModel.ForSidebarRow(context), row => InvokeRow(session, row));
    }

    /// <summary>
    /// The Chat list's own row menu. Its labels are the reference's chat wording
    /// ("Rename chat", Star/Unstar, "Delete chat") rather than the session menu the
    /// Code list draws, which is a different surface with a different vocabulary.
    /// </summary>
    private ContextMenu BuildChatRowMenu(SidebarSessionInput session)
    {
        var menu = new ContextMenu();

        var rename = new MenuItem { Header = ChatListSections.RenameChat };
        rename.Click += (_, _) =>
        {
            if (InputDialog.Prompt(
                    Window.GetWindow(this), ChatListSections.RenameChat, session.Title, "Rename") is { } newTitle)
            {
                SessionRenameRequested?.Invoke(this, (session.Id, newTitle));
            }
        };
        menu.Items.Add(rename);

        if (_groups is not null)
        {
            var starred = session.IsPinned;
            var star = new MenuItem { Header = starred ? ChatListSections.Unstar : ChatListSections.Star };
            star.Click += (_, _) =>
            {
                _groups.SetPinned(session.Id, !starred);
                Render();
            };
            menu.Items.Add(star);
        }

        menu.Items.Add(new Separator());

        var export = new MenuItem { Header = "Export transcript…" };
        export.Click += (_, _) => SessionExportRequested?.Invoke(this, session.Id);
        menu.Items.Add(export);

        var delete = new MenuItem { Header = ChatListSections.DeleteChat };
        delete.Click += (_, _) => SessionDeleteRequested?.Invoke(this, session.Id);
        menu.Items.Add(delete);
        return menu;
    }

    private void InvokeRow(SidebarSessionInput session, SessionMenuRow row)
    {
        var groups = _groups;
        switch (row.Action)
        {
            case SessionMenuAction.Rename:
                _renamingSessionId = session.Id;
                Render();
                break;
            case SessionMenuAction.GoToRoutine when session.RoutineId is { } routineId:
                RoutineRequested?.Invoke(this, routineId);
                break;
            case SessionMenuAction.Pin:
            case SessionMenuAction.Unpin:
                groups?.SetPinned(session.Id, row.Action == SessionMenuAction.Pin);
                if (row.Action == SessionMenuAction.Pin && _services is { } pinServices &&
                    !pinServices.UiSettings.Current.DragPinHintShown)
                {
                    // The reference teaches the drag on the first pin from a menu
                    // (its `maybeShowDragPinHint`), once and then never again.
                    pinServices.UiSettings.Current.DragPinHintShown = true;
                    pinServices.UiSettings.Save();
                    _toasts?.Add(PinHints.DragPinTip);
                }

                if (row.Action == SessionMenuAction.Unpin && groups is not null)
                {
                    // The reference reports an unpin and offers it back; pinning
                    // is visible in the sidebar and says nothing.
                    _toasts?.AddWithAction(
                        ToastText.Unpinned(session.Title),
                        ToastText.Undo,
                        () =>
                        {
                            groups.SetPinned(session.Id, true);
                            Render();
                        });
                }

                Render();
                break;
            case SessionMenuAction.MarkRead:
                groups?.SetUnread(session.Id, false);
                Render();
                break;
            case SessionMenuAction.MarkUnread:
                groups?.SetExplicitUnread(session.Id, true);
                Render();
                break;
            case SessionMenuAction.EditOutputStyles when _services is { } styleServices:
                var editor = new OutputStyleEditor(styleServices, session.WorkingDirectory) { Owner = Window.GetWindow(this) };
                if (editor.ShowDialog() == true)
                {
                    styleServices.UiSettings.Current.SessionOutputStyles[session.Id] = editor.SavedName!;
                    styleServices.UiSettings.Save();
                    Render();
                }
                break;
            case SessionMenuAction.SetOutputStyle when _services is { } services:
                if (row.Argument is { Length: > 0 } styleName)
                {
                    services.UiSettings.Current.SessionOutputStyles[session.Id] = styleName;
                }
                else
                {
                    services.UiSettings.Current.SessionOutputStyles.Remove(session.Id);
                }

                services.UiSettings.Save();
                break;
            case SessionMenuAction.MarkCompleted:
                groups?.Dismiss(session.Id, DateTimeOffset.Now);
                Render();
                break;
            case SessionMenuAction.SetColor:
                groups?.SetColor(session.Id, string.IsNullOrEmpty(row.Argument) ? null : row.Argument);
                Render();
                break;
            case SessionMenuAction.MoveToGroup:
                groups?.MoveSession(session.Id, row.Argument);
                Render();
                break;
            case SessionMenuAction.NewGroup:
                CreateGroupPrompt([session.Id]);
                break;
            case SessionMenuAction.SplitView:
                SessionSplitRequested?.Invoke(this, session.Id);
                break;
            case SessionMenuAction.NewWindow:
                SessionWindowRequested?.Invoke(this, session.Id);
                break;
            case SessionMenuAction.OpenInVsCode:
                SessionMenu.OpenFolder(Window.GetWindow(this), session.WorkingDirectory, "code");
                break;
            case SessionMenuAction.OpenInExplorer:
                SessionMenu.OpenFolder(Window.GetWindow(this), session.WorkingDirectory, "explorer");
                break;
            case SessionMenuAction.CopyLink:
                CopyLink(session.Id);
                break;
            case SessionMenuAction.Export:
                SessionExportRequested?.Invoke(this, session.Id);
                break;
            case SessionMenuAction.Fork:
                _ = ForkAsync(session.Id);
                break;
            case SessionMenuAction.Archive:
            case SessionMenuAction.Unarchive:
                SetArchived(session.Id, row.Action == SessionMenuAction.Archive);
                break;
            case SessionMenuAction.Delete:
                SessionDeleteRequested?.Invoke(this, session.Id);
                break;
        }
    }

    /// <summary>Archives or unarchives one row, reporting a failure the way master does.</summary>
    private void SetArchived(string sessionId, bool archived)
    {
        try
        {
            _groups?.SetArchived(sessionId, archived);
            if (!archived)
            {
                _toasts?.AddSuccess(ToastText.Unarchived);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _toasts?.AddError(archived ? ToastText.ArchiveFailed : ToastText.UnarchiveFailed);
        }

        Render();
    }

    private static void CopyLink(string sessionId)
    {
        try
        {
            Clipboard.SetText(DeepLinks.ForSession(sessionId));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; the reference is silent here too.
        }
    }

    private async Task ForkAsync(string sessionId)
    {
        if (_services is null)
        {
            return;
        }

        var source = await _services.Sessions.LoadAsync(sessionId);
        if (source is null)
        {
            return;
        }

        var (fork, error) = await SessionActions.ForkAsync(_services, source);
        if (fork is null)
        {
            MessageBox.Show(Window.GetWindow(this), error, "Jarvis Code");
            return;
        }

        SessionSelected?.Invoke(this, fork.Id);
    }

    private void CreateGroupPrompt(IReadOnlyList<string>? members = null)
    {
        if (_groups is null ||
            GroupNameDialog.Prompt(Window.GetWindow(this), SessionDialogs.NewGroupTitle) is not { } name)
        {
            return;
        }

        var created = _groups.CreateGroup(name);
        foreach (var id in members ?? [])
        {
            _groups.MoveSession(id, created.Id);
        }

        Render();
    }

    private void HookDropTarget(FrameworkElement target, string? groupId, bool highlight = false)
    {
        target.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        if (highlight && target is Border border)
        {
            target.DragEnter += (_, e) =>
            {
                if (e.Data.GetDataPresent(DragFormat))
                {
                    border.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
                }
            };
            target.DragLeave += (_, _) => border.Background = Brushes.Transparent;
        }

        target.Drop += (_, e) =>
        {
            if (_groups is not null && e.Data.GetData(DragFormat) is string sessionId)
            {
                _groups.MoveSession(sessionId, groupId);
                Render();
            }

            e.Handled = true;
        };
    }

    private static ControlTemplate FlatButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Root");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static TextBlock EmptyLabel(string text)
    {
        var empty = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(8, 12, 8, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return empty;
    }

    private static TextBlock GroupHeaderLabel(string name)
    {
        var header = new TextBlock
        {
            Text = name,
            FontSize = 11.5,
            Margin = new Thickness(8, 10, 8, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return header;
    }

    private static IEnumerable<(string Name, List<SessionSummary> Sessions)> BuildDateBuckets(
        IReadOnlyList<SessionSummary> sessions)
    {
        var today = DateTimeOffset.Now.Date;
        var buckets = new List<(string, List<SessionSummary>)>
        {
            ("Today", []),
            ("Yesterday", []),
            ("Previous 7 days", []),
            ("Older", []),
        };
        foreach (var session in sessions.OrderByDescending(static s => s.UpdatedAt))
        {
            var age = (today - session.UpdatedAt.Date).Days;
            var index = age switch
            {
                <= 0 => 0,
                1 => 1,
                <= 7 => 2,
                _ => 3,
            };
            buckets[index].Item2.Add(session);
        }

        return buckets.Where(static b => b.Item2.Count > 0);
    }

    private void OnNewSessionClick(object sender, RoutedEventArgs e)
        => NewSessionRequested?.Invoke(this, EventArgs.Empty);

    private void OnCustomizeClick(object sender, RoutedEventArgs e)
        => CustomizeRequested?.Invoke(this, EventArgs.Empty);

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Visibility == Visibility.Visible)
        {
            SearchBox.Visibility = Visibility.Collapsed;
            SearchBox.Text = "";
        }
        else
        {
            SearchBox.Visibility = Visibility.Visible;
            SearchBox.Focus();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        => FilterChanged?.Invoke(this, SearchBox.Text);

    /// <summary>/resume — reveal the search box and put the caret in it.</summary>
    public void FocusSearch()
    {
        SearchBox.Visibility = Visibility.Visible;
        SearchBox.Focus();
    }
    /// <summary>Raised when the auto-updater banner is clicked.</summary>
    public event EventHandler? UpdateCardClicked;

    /// <summary>
    /// Draws the reference's auto-updater banner for the state the updater is in,
    /// or hides it. The two working states spin and cannot be clicked; the ready
    /// and failed cards are buttons.
    /// </summary>
    public void SetUpdateCard(Services.UpdateCard card)
    {
        if (card.Kind == Services.UpdateCardKind.None)
        {
            UpdateCard.Visibility = Visibility.Collapsed;
            UpdateCardSpinner.IsSpinning = false;
            return;
        }

        UpdateCard.Visibility = Visibility.Visible;
        UpdateCardTitle.Text = card.Title;
        UpdateCardDetail.Text = card.Detail ?? "";
        UpdateCardDetail.Visibility = card.Detail is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateCardSpinner.IsSpinning =
            card.Kind is Services.UpdateCardKind.Checking or Services.UpdateCardKind.Downloading;
        UpdateCardButton.IsEnabled = card.Clickable;
        UpdateCardButton.Cursor = card.Clickable ? System.Windows.Input.Cursors.Hand : null;
    }

    private void OnUpdateCardClick(object sender, RoutedEventArgs e) =>
        UpdateCardClicked?.Invoke(this, EventArgs.Empty);
}
