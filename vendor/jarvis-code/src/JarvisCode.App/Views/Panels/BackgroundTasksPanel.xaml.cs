using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The Background tasks pane, ported from Claude Code Desktop 1.40609.0.0: a
/// Running and a Finished section of cards, each card a title with a stop
/// button, a kind · status · elapsed line, a detail line, and — for rows with
/// nothing better to open — an expandable body holding the command and the live
/// output. Rows are kept and updated in place across refreshes so expansion,
/// selection and scroll position survive the one-second tick.
/// </summary>
public partial class BackgroundTasksPanel : UserControl
{
    private const double MetaFontSize = 11.5;

    /// <summary>The pane's title while the task list is showing.</summary>
    private const string TaskListTitle = "Background tasks";

    private readonly Dictionary<string, RowVisual> _visuals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>Per-phase open state, keyed by "{rowId}#phase{index}".</summary>
    private readonly Dictionary<string, bool> _phaseExpanded = new(StringComparer.Ordinal);

    /// <summary>A finished row left the pane, so whatever the host kept for it can go.</summary>
    public event EventHandler<string>? TaskCleared;
    private readonly HashSet<string> _cleared = new(StringComparer.Ordinal);

    /// <summary>
    /// Rows this pane asked to stop, kept only until the stop lands so the
    /// screen reader can be told. What the row *looks* like while stopping comes
    /// from the host through <see cref="BackgroundRow.StatusOverride"/>.
    /// </summary>
    private readonly Dictionary<string, BackgroundRowKind> _awaitingStop = new(StringComparer.Ordinal);

    /// <summary>Whether each row's output box was parked at the bottom when last seen.</summary>
    private readonly Dictionary<string, bool> _stickToBottom = new(StringComparer.Ordinal);

    private readonly SectionVisual _running;
    private readonly SectionVisual _finished;
    private readonly DispatcherTimer _liveRegionTimer;

    private IReadOnlyList<BackgroundRow> _rows = [];
    private bool _finishedExpanded;
    private string? _pendingFocusId;

    public BackgroundTasksPanel()
    {
        InitializeComponent();
        _running = BuildSection("Running", collapsible: false);
        _finished = BuildSection("Finished", collapsible: true);
        Sections.Children.Add(_running.Root);
        Sections.Children.Add(_finished.Root);
        EmptyStateText.Text = BackgroundTaskPresentation.EmptyStateText;
        _liveRegionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _liveRegionTimer.Tick += (_, _) =>
        {
            _liveRegionTimer.Stop();
            LiveRegion.Text = "";
        };
    }

    /// <summary>A row's stop button was pressed; the host does the stopping.</summary>
    public event EventHandler<string>? StopRequested;

    /// <summary>
    /// An agent row's "View transcript" was pressed. The row's own title rides
    /// along, because that is what the reference titles the pushed view with.
    /// </summary>
    public event EventHandler<(string CallId, string Title)>? ViewTranscriptRequested;

    /// <summary>The call id of the subagent view on top of the stack, if any.</summary>
    public string? SubagentCallId { get; private set; }

    /// <summary>
    /// Pushes a subagent's transcript over the task list, as the reference pushes
    /// a pane view: the header takes the agent's description and grows a back
    /// arrow, and the list comes back when that arrow is pressed. Pushing again
    /// replaces what is showing, so two agents never stack up.
    /// </summary>
    public void PushSubagentView(
        string callId, string description, FrameworkElement content, bool takeFocus)
    {
        SubagentCallId = callId;
        SubagentHost.Children.Clear();
        SubagentHost.Children.Add(content);
        SubagentHost.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        PanelTitle.Text = string.IsNullOrWhiteSpace(description) ? TaskListTitle : description;
        BackButton.Visibility = Visibility.Visible;
        if (takeFocus)
        {
            BackButton.Focus();
        }
    }

