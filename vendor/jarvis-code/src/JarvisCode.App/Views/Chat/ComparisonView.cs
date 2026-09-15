using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;
using JarvisCode.App.Views.Settings;
using ModelInfo = JarvisCode.Core.Models.ModelInfo;

namespace JarvisCode.App.Views.Chat;

/// <summary>
/// The side-by-side model comparison view a new chat opens in, ported from the
/// reference's own <c>ComparisonRoute</c> (ion-dist chunk
/// <c>cf6c0e3a1-Bi64U0E_.js</c>, desktop 1.40609.1.0, where it sits behind two
/// rollout gates and the account's <c>iron_swift_default_opt_out</c> — the
/// "New chat view" setting this app already carries).
///
/// One prompt, answered by two models at once. The header names the view, offers
/// Hide model names and Hold responses, counts the session's votes and restarts
/// it; the two panels each carry their own model, their own memory setting and
/// their own read-only transcript; and the footer walks the reference's vote →
/// reason → saved phases under one composer. Each panel is a real conversation,
/// named the way the reference names one ("Compare 1 (model): prompt"), so both
/// are in the chat list afterwards.
///
/// The state machine is <see cref="ComparisonSession"/> and the sentences are
/// <see cref="ComparisonStrings"/>; this file is the layout over them.
/// </summary>
public sealed partial class ComparisonView : UserControl
{
    /// <summary>The reference's header and panel-header height, and its card gutter.</summary>
    private const double HeaderHeight = 52;

    /// <summary>The footer's own column, the reference's <c>max-w-3xl</c>.</summary>
    private const double FooterWidth = 768;

    private ComparisonSession _session = new();
    private readonly Panel[] _panels = new Panel[2];
    private readonly TextBlock _voteCount = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _hideNames;
    private readonly CheckBox _holdResponses;
    private StackPanel? _hideNamesRow;
    private readonly StackPanel _phaseRow = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBox _input;
    private readonly TextBlock _placeholder;
    private readonly Button _send;
    private readonly List<string> _pendingReasons = [];

    private AppServices? _services;
    private SessionViewModels? _sessions;
    private string _pendingComment = "";

