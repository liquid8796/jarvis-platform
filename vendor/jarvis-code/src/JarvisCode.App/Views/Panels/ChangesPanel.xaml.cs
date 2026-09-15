using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Infrastructure;

namespace JarvisCode.App.Views.Panels;

/// <summary>What a diff line does to the file.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,

    /// <summary>A side-by-side cell with no counterpart line; drawn hatched.</summary>
    Blank,
}

/// <summary>One rendered line of a unified diff.</summary>
public sealed class DiffLineRow : ObservableObject
{
    public required string Number { get; init; }
    public required string Marker { get; init; }
    public required string Text { get; init; }
    public required DiffLineKind Kind { get; init; }
    private bool _isAnnotated;
    public bool IsAnnotated { get => _isAnnotated; set => SetProperty(ref _isAnnotated, value); }
}

/// <summary>
/// One row of a side-by-side diff: the old line on the left, the new one on the right.
/// A line with no counterpart leaves the other side blank.
/// </summary>
public sealed class DiffPairRow
{
    public DiffLineRow? LeftSource { get; init; }
    public DiffLineRow? RightSource { get; init; }
    public bool HasLeftSource => LeftSource is not null;
    public bool HasRightSource => RightSource is not null;
    public string LeftNumber { get; init; } = "";
    public string LeftMarker { get; init; } = "";
    public string LeftText { get; init; } = "";
    public DiffLineKind LeftKind { get; init; } = DiffLineKind.Context;

    public string RightNumber { get; init; } = "";
    public string RightMarker { get; init; } = "";
    public string RightText { get; init; } = "";
    public DiffLineKind RightKind { get; init; } = DiffLineKind.Context;
}

/// <summary>
/// A run of consecutive diff lines. A collapsible run holds unmodified lines the view
/// folds into one "N unmodified lines" row until the reader opens it.
/// </summary>
public sealed class DiffSegment
{
    public required IReadOnlyList<DiffLineRow> Lines { get; init; }
    public bool IsCollapsible { get; init; }

    /// <summary>For a collapsible run whose lines could not be loaded: just the count.</summary>
    public int HiddenCount { get; init; }

    public bool Expanded { get; set; }

    public int Count => Lines.Count > 0 ? Lines.Count : HiddenCount;

    /// <summary>The lines exist, so the fold can open.</summary>
    public bool CanExpand => Lines.Count > 0;
}

/// <summary>The folded "N unmodified lines" row, shared by both diff layouts.</summary>
public sealed class DiffCollapseRow
{
    public DiffCollapseRow(int count, bool isTop, bool canExpand, Action expand)
    {
        Count = count;
        IsTop = isTop;
        CanExpand = canExpand;
        ExpandCommand = new RelayCommand(expand, () => canExpand);
    }

    public int Count { get; }

    /// <summary>The run at the very top of the file points its chevron up.</summary>
    public bool IsTop { get; }

    public bool CanExpand { get; }
    public ICommand ExpandCommand { get; }
}

