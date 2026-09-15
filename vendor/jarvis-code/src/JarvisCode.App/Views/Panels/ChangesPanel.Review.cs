using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The diff pane's review half: the "Expand diff" dialog with its Unified/Split control,
/// the commit selector, the findings stepper the review's ReportFindings fills, and the
/// line annotations that reach the composer. All ported from the reference's diff frame
/// (ion-dist chunk c360a9e1c-DUoNQd2W.js — its pC expand trigger, iC dialog, fC stepper
/// and gC commit selector).
/// </summary>
public partial class ChangesPanel
{
    private string _sessionId = "";
    private int _findingIndex;
    private string? _selectedCommit;
    private IReadOnlyList<(string Sha, string Subject)> _commits = [];
    private ReviewAnnotationStore? _annotations;
    private ReviewAnnotation? _activeAnnotation;
    private bool _canApplyReview;
    private bool _canRerunReview;

    /// <summary>Whether the expanded dialog lays the diff out side by side.</summary>
    public bool ExpandedIsSplit { get; private set; }

    /// <summary>The composer takes an annotation as a context chip.</summary>
    public event EventHandler<string>? AnnotationRequested;

    /// <summary>"Apply fixes" and "Re-run review" send a message on the session's behalf.</summary>
    public event EventHandler<string>? ReviewMessageRequested;

    /// <summary>Binds the pane to a session, so the stepper reads that session's findings.</summary>
    public void BindSession(string sessionId, string? profileRoot = null)
    {
        if (_sessionId == sessionId && (_annotations is not null || profileRoot is null))
        {
            return;
        }

        _sessionId = sessionId;
        _annotations = profileRoot is null ? null : new ReviewAnnotationStore(System.IO.Path.Combine(profileRoot,
            "review-annotations", ReviewAnnotationStore.Hash(sessionId) + ".json"));
        _activeAnnotation = _annotations?.Load().LastOrDefault();
        _findingIndex = 0;
        ReviewFindingsStore.Changed -= OnFindingsChanged;
        ReviewFindingsStore.Changed += OnFindingsChanged;
        RefreshFindings();
    }

    private void OnFindingsChanged(object? sender, string sessionId)
    {
        if (sessionId == _sessionId)
        {
            Dispatcher.InvokeAsync(RefreshFindings);
        }
    }

    private void RefreshFindings()
    {
        var findings = ReviewFindingsStore.Get(_sessionId);
        if (findings.Count == 0)
        {
            FindingsBar.Visibility = Visibility.Collapsed;
            return;
        }

        _findingIndex = Math.Clamp(_findingIndex, 0, findings.Count - 1);
        FindingsBar.Visibility = Visibility.Visible;
        FindingsStatus.Text = ReviewFindingsPresentation.Status(findings, _findingIndex);

        var arrows = ReviewFindingsPresentation.ShowArrows(findings);
        PreviousFindingButton.Visibility = arrows ? Visibility.Visible : Visibility.Collapsed;
        NextFindingButton.Visibility = arrows ? Visibility.Visible : Visibility.Collapsed;
        PreviousFindingButton.IsEnabled = _findingIndex > 0;
        NextFindingButton.IsEnabled = _findingIndex < findings.Count - 1;

        var applied = ReviewFindingsPresentation.AllAddressed(findings);
        _canRerunReview = applied;
        _canApplyReview = !applied && ReviewFindingsPresentation.ShowApplyFixes(findings);
        RerunReviewButton.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
        ApplyFixesButton.Visibility =
            !applied && ReviewFindingsPresentation.ShowApplyFixes(findings)
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateCompactReviewToolbar();
    }