    /// <summary>Returns to the task list; harmless when nothing was pushed.</summary>
    public void PopSubagentView()
    {
        if (SubagentCallId is null)
        {
            return;
        }

        SubagentCallId = null;
        SubagentHost.Children.Clear();
        SubagentHost.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        PanelTitle.Text = TaskListTitle;
        BackButton.Visibility = Visibility.Collapsed;
        // The list was not rendered while the view covered it, and a row may have
        // finished, been cleared or gained output in the meantime.
        Render();
    }

    private void OnBack(object sender, RoutedEventArgs e) => PopSubagentView();

    /// <summary>The Finished section was collapsed or expanded, for the host to persist.</summary>
    public event EventHandler? FinishedExpandedChanged;

    /// <summary>
    /// The reference keeps this per session and starts collapsed; the host loads
    /// and stores it, so switching sessions restores what that session had.
    /// </summary>
    public bool FinishedExpanded
    {
        get => _finishedExpanded;
        set
        {
            if (_finishedExpanded == value)
            {
                return;
            }

            _finishedExpanded = value;
            Render();
        }
    }

    /// <summary>Rebuilds the pane from the current rows, reusing the visuals it already has.</summary>
    public void Update(IReadOnlyList<BackgroundRow> rows)
    {
        _rows = rows;

        // A cleared row that starts running again comes back, the way the
        // reference drops it from the hidden set the moment its status flips.
        foreach (var row in rows)
        {
            if (row.IsRunning)
            {
                _cleared.Remove(row.Id);
            }
        }

        // A stop that has landed (the row is gone, or no longer running) leaves
        // the pending set, and servers and loops announce it.
        var serverStopped = false;
        var loopStopped = false;
        foreach (var (id, kind) in _awaitingStop.ToList())
        {
            var row = rows.FirstOrDefault(r => r.Id == id);
            if (row is not null && row.IsRunning)
            {
                continue;
            }

            _awaitingStop.Remove(id);
            serverStopped |= kind == BackgroundRowKind.Preview;
            loopStopped |= kind == BackgroundRowKind.Loop;
        }

        Announce(serverStopped, loopStopped);
        Render();
    }

    /// <summary>
    /// Opens the pane at one row: expands it (and the Finished section when it
    /// lives there) and scrolls it to the middle — the reference's
    /// openTasksPaneAtTask.
    /// </summary>
    public void FocusTask(string id)
    {
        // Focusing a row means showing the list, so a pushed view steps aside.
        PopSubagentView();
        _cleared.Remove(id);
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (row is { Expandable: true })
        {
            _expanded.Add(id);
        }

        if (row is not null && !row.IsRunning && !_finishedExpanded)
        {
            _finishedExpanded = true;
            FinishedExpandedChanged?.Invoke(this, EventArgs.Empty);
        }

        _pendingFocusId = id;
        Render();
    }

    private void Render()
    {
        // A pushed view owns the pane; the list is rebuilt when it is popped, so
        // the tick does not repaint what nobody can see.
        if (SubagentCallId is not null)
        {
            return;
        }

        var visible = _rows.Where(r => !_cleared.Contains(r.Id)).ToList();
        var running = BackgroundTaskPresentation.SortRunning(visible.Where(r => r.IsRunning));
        var finished = BackgroundTaskPresentation.SortFinished(visible.Where(r => !r.IsRunning));

        var empty = running.Count == 0 && finished.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        RenderSection(_running, running, collapsed: false);
        RenderSection(_finished, finished, collapsed: !_finishedExpanded);

        foreach (var id in _visuals.Keys.ToList())
        {
            if (!visible.Any(r => r.Id == id))
            {
                _visuals.Remove(id);
                _stickToBottom.Remove(id);
            }
        }

        if (_pendingFocusId is { } focusId)
        {
            _pendingFocusId = null;
            if (_visuals.TryGetValue(focusId, out var target))
            {
                // The row has to be laid out before it can be centred.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToCenter(target.Card));
            }
        }
    }

    private void ScrollToCenter(FrameworkElement element)
    {
        if (!element.IsDescendantOf(Scroller) || Scroller.ViewportHeight <= 0)
        {
            return;
        }

        var top = element.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y + Scroller.VerticalOffset;
        Scroller.ScrollToVerticalOffset(Math.Max(0, top - (Scroller.ViewportHeight - element.ActualHeight) / 2));
    }

