using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The File pane, ported from the reference's file view (ion-dist chunk cd2efac6e-C6Wbl1p6.js
/// at the "Find in file" literal): the file's own text with line numbers, a find bar, the
/// Preview/View source pair for markdown, "Open in…", Download file and Copy, and an edit
/// mode whose Cancel/Save replace them. Leaving a file with unsaved edits raises the
/// reference's "Unsaved changes" question with its "Discard changes" answer.
/// </summary>
public sealed class FileViewerPanel : UserControl
{
    private readonly TextBlock _title;
    private readonly TextBox _editor;
    private readonly MarkdownView _preview = new() { Profile = MarkdownProfile.Code };
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly Grid _findBar;
    private readonly TextBox _findBox = new();
    private readonly TextBlock _findCount;
    private readonly StackPanel _readActions = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _editActions = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly Button _previewButton;
    private readonly TextBlock _alert;

    /// <summary>The reference's refusal when the file is outside what the session may write.</summary>
    private const string CannotSave =
        "This file can’t be saved. Its path is outside the working directory, or this is a remote session.";

    private const double PaneChromeWidth = PanePrimitives.ChromeWidth;

    private string _path = "";
    private string _original = "";
    private int? _line;
    private int? _endLine;
    private bool _showingPreview;

    public FileViewerPanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PanePrimitives.HeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title = PanePrimitives.Title(SidePanes.Title(SidePanes.File));
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_title);

        _previewButton = PanePrimitives.IconButton("EyeGlyph", "Preview", TogglePreview);
        _readActions.Children.Add(_previewButton);
        _readActions.Children.Add(PanePrimitives.IconButton("SearchGlyph", "Find in file", ToggleFind));
        _readActions.Children.Add(PanePrimitives.IconButton("EditIconGlyph", "Edit", BeginEdit));
        _readActions.Children.Add(PanePrimitives.IconButton("FolderOpenGlyph", "Open in…", OpenIn));
        _readActions.Children.Add(PanePrimitives.IconButton("DownloadGlyph", "Download file", SaveCopy));
        _readActions.Children.Add(PanePrimitives.IconButton("CopyIconGlyph", "Copy", CopyText));
        _readActions.Margin = new Thickness(0, 0, PaneChromeWidth, 0);
        _readActions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_readActions, 1);
        header.Children.Add(_readActions);

        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2, 8, 2) };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        cancel.Click += (_, _) => CancelEdit();
        var save = new Button { Content = "Save", Padding = new Thickness(8, 2, 8, 2) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => Save();
        _editActions.Children.Add(cancel);
        _editActions.Children.Add(save);
        _editActions.Margin = new Thickness(0, 0, PaneChromeWidth, 0);
        _editActions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_editActions, 1);
        header.Children.Add(_editActions);
        grid.Children.Add(header);

        _alert = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 2, 12, 6),
        };
        _alert.SetResourceReference(TextBlock.ForegroundProperty, "Danger100Brush");
        Grid.SetRow(_alert, 1);
        grid.Children.Add(_alert);

        _findBar = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 12, 6) };
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _findBox.SetResourceReference(StyleProperty, "PanelAddressBox");
        Controls.PlaceholderText.SetText(_findBox, "Find in file");
        _findBox.TextChanged += (_, _) => RunFind();
        _findBar.Children.Add(_findBox);
        _findCount = PanePrimitives.Muted("", 11.5);
        _findCount.VerticalAlignment = VerticalAlignment.Center;
        _findCount.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_findCount, 1);
        _findBar.Children.Add(_findCount);
        Grid.SetRow(_findBar, 2);
        grid.Children.Add(_findBar);

        _editor = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 12.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 4, 12, 16),
        };
        _editor.SetResourceReference(FontFamilyProperty, "CodeFontFamily");
        _editor.SetResourceReference(ForegroundProperty, "Text200Brush");
        _editor.SetResourceReference(
            System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "SelectionBrush");
        _editor.TextChanged += (_, _) => UpdateDirty();

        _host.Content = _editor;
        Grid.SetRow(_host, 3);
        grid.Children.Add(_host);
        Content = grid;
    }

    /// <summary>Whether the pane holds edits the user has not saved.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Opens a file, optionally scrolled to a line range (the reference's #L5-L20).</summary>
    public void Show(string path, int? line = null, int? endLine = null)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        _path = path;
        _line = line;
        _endLine = endLine;
        Reload();
    }

    /// <summary>Re-reads the open file from disk, keeping the viewer where it was.</summary>
    public void Reload()
    {
        if (_path.Length == 0)
        {
            _title.Text = SidePanes.Title(SidePanes.File);
            _host.Content = PanePrimitives.EmptyState(
                "FileGlyph", "No files to show yet. Type to search.", "");
            _readActions.Visibility = Visibility.Collapsed;
            return;
        }

        _readActions.Visibility = Visibility.Visible;
        _title.Text = Path.GetFileName(_path) + LineSuffix();
        _title.ToolTip = _path;
        try
        {
            _original = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _original = "";
            ShowAlert(CannotSave);
        }

        _editor.Text = _original;
        IsDirty = false;
        _previewButton.Visibility = IsMarkdown ? Visibility.Visible : Visibility.Collapsed;
        _showingPreview = false;
        _host.Content = _editor;
        if (_line is { } start)
        {
            ScrollToLine(start);
        }
    }

    private string LineSuffix() => _line is { } start
        ? _endLine is { } end && end != start ? $" #L{start}-L{end}" : $" #L{start}"
        : "";

    private bool IsMarkdown =>
        Path.GetExtension(_path).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(_path).Equals(".markdown", StringComparison.OrdinalIgnoreCase);

    private void ScrollToLine(int line)
    {
        var index = _editor.GetCharacterIndexFromLineIndex(Math.Max(0, line - 1));
        if (index >= 0)
        {
            _editor.Select(index, 0);
            _editor.ScrollToLine(Math.Max(0, line - 1));
        }
    }

    // ---- header actions ----

    private void TogglePreview()
    {
        _showingPreview = !_showingPreview;
        if (_showingPreview)
        {
            _preview.Markdown = _editor.Text;
            _host.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(14, 4, 14, 16),
                Content = _preview,
            };
            _previewButton.ToolTip = "View source";
            AutomationProperties.SetName(_previewButton, "View source");
        }
        else
        {
            _host.Content = _editor;
            _previewButton.ToolTip = "Preview";
            AutomationProperties.SetName(_previewButton, "Preview");
        }
    }

    /// <summary>Ctrl+F inside the pane, and the Search button: the reference's find bar.</summary>
    public void ToggleFind()
    {
        _findBar.Visibility = _findBar.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_findBar.Visibility == Visibility.Visible)
        {
            _findBox.Focus();
            _findBox.SelectAll();
        }
    }

    private void RunFind()
    {
        var needle = _findBox.Text;
        if (needle.Length == 0)
        {
            _findCount.Text = "";
            return;
        }

        var text = _editor.Text;
        var count = 0;
        var first = -1;
        var index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            if (first < 0)
            {
                first = index;
            }

            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.OrdinalIgnoreCase);
        }

        _findCount.Text = count == 0 ? "No matches" : $"{count} matches";
        if (first >= 0)
        {
            _editor.Select(first, needle.Length);
            _editor.ScrollToLine(_editor.GetLineIndexFromCharacterIndex(first));
        }
    }

    private void BeginEdit()
    {
        _editor.IsReadOnly = false;
        _readActions.Visibility = Visibility.Collapsed;
        _editActions.Visibility = Visibility.Visible;
        _host.Content = _editor;
        _showingPreview = false;
        _editor.Focus();
    }

    private void CancelEdit()
    {
        if (IsDirty && !ConfirmDiscard())
        {
            return;
        }

        _editor.Text = _original;
        EndEdit();
    }

    private void EndEdit()
    {
        _editor.IsReadOnly = true;
        IsDirty = false;
        _readActions.Visibility = Visibility.Visible;
        _editActions.Visibility = Visibility.Collapsed;
        HideAlert();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, _editor.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _original = _editor.Text;
            EndEdit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowAlert(CannotSave);
        }
    }

    private void UpdateDirty() => IsDirty = !_editor.IsReadOnly && _editor.Text != _original;

    /// <summary>
    /// The reference's guard before the pane leaves a file with edits in it: the
    /// "Unsaved changes" question, answered by "Discard changes".
    /// </summary>
    public bool ConfirmDiscard()
    {
        if (!IsDirty)
        {
            return true;
        }

        var discard = ConfirmDialog.Ask(
            Window.GetWindow(this),
            "Unsaved changes",
            "You have unsaved changes. Are you sure you want to discard them?",
            "Discard changes",
            focusCancel: true);
        if (discard)
        {
            IsDirty = false;
        }

        return discard;
    }

    private void OpenIn()
    {
        if (_path.Length == 0 || !File.Exists(_path))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{_path}\"",
                UseShellExecute = false,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No shell to reveal it in; nothing sensible to do.
        }
    }

    private void SaveCopy()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(_path) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _editor.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowAlert("This file changed on disk since you started editing.");
        }
    }

    private void CopyText()
    {
        try
        {
            Clipboard.SetText(_editor.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process owns the clipboard; nothing to report.
        }
    }

    private void ShowAlert(string text)
    {
        _alert.Text = text;
        _alert.Visibility = Visibility.Visible;
    }

    private void HideAlert() => _alert.Visibility = Visibility.Collapsed;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ToggleFind();
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