    private void OnFindingsBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCompactReviewToolbar();
    private void UpdateCompactReviewToolbar()
    {
        var compact = FindingsBar.ActualWidth > 0 && FindingsBar.ActualWidth < 480;
        ApplyFixesButton.Visibility = _canApplyReview && !compact ? Visibility.Visible : Visibility.Collapsed;
        RerunReviewButton.Visibility = _canRerunReview && !compact ? Visibility.Visible : Visibility.Collapsed;
        DismissFindingsButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactReviewActionsButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnCompactReviewActionsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = CompactReviewActionsButton };
        if (_canApplyReview) Add("Apply fixes", OnApplyFixesClick);
        if (_canRerunReview) Add("Re-run review", OnRerunReviewClick);
        Add("Dismiss", OnDismissFindingsClick);
        CompactReviewActionsButton.ContextMenu = menu;
        menu.IsOpen = true;
        void Add(string label, RoutedEventHandler action) { var item = new MenuItem { Header = label }; item.Click += action; menu.Items.Add(item); }
    }

    private void OnPreviousFindingClick(object sender, RoutedEventArgs e) => StepFinding(-1);

    private void OnNextFindingClick(object sender, RoutedEventArgs e) => StepFinding(1);

    private void StepFinding(int direction)
    {
        var findings = ReviewFindingsStore.Get(_sessionId);
        if (findings.Count == 0)
        {
            return;
        }

        _findingIndex = Math.Clamp(_findingIndex + direction, 0, findings.Count - 1);
        RefreshFindings();
        RevealFinding(findings[_findingIndex]);
    }

    /// <summary>Opens the file the finding names and scrolls its row into view.</summary>
    private void RevealFinding(ReviewFinding finding)
    {
        foreach (var row in FileList.Items.OfType<ChangedFileRow>())
        {
            if (!string.Equals(row.Path, finding.File, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            row.EnsureExpanded();
            if (FileList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            {
                container.BringIntoView();
            }

            return;
        }
    }

    private void OnApplyFixesClick(object sender, RoutedEventArgs e)
    {
        var findings = ReviewFindingsStore.Get(_sessionId);
        if (findings.Count == 0)
        {
            return;
        }

        ReviewFindingsStore.MarkFixing(_sessionId);
        ReviewMessageRequested?.Invoke(this, ReviewFindingsPresentation.ApplyFixesMessage(findings));
    }

    /// <summary>The reference re-runs the review by sending its own slash command.</summary>
    private void OnRerunReviewClick(object sender, RoutedEventArgs e) =>
        ReviewMessageRequested?.Invoke(this, "/code-review");

    private void OnDismissFindingsClick(object sender, RoutedEventArgs e) =>
        ReviewFindingsStore.Clear(_sessionId);

    // ---- line annotations ----

    private void OnLineAnnotateClick(object sender, RoutedEventArgs e) => Annotate(sender, comment: false);

    private void OnLineCommentClick(object sender, RoutedEventArgs e) => Annotate(sender, comment: true);

    private void Annotate(object sender, bool comment)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiffLineRow line)
        {
            return;
        }

        var file = FileOf(line);
        var where = file is { Length: > 0 } && line.Number.Trim().Length > 0
            ? $"{file}:{line.Number.Trim()}"
            : file ?? "";
        var text = where.Length > 0 ? $"{where}\n{line.Text}" : line.Text;
        if (file is not null && int.TryParse(line.Number, out _))
        {
            _activeAnnotation = new ReviewAnnotation(file, line.Number, line.Kind == DiffLineKind.Removed,
                ReviewAnnotationStore.Hash(line.Text), line.Text.Length > 4000 ? line.Text[..4000] : line.Text, comment, DateTimeOffset.Now);
            _annotations?.Add(_activeAnnotation);
            line.IsAnnotated = true;
        }
        AnnotationRequested?.Invoke(this, comment ? $"Add comment on {text}" : text);
    }

    private void OnLineRemoveAnnotationClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiffLineRow line || FileOf(line) is not { } file) return;
        var annotation = _annotations?.Load().FirstOrDefault(a => ReviewAnnotationStore.Matches(a, file, line.Number, line.Kind == DiffLineKind.Removed, line.Text));
        if (annotation is not null) _annotations!.Remove(annotation);
        line.IsAnnotated = false;
        if (_activeAnnotation?.File == file && _activeAnnotation.Line == line.Number) _activeAnnotation = null;
    }

    private void ApplyAnnotations(ChangedFileRow row)
    {
        var annotations = _annotations?.Load() ?? [];
        row.MarkAnnotations(line => annotations.Any(a => ReviewAnnotationStore.Matches(a, row.Path, line.Number, line.Kind == DiffLineKind.Removed, line.Text)));
    }

    /// <summary>Which file's diff a line belongs to — the row that holds it.</summary>
    private string? FileOf(DiffLineRow line)
    {
        foreach (var row in FileList.Items.OfType<ChangedFileRow>())
        {
            if (row.Lines.Contains(line))
            {
                return row.Path;
            }
        }

        return null;
    }

    /// <summary>
    /// The reference names the handler on the row's Open item — "Open in {appName}" —
    /// and falls back to a plain "Open" when the extension has none registered.
    /// </summary>
    private void OnFileRowMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu ||
            menu.PlacementTarget is not FrameworkElement { DataContext: ChangedFileRow row } ||
            menu.Items.Count == 0 ||
            menu.Items[0] is not MenuItem open)
        {
            return;
        }

        var editors = IdeServices.Bridge.Discover(_workingDirectory);
        open.Header = editors.Count == 1 ? "Open in " + editors[0].Name
            : editors.Count > 1 ? "Open in editor" : DefaultApplication.OpenLabel(row.Path);
    }

    // ---- expand dialog ----

    private void OnExpandDiffClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Changes",
            Width = 1100,
            Height = 780,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.SetResourceReference(BackgroundProperty, "Bg200Brush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(14, 12, 14, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        var title = new TextBlock { Text = "Changes", FontSize = 14, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        titles.Children.Add(title);
        var description = PanePrimitives.Muted($"{BranchText.Text} → {ScopeText.Text}", 11.5);
        titles.Children.Add(description);
        header.Children.Add(titles);

        // The reference's "Diff layout" control: Unified and Split, one of them chosen.
        var layout = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(layout, "Diff layout");
        var unified = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = "Unified",
            IsChecked = !ExpandedIsSplit,
            Padding = new Thickness(10, 3, 10, 3),
        };
        var split = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = "Side by side",
            IsChecked = ExpandedIsSplit,
            Padding = new Thickness(10, 3, 10, 3),
        };
        unified.SetResourceReference(StyleProperty, "PanelTab");
        split.SetResourceReference(StyleProperty, "PanelTab");
        var host = new ContentControl { Focusable = false };
        void Apply(bool isSplit)
        {
            ExpandedIsSplit = isSplit;
            unified.IsChecked = !isSplit;
            split.IsChecked = isSplit;
            IsSideBySide = isSplit;
        }

        unified.Click += (_, _) => Apply(false);
        split.Click += (_, _) => Apply(true);
        layout.Children.Add(unified);
        layout.Children.Add(split);
        Grid.SetColumn(layout, 1);
        header.Children.Add(layout);
        root.Children.Add(header);

        // The dialog shows the pane's own file list: the panel is one control, so it is
        // lent to the dialog and handed back when it closes.
        var parent = (Panel)FilesScroll.Parent;
        var index = parent.Children.IndexOf(FilesScroll);
        parent.Children.Remove(FilesScroll);
        host.Content = FilesScroll;
        Grid.SetRow(host, 1);
        root.Children.Add(host);
        dialog.Content = root;
        dialog.Loaded += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (_activeAnnotation is null) return;
            foreach (var element in Descendants(FilesScroll).OfType<FrameworkElement>())
            {
                var candidates = element.DataContext switch
                {
                    DiffLineRow line => new[] { line },
                    DiffPairRow pair => new[] { pair.LeftSource, pair.RightSource },
                    _ => [],
                };
                if (candidates.Any(line => line is { IsAnnotated: true } &&
                    ReviewAnnotationStore.Matches(_activeAnnotation, FileOf(line) ?? "", line.Number, line.Kind == DiffLineKind.Removed, line.Text)))
                { element.BringIntoView(); break; }
            }
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        dialog.Closed += (_, _) =>
        {
            host.Content = null;
            parent.Children.Insert(index, FilesScroll);
        };
        dialog.ShowDialog();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    // ---- commit selector ----

    /// <summary>The recent commits the scope menu offers, newest first.</summary>
    internal void LoadCommits(string workingDirectory)
    {
        _ = Task.Run(() =>
        {
            var log = Git(workingDirectory, "log -n 20 --pretty=format:%h%x09%s");
            var commits = new List<(string Sha, string Subject)>();
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t', 2);
                if (parts.Length == 2)
                {
                    commits.Add((parts[0], parts[1]));
                }
            }

            Dispatcher.InvokeAsync(() => _commits = commits);
        });
    }

    /// <summary>Adds the reference's commit rows under the scope rows, as one radio group.</summary>
    private void AddCommitItems(ContextMenu menu)
    {
        if (_commits.Count < 1)
        {
            return;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(SessionMenu.SectionHeader("Commits"));
        foreach (var (sha, subject) in _commits)
        {
            var item = new MenuItem
            {
                Header = $"{sha}  {subject}",
                IsCheckable = true,
                IsChecked = _selectedCommit == sha,
            };
            var chosen = sha;
            item.Click += (_, _) =>
            {
                _selectedCommit = _selectedCommit == chosen ? null : chosen;
                _ = RefreshAsync();
            };
            menu.Items.Add(item);
        }
    }
}