    private void RenderSection(SectionVisual section, IReadOnlyList<BackgroundRow> rows, bool collapsed)
    {
        // The reference hides a section with nothing in it rather than showing
        // an empty heading.
        section.Root.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (rows.Count == 0)
        {
            section.Rows.Children.Clear();
            return;
        }

        section.Count.Text = rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        if (section.Caret is not null)
        {
            section.Caret.Data = (Geometry)FindResource(collapsed ? "DisclosureRightGlyph" : "DisclosureDownGlyph");
            AutomationProperties.SetHelpText(section.Header, collapsed ? "Collapsed" : "Expanded");
        }

        section.Rows.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (collapsed)
        {
            return;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var visual = GetOrCreateVisual(row);
            Apply(visual, row);
            if (visual.Card.Parent is StackPanel host && !ReferenceEquals(host, section.Rows))
            {
                host.Children.Remove(visual.Card);
            }

            var at = section.Rows.Children.IndexOf(visual.Card);
            if (at != i)
            {
                if (at >= 0)
                {
                    section.Rows.Children.RemoveAt(at);
                }

                section.Rows.Children.Insert(Math.Min(i, section.Rows.Children.Count), visual.Card);
            }
        }

        while (section.Rows.Children.Count > rows.Count)
        {
            section.Rows.Children.RemoveAt(section.Rows.Children.Count - 1);
        }
    }

    // ---- section chrome ----

    private sealed class SectionVisual
    {
        public required StackPanel Root { get; init; }

        public required FrameworkElement Header { get; init; }

        public required TextBlock Count { get; init; }

        public required StackPanel Rows { get; init; }

        public Path? Caret { get; init; }
    }

    private SectionVisual BuildSection(string heading, bool collapsible)
    {
        var root = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition());
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = heading,
            FontSize = MetaFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        var count = new TextBlock
        {
            FontSize = MetaFontSize,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");

        FrameworkElement header;
        Path? caret = null;
        if (collapsible)
        {
            caret = NewCaret(new Thickness(5, 1, 0, 0), VerticalAlignment.Center);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(label);
            content.Children.Add(count);
            content.Children.Add(caret);

            var button = new Button
            {
                Content = content,
                Padding = new Thickness(2, 1, 2, 1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
            };
            button.SetResourceReference(StyleProperty, "RowButton");
            AutomationProperties.SetName(button, heading);
            button.Click += (_, _) =>
            {
                _finishedExpanded = !_finishedExpanded;
                FinishedExpandedChanged?.Invoke(this, EventArgs.Empty);
                Render();
            };
            header = button;

            var clear = new Button
            {
                Content = new TextBlock { Text = "Clear", FontSize = MetaFontSize },
                Padding = new Thickness(7, 2, 7, 2),
                Cursor = Cursors.Hand,
            };
            clear.SetResourceReference(StyleProperty, "RowButton");
            AutomationProperties.SetName(clear, "Clear finished background tasks");
            clear.Click += (_, _) =>
            {
                foreach (var row in _rows.Where(static r => !r.IsRunning))
                {
                    _cleared.Add(row.Id);
                    foreach (var key in _phaseExpanded.Keys
                                 .Where(k => k.StartsWith(row.Id + "#phase", StringComparison.Ordinal))
                                 .ToList())
                    {
                        _phaseExpanded.Remove(key);
                    }

                    TaskCleared?.Invoke(this, row.Id);
                }

                Render();
            };
            Grid.SetColumn(clear, 1);
            headerRow.Children.Add(clear);
        }
        else
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(label);
            count.Visibility = Visibility.Collapsed;
            content.Children.Add(count);
            header = content;
        }

        headerRow.Children.Insert(0, header);
        var rows = new StackPanel();
        root.Children.Add(headerRow);
        root.Children.Add(rows);
        return new SectionVisual { Root = root, Header = header, Count = count, Rows = rows, Caret = caret };
    }

