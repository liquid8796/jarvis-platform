using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using JarvisCode.App.Infrastructure;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>One entry in the files panel; folders fill their children when first opened.</summary>
public sealed class FileNode : ObservableObject
{
    private bool _loaded;

    public FileNode(string fullPath, bool isDirectory)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
        if (isDirectory)
        {
            // A placeholder keeps the expander visible until the real children load.
            Children.Add(new FileNode(fullPath, isDirectory: false) { IsPlaceholder = true });
        }
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public bool IsPlaceholder { get; private init; }
    public Visibility FolderVisibility => IsDirectory ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<FileNode> Children { get; } = [];

    public void EnsureChildren()
    {
        if (!IsDirectory || _loaded)
        {
            return;
        }

        _loaded = true;
        Children.Clear();
        foreach (var node in FilesPanel.Enumerate(FullPath))
        {
            Children.Add(node);
        }
    }
}

/// <summary>
/// The Files pane, ported from the reference's file browser (ion-dist chunk
/// cd2efac6e-C6Wbl1p6.js): the project tree, a filter box whose "?" prefix switches to a
/// content search, name hits with their matched characters in semibold, content hits as
/// line rows carrying "Ask about this", and a folder's own "Open in terminal".
/// </summary>
public partial class FilesPanel : UserControl
{
    private static readonly string[] Skipped = [".git", "node_modules", "bin", "obj"];

    private readonly FileNameIndex _index = new();
    private readonly DispatcherTimer _debounce = new();
    private string _root = "";
    private IReadOnlyList<string> _entries = [];

