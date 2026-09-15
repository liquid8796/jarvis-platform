using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The reference's PR bar above the composer (desktop 1.40609.1.0: `bh` in the ccd
/// chunk `ca80fca8d`@161933). Left to right: the PR state icon, its `#number` link,
/// the repo chip and the branch chip; then the trailing cluster — a warning, the
/// "behind" count, the ± diff, the CI button, and the mode button with its
/// "More PR options" menu — and the dismiss ×. What each of those reads is
/// <see cref="GitBarPresentation"/>'s; this draws it.
/// </summary>
public sealed class GitBarView : Border
{
    private readonly Grid _root = new();
    private readonly StackPanel _leading = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _trailing = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private GitBarRow? _row;
    private GitBarInput _input = new();

    public GitBarView()
    {
        Padding = new Thickness(10, 6, 6, 6);
        CornerRadius = new CornerRadius(10);
        Margin = new Thickness(0, 0, 0, 8);
        SetResourceReference(BackgroundProperty, "Bg300Brush");
        SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            GitBarPresentation.RepositoryControls);

        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.Children.Add(_leading);
        Grid.SetColumn(_trailing, 1);
        _root.Children.Add(_trailing);
        _rows.Children.Add(_root);
        Child = _rows;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The bar is a column of rows: the branch's own, then one per related or
    /// stacked pull request, which is how the reference stacks them.
    /// </summary>
    private readonly StackPanel _rows = new();

    private IReadOnlyList<RelatedPr> _extra = [];

    /// <summary>The PR number link, or the mode button, was pressed.</summary>
    public event EventHandler<string>? OpenUrlRequested;

    /// <summary>The mode button in a create mode: run `gh pr create` the chosen way.</summary>
    public event EventHandler<PrMode>? PrActionRequested;

    /// <summary>The mode button in commit mode: send the reference's commit prompt.</summary>
    public event EventHandler<string>? CommitRequested;

    /// <summary>The ± cluster: open the diff pane.</summary>
    public event EventHandler? DiffRequested;

    /// <summary>The CI button: show the checks.</summary>
    public event EventHandler? CiRequested;

    /// <summary>The × : hide the bar for this session.</summary>
    public event EventHandler? Dismissed;

    /// <summary>A row of the repo or branch chip's menu was picked.</summary>
    public event EventHandler<WorkingDirectoryAction>? WorkingDirectoryAction;

    /// <summary>The create mode changed from the "More PR options" menu.</summary>
    public event EventHandler<string>? CreateModeChanged;

    public GitBarRow? Row => _row;

    public void Show(GitBarInput input) => Show(input, []);

    /// <summary>
    /// Draws the bar with the extra rows the reference puts beside the branch's own
    /// pull request: the ones this session opened earlier, and the stack above it.
    /// </summary>
    public void Show(GitBarInput input, IReadOnlyList<RelatedPr> extra)
    {
        _input = input;
        _extra = GitBarPresentation.ExtraRows(input.Pr, extra);
        _row = GitBarPresentation.Resolve(input);
        Render();
    }

    private void Render()
    {
        var row = _row;
        if (row is null || !row.Visible)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        _leading.Children.Clear();
        _trailing.Children.Clear();
        while (_rows.Children.Count > 1)
        {
            _rows.Children.RemoveAt(1);
        }

        foreach (var extra in _extra)
        {
            _rows.Children.Add(ExtraRow(extra));
        }

        if (row.State != PrDisplayState.None && row.StateLabel is { } stateLabel)
        {
            _leading.Children.Add(StateIcon(row.State, stateLabel));
            if (_input.Pr is { } pr)
            {
                _leading.Children.Add(PrNumberLink(pr, row.State));
            }
        }

        _leading.Children.Add(RepoChip(row));
        _leading.Children.Add(BranchChip(row));

        if (row.Behind > 0)
        {
            _trailing.Children.Add(BehindIndicator(row.Behind));
        }

        if (row.ShowDiff)
        {
            _trailing.Children.Add(DiffCluster());
        }

        if (row.Ci is { } ci && row.CiLabel is { } ciLabel)
        {
            _trailing.Children.Add(CiButton(ci, ciLabel));
        }

        if (row.ShowButton)
        {
            _trailing.Children.Add(ModeControl(row));
        }

        _trailing.Children.Add(DismissButton());
    }

    /// <summary>
    /// One extra pull request's row: the reference draws the same row component
    /// without the diff, the CI slot or the behind count, and its mode control is
    /// View — or the tinted Merged label — rather than a create mode.
    /// </summary>
    private FrameworkElement ExtraRow(RelatedPr pr)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leading = new StackPanel { Orientation = Orientation.Horizontal };
        var label = GitBarPresentation.StateLabel(pr.State);
        leading.Children.Add(StateIcon(pr.State, label));