    public ComparisonView()
    {
        Focusable = false;

        _hideNames = SwitchWithLabel(ComparisonStrings.HideModelNames, on => OnHideNamesChanged(on));
        _holdResponses = SwitchWithLabel(ComparisonStrings.HoldResponses, on =>
        {
            if (_services is not null)
            {
                _services.UiSettings.Current.ComparisonHoldResponses = on;
                _services.UiSettings.Save();
            }

            Refresh();
        });

        _voteCount.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 14,
            MinHeight = 24,
            MaxHeight = 160,
            Padding = new Thickness(10, 8, 6, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _input.SetResourceReference(ForegroundProperty, "Text100Brush");
        _input.PreviewKeyDown += OnInputKeyDown;
        _input.TextChanged += (_, _) => UpdatePlaceholder();
        InitializeComposerInputs();

        _placeholder = Controls.PlaceholderText.Overlay(_input, Services.ChatPlaceholders.NewConversation);

        _send = new Button { Content = "", FontFamily = (FontFamily)FindResourceOrDefault("IconFontFamily"), FontSize = 12 };
        _send.SetResourceReference(StyleProperty, "IconButton");
        _send.Click += (_, _) => Submit();

        Content = BuildRoot();
        _session.Changed += Refresh;
    }

    public void Initialize(AppServices services, SessionViewModels sessions)
    {
        _services = services;
        _sessions = sessions;
        _hideNames.IsChecked = services.UiSettings.Current.ComparisonHideModelNames;
        _holdResponses.IsChecked = services.UiSettings.Current.ComparisonHoldResponses;
        Reset();
    }

    /// <summary>
    /// The header's "New chat": a pending question is answered the way the
    /// reference answers it — a chosen side is saved, an unanswered one skipped —
    /// and the view is rebuilt on two fresh conversations.
    /// </summary>
    public void Reset()
    {
        if (_session.Phase == ComparisonPhase.Reason)
        {
            _session.SaveVote(_pendingReasons, _pendingComment);
        }
        else if (_session.Phase == ComparisonPhase.Vote)
        {
            _session.SkipVote();
        }

        if (_services is null || _sessions is null)
        {
            return;
        }

        // The reference's own New chat remounts the whole route, so the turns,
        // the phase and the vote counter all start again with the conversations.
        _session.Changed -= Refresh;
        _session = new ComparisonSession();
        _session.Changed += Refresh;

        var models = ChooseModels(_services);
        for (var index = 0; index < 2; index++)
        {
            _panels[index].Adopt(_sessions.ForNewSession(null), models[index]);
        }

        // The sides carry no information: the reference reverses them on a coin
        // flip before the first turn, so the left column is not always the same
        // model. A fresh session is always at that point.
        if (_session.ShouldShuffleSides(System.Random.Shared.NextDouble))
        {
            var first = _panels[0].Model;
            _panels[0].SetModel(_panels[1].Model);
            _panels[1].SetModel(first);
        }

        _pendingReasons.Clear();
        _pendingComment = "";
        _input.Clear();
        ClearComposerAttachments();
        Refresh();
    }

    /// <summary>
    /// The two models the panels open on. The reference reads a preferred pair
    /// from a rollout configuration and falls back to the stored model plus the
    /// first other one; there is no such configuration here, so the fallback is
    /// what this build uses — and a single-model installation runs both panels on
    /// it, which is also what the reference does with one selectable model.
    /// </summary>
    private static ModelInfo?[] ChooseModels(AppServices services)
    {
        var models = services.Settings.Models;
        if (models.Count == 0)
        {
            return [null, null];
        }

        var stored = services.Settings.Current.DefaultModelId;
        var first = models.FirstOrDefault(m => m.ModelId == stored) ?? models[0];
        var second = models.FirstOrDefault(m => m.ModelId != first.ModelId) ?? first;
        return [first, second];
    }

    // ---- layout ----

    private FrameworkElement BuildRoot()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(BuildHeader());

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < 2; index++)
        {
            _panels[index] = new Panel(index, this);
            Grid.SetColumn(_panels[index], index);
            columns.Children.Add(_panels[index]);
        }

        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = ComparisonStrings.Title,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        grid.Children.Add(title);

        var newChat = new Button { Content = ComparisonStrings.NewChat, Margin = new Thickness(10, 0, 0, 0) };
        newChat.SetResourceReference(StyleProperty, "SecondaryButton");
        newChat.Click += (_, _) => Reset();

