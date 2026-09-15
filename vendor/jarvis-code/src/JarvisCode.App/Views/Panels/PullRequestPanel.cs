using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The Pull request pane, ported from the reference's Gt/Qt and its panel body (ion-dist
/// chunks c44d00c7f-CrclqAq4.js and ca9ee6f68-BJyCBfEF.js): a header of #n · state badge ·
/// author · updated, then Description, Reviews, Checks and Activity sections and the
/// changed-file list with its ± counts and a Review changes action. Its data comes from
/// `gh pr view`, the same CLI the PR-activity subscription already polls.
/// </summary>
public sealed class PullRequestPanel : UserControl
{
    private readonly StackPanel _header = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _content = new();
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly Button _menuButton;
    private PullRequestView? _view;
    private string _workingDirectory = "";

    public PullRequestPanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PanePrimitives.HeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.Margin = new Thickness(12, 0, 0, 0);
        headerRow.Children.Add(_header);
        _menuButton = PanePrimitives.IconButton("KebabGlyph", "Pull request actions", ShowMenu);
        _menuButton.Margin = new Thickness(0, 0, PanePrimitives.ChromeWidth, 0);
        Grid.SetColumn(_menuButton, 1);
        headerRow.Children.Add(_menuButton);
        grid.Children.Add(headerRow);

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

    /// <summary>"Review changes" — the diff pane opens on this pull request's branch.</summary>
    public event EventHandler? ReviewChangesRequested;

    /// <summary>Reads the repository's current pull request through `gh`.</summary>
    public async Task LoadAsync(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        ShowMessage("No pull request");
        var json = await GhAsync(workingDirectory,
            "pr view --json number,title,body,author,state,isDraft,reviewDecision,mergeable,updatedAt,url," +
            "headRefOid,additions,deletions,files,reviews,statusCheckRollup");
        _view = json is null ? null : PullRequestPresentation.Parse(json);
        Build();
    }

    private void ShowMessage(string message)
    {
        _header.Children.Clear();
        _header.Children.Add(PanePrimitives.Title(SidePanes.Title(SidePanes.PullRequest)));
        _host.Content = PanePrimitives.EmptyState("GitPullRequestGlyph", message, "");
        _menuButton.Visibility = Visibility.Collapsed;
    }

