using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using Role = JarvisCode.Core.Models.Role;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The Session pane, ported from the reference's _U (ion-dist chunk c360a9e1c-DUoNQd2W.js,
/// its session case): another session's transcript, opened read-only beside this one. The
/// reference hands that view a context with every opener null — no file opener, no
/// terminal run, no subagent opener — so it is a reader, not a second surface, and this
/// port renders the stored messages rather than re-hosting a live one.
/// </summary>
public sealed class SessionPanePanel : UserControl
{
    private readonly TextBlock _title;
    private readonly StackPanel _content = new();
    private readonly ContentControl _host = new() { Focusable = false };

    public SessionPanePanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PanePrimitives.HeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _title = PanePrimitives.Title(SidePanes.Title(SidePanes.Session));
        grid.Children.Add(_title);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 4, 14, 20),
            Content = _host,
        };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);
        Content = grid;
    }

    /// <summary>The session the pane is reading, set before the pane opens.</summary>
    public string? TargetSessionId { get; set; }

    public void Load(AppServices services, string sessionId)
    {
        TargetSessionId = sessionId;
        _content.Children.Clear();
        var session = services.Sessions.LoadAsync(sessionId).GetAwaiter().GetResult();
        if (session is null)
        {
            _title.Text = SidePanes.Title(SidePanes.Session);
            _host.Content = PanePrimitives.EmptyState(
                "FileGlyph", "No messages yet", "");
            return;
        }

        _title.Text = session.Title;
        _host.Content = _content;

        var summary = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
        summary.Children.Add(PanePrimitives.Muted(session.WorkingDirectory, 11.5));
        if (session.ModelId is { Length: > 0 } model)
        {
            summary.Children.Add(PanePrimitives.Muted(model, 11.5));
        }

        summary.Children.Add(PanePrimitives.Muted(
            RunHistoryPresentation.RelativeTime(session.UpdatedAt, DateTimeOffset.Now), 11.5));
        _content.Children.Add(summary);

        if (session.Messages.Count == 0)
        {
            _content.Children.Add(PanePrimitives.EmptyState("FileGlyph", "No messages yet", ""));
            return;
        }

        foreach (var message in session.Messages)
        {
            var text = SystemReminders.VisibleText(message);
            if (text.Trim().Length == 0)
            {
                continue;
            }

            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            block.Children.Add(PanePrimitives.Muted(message.Role == Role.User ? "You" : "Jarvis", 11));
            var body = new System.Windows.Controls.TextBox
            {
                Text = text,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            };
            body.SetResourceReference(Control.ForegroundProperty, "Text200Brush");
            body.SetResourceReference(
                System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "SelectionBrush");
            block.Children.Add(body);
            _content.Children.Add(block);
        }
    }
}
