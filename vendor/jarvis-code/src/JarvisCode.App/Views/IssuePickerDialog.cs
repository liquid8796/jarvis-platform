using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The reference's Import issue picker (its <c>IA</c> shell around <c>jA</c> in the
/// ccd chunk <c>c11959232-DM8o5ho4.js</c>): a header, a search box debounced by
/// 300ms, and a scrolling list capped at half the viewport whose rows are a state
/// dot, <c>#number title</c>, and the repository with the issue's labels beside it.
/// Its four empty states are Loading… / the failure line / the no-issues line /
/// "No issues match your search."
/// </summary>
public sealed class IssuePickerDialog : Window
{
    private readonly string _workingDirectory;
    private readonly string _repoSlug;
    private readonly TextBox _search;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroller;
    private readonly DispatcherTimer _debounce;

    private GitHubIssue? _chosen;
    private string _query = "";
    private bool _loading = true;
    private bool _failed;

    private IssuePickerDialog(string workingDirectory, string repoSlug)
    {
        _workingDirectory = workingDirectory;
        _repoSlug = repoSlug;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(FontFamilyProperty, "UiFontFamily");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
        };

        var heading = new TextBlock
        {
            Text = GitHubIssues.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        _search = new TextBox();
        _search.SetResourceReference(StyleProperty, "InputTextBox");
        _search.SetValue(TagProperty, GitHubIssues.SearchPlaceholder);
        _search.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty, GitHubIssues.SearchPlaceholder);
        _search.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };

        _debounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(GitHubIssues.SearchDebounceMs),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _query = _search.Text.Trim();
            _ = LoadAsync();
        };

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.PrimaryScreenHeight / 2,
            Margin = new Thickness(0, 12, 0, 0),
            Content = _list,
        };

        var body = new StackPanel();
        body.Children.Add(heading);
        body.Children.Add(_search);
        body.Children.Add(_scroller);

        var card = new Border
        {
            Child = body,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 4,
                Opacity = 0.4,
                Color = Colors.Black,
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        Content = card;

        Loaded += (_, _) =>
        {
            _search.Focus();
            _ = LoadAsync();
        };
    }

    /// <summary>
    /// Opens the picker; null when it was cancelled, otherwise the context block the
    /// chosen issue puts on the composer.
    /// </summary>
    public static string? Pick(Window? owner, string workingDirectory)
    {
        var slug = GitHubIssues.RepoSlug(workingDirectory);
        if (slug is null)
        {
            return null;
        }

        var dialog = new IssuePickerDialog(workingDirectory, slug) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog._chosen is not { } issue)
        {
            return null;
        }

        var detail = GitHubIssues.Fetch(workingDirectory, slug, issue.Number);
        return GitHubIssues.ContextBlock(issue, detail);
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _failed = false;
        Render([]);
        var query = _query;
        IReadOnlyList<GitHubIssue> rows;
        try
        {
            rows = await Task.Run(() => GitHubIssues.Search(_workingDirectory, _repoSlug, query));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _loading = false;
            _failed = true;
            Render([]);
            return;
        }

        if (query != _query)
        {
            return;
        }

        _loading = false;
        Render(rows);
    }

    private void Render(IReadOnlyList<GitHubIssue> rows)
    {
        _list.Children.Clear();
        if (_loading)
        {
            _list.Children.Add(Empty(GitHubIssues.Loading));
            return;
        }

        if (_failed)
        {
            _list.Children.Add(Empty(GitHubIssues.Failed));
            return;
        }

        if (rows.Count == 0)
        {
            _list.Children.Add(Empty(_query.Length > 0 ? GitHubIssues.NoMatches : GitHubIssues.NoIssues));
            return;
        }

        foreach (var row in rows)
        {
            _list.Children.Add(Row(row));
        }
    }

    private FrameworkElement Row(GitHubIssue issue)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(0, 5, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        dot.SetResourceReference(
            Shape.FillProperty, issue.State == "closed" ? "GitMergedBrush" : "GitOpenedBrush");
        grid.Children.Add(dot);

        var lines = new StackPanel();
        var title = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
        var number = new Run($"#{issue.Number} ");
        number.SetResourceReference(Run.ForegroundProperty, "Text400Brush");
        title.Inlines.Add(number);
        title.Inlines.Add(new Run(issue.Title));
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        lines.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = issue.Labels.Count > 0
                ? $"{issue.Repo} · {string.Join(", ", issue.Labels)}"
                : issue.Repo,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        lines.Children.Add(subtitle);
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);

        var button = new Button
        {
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8, 10, 8),
        };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.Click += (_, _) =>
        {
            _chosen = issue;
            DialogResult = true;
        };
        return button;
    }

    private static TextBlock Empty(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(16, 32, 16, 32),
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        return block;
    }
}
