using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The Plan pane, ported from the reference's MM/NM (ion-dist chunk c360a9e1c-DUoNQd2W.js
/// at the "No plan yet" literal): the session's plan file rendered as markdown in a 68ch
/// column, a "Tasks" section under it carrying the todo checklist, and — when there is
/// neither — the empty state over a Checklist glyph. Its header actions are the
/// reference's two: "Open in…" and Copy plan, which becomes Copied once it has copied.
/// </summary>
public sealed class PlanPanel : UserControl
{
    private readonly MarkdownView _markdown = new() { Profile = MarkdownProfile.Code };
    private readonly StackPanel _tasks = new();
    private readonly StackPanel _body = new();
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly Button _copyButton;
    private readonly Button _openInButton;
    private string _planText = "";
    private string _planPath = "";

    public PlanPanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PanePrimitives.HeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(PanePrimitives.Title(SidePanes.Title(SidePanes.Plan)));
        _openInButton = PanePrimitives.IconButton("FolderOpenGlyph", "Open in…", OpenIn);
        _openInButton.Margin = new Thickness(8, 0, 0, 0);
        header.Children.Add(_openInButton);
        _copyButton = PanePrimitives.IconButton("CopyIconGlyph", "Copy plan", CopyPlan);
        header.Children.Add(_copyButton);
        grid.Children.Add(header);

        _body.Margin = new Thickness(0, 0, 0, 24);
        _body.MaxWidth = PanePrimitives.ProseWidth;
        _body.HorizontalAlignment = HorizontalAlignment.Center;
        _body.Children.Add(_markdown);
        _body.Children.Add(_tasks);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 4, 16, 12),
            Content = _host,
        };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);
        Content = grid;
    }

    /// <summary>Reads the session's plan file and its todos into the pane.</summary>
    public void Load(ChatViewModel viewModel)
    {
        var session = viewModel.Session;
        _planPath = PlanModeTools.PlanFilePath(session.WorkingDirectory, session.Id);
        _planText = ReadPlan(_planPath);
        BuildTasks(viewModel.Todos);

        var hasTasks = _tasks.Children.Count > 0;
        if (_planText.Length == 0 && !hasTasks)
        {
            // The reference names the assistant here; in front of this app's user it is Jarvis.
            _host.Content = PanePrimitives.EmptyState(
                "ChecklistGlyph",
                "No plan yet",
                "Jarvis writes the plan here as it explores. Keep chatting.");
            _openInButton.Visibility = Visibility.Collapsed;
            _copyButton.Visibility = Visibility.Collapsed;
            return;
        }

        _markdown.Markdown = _planText;
        _markdown.Visibility = _planText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _openInButton.Visibility = _planText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _copyButton.Visibility = _planText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _host.Content = _body;
    }

    private static string ReadPlan(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private void BuildTasks(IReadOnlyList<TodoItem> todos)
    {
        _tasks.Children.Clear();
        if (todos.Count == 0)
        {
            _tasks.Visibility = Visibility.Collapsed;
            return;
        }

        _tasks.Visibility = Visibility.Visible;
        _tasks.Margin = new Thickness(0, 16, 0, 0);
        _tasks.Children.Add(PanePrimitives.SectionHeading("Tasks"));
        foreach (var todo in todos)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var (glyph, colorKey) = todo.Status switch
            {
                TodoStatus.Completed => ("●", "Success100Brush"),
                TodoStatus.InProgress => ("◐", "AccentBrandBrush"),
                _ => ("○", "Text500Brush"),
            };
            var dot = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            row.Children.Add(dot);

            var label = new TextBlock
            {
                Text = todo.Content,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                todo.Status == TodoStatus.Completed ? "Text400Brush" : "Text200Brush");
            row.Children.Add(label);
            _tasks.Children.Add(row);
        }
    }

    private void CopyPlan()
    {
        if (_planText.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_planText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process owns the clipboard; the button simply does not confirm.
            return;
        }

        _copyButton.ToolTip = "Copied";
        _copyButton.SetValue(AutomationProperties.NameProperty, "Copied");
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _copyButton.ToolTip = "Copy plan";
            _copyButton.SetValue(AutomationProperties.NameProperty, "Copy plan");
        };
        timer.Start();
    }

    /// <summary>"Open in…" — the plan file is revealed rather than shell-executed.</summary>
    private void OpenIn()
    {
        if (_planPath.Length == 0 || !File.Exists(_planPath))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{_planPath}\"",
                UseShellExecute = false,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No shell to reveal it in; nothing sensible to do.
        }
    }
}