    private void Build()
    {
        if (_view is not { } view)
        {
            return;
        }

        _menuButton.Visibility = Visibility.Visible;
        _header.Children.Clear();
        var number = new TextBlock { Text = $"#{view.Number}", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        number.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        _header.Children.Add(number);
        _header.Children.Add(Separator());

        var badge = new TextBlock
        {
            Text = PullRequestPresentation.BadgeLabel(view.Badge),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.SetResourceReference(TextBlock.ForegroundProperty, PullRequestPresentation.BadgeBrushKey(view.Badge));
        _header.Children.Add(badge);

        if (view.Author.Length > 0)
        {
            _header.Children.Add(Separator());
            var author = new TextBlock { Text = view.Author, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            author.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            _header.Children.Add(author);
        }

        if (view.UpdatedAt is { } updated)
        {
            _header.Children.Add(Separator());
            var when = PanePrimitives.Muted(
                RunHistoryPresentation.RelativeTime(updated, DateTimeOffset.Now), 11.5);
            when.VerticalAlignment = VerticalAlignment.Center;
            _header.Children.Add(when);
        }

        _content.Children.Clear();
        _host.Content = _content;

        var title = new TextBlock
        {
            Text = view.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        _content.Children.Add(title);

        AddSection("Description", view.Body is { Length: > 0 } body
            ? new MarkdownView { Markdown = body, Profile = MarkdownProfile.Code }
            : PanePrimitives.Muted("No description provided."));

        if (view.Reviews.Count > 0)
        {
            var reviews = new StackPanel();
            foreach (var review in view.Reviews)
            {
                reviews.Children.Add(PanePrimitives.Muted(review, 12));
            }

            AddSection("Reviews", reviews);
        }

        if (view.ChecksPassed + view.ChecksFailed + view.ChecksPending > 0)
        {
            var parts = new List<string>();
            if (view.ChecksFailed > 0)
            {
                parts.Add($"{view.ChecksFailed} failing");
            }

            if (view.ChecksPending > 0)
            {
                parts.Add(view.ChecksPending == 1 ? "1 check still running" : $"{view.ChecksPending} checks still running");
            }

            if (view.ChecksPassed > 0)
            {
                parts.Add($"{view.ChecksPassed} passing");
            }

            AddSection("Checks", PanePrimitives.Muted(string.Join(" · ", parts), 12));
        }

        AddSection("Activity", BuildFiles(view));
    }

    private FrameworkElement BuildFiles(PullRequestView view)
    {
        var panel = new StackPanel();
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var review = new Button { Content = "Review changes", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 10, 0) };
        review.SetResourceReference(StyleProperty, "SecondaryButton");
        review.Click += (_, _) => ReviewChangesRequested?.Invoke(this, EventArgs.Empty);
        summary.Children.Add(review);
        var counts = PanePrimitives.Muted(PullRequestPresentation.FileCountLabel(view.Files.Count), 11.5);
        counts.VerticalAlignment = VerticalAlignment.Center;
        summary.Children.Add(counts);

        var adds = new TextBlock
        {
            Text = $"+{view.Additions}",
            FontSize = 11.5,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        adds.SetResourceReference(TextBlock.ForegroundProperty, "Success100Brush");
        summary.Children.Add(adds);
        var dels = new TextBlock
        {
            Text = $"−{view.Deletions}",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dels.SetResourceReference(TextBlock.ForegroundProperty, "Danger100Brush");
        summary.Children.Add(dels);
        panel.Children.Add(summary);

        foreach (var file in view.Files)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var path = new TextBlock
            {
                Text = file.Path,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            path.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
            row.Children.Add(path);

            var change = new TextBlock { FontSize = 11.5 };
            change.Inlines.Add(new System.Windows.Documents.Run($"+{file.Additions} ")
            {
                Foreground = (System.Windows.Media.Brush)FindResource("Success100Brush"),
            });
            change.Inlines.Add(new System.Windows.Documents.Run($"−{file.Deletions}")
            {
                Foreground = (System.Windows.Media.Brush)FindResource("Danger100Brush"),
            });
            Grid.SetColumn(change, 1);
            row.Children.Add(change);
            panel.Children.Add(row);
        }

        return panel;
    }

    private void AddSection(string heading, FrameworkElement body)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        section.Children.Add(PanePrimitives.SectionHeading(heading));
        section.Children.Add(body);
        _content.Children.Add(section);
    }

    private static TextBlock Separator()
    {
        var dot = new TextBlock
        {
            Text = " · ",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return dot;
    }

    /// <summary>The reference's pr-links group: Open on GitHub · Review changes · Copy link.</summary>
    private void ShowMenu()
    {
        if (_view is not { } view)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = _menuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 180,
        };

        var open = new MenuItem { Header = "Open on GitHub" };
        open.Click += (_, _) => OpenUrl(view.Url);
        menu.Items.Add(open);

        var review = new MenuItem { Header = "Review changes" };
        review.Click += (_, _) => ReviewChangesRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(review);

        var copyLink = new MenuItem { Header = "Copy link" };
        copyLink.Click += (_, _) => Copy(view.Url);
        menu.Items.Add(copyLink);

        if (view.HeadSha.Length > 0)
        {
            var copySha = new MenuItem { Header = "Copy commit SHA" };
            copySha.Click += (_, _) => Copy(view.HeadSha);
            menu.Items.Add(copySha);
        }

        menu.IsOpen = true;
    }

    private static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process owns the clipboard.
        }
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser to open it in.
        }
    }

    private static async Task<string?> GhAsync(string workingDirectory, string arguments)
    {
        if (workingDirectory.Length == 0 || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