        var link = new Button
        {
            Content = new TextBlock { Text = $"#{pr.Number}", FontSize = 12.5 },
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 0, 12, 0),
            ToolTip = GitBarPresentation.OpenPullRequest,
        };
        link.SetResourceReference(StyleProperty, "GhostButton");
        link.SetResourceReference(Control.ForegroundProperty, GitBarPresentation.StateBrushKey(pr.State));
        link.Click += (_, _) => OpenUrlRequested?.Invoke(this, pr.Url);
        leading.Children.Add(link);

        var branch = Chip(pr.Branch, 220);
        branch.ToolTip = pr.Branch;
        branch.Click += (_, _) => OpenUrlRequested?.Invoke(this, pr.Url);
        leading.Children.Add(branch);
        grid.Children.Add(leading);

        var mode = GitBarPresentation.ExtraMode(pr.State);
        var accent = GitBarPresentation.Accent(pr.State);
        FrameworkElement trailing;
        if (accent != GitBarAccent.None)
        {
            var tinted = new TextBlock
            {
                Text = GitBarPresentation.ButtonLabel(mode),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            tinted.SetResourceReference(
                TextBlock.ForegroundProperty, GitBarPresentation.AccentBrushKey(accent));
            trailing = tinted;
        }
        else
        {
            var view = new Button { Content = GitBarPresentation.ButtonLabel(mode) };
            view.SetResourceReference(StyleProperty, "SecondaryButton");
            view.Click += (_, _) => OpenUrlRequested?.Invoke(this, pr.Url);
            trailing = view;
        }

        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);
        return grid;
    }

    private FrameworkElement StateIcon(PrDisplayState state, string label)
    {
        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = label,
        };
        dot.SetResourceReference(Shape.FillProperty, GitBarPresentation.StateBrushKey(state));
        dot.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label);
        return dot;
    }

    private FrameworkElement PrNumberLink(PrInfo pr, PrDisplayState state)
    {
        var link = new Button
        {
            Content = new TextBlock { Text = $"#{pr.Number}", FontSize = 12.5 },
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 0, 12, 0),
            ToolTip = string.IsNullOrEmpty(pr.Url)
                ? GitBarPresentation.ViewOnGithub
                : GitBarPresentation.OpenPullRequest,
        };
        link.SetResourceReference(StyleProperty, "GhostButton");
        link.SetResourceReference(Control.ForegroundProperty, GitBarPresentation.StateBrushKey(state));
        link.Click += (_, _) => OpenUrlRequested?.Invoke(this, pr.Url);
        return link;
    }

    private FrameworkElement RepoChip(GitBarRow row)
    {
        var chip = Chip(row.RepoName, 160);
        chip.ToolTip = WorkingDirectoryMenu.ChipTooltip(_input.Cwd, [row.RepoName], row.RepoName);
        chip.ContextMenu = WorkingDirectoryContextMenu();
        chip.Click += (_, _) =>
        {
            chip.ContextMenu.PlacementTarget = chip;
            chip.ContextMenu.IsOpen = true;
        };
        return chip;
    }

    private FrameworkElement BranchChip(GitBarRow row)
    {
        var chip = Chip(row.BranchName, 220);
        chip.ToolTip = row.BranchName;

        var menu = new ContextMenu { MinWidth = 200 };
        var copy = new MenuItem { Header = WorkingDirectoryMenu.CopyBranchName };
        copy.Click += (_, _) => WorkingDirectoryAction?.Invoke(this, Services.WorkingDirectoryAction.CopyBranchName);
        menu.Items.Add(copy);
        if (_input.BranchUrl is { Length: > 0 })
        {
            var open = new MenuItem { Header = GitBarPresentation.OpenBranchOnGithub };
            open.Click += (_, _) => OpenUrlRequested?.Invoke(this, _input.BranchUrl!);
            menu.Items.Add(open);
        }

        chip.ContextMenu = menu;
        chip.Click += (_, _) =>
        {
            menu.PlacementTarget = chip;
            menu.IsOpen = true;
        };
        return chip;
    }

    private ContextMenu WorkingDirectoryContextMenu()
    {
        var menu = new ContextMenu { MinWidth = 200 };
        var context = new WorkingDirectoryContext
        {
            RepoName = _input.RepoName,
            Cwd = _input.Cwd,
            Branch = _input.BranchName,
            RepoUrl = _input.RepoUrl,
            CanChangeDirectory = true,
            CanOpenInTerminal = true,
        };
        menu.Items.Add(Panels.SessionMenu.SectionHeader(WorkingDirectoryMenu.Label(context)));
        foreach (var row in WorkingDirectoryMenu.Build(context))
        {
            if (row.SeparatorBefore)
            {
                menu.Items.Add(new Separator());
            }

            var item = new MenuItem { Header = row.Label };
            var action = row.Action;
            item.Click += (_, _) =>
            {
                if (action == Services.WorkingDirectoryAction.OpenRepoOnGithub && _input.RepoUrl is { } url)
                {
                    OpenUrlRequested?.Invoke(this, url);
                    return;
                }

                WorkingDirectoryAction?.Invoke(this, action);
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private static Button Chip(string text, double maxWidth)
    {
        var chip = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            Padding = new Thickness(4, 0, 4, 0),
            MaxWidth = maxWidth,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        chip.SetResourceReference(StyleProperty, "GhostButton");
        return chip;
    }

    /// <summary>The reference's behind indicator: a down arrow and the count.</summary>
    private static FrameworkElement BehindIndicator(int count)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var arrow = new TextBlock { Text = "↓", FontSize = 12 };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        panel.Children.Add(arrow);
        var text = new TextBlock { Text = count.ToString(), FontSize = 12.5, Margin = new Thickness(2, 0, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        panel.Children.Add(text);
        return panel;
    }

    private FrameworkElement DiffCluster()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var adds = new TextBlock { Text = $"+{_input.Added:N0}", FontSize = 12.5, FontWeight = FontWeights.Medium };
        adds.SetResourceReference(TextBlock.ForegroundProperty, "GitOpenedBrush");
        panel.Children.Add(adds);
        var dels = new TextBlock
        {
            Text = $"−{_input.Removed:N0}",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(6, 0, 0, 0),
        };
        dels.SetResourceReference(TextBlock.ForegroundProperty, "GitRemovedBrush");
        panel.Children.Add(dels);

        var button = new Button
        {
            Content = panel,
            Padding = new Thickness(6, 0, 6, 0),
            ToolTip = GitBarPresentation.ViewDiff,
        };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.Click += (_, _) => DiffRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    private FrameworkElement CiButton(CiStatus status, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(CiDot(status));
        var text = new TextBlock { Text = "CI", FontSize = 12.5, Margin = new Thickness(5, 0, 3, 0) };
        panel.Children.Add(text);
        var caret = new TextBlock { Text = "▾", FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
        caret.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        panel.Children.Add(caret);

        var button = new Button { Content = panel, Padding = new Thickness(6, 0, 6, 0), ToolTip = label };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label);
        button.Click += (_, _) => CiRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    /// <summary>The CI dot: a filled circle for a settled run, a spinner while checks run.</summary>
    private static FrameworkElement CiDot(CiStatus status)
    {
        if (status is CiStatus.Pending or CiStatus.Loading)
        {
            return new SpinnerGlyph { Width = 11, Height = 11, IsSpinning = true, VerticalAlignment = VerticalAlignment.Center };
        }

        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, status switch
        {
            CiStatus.Failed => "GitRemovedBrush",
            CiStatus.Passed => "GitOpenedBrush",
            _ => "Text500Brush",
        });
        return dot;
    }

    private FrameworkElement ModeControl(GitBarRow row)
    {
        // A merged or queued row is a tinted label rather than a control, which is
        // the reference's `accent` branch.
        if (row.Accent != GitBarAccent.None)
        {
            var label = new TextBlock
            {
                Text = row.ButtonLabel,
                FontSize = 12.5,
                Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, GitBarPresentation.AccentBrushKey(row.Accent));
            return label;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        var button = new Button
        {
            Content = new TextBlock { Text = row.BusyLabel ?? row.ButtonLabel, FontSize = 12.5 },
            Padding = new Thickness(10, 2, 10, 2),
            IsEnabled = !row.ButtonDisabled,
            ToolTip = row.DisabledReason,
        };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.Click += (_, _) =>
        {
            if (row.Mode == PrMode.Commit)
            {
                CommitRequested?.Invoke(this, GitBarPresentation.CommitPrompt);
            }
            else
            {
                PrActionRequested?.Invoke(this, row.Mode);
            }
        };
        panel.Children.Add(button);

        if (row.MenuItems.Count > 0)
        {
            var menu = new ContextMenu { MinWidth = 200 };
            foreach (var item in row.MenuItems)
            {
                var entry = new MenuItem
                {
                    Header = item.Label,
                    IsCheckable = true,
                    IsChecked = item.Checked,
                    IsEnabled = !item.Disabled,
                };
                var key = item.Key;
                entry.Click += (_, _) => CreateModeChanged?.Invoke(this, key);
                menu.Items.Add(entry);
            }

            var caret = new Button
            {
                Content = new TextBlock { Text = "▾", FontSize = 9 },
                Padding = new Thickness(4, 0, 4, 0),
                ToolTip = GitBarPresentation.MorePrOptions,
            };
            caret.SetResourceReference(StyleProperty, "GhostButton");
            caret.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                GitBarPresentation.MorePrOptions);
            caret.ContextMenu = menu;
            caret.Click += (_, _) =>
            {
                menu.PlacementTarget = caret;
                menu.IsOpen = true;
            };
            panel.Children.Add(caret);
        }

        return panel;
    }

    private FrameworkElement DismissButton()
    {
        var button = new Button
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            Content = new TextBlock { Text = "✕", FontSize = 10 },
        };
        button.SetResourceReference(StyleProperty, "IconButton");
        button.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Dismiss");
        button.Click += (_, _) => Dismissed?.Invoke(this, EventArgs.Empty);
        return button;
    }
}