    public FilesPanel()
    {
        InitializeComponent();
        Tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnItemExpanded));
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RunSearch();
        };
    }

    /// <summary>A file the reader picked; the workspace opens it in the File pane.</summary>
    public event EventHandler<(string Path, int? Line)>? FileOpened;

    /// <summary>"Ask about this" on a content hit: the line reaches the composer.</summary>
    public event EventHandler<string>? AskAboutRequested;

    /// <summary>A folder's "Open in terminal": the terminal pane opens there.</summary>
    public event EventHandler<string>? OpenInTerminalRequested;

    /// <summary>Points the tree at a project folder.</summary>
    public void Load(string workingDirectory)
    {
        if (_root == workingDirectory && Tree.Items.Count > 0)
        {
            // Reloading would collapse whatever the reader had opened.
            return;
        }

        _root = workingDirectory;
        Tree.Items.Clear();
        if (workingDirectory.Length == 0 || !Directory.Exists(workingDirectory))
        {
            RootText.Text = "Files";
            EmptyText.Text = "This session has no project folder.";
            EmptyText.Visibility = Visibility.Visible;
            Tree.Visibility = Visibility.Collapsed;
            return;
        }

        RootText.Text = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar));
        EmptyText.Visibility = Visibility.Collapsed;
        Tree.Visibility = Visibility.Visible;
        foreach (var node in Enumerate(workingDirectory))
        {
            Tree.Items.Add(node);
        }

        var root = workingDirectory;
        _ = Task.Run(() =>
        {
            var entries = JarvisCode.Core.Utilities.FileMentions.ListCandidateEntries(root);
            Dispatcher.InvokeAsync(() =>
            {
                _entries = entries;
                _index.Load(entries.Where(e => !e.EndsWith('/')));
            });
        });
    }

    /// <summary>Folders first, then files, both alphabetical — build-output folders skipped.</summary>
    internal static List<FileNode> Enumerate(string directory)
    {
        var nodes = new List<FileNode>();
        try
        {
            foreach (var path in Directory.EnumerateDirectories(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                if (Skipped.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                nodes.Add(new FileNode(path, isDirectory: true));
            }

            foreach (var path in Directory.EnumerateFiles(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                nodes.Add(new FileNode(path, isDirectory: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder simply shows as empty.
        }

        return nodes;
    }

    private static void OnItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FileNode node })
        {
            node.EnsureChildren();
        }
    }

    // ---- filtering ----

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var query = FilterBox.Text;
        ClearFilterButton.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(
            FileContentSearch.IsContentQuery(query)
                ? FileContentSearch.ContentDebounceMs
                : FileContentSearch.NameDebounceMs);
        _debounce.Start();
    }

    private void OnFilterKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FilterBox.Text.Length > 0)
        {
            e.Handled = true;
            ClearFilter();
        }
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e) => ClearFilter();

    private void ClearFilter()
    {
        FilterBox.Text = "";
        _debounce.Stop();
        RunSearch();
    }

    private void RunSearch()
    {
        var query = FilterBox.Text.Trim();
        Results.Children.Clear();
        if (query.Length == 0)
        {
            ResultsScroll.Visibility = Visibility.Collapsed;
            Tree.Visibility = Visibility.Visible;
            EmptyText.Visibility = Visibility.Collapsed;
            return;
        }

        Tree.Visibility = Visibility.Collapsed;
        ResultsScroll.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;

        if (FileContentSearch.IsContentQuery(query))
        {
            RunContentSearch(FileContentSearch.ContentTerm(query));
            return;
        }

        var hits = _index.Search(query, 50);
        if (hits.Count == 0)
        {
            ShowMessage("No matching files");
            return;
        }

        foreach (var hit in hits)
        {
            Results.Children.Add(BuildFileRow(hit));
        }
    }

    private void RunContentSearch(string term)
    {
        if (term.Length == 0)
        {
            ShowMessage("Type after ? to search file contents");
            return;
        }

        var root = _root;
        var files = _entries.Where(e => !e.EndsWith('/')).ToList();
        var hits = FileContentSearch.Search(root, files, term);
        if (hits.Count == 0)
        {
            ShowMessage("No matches in file contents");
            return;
        }

        foreach (var (relative, matches) in FileContentSearch.Group(hits))
        {
            Results.Children.Add(BuildFileRow(new FileNameHit(relative, 0, [])));
            foreach (var match in matches)
            {
                Results.Children.Add(BuildMatchRow(match));
            }
        }
    }

    private void ShowMessage(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        Results.Children.Add(text);
    }

    /// <summary>A name hit: the file's own name with the matched characters in semibold.</summary>
    private FrameworkElement BuildFileRow(FileNameHit hit)
    {
        var slash = hit.Path.LastIndexOf('/');
        var name = slash >= 0 ? hit.Path[(slash + 1)..] : hit.Path;
        var offset = slash + 1;

        var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(Highlight(name, hit.Positions, offset, 12.5, "Text100Brush"));
        if (slash >= 0)
        {
            var path = Highlight(hit.Path[..slash], hit.Positions, 0, 11, "Text500Brush");
            path.Margin = new Thickness(8, 0, 0, 0);
            stack.Children.Add(path);
        }

        row.Children.Add(stack);

        var absolute = Path.Combine(_root, hit.Path.Replace('/', Path.DirectorySeparatorChar));
        var actions = PanePrimitives.IconButton("KebabGlyph", "File actions", () => ShowFileMenu(absolute));
        actions.Opacity = 0;
        row.MouseEnter += (_, _) => actions.Opacity = 1;
        row.MouseLeave += (_, _) => actions.Opacity = 0;
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        row.MouseLeftButtonUp += (_, _) => FileOpened?.Invoke(this, (absolute, null));
        AutomationProperties.SetName(row, hit.Path);
        return row;
    }

    /// <summary>A content hit: the line number, then the line, then its own actions.</summary>
    private FrameworkElement BuildMatchRow(FileContentHit hit)
    {
        var row = new Grid { Margin = new Thickness(24, 0, 0, 0), Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var number = new TextBlock
        {
            Text = hit.Line.ToString(),
            FontSize = 11,
            MinWidth = 24,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        number.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        row.Children.Add(number);

        var preview = new TextBlock
        {
            Text = hit.Preview.Trim(),
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        preview.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");
        preview.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        Grid.SetColumn(preview, 1);
        row.Children.Add(preview);

        var actions = PanePrimitives.IconButton("KebabGlyph", "Match actions", () => ShowMatchMenu(hit));
        actions.Opacity = 0;
        row.MouseEnter += (_, _) => actions.Opacity = 1;
        row.MouseLeave += (_, _) => actions.Opacity = 0;
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        row.MouseLeftButtonUp += (_, _) => FileOpened?.Invoke(this, (hit.AbsPath, hit.Line));
        AutomationProperties.SetName(row, $"{hit.RelativePath}:{hit.Line}");
        return row;
    }

    private static TextBlock Highlight(string text, IReadOnlyList<int> positions, int offset, double size, string brush)
    {
        var block = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var matched = new HashSet<int>(positions.Select(p => p - offset));
        var run = new System.Text.StringBuilder();
        var bold = false;
        for (int i = 0; i < text.Length; i++)
        {
            var isMatch = matched.Contains(i);
            if (isMatch != bold && run.Length > 0)
            {
                block.Inlines.Add(new Run(run.ToString()) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
                run.Clear();
            }

            bold = isMatch;
            run.Append(text[i]);
        }

        if (run.Length > 0)
        {
            block.Inlines.Add(new Run(run.ToString()) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
        }

        return block;
    }

    private void ShowFileMenu(string absolutePath)
    {
        var menu = new ContextMenu { MinWidth = 180 };
        var open = new MenuItem { Header = "Open in pane" };
        open.Click += (_, _) => FileOpened?.Invoke(this, (absolutePath, null));
        menu.Items.Add(open);

        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += (_, _) => CopyPath(absolutePath);
        menu.Items.Add(copy);

        var terminal = new MenuItem { Header = "Open in terminal" };
        terminal.Click += (_, _) => OpenInTerminalRequested?.Invoke(
            this, Path.GetDirectoryName(absolutePath) ?? _root);
        menu.Items.Add(terminal);
        menu.IsOpen = true;
    }

    private void ShowMatchMenu(FileContentHit hit)
    {
        var menu = new ContextMenu { MinWidth = 180 };
        var ask = new MenuItem { Header = "Ask about this" };
        ask.Click += (_, _) => AskAboutRequested?.Invoke(
            this, $"{hit.RelativePath}:{hit.Line}\n{hit.Preview.Trim()}");
        menu.Items.Add(ask);

        var open = new MenuItem { Header = "Open in pane" };
        open.Click += (_, _) => FileOpened?.Invoke(this, (hit.AbsPath, hit.Line));
        menu.Items.Add(open);
        menu.IsOpen = true;
    }

    private static void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process owns the clipboard.
        }
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Tree.SelectedItem is not FileNode { IsPlaceholder: false } node)
        {
            return;
        }

        if (node.IsDirectory)
        {
            OpenInTerminalRequested?.Invoke(this, node.FullPath);
            return;
        }

        FileOpened?.Invoke(this, (node.FullPath, null));
    }

    /// <summary>The tree's own right-click menu: a folder opens a terminal, a file the pane.</summary>
    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (Tree.SelectedItem is FileNode { IsPlaceholder: false } node)
        {
            e.Handled = true;
            if (node.IsDirectory)
            {
                var menu = new ContextMenu { MinWidth = 180 };
                var terminal = new MenuItem { Header = "Open in terminal" };
                terminal.Click += (_, _) => OpenInTerminalRequested?.Invoke(this, node.FullPath);
                menu.Items.Add(terminal);
                var reveal = new MenuItem { Header = "Show in Explorer" };
                reveal.Click += (_, _) => Reveal(node.FullPath);
                menu.Items.Add(reveal);
                menu.IsOpen = true;
                return;
            }

            ShowFileMenu(node.FullPath);
            return;
        }

        base.OnPreviewMouseRightButtonUp(e);
    }

    private static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = false,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No shell to reveal it in.
        }
    }
}