/// <summary>One changed file in the changes panel, with its diff loaded on expand.</summary>
public sealed class ChangedFileRow : ObservableObject
{
    /// <summary>Extensions drawn with the code glyph; anything else gets the plain file.</summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".slnx", ".sln", ".props",
        ".js", ".ts", ".jsx", ".tsx", ".mjs", ".py", ".rb", ".go", ".rs", ".java", ".kt",
        ".c", ".h", ".cpp", ".hpp", ".sh", ".ps1", ".bat", ".cmd",
        ".json", ".xml", ".yml", ".yaml", ".toml", ".html", ".css", ".scss", ".sql",
    };

    private readonly Func<ChangedFileRow, Task> _load;
    private List<DiffSegment> _segments = [];
    private bool _isExpanded;
    private bool _loaded;

    public ChangedFileRow(string path, int added, int removed, bool binary, Func<ChangedFileRow, Task> load)
    {
        _load = load;
        Path = path;
        var slash = path.LastIndexOf('/');
        Name = slash < 0 ? path : path[(slash + 1)..];
        Directory = slash < 0 ? "" : path[..slash];
        IsBinary = binary;
        Added = binary ? "bin" : $"+{added}";
        Removed = binary ? "" : $"−{removed}";
        IsCodeFile = CodeExtensions.Contains(System.IO.Path.GetExtension(path));
        ToggleCommand = new RelayCommand(() => _ = ToggleAsync());
    }

    public string Path { get; }
    public string Name { get; }
    public string Directory { get; }
    public string Added { get; }
    public string Removed { get; }
    public bool IsBinary { get; }
    public bool IsCodeFile { get; }

    /// <summary>Unified layout: DiffLineRow and DiffCollapseRow items.</summary>
    public ObservableCollection<object> Lines { get; } = [];

    /// <summary>Side-by-side layout: DiffPairRow and DiffCollapseRow items.</summary>
    public ObservableCollection<object> Pairs { get; } = [];

    public ICommand ToggleCommand { get; }

    /// <summary>One visible run, no folding — fallback messages and new files.</summary>
    public void SetLines(IReadOnlyList<DiffLineRow> lines) =>
        SetSegments([new DiffSegment { Lines = lines }]);

    /// <summary>Fills both layouts from the segment structure.</summary>
    public void SetSegments(List<DiffSegment> segments)
    {
        _segments = segments;
        RebuildViews();
    }

    public void MarkAnnotations(Func<DiffLineRow, bool> matches)
    {
        foreach (var segment in _segments)
        {
            foreach (var line in segment.Lines) line.IsAnnotated = matches(line);
            if (segment.Lines.Any(line => line.IsAnnotated)) segment.Expanded = true;
        }
        RebuildViews();
    }

    /// <summary>Opens the file if it is closed; used when the file list is hidden.</summary>
    public void EnsureExpanded()
    {
        if (!IsExpanded)
        {
            _ = ToggleAsync();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Materializes segments into both layout collections; folds stay folded.</summary>
    private void RebuildViews()
    {
        Lines.Clear();
        Pairs.Clear();
        for (var i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            if (segment.IsCollapsible && !segment.Expanded)
            {
                var fold = new DiffCollapseRow(
                    segment.Count,
                    isTop: i == 0,
                    canExpand: segment.CanExpand,
                    expand: () =>
                    {
                        segment.Expanded = true;
                        RebuildViews();
                    });
                Lines.Add(fold);
                Pairs.Add(fold);
                continue;
            }

            foreach (var line in segment.Lines)
            {
                Lines.Add(line);
            }

            foreach (var pair in ChangesPanel.PairForSideBySide(segment.Lines))
            {
                Pairs.Add(pair);
            }
        }
    }

    private async Task ToggleAsync()
    {
        IsExpanded = !IsExpanded;
        if (!IsExpanded || _loaded)
        {
            return;
        }

        _loaded = true;
        await _load(this);
    }
}

/// <summary>
/// The changes panel: the reference app's review view — a compare-target breadcrumb over
/// one row per changed file, each opening into a diff whose unmodified stretches fold
/// into "N unmodified lines" rows.
/// </summary>
public partial class ChangesPanel : UserControl
{
    private static readonly (string Label, string Arguments)[] Scopes =
    [
        ("working tree", "diff"),
        ("staged", "diff --staged"),
        ("last commit", "diff HEAD~1 HEAD"),
    ];

    /// <summary>Unmodified lines kept visible on each side of a change.</summary>
    private const int KeptContext = 3;

    /// <summary>A run folds only when it hides at least this many lines.</summary>
    private const int MinCollapsed = 5;

    /// <summary>Above this many characters, the full-context diff falls back to plain hunks.</summary>
    private const int FullContextCap = 4_000_000;

    private int _scope;
    private string _workingDirectory = "";

    /// <summary>Right-click "Attach as context" on a file row — the composer @-mentions it.</summary>
    public event EventHandler<string>? AttachFileRequested;

    private static ChangedFileRow? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as ChangedFileRow;

    private async void OnFileRowOpenClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        var full = System.IO.Path.Combine(_workingDirectory, row.Path);
        try
        {
            if (Services.IdeServices.Bridge.Discover(_workingDirectory).Count > 0)
            {
                await Services.IdeServices.CallAsync(_workingDirectory, "openFile",
                    new System.Text.Json.Nodes.JsonObject { ["file_path"] = full });
                return;
            }
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or System.IO.IOException)
        {
            MessageBox.Show(Window.GetWindow(this), $"Could not open the file: {ex.Message}", "Jarvis Code");
        }
    }

    private async void OnFileRowEditorDiffClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || row.IsBinary) return;
        try
        {
            var full = System.IO.Path.Combine(_workingDirectory, row.Path);
            var current = System.IO.File.Exists(full) ? await System.IO.File.ReadAllTextAsync(full) : "";
            var baseline = _selectedCommit is { Length: > 0 } commit ? commit + "~1" : _scope switch { 0 => "", 1 => "HEAD", _ => "HEAD~1" };
            var old = await Task.Run(() => GitBlob(_workingDirectory, baseline, row.Path));
            var proposed = _scope == 1 && _selectedCommit is null ? await Task.Run(() => GitBlob(_workingDirectory, "", row.Path))
                : _selectedCommit is { Length: > 0 } chosen ? await Task.Run(() => GitBlob(_workingDirectory, chosen, row.Path))
                : _scope == 2 ? await Task.Run(() => GitBlob(_workingDirectory, "HEAD", row.Path)) : current;
            await Services.IdeServices.CallAsync(_workingDirectory, "openDiff", new System.Text.Json.Nodes.JsonObject
            {
                ["old_file_path"] = full, ["new_file_path"] = full, ["old_file_contents"] = old,
                ["new_file_contents"] = proposed, ["tab_name"] = row.Name + " — Jarvis Code", ["wait_for_decision"] = false,
                ["read_only"] = true,
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Services.ToastQueue.Current?.AddError("Could not open editor diff: " + ex.Message); }
    }

    private async void OnCloseEditorDiffsClick(object sender, RoutedEventArgs e)
    {
        try { await Services.IdeServices.CallAsync(_workingDirectory, "closeAllDiffTabs", new System.Text.Json.Nodes.JsonObject()); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Services.ToastQueue.Current?.AddError("Could not close editor diffs: " + ex.Message); }
    }

    private static string GitBlob(string directory, string revision, string relativePath)
    {
        var start = new ProcessStartInfo("git")
        { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("show"); start.ArgumentList.Add(revision + ":" + relativePath.Replace('\\', '/'));
        using var process = Process.Start(start) ?? throw new IOException("Git did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new IOException("Git did not return the comparison within 15 seconds."); }
        // A newly added file has no baseline. That is the empty side of its diff.
        return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : "";
    }

    private void OnFileRowCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Controls.MarkdownView.TrySetClipboard(System.IO.Path.Combine(_workingDirectory, row.Path));
            Services.ToastQueue.Current?.AddSuccess(Services.ToastText.PathCopied);
        }
    }

    private void OnFileRowAttachClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            AttachFileRequested?.Invoke(this, row.Path);
        }
    }

    /// <summary>The reference's "Show files" toggle: the file list above the diffs.</summary>
    public static readonly DependencyProperty ShowFilesProperty = DependencyProperty.Register(
        nameof(ShowFiles), typeof(bool), typeof(ChangesPanel), new PropertyMetadata(true, OnShowFilesChanged));

    /// <summary>The reference's Layout group: unified when false, side by side when true.</summary>
    public static readonly DependencyProperty IsSideBySideProperty = DependencyProperty.Register(
        nameof(IsSideBySide), typeof(bool), typeof(ChangesPanel), new PropertyMetadata(false));

    public ChangesPanel()
    {
        InitializeComponent();
    }

    public bool ShowFiles
    {
        get => (bool)GetValue(ShowFilesProperty);
        set => SetValue(ShowFilesProperty, value);
    }

    public bool IsSideBySide
    {
        get => (bool)GetValue(IsSideBySideProperty);
        set => SetValue(IsSideBySideProperty, value);
    }

    /// <summary>
    /// Hiding the list with every file closed would leave an empty panel, so the first
    /// file opens - hiding the list means "show me the diff", not "show me nothing".
    /// </summary>
    private static void OnShowFilesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ChangesPanel panel || (bool)e.NewValue)
        {
            return;
        }

        var rows = panel.FileList.Items.OfType<ChangedFileRow>().ToList();
        if (rows.Count > 0 && !rows.Any(row => row.IsExpanded))
        {
            rows[0].EnsureExpanded();
        }
    }

    /// <summary>
    /// Shows one file's diff, expanded and scrolled into view. False when this
    /// file is not among the changes, so the caller can fall back.
    /// "Click a filename on an Edited or Wrote row to open that file in the
    /// diff pane."
    /// </summary>
    public bool TryFocusFile(string path)
    {
        var wanted = path.Replace('\\', '/').TrimStart('.', '/');
        foreach (var item in FileList.Items)
        {
            if (item is not ChangedFileRow row)
            {
                continue;
            }

            var candidate = row.Path.Replace('\\', '/');
            if (!candidate.EndsWith(wanted, StringComparison.OrdinalIgnoreCase) &&
                !wanted.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            row.EnsureExpanded();
            if (FileList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
            {
                container.BringIntoView();
            }

            return true;
        }

        return false;
    }

    /// <summary>Points the panel at a project and reloads it.</summary>
    public void Load(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        LoadCommits(workingDirectory);
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_workingDirectory.Length == 0 || !System.IO.Directory.Exists(_workingDirectory))
        {
            ShowEmpty("This session has no project folder, so there is nothing to compare.");
            return;
        }

        // A commit picked in the selector replaces the scope: the pane then shows what
        // that commit changed, which is the reference's selectedCommitSha.
        (string Label, string Arguments) scope = _selectedCommit is { Length: > 0 } sha
            ? ($"commit {sha}", $"diff {sha}~1 {sha}")
            : Scopes[_scope];
        ScopeText.Text = scope.Label;

        var cwd = _workingDirectory;
        var branch = await Task.Run(() => Git(cwd, "rev-parse --abbrev-ref HEAD").Trim());
        if (branch.Length == 0)
        {
            ShowEmpty("This project is not a git repository.");
            BranchText.Text = "—";
            return;
        }

        BranchText.Text = branch;
        var numstat = await Task.Run(() => Git(cwd, $"{scope.Arguments} --numstat"));
        var rows = ParseNumstat(numstat, LoadFileDiffAsync);

        // A file git has never seen has no diff, but it is still a change the reviewer
        // needs: the reference lists it alongside the rest, counted as all additions.
        if (_scope == 0 && _selectedCommit is null)
        {
            var untracked = await Task.Run(() => Git(cwd, "ls-files --others --exclude-standard"));
            foreach (var path in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var relative = path.TrimEnd('\r');
                var text = await Task.Run(() => ReadNewFile(System.IO.Path.Combine(cwd, relative)));
                rows.Add(new ChangedFileRow(relative, text?.Count ?? 0, 0, text is null, LoadNewFileAsync));
            }

            rows.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        }

        FileList.Items.Clear();
        foreach (var row in rows)
        {
            FileList.Items.Add(row);
        }

        // The reference opens every text file's diff up front; binaries stay a bare row.
        foreach (var row in rows.Where(r => !r.IsBinary))
        {
            row.EnsureExpanded();
        }

        if (rows.Count == 0)
        {
            ShowEmpty($"No changes in the {scope.Label}.");
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        FilesScroll.Visibility = Visibility.Visible;
    }

    private void ShowEmpty(string message)
    {
        FileList.Items.Clear();
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
        FilesScroll.Visibility = Visibility.Collapsed;
    }

    private void OnScopeClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ScopeButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        for (var i = 0; i < Scopes.Length; i++)
        {
            var index = i;
            var item = new MenuItem
            {
                Header = Scopes[i].Label,
                IsCheckable = true,
                IsChecked = i == _scope,
            };
            item.Click += (_, _) =>
            {
                _scope = index;
                _ = RefreshAsync();
            };
            menu.Items.Add(item);
        }

        AddCommitItems(menu);
        menu.IsOpen = true;
    }

    // ---- git ----

    private static List<ChangedFileRow> ParseNumstat(string numstat, Func<ChangedFileRow, Task> load)
    {
        var rows = new List<ChangedFileRow>();
        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var binary = parts[0] == "-";
            _ = int.TryParse(parts[0], out var added);
            _ = int.TryParse(parts[1], out var removed);
            rows.Add(new ChangedFileRow(parts[2], added, removed, binary, load));
        }

        return rows;
    }

    private async Task LoadFileDiffAsync(ChangedFileRow row)
    {
        var cwd = _workingDirectory;
        var scope = Scopes[_scope].Arguments;

        // Full context makes every unmodified line available, so the folds can open
        // without a second fetch and stay correct for every compare scope.
        var diff = await Task.Run(() => Git(cwd, $"{scope} -U1000000 -- \"{row.Path}\""));
        if (diff.Length > FullContextCap)
        {
            var fallback = await Task.Run(() => Git(cwd, $"{scope} -- \"{row.Path}\""));
            int? totalNewLines = _scope == 0
                ? await Task.Run(() => CountFileLines(System.IO.Path.Combine(cwd, row.Path)))
                : null;
            row.SetSegments(BuildFallbackSegments(ParseUnifiedDiff(fallback), totalNewLines));
            ApplyAnnotations(row);
            return;
        }

        var parsed = ParseUnifiedDiff(diff);
        if (parsed.Count == 0)
        {
            row.SetLines([new DiffLineRow { Number = "", Marker = "", Text = "No textual diff for this file.", Kind = DiffLineKind.Hunk }]);
            return;
        }

        row.SetSegments(BuildCollapsedSegments(parsed));
        ApplyAnnotations(row);
    }

    /// <summary>An untracked file reads as added lines from 1; nothing to fold.</summary>
    private async Task LoadNewFileAsync(ChangedFileRow row)
    {
        var full = System.IO.Path.Combine(_workingDirectory, row.Path);
        var lines = await Task.Run(() => ReadNewFile(full));
        if (lines is null)
        {
            row.SetLines([new DiffLineRow { Number = "", Marker = "", Text = "Binary file.", Kind = DiffLineKind.Hunk }]);
            return;
        }

        var rows = new List<DiffLineRow>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            rows.Add(new DiffLineRow
            {
                Number = (i + 1).ToString(),
                Marker = "+",
                Text = lines[i],
                Kind = DiffLineKind.Added,
            });
        }

        row.SetLines(rows);
        ApplyAnnotations(row);
    }

    /// <summary>Lines of a new file, or null when it is binary, huge or unreadable.</summary>
    private static List<string>? ReadNewFile(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > 1_000_000)
            {
                return null;
            }

            var text = File.ReadAllText(fullPath);
            return text.Contains('\0')
                ? null
                : [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    private static int? CountFileLines(string fullPath)
    {
        try
        {
            return File.Exists(fullPath) ? File.ReadLines(fullPath).Count() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Turns `git diff` output into numbered rows, dropping the file headers.</summary>
    public static List<DiffLineRow> ParseUnifiedDiff(string diff)
    {
        var rows = new List<DiffLineRow>();
        var oldLine = 0;
        var newLine = 0;

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("new file mode", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode", StringComparison.Ordinal)
                || line.StartsWith("similarity index", StringComparison.Ordinal)
                || line.StartsWith("rename ", StringComparison.Ordinal)
                || line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldLine, newLine) = ParseHunkHeader(line);
                rows.Add(new DiffLineRow { Number = "", Marker = "", Text = line, Kind = DiffLineKind.Hunk });
                continue;
            }

            switch (line[0])
            {
                case '+':
                    rows.Add(new DiffLineRow { Number = newLine.ToString(), Marker = "+", Text = line[1..], Kind = DiffLineKind.Added });
                    newLine++;
                    break;
                case '-':
                    rows.Add(new DiffLineRow { Number = oldLine.ToString(), Marker = "−", Text = line[1..], Kind = DiffLineKind.Removed });
                    oldLine++;
                    break;
                case '\\':
                    // "\ No newline at end of file" — context, not a change.
                    rows.Add(new DiffLineRow { Number = "", Marker = "", Text = line, Kind = DiffLineKind.Hunk });
                    break;
                default:
                    rows.Add(new DiffLineRow { Number = newLine.ToString(), Marker = "", Text = line[1..], Kind = DiffLineKind.Context });
                    oldLine++;
                    newLine++;
                    break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Folds a full-context diff the way the reference reads: three unmodified lines stay
    /// visible beside each change, the rest of every run collapses into an expandable
    /// "N unmodified lines" segment, and @@ headers disappear — the folds replace them.
    /// A run at the file's edge keeps lines only on the side that touches a change.
    /// </summary>
    public static List<DiffSegment> BuildCollapsedSegments(IReadOnlyList<DiffLineRow> rows)
    {
        var display = rows
            .Where(row => row.Kind != DiffLineKind.Hunk || !row.Text.StartsWith("@@", StringComparison.Ordinal))
            .ToList();

        var segments = new List<DiffSegment>();
        var visible = new List<DiffLineRow>();

        void FlushVisible()
        {
            if (visible.Count > 0)
            {
                segments.Add(new DiffSegment { Lines = visible });
                visible = [];
            }
        }

        var i = 0;
        while (i < display.Count)
        {
            if (display[i].Kind != DiffLineKind.Context)
            {
                visible.Add(display[i]);
                i++;
                continue;
            }

            var start = i;
            while (i < display.Count && display[i].Kind == DiffLineKind.Context)
            {
                i++;
            }

            var run = display[start..i];
            var keepStart = start > 0 ? KeptContext : 0;
            var keepEnd = i < display.Count ? KeptContext : 0;
            var hidden = run.Count - keepStart - keepEnd;
            if (hidden < MinCollapsed)
            {
                visible.AddRange(run);
                continue;
            }

            visible.AddRange(run.Take(keepStart));
            FlushVisible();
            segments.Add(new DiffSegment { Lines = run.Skip(keepStart).Take(hidden).ToList(), IsCollapsible = true });
            visible.AddRange(run.Skip(keepStart + hidden));
        }

        FlushVisible();
        return segments;
    }

    /// <summary>
    /// The huge-file fallback: a default-context diff still shows its changes, and the
    /// gaps between hunks become fold rows that state their size but cannot open.
    /// </summary>
    public static List<DiffSegment> BuildFallbackSegments(IReadOnlyList<DiffLineRow> rows, int? totalNewLines)
    {
        var segments = new List<DiffSegment>();
        var visible = new List<DiffLineRow>();
        var lastNewLine = 0;

        void FlushVisible()
        {
            if (visible.Count > 0)
            {
                segments.Add(new DiffSegment { Lines = visible });
                visible = [];
            }
        }

        foreach (var row in rows)
        {
            if (row.Kind == DiffLineKind.Hunk && row.Text.StartsWith("@@", StringComparison.Ordinal))
            {
                var (_, newStart) = ParseHunkHeader(row.Text);
                var hidden = newStart - lastNewLine - 1;
                if (hidden > 0)
                {
                    FlushVisible();
                    segments.Add(new DiffSegment { Lines = [], IsCollapsible = true, HiddenCount = hidden });
                }

                continue;
            }

            visible.Add(row);
            if (row.Kind is DiffLineKind.Context or DiffLineKind.Added && int.TryParse(row.Number, out var number))
            {
                lastNewLine = number;
            }
        }

        FlushVisible();
        if (totalNewLines is { } total && total > lastNewLine)
        {
            segments.Add(new DiffSegment { Lines = [], IsCollapsible = true, HiddenCount = total - lastNewLine });
        }

        return segments;
    }

    /// <summary>
    /// Pairs a unified diff into side-by-side rows: a run of removed lines lines up with
    /// the run of added lines that follows it, and whichever run is longer leaves hatched
    /// blanks on the other side. Context and hunk notes sit on both sides.
    /// </summary>
    public static List<DiffPairRow> PairForSideBySide(IReadOnlyList<DiffLineRow> rows)
    {
        var pairs = new List<DiffPairRow>();
        var removed = new List<DiffLineRow>();
        var added = new List<DiffLineRow>();

        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                var left = i < removed.Count ? removed[i] : null;
                var right = i < added.Count ? added[i] : null;
                pairs.Add(new DiffPairRow
                {
                    LeftSource = left, RightSource = right,
                    LeftNumber = left?.Number ?? "",
                    LeftMarker = left?.Marker ?? "",
                    LeftText = left?.Text ?? "",
                    LeftKind = left?.Kind ?? DiffLineKind.Blank,
                    RightNumber = right?.Number ?? "",
                    RightMarker = right?.Marker ?? "",
                    RightText = right?.Text ?? "",
                    RightKind = right?.Kind ?? DiffLineKind.Blank,
                });
            }

            removed.Clear();
            added.Clear();
        }

        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case DiffLineKind.Removed:
                    removed.Add(row);
                    break;
                case DiffLineKind.Added:
                    added.Add(row);
                    break;
                case DiffLineKind.Hunk:
                    Flush();
                    // The note names the range for both files, so it only reads once.
                    pairs.Add(new DiffPairRow { LeftText = row.Text, LeftKind = DiffLineKind.Hunk, RightKind = DiffLineKind.Hunk });
                    break;
                default:
                    Flush();
                    pairs.Add(new DiffPairRow
                    {
                        LeftSource = row, RightSource = row,
                        LeftNumber = row.Number,
                        LeftText = row.Text,
                        RightNumber = row.Number,
                        RightText = row.Text,
                    });
                    break;
            }
        }

        Flush();
        return pairs;
    }

    /// <summary>"@@ -12,7 +12,9 @@" → the first old and new line numbers of the hunk.</summary>
    private static (int Old, int New) ParseHunkHeader(string header)
    {
        var old = 0;
        var @new = 0;
        var parts = header.Split(' ');
        foreach (var part in parts)
        {
            if (part.StartsWith("-", StringComparison.Ordinal) && int.TryParse(part[1..].Split(',')[0], out var o))
            {
                old = o;
            }
            else if (part.StartsWith("+", StringComparison.Ordinal) && int.TryParse(part[1..].Split(',')[0], out var n))
            {
                @new = n;
            }
        }

        return (old, @new);
    }

    private static string Git(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return "";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }
}