    private Path NewCaret(Thickness margin, VerticalAlignment alignment)
    {
        var caret = new Path
        {
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = margin,
            VerticalAlignment = alignment,
            Data = (Geometry)FindResource("DisclosureRightGlyph"),
        };
        caret.SetResourceReference(Shape.StrokeProperty, "Text400Brush");
        return caret;
    }

    // ---- rows ----

    private sealed class RowVisual
    {
        public required Border Card { get; init; }

        public required TextBlock Title { get; init; }

        public required TextBlock Kind { get; init; }

        public required TextBlock Status { get; init; }

        public required TextBlock Elapsed { get; init; }

        public required StackPanel Details { get; init; }

        public required Button ViewTranscript { get; init; }

        public required Button Stop { get; init; }

        public required StackPanel Body { get; init; }

        public required Border CommandBox { get; init; }

        public required CodeText Command { get; init; }

        public required Border OutputBox { get; init; }

        public required TextBox Output { get; init; }

        public required TextBlock OutputNotice { get; init; }

        public required TextBlock Summary { get; init; }

        /// <summary>The "Phases" heading and its list; only workflow rows show it.</summary>
        public required StackPanel PhaseSection { get; init; }

        public required StackPanel PhaseList { get; init; }

        public ToggleButton? Toggle { get; init; }

        public Path? Caret { get; init; }

        public bool ShimmerRunning { get; set; }

        /// <summary>What the phase list was last drawn from; null when it has none.</summary>
        public string? PhaseSignature { get; set; }
    }

    private RowVisual GetOrCreateVisual(BackgroundRow row)
    {
        // A row that gains or loses its expander is rebuilt; everything else is
        // updated in place so the open card the user is reading does not blink.
        if (_visuals.TryGetValue(row.Id, out var existing) && (existing.Toggle is not null) == row.Expandable)
        {
            return existing;
        }

        var visual = BuildRow(row.Id, row.Expandable);
        _visuals[row.Id] = visual;
        return visual;
    }

