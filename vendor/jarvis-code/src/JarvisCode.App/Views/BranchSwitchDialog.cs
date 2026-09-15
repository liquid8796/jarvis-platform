using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The reference's dirty-tree dialog (the ccd chunk <c>c11959232-DM8o5ho4.js</c>):
/// "Uncommitted changes on {currentBranch}" over "Handle them before switching to
/// {targetBranch}.", the other-session count, the changed-file row with its ±
/// pair, and a footer whose split button carries Stash changes / Commit as WIP /
/// Discard changes as radios. Picking Discard swaps in the second step — "Discard
/// uncommitted changes?" over Back and a danger Discard.
/// </summary>
public sealed class BranchSwitchDialog : Window
{
    private readonly string _currentBranch;
    private readonly string _targetBranch;
    private readonly int _otherSessions;
    private readonly WorkingTreeStatus _status;

    private readonly StackPanel _body = new();
    private readonly StackPanel _footer = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 18, 0, 0),
    };

    private DirtyTreeResolution _resolution = DirtyTreeResolution.Stash;
    private bool _confirmingDiscard;

    private BranchSwitchDialog(
        string currentBranch, string targetBranch, int otherSessions, WorkingTreeStatus status)
    {
        _currentBranch = currentBranch;
        _targetBranch = targetBranch;
        _otherSessions = otherSessions;
        _status = status;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(FontFamilyProperty, "UiFontFamily");
        PreviewKeyDown += OnKey;

        var content = new StackPanel();
        content.Children.Add(_body);
        content.Children.Add(_footer);

        var card = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 16),
            MinWidth = 380,
            MaxWidth = 480,
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

        Render();
    }

    /// <summary>
    /// Raises the dialog; null when the user cancelled, otherwise the answer they
    /// chose. The caller runs it — <see cref="BranchSwitch.Resolve"/> does the git.
    /// </summary>
    public static DirtyTreeResolution? Ask(
        Window? owner,
        string currentBranch,
        string targetBranch,
        int otherSessions,
        WorkingTreeStatus status)
    {
        var dialog = new BranchSwitchDialog(currentBranch, targetBranch, otherSessions, status)
        {
            Owner = owner,
        };
        return dialog.ShowDialog() == true ? dialog._resolution : null;
    }

    private void Render()
    {
        _body.Children.Clear();
        _footer.Children.Clear();

        if (_confirmingDiscard)
        {
            _body.Children.Add(TitleBlock(SessionDialogs.DiscardConfirmTitle));
            _body.Children.Add(WithCode(
                Description(""), SessionDialogs.DiscardConfirmBody(_currentBranch), _currentBranch));

            var back = Secondary(SessionDialogs.Back);
            back.Click += (_, _) =>
            {
                _confirmingDiscard = false;
                Render();
            };
            _footer.Children.Add(back);

            var discard = new Button { Content = SessionDialogs.Discard, Margin = new Thickness(8, 0, 0, 0) };
            discard.SetResourceReference(StyleProperty, "DangerButton");
            discard.Click += (_, _) => DialogResult = true;
            _footer.Children.Add(discard);
            return;
        }

        _body.Children.Add(WithCode(
            TitleBlock(""), SessionDialogs.UncommittedOnBranchTitle(_currentBranch), _currentBranch));
        _body.Children.Add(WithCode(
            Description(""), SessionDialogs.HandleThemBody(_targetBranch), _targetBranch));

        if (_otherSessions > 0)
        {
            _body.Children.Add(Description(SessionDialogs.OtherSessionsUsingFolder(_otherSessions)));
        }

        if (_status.Files.Count > 0)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var count = new TextBlock { Text = BranchSwitch.FilesChanged(_status.Files.Count), FontSize = 12.5 };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
            row.Children.Add(count);
            if (_status.Additions > 0 || _status.Deletions > 0)
            {
                var diff = new TextBlock
                {
                    Margin = new Thickness(8, 0, 0, 0),
                    FontSize = 12.5,
                };
                diff.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");
                diff.SetValue(
                    System.Windows.Automation.AutomationProperties.NameProperty,
                    BranchSwitch.DiffLabel(_status.Additions, _status.Deletions));
                var plus = new Run($"+{_status.Additions}");
                plus.SetResourceReference(Run.ForegroundProperty, "SuccessBrush");
                var minus = new Run($" −{_status.Deletions}");
                minus.SetResourceReference(Run.ForegroundProperty, "DangerBrush");
                diff.Inlines.Add(plus);
                diff.Inlines.Add(minus);
                row.Children.Add(diff);
            }

            _body.Children.Add(row);
        }

        var cancel = Secondary(SessionDialogs.Cancel);
        cancel.Click += (_, _) => DialogResult = false;
        _footer.Children.Add(cancel);

        var primary = Secondary(BranchSwitch.ResolutionLabel(_resolution));
        primary.Margin = new Thickness(8, 0, 0, 0);
        primary.Click += (_, _) => Commit();
        primary.ContextMenu = BuildMenu(primary);
        _footer.Children.Add(primary);

        var chevron = Secondary("▾");
        chevron.Margin = new Thickness(2, 0, 0, 0);
        chevron.Click += (_, _) =>
        {
            var menu = BuildMenu(chevron);
            menu.PlacementTarget = chevron;
            menu.IsOpen = true;
        };
        _footer.Children.Add(chevron);
    }

    private ContextMenu BuildMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, MinWidth = 180 };
        foreach (var resolution in new[]
                 {
                     DirtyTreeResolution.Stash, DirtyTreeResolution.Commit, DirtyTreeResolution.Discard,
                 })
        {
            var item = new MenuItem
            {
                Header = BranchSwitch.ResolutionLabel(resolution),
                IsCheckable = true,
                IsChecked = resolution == _resolution,
            };
            var picked = resolution;
            item.Click += (_, _) =>
            {
                _resolution = picked;
                Render();
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private void Commit()
    {
        if (_resolution == DirtyTreeResolution.Discard)
        {
            _confirmingDiscard = true;
            Render();
            return;
        }

        DialogResult = true;
    }

    /// <summary>
    /// The reference renders the branch name inside its sentence as a code chip
    /// (its <c>&lt;b&gt;</c> slot, which it fills with
    /// <c>&lt;code className="text-code text-primary"&gt;</c>), so the name is set in
    /// the mono face while the rest of the sentence is not.
    /// </summary>
    private static TextBlock WithCode(TextBlock block, string sentence, string branch)
    {
        block.Text = "";
        var at = branch.Length == 0 ? -1 : sentence.IndexOf(branch, StringComparison.Ordinal);
        if (at < 0)
        {
            block.Text = sentence;
            return block;
        }

        block.Inlines.Add(new Run(sentence[..at]));
        var code = new Run(branch);
        code.SetResourceReference(Run.FontFamilyProperty, "MonoFontFamily");
        block.Inlines.Add(code);
        block.Inlines.Add(new Run(sentence[(at + branch.Length)..]));
        return block;
    }

    private static TextBlock TitleBlock(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        return block;
    }

    private static TextBlock Description(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        return block;
    }

    private static Button Secondary(string content)
    {
        var button = new Button { Content = content };
        button.SetResourceReference(StyleProperty, "SecondaryButton");
        return button;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (_confirmingDiscard)
        {
            _confirmingDiscard = false;
            Render();
            return;
        }

        DialogResult = false;
    }
}