        _hideNamesRow = Labelled(_hideNames, ComparisonStrings.HideModelNames);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _hideNamesRow,
                Labelled(_holdResponses, ComparisonStrings.HoldResponses),
                _voteCount,
                newChat,
            },
        };
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        var border = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 0.5) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return border;
    }

    private FrameworkElement BuildFooter()
    {
        var composerBorder = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
        };
        composerBorder.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        composerBorder.SetResourceReference(Border.BorderBrushProperty, "Border300Brush");

        var composer = new Grid();
        composer.ColumnDefinitions.Add(new ColumnDefinition());
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.Children.Add(_input);
        composer.Children.Add(_placeholder);
        Grid.SetColumn(_send, 1);
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _send.Margin = new Thickness(0, 0, 2, 4);
        composer.Children.Add(_send);
        composerBorder.Child = BuildRichComposer(composer);

        var column = new StackPanel
        {
            MaxWidth = FooterWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _phaseRow, composerBorder },
        };

        var border = new Border
        {
            Child = column,
            BorderThickness = new Thickness(0, 0.5, 0, 0),
            Padding = new Thickness(24, 12, 24, 10),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return border;
    }

    private static StackPanel Labelled(CheckBox box, string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Children = { box, label },
        };
    }

    private CheckBox SwitchWithLabel(string accessibleName, Action<bool> onChanged)
    {
        var box = SettingsUi.Switch(false, onChanged);
        System.Windows.Automation.AutomationProperties.SetName(box, accessibleName);
        box.VerticalAlignment = VerticalAlignment.Center;
        return box;
    }

    private object FindResourceOrDefault(string key) =>
        TryFindResource(key) ?? Application.Current?.TryFindResource(key) ?? new FontFamily("Segoe UI");

    private object FindResourceOrDefaultGeometry(string key) =>
        TryFindResource(key) ?? Application.Current?.TryFindResource(key) ?? Geometry.Empty;

    // ---- behaviour ----

    /// <summary>
    /// The reference offers Hide model names only when the comparison's own model
    /// list holds at least two — hiding one model from itself says nothing. There
    /// is no such list here, so the local reading is the models the user added.
    /// </summary>
    private bool BlindAvailable => _services?.Settings.Models.Count > 1;

    private bool Blind => _hideNames.IsChecked == true && BlindAvailable;

    private bool Concealed =>
        _holdResponses.IsChecked == true && _session.Phase == ComparisonPhase.Streaming;

    private string LabelFor(int panel) => Blind
        ? ComparisonStrings.BlindLabel(panel)
        : ComparisonStrings.PanelLabel(_panels[panel].Model?.DisplayName ?? "", null);

    private void OnHideNamesChanged(bool on)
    {
        if (_services is not null)
        {
            _services.UiSettings.Current.ComparisonHideModelNames = on;
            _services.UiSettings.Save();
        }

        // Hiding the names shuffles the sides, and a memory-off arm cannot be
        // shuffled out from under the reader — so hiding also clears both.
        if (on)
        {
            _panels[0].SetMemoryOff(false);
            _panels[1].SetMemoryOff(false);
            if (_session.ShouldShuffleSides(System.Random.Shared.NextDouble))
            {
                var first = _panels[0].Model;
                _panels[0].SetModel(_panels[1].Model);
                _panels[1].SetModel(first);
            }
        }

        Refresh();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Submit();
        }
    }

    private void UpdatePlaceholder() =>
        _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Submit()
    {
        var prompt = _input.Text;
        if (!_session.BeginTurn(prompt, _attachments.Count > 0))
        {
            return;
        }

        _pendingReasons.Clear();
        _pendingComment = "";
        var attachments = _attachments.ToArray();
        _voice?.Stop();
        _input.Clear();
        ClearComposerAttachments();
        UpdatePlaceholder();
        for (var index = 0; index < 2; index++)
        {
            _panels[index].Send(prompt.Trim(), Blind, attachments);
        }

        Refresh();
    }

    /// <summary>A panel finished; the session decides whether that ends the turn.</summary>
    internal void PanelSettled(int panel, string? error) => _session.Settle(panel, error);

    /// <summary>
    /// Poses the view for a screen grab, driving the real state machine rather
    /// than drawing a picture of it: one prompt, two answers, then as far along
    /// the vote → reason → saved walk as the flag asks for.
    /// </summary>
    public void Pose(string state)
    {
        if (state.Length == 0)
        {
            return;
        }

        const string prompt = "Explain a B-tree in two sentences.";
        if (state == "send")
        {
            // The only pose that runs the real path: it types into the composer
            // and submits, so both arms really answer (or really fail).
            _input.Text = prompt;
            Submit();
            return;
        }

        _session.BeginTurn(prompt);
        _panels[0].Pose(prompt,
            "A B-tree keeps its keys sorted in wide nodes so a lookup touches very few of them. "
            + "Every leaf sits at the same depth, which is what keeps the height low as it grows.");
        _panels[1].Pose(prompt,
            "It is a balanced search tree whose nodes hold many keys at once, so each step of a "
            + "search rules out a large slice of the data. Splits and merges keep every leaf level.");
        _session.Settle(0, null);
        _session.Settle(1, null);

        if (state is "reason" or "saved")
        {
            _session.Vote(ComparisonVote.B);
            _pendingReasons.Add("Clearer writing");
        }

        if (state == "saved")
        {
            _session.SaveVote(_pendingReasons, null);
        }

        Refresh();
    }

    /// <summary>
    /// The reference votes on ArrowLeft and ArrowRight while the question is up,
    /// and only when the key did not land in a text field or a menu.
    /// </summary>
    internal bool HandleVoteKey(Key key)
    {
        // The reference ignores the key wherever it landed in something a person
        // is typing into; anywhere in this window will do for that.
        if (_session.Phase != ComparisonPhase.Vote
            || Keyboard.FocusedElement is TextBoxBase or System.Windows.Documents.TextElement)
        {
            return false;
        }

        switch (key)
        {
            case Key.Left:
                _session.Vote(ComparisonVote.A);
                return true;
            case Key.Right:
                _session.Vote(ComparisonVote.B);
                return true;
            default:
                return false;
        }
    }

    private void Refresh()
    {
        var labels = ComparisonStrings.Disambiguate([LabelFor(0), LabelFor(1)]);
        for (var index = 0; index < 2; index++)
        {
            _panels[index].Refresh(labels[index], Blind, Concealed, _session);
        }

        _voteCount.Text = ComparisonStrings.VotesThisSession(_session.Votes);
        _voteCount.Visibility = _session.Votes > 0 ? Visibility.Visible : Visibility.Collapsed;
        _voteCount.Margin = new Thickness(0, 0, 0, 0);

        // Hiding is refused while a memory-off arm is running, because turning it
        // on would clear that arm and shuffle it under the reader.
        var blocked = !Blind && Enumerable.Range(0, 2).Any(i => _panels[i].MemoryOff && _session.MemoryLocked(i));
        _hideNames.IsEnabled = !blocked;
        _hideNames.ToolTip = blocked ? ComparisonStrings.HideBlockedByMemory : null;
        if (_hideNamesRow is not null)
        {
            _hideNamesRow.Visibility = BlindAvailable ? Visibility.Visible : Visibility.Collapsed;
        }

        _send.IsEnabled = _session.Phase != ComparisonPhase.Streaming;
        _input.IsEnabled = _session.Phase != ComparisonPhase.Streaming;
        if (_composerActions is not null) _composerActions.IsEnabled = _session.Phase != ComparisonPhase.Streaming;
        BuildPhaseRow(labels);
        UpdatePlaceholder();
    }

    private void BuildPhaseRow(IReadOnlyList<string> labels)
    {
        _phaseRow.Children.Clear();
        _phaseRow.Visibility = Visibility.Visible;
        switch (_session.Phase)
        {
            case ComparisonPhase.Vote:
                _phaseRow.Children.Add(BuildVoteRow(labels));
                break;
            case ComparisonPhase.Reason:
                _phaseRow.Children.Add(BuildReasonRow(labels));
                break;
            case ComparisonPhase.Saved:
                _phaseRow.Children.Add(BuildSavedRow(labels));
                break;
            default:
                _phaseRow.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private FrameworkElement BuildVoteRow(IReadOnlyList<string> labels)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var question = new TextBlock
        {
            Text = ComparisonStrings.WhichDoYouPrefer,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        };
        question.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        stack.Children.Add(question);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        buttons.Children.Add(VoteButton("◀  " + labels[0], ComparisonVote.A));
        buttons.Children.Add(VoteButton(ComparisonStrings.Tie, ComparisonVote.Tie));
        buttons.Children.Add(VoteButton(labels[1] + "  ▶", ComparisonVote.B));
        stack.Children.Add(buttons);
        return stack;
    }

    private Button VoteButton(string text, ComparisonVote vote)
    {
        var button = new Button { Content = text, Margin = new Thickness(5, 0, 5, 0) };
        button.SetResourceReference(StyleProperty, "SecondaryButton");
        button.Click += (_, _) => _session.Vote(vote);
        return button;
    }

    private FrameworkElement BuildReasonRow(IReadOnlyList<string> labels)
    {
        var turn = _session.LastTurn;
        var tie = turn?.Vote is null or ComparisonVote.Tie;
        var winner = tie ? null : labels[turn!.Vote == ComparisonVote.A ? 0 : 1];

        var stack = new StackPanel();
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var back = new Button { Content = "◀", Width = 24, Height = 24 };
        back.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(back, ComparisonStrings.BackToVote);
        back.ToolTip = ComparisonStrings.BackToVote;
        back.Click += (_, _) => _session.BackToVote();
        head.Children.Add(back);

        var question = new TextBlock
        {
            Text = ComparisonStrings.ReasonQuestion(winner),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        question.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        head.Children.Add(question);
        stack.Children.Add(head);

        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var reason in tie ? ComparisonStrings.TieReasons : ComparisonStrings.WinReasons)
        {
            var chip = new ToggleButton
            {
                Content = reason,
                Margin = new Thickness(4),
                IsChecked = _pendingReasons.Contains(reason),
                Padding = new Thickness(10, 4, 10, 4),
            };
            chip.SetResourceReference(StyleProperty, "ChipToggle");
            var captured = reason;
            chip.Checked += (_, _) => _pendingReasons.Add(captured);
            chip.Unchecked += (_, _) => _pendingReasons.Remove(captured);
            chips.Children.Add(chip);
        }

        stack.Children.Add(chips);

        var commentRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        commentRow.ColumnDefinitions.Add(new ColumnDefinition());
        commentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var comment = new TextBox { Text = _pendingComment };
        comment.SetResourceReference(StyleProperty, "InputTextBox");
        System.Windows.Automation.AutomationProperties.SetName(comment, ComparisonStrings.Comment);
        Controls.PlaceholderText.SetText(comment, ComparisonStrings.CommentPlaceholder);
        comment.TextChanged += (_, _) => _pendingComment = comment.Text;
        commentRow.Children.Add(comment);

        var save = new Button { Content = ComparisonStrings.Save, Margin = new Thickness(8, 0, 0, 0) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => _session.SaveVote(_pendingReasons, _pendingComment);
        Grid.SetColumn(save, 1);
        commentRow.Children.Add(save);
        stack.Children.Add(commentRow);
        return stack;
    }

    private FrameworkElement BuildSavedRow(IReadOnlyList<string> labels)
    {
        var turn = _session.LastTurn;
        var winner = turn?.Vote switch
        {
            ComparisonVote.A => labels[0],
            ComparisonVote.B => labels[1],
            _ => "tie",
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var tick = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResourceOrDefaultGeometry("CheckCircleGlyph"),
            StrokeThickness = 1.6,
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        tick.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrandBrush");
        row.Children.Add(tick);

        var text = new TextBlock
        {
            Text = ComparisonStrings.PreferenceRecorded(winner, turn?.Reasons),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        row.Children.Add(text);

        // The reference draws this as a link in the secondary colour, not a button.
        var change = new Button
        {
            Content = ComparisonStrings.Change,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        change.SetResourceReference(StyleProperty, "LinkButton");
        change.Click += (_, _) => _session.UndoVote();
        row.Children.Add(change);
        return row;
    }

    /// <summary>
    /// One column: its own model, its own memory setting, its own conversation.
    /// The reference gives it the ordinary chat transcript with the header, the
    /// composer and every editing affordance turned off, which is what an
    /// ItemsControl over the view model's transcript is here.
    /// </summary>
    private sealed class Panel : Grid
    {
        private readonly int _index;
        private readonly ComparisonView _owner;
        private readonly Button _modelButton = new();
        private readonly TextBlock _modelText = new() { FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _blindLabel = new() { FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _settings = new();
        private readonly ItemsControl _items = new() { Focusable = false, Margin = new Thickness(16, 8, 16, 16) };
        private readonly ScrollViewer _scroll;
        private readonly StackPanel _empty;
        private readonly TextBlock _emptyLabel = new()
        {
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        private readonly Border _errorStrip;
        private readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        private readonly Border _conceal;

        private ChatViewModel? _viewModel;

        public Panel(int index, ComparisonView owner)
        {
            _index = index;
            _owner = owner;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _modelButton.SetResourceReference(StyleProperty, "GhostButton");
            _modelButton.Content = _modelText;
            _modelButton.Click += (_, _) => OpenModelMenu();
            _modelText.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");

            _blindLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
            _blindLabel.Visibility = Visibility.Collapsed;

            _settings.SetResourceReference(StyleProperty, "IconButton");
            _settings.Content = "";
            _settings.FontFamily = (FontFamily)owner.FindResourceOrDefault("IconFontFamily");
            _settings.FontSize = 12;
            _settings.Width = 26;
            _settings.Height = 26;
            System.Windows.Automation.AutomationProperties.SetName(
                _settings, ComparisonStrings.PanelSettingsLabel(index));
            _settings.Click += (_, _) => OpenPanelSettings();

            var header = new Grid { Margin = new Thickness(24, 0, 24, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _modelButton, _blindLabel },
            };
            header.Children.Add(left);
            Grid.SetColumn(_settings, 1);
            header.Children.Add(_settings);
            Children.Add(header);

            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = _items,
            };

            var emptyText = new TextBlock
            {
                Text = ComparisonStrings.PanelEmpty,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 280,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            _emptyLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
            _empty = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(40, 0, 40, 0),
                Children = { _emptyLabel, emptyText },
            };

            _errorText.SetResourceReference(TextBlock.ForegroundProperty, "Danger000Brush");
            _errorStrip = new Border
            {
                Child = _errorText,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(16, 0, 16, 12),
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed,
            };
            _errorStrip.SetResourceReference(Border.BackgroundProperty, "DangerBgBrush");

            var concealLabel = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            concealLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
            concealLabel.Text = "Thinking";
            _conceal = new Border
            {
                Visibility = Visibility.Collapsed,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new JarvisCode.App.Views.SpinnerGlyph
                        {
                            Width = 20,
                            Height = 20,
                            IsSpinning = true,
                            Margin = new Thickness(0, 0, 8, 0),
                        },
                        concealLabel,
                    },
                },
            };
            _conceal.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
            System.Windows.Automation.AutomationProperties.SetName(_conceal, ComparisonStrings.WaitingForBoth);

            var body = new Grid { Children = { _scroll, _empty, _errorStrip, _conceal } };
            Grid.SetRow(body, 1);
            Children.Add(body);

            var rule = new Border { BorderThickness = new Thickness(0, 0, index == 0 ? 0.5 : 0, 0) };
            rule.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
            rule.IsHitTestVisible = false;
            Grid.SetRowSpan(rule, 2);
            Children.Add(rule);
        }

        public ModelInfo? Model { get; private set; }

        public ChatViewModel? ViewModel => _viewModel;

        public bool MemoryOff { get; private set; }

        public void Adopt(ChatViewModel viewModel, ModelInfo? model)
        {
            if (_viewModel is not null)
            {
                _viewModel.TurnFinished -= OnTurnFinished;
                _viewModel.Transcript.CollectionChanged -= OnTranscriptChanged;
            }

            _viewModel = viewModel;
            viewModel.TurnFinished += OnTurnFinished;
            viewModel.MemoryPaused = MemoryOff;
            viewModel.Transcript.CollectionChanged += OnTranscriptChanged;
            _items.ItemsSource = viewModel.Transcript;
            SetModel(model);
        }

        public void SetModel(ModelInfo? model)
        {
            Model = model;
            if (_viewModel is not null && model is not null)
            {
                _viewModel.Session.ModelId = model.ModelId;
            }
        }

        public void SetMemoryOff(bool off)
        {
            MemoryOff = off;
            if (_viewModel is not null)
            {
                _viewModel.MemoryPaused = off;
            }
        }

        public void Send(string prompt, bool blind, IReadOnlyList<ComposerAttachment>? attachments = null)
        {
            if (_viewModel is not { } viewModel)
            {
                return;
            }

            var name = ComparisonStrings.SessionName(_index, Model?.DisplayName ?? "", prompt, blind);
            foreach (var attachment in attachments ?? []) viewModel.ComposerAttachments.Add(attachment);
            _ = SendAsync(viewModel, prompt, name);
        }

        private static async Task SendAsync(ChatViewModel viewModel, string prompt, string name)
        {
            var first = viewModel.Session.Messages.Count == 0;
            await viewModel.SendAsync(prompt);
            if (first)
            {
                // The reference names each arm's conversation after the panel and
                // the prompt; this app titles a new session from its first message,
                // so the name is written once that has happened.
                await viewModel.RenameAsync(name);
            }
        }

        /// <summary>The pose's own answer, put in this panel's transcript.</summary>
        public void Pose(string prompt, string answer)
        {
            if (_viewModel is not { } viewModel)
            {
                return;
            }

            viewModel.Transcript.Add(new UserMessageItem { Text = prompt });
            viewModel.Transcript.Add(new AssistantTextItem { Markdown = answer, IsStreaming = false });
        }

        private void OnTranscriptChanged(
            object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            _owner.Refresh();
            _scroll.ScrollToEnd();
        }

        private void OnTurnFinished(object? sender, EventArgs e)
        {
            var error = _viewModel?.LastTurnEnd is { } end && end.Reason != JarvisCode.Core.Agent.TurnEndReason.Completed
                ? end.Detail ?? end.Reason.ToString()
                : null;
            _owner.PanelSettled(_index, error);
        }

        public void Refresh(string label, bool blind, bool concealed, ComparisonSession session)
        {
            _blindLabel.Text = label;
            _blindLabel.Visibility = blind ? Visibility.Visible : Visibility.Collapsed;
            _modelButton.Visibility = blind ? Visibility.Collapsed : Visibility.Visible;
            _modelText.Text = Model is null
                ? "Choose model"
                : MemoryOff
                    ? Model.DisplayName + "  " + ComparisonStrings.MemoryOff
                    : Model.DisplayName;

            _emptyLabel.Text = label;
            var empty = _viewModel is null || _viewModel.Transcript.Count == 0;
            _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            var error = session.LastTurn?.Errors[_index];
            _errorStrip.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
            _errorText.Text = error ?? "";

            _conceal.Visibility = concealed ? Visibility.Visible : Visibility.Collapsed;

            // The reference offers the panel's settings only while its own memory
            // rollout is on; the local equivalent is Customize › Memory's switch,
            // since a panel setting for a feature that is off would decide nothing.
            _settings.Visibility = _owner._services?.UiSettings.Current.MemoryEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OpenModelMenu()
        {
            if (_owner._services is not { } services)
            {
                return;
            }

            var menu = new ContextMenu { PlacementTarget = _modelButton, Placement = PlacementMode.Bottom };
            foreach (var group in ModelMenuPresentation.Group(
                services.Settings.Models,
                id => services.Providers.All
                    .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName))
            {
                if (menu.Items.Count > 0)
                {
                    menu.Items.Add(new Separator());
                }

                menu.Items.Add(new MenuItem { Header = group.Label, IsEnabled = false });
                foreach (var model in group.Models)
                {
                    var item = new MenuItem { Header = model.DisplayName };
                    if (Model?.ModelId == model.ModelId)
                    {
                        item.FontWeight = FontWeights.SemiBold;
                    }

                    var captured = model;
                    // A panel's pick is that panel's, not the account's: the
                    // reference's own picker here confirms nothing and never
                    // rewrites the stored model.
                    item.Click += (_, _) =>
                    {
                        SetModel(captured);
                        _owner.Refresh();
                    };
                    menu.Items.Add(item);
                }
            }

            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "No models yet — add one in Settings › Providers",
                    IsEnabled = false,
                });
            }

            menu.IsOpen = true;
        }

        private void OpenPanelSettings()
        {
            var locked = _owner._session.MemoryLocked(_index);
            var blind = _owner.Blind;
            var reason = locked ? ComparisonStrings.MemoryLocked
                : blind ? ComparisonStrings.MemoryBlind
                : null;

            var stack = new StackPanel { Width = 280, Margin = new Thickness(12) };
            var title = new TextBlock
            {
                Text = ComparisonStrings.PanelSettings,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 10),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            stack.Children.Add(title);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel();
            var name = new TextBlock { Text = ComparisonStrings.Memory };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            var description = new TextBlock
            {
                Text = ComparisonStrings.MemoryDescription,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 8, 0),
            };
            description.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            labels.Children.Add(name);
            labels.Children.Add(description);
            row.Children.Add(labels);

            var toggle = SettingsUi.Switch(!MemoryOff, on =>
            {
                SetMemoryOff(!on);
                _owner.Refresh();
            });
            toggle.IsEnabled = reason is null;
            toggle.ToolTip = reason;
            toggle.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(toggle, ComparisonStrings.Memory);
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            stack.Children.Add(row);

            var card = new Border { Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
            card.SetResourceReference(Border.BorderBrushProperty, "Border300Brush");

            var popup = new Popup
            {
                PlacementTarget = _settings,
                Placement = PlacementMode.Bottom,
                HorizontalOffset = -254,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = card,
            };
            popup.IsOpen = true;
        }
    }
}