    private RowVisual BuildRow(string id, bool expandable)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(10),
        };
        card.SetResourceReference(BackgroundProperty, "HoverOverlayBrush");
        card.MouseEnter += (_, _) => card.SetResourceReference(BackgroundProperty, "SelectedOverlayBrush");
        card.MouseLeave += (_, _) => card.SetResourceReference(BackgroundProperty, "HoverOverlayBrush");

        var stack = new StackPanel();
        card.Child = stack;

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        ToggleButton? toggle = null;
        Path? caret = null;
        if (expandable)
        {
            caret = NewCaret(new Thickness(8, 3, 0, 0), VerticalAlignment.Top);
            caret.Opacity = 0;
            var showCaret = caret;
            card.MouseEnter += (_, _) => showCaret.Opacity = 1;
            card.MouseLeave += (_, _) => showCaret.Opacity = card.IsKeyboardFocusWithin ? 1 : 0;
            card.IsKeyboardFocusWithinChanged += (_, e) =>
                showCaret.Opacity = (bool)e.NewValue || card.IsMouseOver ? 1 : 0;
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(title);
            Grid.SetColumn(caret, 1);
            content.Children.Add(caret);

            toggle = new ToggleButton
            {
                Content = content,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
            };
            toggle.SetResourceReference(StyleProperty, "RowButton");
            toggle.Checked += (_, _) => OnToggled(id, true);
            toggle.Unchecked += (_, _) => OnToggled(id, false);
            titleRow.Children.Add(toggle);
        }
        else
        {
            titleRow.Children.Add(title);
        }

        var stopIcon = new Path
        {
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            Data = (Geometry)FindResource("StopGlyph"),
        };
        stopIcon.SetResourceReference(Shape.FillProperty, "Text300Brush");
        var stop = new Button
        {
            Content = stopIcon,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
        };
        stop.SetResourceReference(StyleProperty, "RowButton");
        stop.Click += (_, _) =>
        {
            if (_rows.FirstOrDefault(r => r.Id == id) is { } row)
            {
                _awaitingStop[id] = row.Kind;
            }

            StopRequested?.Invoke(this, id);
            Render();
        };
        Grid.SetColumn(stop, 1);
        titleRow.Children.Add(stop);
        stack.Children.Add(titleRow);

        var metaOne = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var kind = new TextBlock { FontSize = MetaFontSize };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        var status = new TextBlock { FontSize = MetaFontSize, Margin = new Thickness(8, 0, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        var elapsed = new TextBlock { FontSize = MetaFontSize, Margin = new Thickness(8, 0, 0, 0) };
        elapsed.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        metaOne.Children.Add(kind);
        metaOne.Children.Add(status);
        metaOne.Children.Add(elapsed);
        stack.Children.Add(metaOne);

        var details = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var viewTranscript = new Button
        {
            Content = new TextBlock { Text = "View transcript", FontSize = MetaFontSize },
            Padding = new Thickness(4, 1, 4, 1),
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
        };
        viewTranscript.SetResourceReference(StyleProperty, "RowButton");
        viewTranscript.SetResourceReference(ForegroundProperty, "Accent000Brush");
        viewTranscript.Click += (_, _) =>
        {
            if (_rows.FirstOrDefault(r => r.Id == id) is { TranscriptCallId: { } callId } row)
            {
                ViewTranscriptRequested?.Invoke(this, (callId, row.Title));
            }
        };
        details.Children.Add(viewTranscript);
        stack.Children.Add(details);

        var command = new CodeText { Language = "bash", Wrap = true };
        var commandBox = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = command,
            Visibility = Visibility.Collapsed,
        };
        commandBox.SetResourceReference(BackgroundProperty, "SelectedOverlayBrush");

        var output = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = MetaFontSize,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 200,
        };
        output.SetResourceReference(FontFamilyProperty, "MonoFontFamily");
        output.SetResourceReference(ForegroundProperty, "Text200Brush");
        output.SetResourceReference(TextBoxBase.SelectionBrushProperty, "SelectionBrush");
        var outputBox = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Child = output,
            Visibility = Visibility.Collapsed,
        };
        outputBox.SetResourceReference(BackgroundProperty, "SelectedOverlayBrush");

        var outputNotice = new TextBlock { FontSize = MetaFontSize, Visibility = Visibility.Collapsed };
        outputNotice.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        var summary = new TextBlock
        {
            FontSize = MetaFontSize,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");

        var phaseHeading = new TextBlock
        {
            Text = JarvisCode.Core.Agent.WorkflowPhaseLabels.Phases,
            FontSize = MetaFontSize,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 4),
        };
        phaseHeading.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        var phaseList = new StackPanel();
        var phaseSection = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        phaseSection.Children.Add(phaseHeading);
        phaseSection.Children.Add(phaseList);

        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        body.Children.Add(phaseSection);
        body.Children.Add(commandBox);
        body.Children.Add(outputBox);
        body.Children.Add(outputNotice);
        body.Children.Add(summary);
        stack.Children.Add(body);

        return new RowVisual
        {
            Card = card,
            Title = title,
            Kind = kind,
            Status = status,
            Elapsed = elapsed,
            Details = details,
            ViewTranscript = viewTranscript,
            Stop = stop,
            Body = body,
            CommandBox = commandBox,
            Command = command,
            OutputBox = outputBox,
            Output = output,
            OutputNotice = outputNotice,
            Summary = summary,
            PhaseSection = phaseSection,
            PhaseList = phaseList,
            Toggle = toggle,
            Caret = caret,
        };
    }

    /// <summary>
    /// The reference's phase list: one collapsible row per phase — title,
    /// "{done}/{total}" once it has agents, a caret — opening onto its agent
    /// table (Agent · Model · Tokens · Time) or the empty notice for the run's
    /// own status. A phase with agents opens by itself while the run is live.
    /// </summary>
    private void RenderPhases(RowVisual visual, BackgroundRow row)
    {
        if (row.Phases is not { Phases.Count: > 0 } view)
        {
            visual.PhaseSection.Visibility = Visibility.Collapsed;
            visual.PhaseList.Children.Clear();
            visual.PhaseSignature = null;
            return;
        }

        var signature = PhaseSignature(row, view);
        visual.PhaseSection.Visibility = Visibility.Visible;
        if (visual.PhaseSignature == signature)
        {
            return;
        }

        visual.PhaseSignature = signature;
        visual.PhaseList.Children.Clear();
        foreach (var phase in view.Phases)
        {
            var key = $"{row.Id}#phase{phase.Index}";
            var open = _phaseExpanded.TryGetValue(key, out var stored)
                ? stored
                : row.IsRunning && phase.Counts.Total > 0;

            var header = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

            var title = new TextBlock
            {
                Text = phase.Title,
                FontSize = MetaFontSize,
                TextWrapping = TextWrapping.Wrap,
            };
            title.SetResourceReference(
                TextBlock.ForegroundProperty,
                phase.Status == Core.Agent.WorkflowPhaseStatus.Error ? "DangerBrush" :
                phase.Status == Core.Agent.WorkflowPhaseStatus.Pending ? "Text400Brush" : "Text200Brush");
            Grid.SetColumn(title, 0);
            header.Children.Add(title);

            if (phase.Counts.Total > 0)
            {
                var tally = new TextBlock
                {
                    Text = Core.Agent.WorkflowPhaseLabels.Tally(phase.Counts),
                    FontSize = MetaFontSize,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                tally.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
                Grid.SetColumn(tally, 1);
                header.Children.Add(tally);
            }

            var caret = new TextBlock
            {
                Text = open ? "\u25be" : "\u25b8",
                FontSize = MetaFontSize,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            caret.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            Grid.SetColumn(caret, 2);
            header.Children.Add(caret);

            var button = new Button
            {
                Content = header,
                Padding = new Thickness(6, 3, 6, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            button.SetResourceReference(StyleProperty, "RowButton");
            AutomationProperties.SetName(button, Core.Agent.WorkflowPhaseLabels.AccessibleName(phase));
            var rowId = row.Id;
            var phaseIndex = phase.Index;
            var wasOpen = open;
            button.Click += (_, _) =>
            {
                _phaseExpanded[$"{rowId}#phase{phaseIndex}"] = !wasOpen;
                Render();
            };
            visual.PhaseList.Children.Add(button);

            if (!open)
            {
                continue;
            }

            visual.PhaseList.Children.Add(phase.Agents.Count == 0
                ? PhaseEmptyNotice(row)
                : PhaseAgentTable(phase));
        }
    }

    /// <summary>
    /// Everything the phase list draws, so a tick that changed nothing redraws
    /// nothing. The agents ride it too — a token or a state moving must repaint.
    /// </summary>
    private string PhaseSignature(BackgroundRow row, Core.Agent.WorkflowProgressView view)
    {
        var text = new System.Text.StringBuilder();
        text.Append(row.RunStatus).Append('|');
        foreach (var phase in view.Phases)
        {
            var open = _phaseExpanded.TryGetValue($"{row.Id}#phase{phase.Index}", out var stored)
                ? stored
                : row.IsRunning && phase.Counts.Total > 0;
            text.Append(phase.Index).Append(':').Append(phase.Title).Append(':')
                .Append(phase.Status).Append(':').Append(open ? '+' : '-').Append(':')
                .Append(phase.Counts.Done).Append('/').Append(phase.Counts.Total).Append(';');
            if (!open)
            {
                continue;
            }

            foreach (var agent in phase.Agents)
            {
                text.Append(agent.Index).Append(',').Append(agent.Label).Append(',')
                    .Append(agent.Model).Append(',').Append(agent.Tokens).Append(',')
                    .Append(agent.Duration?.Ticks ?? 0).Append(' ');
            }
        }

        return text.ToString();
    }

    private TextBlock PhaseEmptyNotice(BackgroundRow row)
    {
        var notice = new TextBlock
        {
            Text = Core.Agent.WorkflowPhaseLabels.NoAgents(row.RunStatus),
            FontSize = MetaFontSize,
            Margin = new Thickness(12, 0, 0, 4),
        };
        notice.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        return notice;
    }

    private Grid PhaseAgentTable(Core.Agent.WorkflowPhaseGroup phase)
    {
        var table = new Grid { Margin = new Thickness(12, 2, 0, 6) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Cell(string text, int column, int rowIndex, bool muted, bool rightAligned = false)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = MetaFontSize,
                Margin = new Thickness(column == 0 ? 0 : 12, 1, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = rightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Text400Brush" : "Text200Brush");
            Grid.SetColumn(block, column);
            Grid.SetRow(block, rowIndex);
            table.Children.Add(block);
        }

        Cell(Core.Agent.WorkflowPhaseLabels.AgentColumn, 0, 0, muted: true);
        Cell(Core.Agent.WorkflowPhaseLabels.ModelColumn, 1, 0, muted: true);
        Cell(Core.Agent.WorkflowPhaseLabels.TokensColumn, 2, 0, muted: true);
        Cell(Core.Agent.WorkflowPhaseLabels.TimeColumn, 3, 0, muted: true, rightAligned: true);

        var line = 1;
        foreach (var agent in phase.Agents)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(agent.Label, 0, line, muted: false);
            Cell(agent.Model ?? "\u2014", 1, line, muted: true);
            Cell(agent.Tokens > 0 ? Controls.TurnStatusLine.FormatTokenCount(agent.Tokens) : "\u2014", 2, line, muted: true);
            Cell(
                agent.Duration is { } duration
                    ? BackgroundTaskPresentation.FormatDuration(duration)
                    : "\u2014",
                3, line, muted: true, rightAligned: true);
            line++;
        }

        return table;
    }

    private void OnToggled(string id, bool expanded)
    {
        if (expanded)
        {
            _expanded.Add(id);
        }
        else
        {
            _expanded.Remove(id);
            _stickToBottom.Remove(id);
        }

        Render();
    }

    private void Apply(RowVisual visual, BackgroundRow row)
    {
        visual.Title.Text = row.Title;
        visual.Title.SetResourceReference(TextBlock.ForegroundProperty,
            row.IsRunning ? "Text100Brush" : "Text400Brush");
        AutomationProperties.SetName(
            (FrameworkElement?)visual.Toggle ?? visual.Title, $"Background task: {row.Title}");
        ApplyShimmer(visual, row.IsRunning);

        visual.Kind.Text = BackgroundTaskPresentation.KindLabel(row.Kind);

        // The reference names the state only once a row has left Running; the
        // overrides ("Starting…", "Stop requested…") show in its place.
        var statusText = row.StatusOverride ??
                         (row.IsRunning ? null : BackgroundTaskPresentation.StatusLabel(row.Status));
        visual.Status.Text = statusText ?? "";
        visual.Status.Visibility = statusText is null ? Visibility.Collapsed : Visibility.Visible;
        visual.Status.SetResourceReference(TextBlock.ForegroundProperty,
            row.Status == BackgroundRowStatus.Failed && row.StatusOverride is null
                ? "Danger000Brush"
                : "Text400Brush");

        var elapsed = row.IsRunning
            ? row.StartedAt is { } startedAt ? DateTimeOffset.Now - startedAt : (TimeSpan?)null
            : row.StartedAt is { } from && row.CompletedAt is { } to ? to - from : null;
        visual.Elapsed.Text = elapsed is { } span ? BackgroundTaskPresentation.FormatDuration(span) : "";
        visual.Elapsed.Visibility = elapsed is null ? Visibility.Collapsed : Visibility.Visible;

        ApplyDetails(visual, row);

        visual.Stop.Visibility = row.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        visual.Stop.IsEnabled = row.CanStop;
        AutomationProperties.SetName(visual.Stop, row.Kind switch
        {
            BackgroundRowKind.Preview => row.CanStop ? "Stop this server" : "Stopping server…",
            BackgroundRowKind.Loop => "Stop this loop",
            _ => "Stop this task",
        });

        var expanded = row.Expandable && _expanded.Contains(row.Id);
        if (visual.Toggle is not null)
        {
            visual.Toggle.IsChecked = expanded;
            visual.Caret!.Data = (Geometry)FindResource(expanded ? "DisclosureDownGlyph" : "DisclosureRightGlyph");
        }

        visual.Body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            ApplyBody(visual, row);
        }
    }

    private void ApplyDetails(RowVisual visual, BackgroundRow row)
    {
        var chips = row.Details;
        var current = visual.Details.Children.OfType<TextBlock>().ToList();
        if (current.Count != chips.Count || current.Where((t, i) => t.Text != chips[i]).Any())
        {
            foreach (var chip in current)
            {
                visual.Details.Children.Remove(chip);
            }

            for (var i = 0; i < chips.Count; i++)
            {
                var text = new TextBlock
                {
                    Text = chips[i],
                    FontSize = MetaFontSize,
                    Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
                visual.Details.Children.Insert(i, text);
            }
        }

        visual.ViewTranscript.Visibility = row.TranscriptCallId is null ? Visibility.Collapsed : Visibility.Visible;
        visual.ViewTranscript.Margin = new Thickness(chips.Count == 0 ? 0 : 8, 0, 0, 0);
        visual.Details.Visibility = chips.Count == 0 && row.TranscriptCallId is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ApplyBody(RowVisual visual, BackgroundRow row)
    {
        RenderPhases(visual, row);

        var showCommand = !string.IsNullOrWhiteSpace(row.Command) && row.Command != row.Title;
        visual.CommandBox.Visibility = showCommand ? Visibility.Visible : Visibility.Collapsed;
        if (showCommand && visual.Command.Code != row.Command)
        {
            visual.Command.Code = row.Command!;
            AutomationProperties.SetName(visual.CommandBox, $"Command for {row.Title}");
        }

        var summary = row.Summary;
        var hasSummary = !string.IsNullOrWhiteSpace(summary) && summary != row.Title;
        var output = string.IsNullOrEmpty(row.Output)
            ? null
            : BackgroundTaskPresentation.ShapeOutput(row.Output, row.OutputIsTail);
        var hasOutput = !string.IsNullOrWhiteSpace(output);

        visual.OutputBox.Visibility = hasOutput ? Visibility.Visible : Visibility.Collapsed;
        if (hasOutput && visual.Output.Text != output)
        {
            var stick = !_stickToBottom.TryGetValue(row.Id, out var value) || value;
            visual.Output.Text = output;
            AutomationProperties.SetName(visual.OutputBox, $"Output for {row.Title}");
            if (stick && visual.Output.SelectionLength == 0)
            {
                visual.Output.ScrollToEnd();
            }
        }

        if (hasOutput)
        {
            _stickToBottom[row.Id] = visual.Output.VerticalOffset >=
                visual.Output.ExtentHeight - visual.Output.ViewportHeight - 16;
        }

        visual.Summary.Visibility = !hasOutput && hasSummary ? Visibility.Visible : Visibility.Collapsed;
        visual.Summary.Text = hasSummary ? summary : "";

        // The reference's remaining output states: nothing captured on a settled
        // row, and "No output yet" while one is still running.
        var notice = hasOutput || hasSummary
            ? null
            : row.IsRunning
                ? BackgroundTaskPresentation.NoOutputYet
                : BackgroundTaskPresentation.NoOutputCaptured;
        visual.OutputNotice.Text = notice ?? "";
        visual.OutputNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void ApplyShimmer(RowVisual visual, bool running)
    {
        if (visual.ShimmerRunning == running)
        {
            return;
        }

        visual.ShimmerRunning = running;
        if (!running)
        {
            visual.Title.BeginAnimation(OpacityProperty, null);
            visual.Title.Opacity = 1;
            return;
        }

        visual.Title.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    /// <summary>The reference announces a landed stop, then clears the message after five seconds.</summary>
    private void Announce(bool serverStopped, bool loopStopped)
    {
        var message = serverStopped && loopStopped ? "Preview server stopped. Loop stopped"
            : serverStopped ? "Preview server stopped"
            : loopStopped ? "Loop stopped"
            : null;
        if (message is null)
        {
            return;
        }

        LiveRegion.Text = message;
        UIElementAutomationPeer.FromElement(LiveRegion)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _liveRegionTimer.Stop();
        _liveRegionTimer.Start();
    }
}
