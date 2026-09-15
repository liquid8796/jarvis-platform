using System.Collections.ObjectModel;
using JarvisCode.App.Infrastructure;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.ViewModels;

/// <summary>One expanded slash command riding a queued or outgoing message: the envelope plus its rendered body.</summary>
public sealed record SkillExpansionPart(string Envelope, string Body);

/// <summary>One prompt waiting in the composer queue while a turn runs.</summary>
public sealed class QueuedMessageItem(string text, IReadOnlyList<ComposerAttachment>? attachments = null)
{
    public string Text { get; } = text;

    /// <summary>The composer chips attached to this prompt when it was queued.</summary>
    public IReadOnlyList<ComposerAttachment> Attachments { get; } = attachments ?? [];

    /// <summary>An already-expanded skill invocation; Text then holds only the typed "/name args".</summary>
    public IReadOnlyList<SkillExpansionPart>? Expansion { get; init; }

    /// <summary>
    /// Single-line preview for the queue row, built the reference's way: the text
    /// followed by a marker per attachment, whitespace collapsed, and an ellipsis
    /// when there is nothing to show.
    /// </summary>
    public string Preview => Services.ChatQueue.Preview(
        Text,
        Attachments.Select(static a => (a.IsImage, a.Label)));
}

/// <summary>One selectable option row on the AskUserQuestion card.</summary>
public sealed class QuestionOptionViewModel(
    UserQuestionOption option,
    Action<QuestionOptionViewModel> picked,
    Action<QuestionOptionViewModel>? hovered = null)
    : ObservableObject
{
    private bool _isSelected;
    private RelayCommand? _pickCommand;

    public string Label => option.Label;

    public string Description => option.Description;

    /// <summary>Self-contained HTML fragment shown beside the list when focused.</summary>
    public string? Preview => option.Preview;

    public bool HasPreview => !string.IsNullOrWhiteSpace(option.Preview);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand PickCommand => _pickCommand ??= new RelayCommand(() => picked(this));

    /// <summary>Mouse-over from the view; drives the preview pane.</summary>
    public void NotifyHovered() => hovered?.Invoke(this);
}

/// <summary>One question block: header chip, question text, its options, and "Other".</summary>
public sealed class QuestionViewModel : ObservableObject
{
    private string _otherText = "";

    public QuestionViewModel(UserQuestion question)
    {
        Question = question;
        foreach (var option in question.Options)
        {
            Options.Add(new QuestionOptionViewModel(option, OnPicked, OnHovered));
        }

        // The reference opens the side-by-side view on the first previewable option.
        PreviewedOption = Options.FirstOrDefault(o => o.HasPreview);
    }

    public UserQuestion Question { get; }

    public string Header => Question.Header;

    public string Text => Question.Question;

    public bool MultiSelect => Question.MultiSelect;

    public ObservableCollection<QuestionOptionViewModel> Options { get; } = [];

    /// <summary>Any option carrying a preview switches the card to side-by-side.</summary>
    public bool HasPreviews => Options.Any(o => o.HasPreview);

    private QuestionOptionViewModel? _previewedOption;

    /// <summary>The option whose preview the right pane shows (focused or selected).</summary>
    public QuestionOptionViewModel? PreviewedOption
    {
        get => _previewedOption;
        private set
        {
            if (SetProperty(ref _previewedOption, value))
            {
                OnPropertyChanged(nameof(PreviewHtml));
            }
        }
    }

    public string? PreviewHtml => PreviewedOption?.Preview;

    private void OnHovered(QuestionOptionViewModel option)
    {
        if (HasPreviews && option.HasPreview)
        {
            PreviewedOption = option;
        }
    }

    /// <summary>Free text typed under "Other"; overrides the option picks when non-empty.</summary>
    public string OtherText
    {
        get => _otherText;
        set
        {
            if (SetProperty(ref _otherText, value))
            {
                OnPropertyChanged(nameof(IsAnswered));
            }
        }
    }

    public bool IsAnswered => Options.Any(o => o.IsSelected) || OtherText.Trim().Length > 0;

    /// <summary>The answer string the tool result reports for this question.</summary>
    public string Answer
    {
        get
        {
            if (OtherText.Trim() is { Length: > 0 } other)
            {
                return other;
            }

            return string.Join(", ", Options.Where(o => o.IsSelected).Select(o => o.Label));
        }
    }

    private void OnPicked(QuestionOptionViewModel picked)
    {
        if (!MultiSelect)
        {
            foreach (var option in Options)
            {
                option.IsSelected = ReferenceEquals(option, picked);
            }
        }
        else
        {
            picked.IsSelected = !picked.IsSelected;
        }

        if (HasPreviews && picked.HasPreview)
        {
            PreviewedOption = picked;
        }

        OnPropertyChanged(nameof(IsAnswered));
        Picked?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Picked;
}

/// <summary>The pending AskUserQuestion call, rendered as an inline card.</summary>
public sealed class QuestionPromptViewModel : ObservableObject
{
    private readonly TaskCompletionSource<UserQuestionAnswers?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QuestionPromptViewModel(IReadOnlyList<UserQuestion> questions)
    {
        foreach (var question in questions)
        {
            var vm = new QuestionViewModel(question);
            vm.Picked += (_, _) =>
            {
                SubmitCommand.RaiseCanExecuteChanged();
                // A lone single-select question resolves on click, like the
                // reference — unless previews are up, which the user browses
                // by focusing options before confirming.
                if (Questions.Count == 1 && !vm.MultiSelect && !vm.HasPreviews && vm.OtherText.Trim().Length == 0)
                {
                    Submit();
                }
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QuestionViewModel.OtherText))
                {
                    SubmitCommand.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(NeedsSubmit));
                }
            };
            Questions.Add(vm);
        }

        SubmitCommand = new RelayCommand(Submit, () => Questions.All(q => q.IsAnswered));
        DismissCommand = new RelayCommand(() => _completion.TrySetResult(null));
    }

    public ObservableCollection<QuestionViewModel> Questions { get; } = [];

    /// <summary>Submit shows when anything needs an explicit confirm (multi-select, several questions, previews, Other).</summary>
    public bool NeedsSubmit =>
        Questions.Count > 1 ||
        Questions.Any(q => q.MultiSelect || q.HasPreviews || q.OtherText.Trim().Length > 0);

    public RelayCommand SubmitCommand { get; }

    public RelayCommand DismissCommand { get; }

    public Task<UserQuestionAnswers?> Task => _completion.Task;

    public void Cancel() => _completion.TrySetResult(null);

    private void Submit()
    {
        if (Questions.Any(q => !q.IsAnswered))
        {
            return;
        }

        var answers = Questions.ToDictionary(q => q.Question.Question, q => q.Answer);
        _completion.TrySetResult(new UserQuestionAnswers(answers));
    }
}

/// <summary>The pending ExitPlanMode approval, rendered as an inline card.</summary>
public sealed class PlanApprovalViewModel : ObservableObject
{
    private readonly TaskCompletionSource<PlanApprovalDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _feedbackText = "";
    private string _editedPlan = "";
    private bool _isEditing;

    public PlanApprovalViewModel(string planMarkdown)
    {
        PlanMarkdown = planMarkdown;
        _editedPlan = planMarkdown;
        // The reference lets the user edit the plan in this card and marks what
        // goes back to the model as edited; an untouched plan is approved as-is.
        ApproveCommand = new RelayCommand(() => _completion.TrySetResult(
            new PlanApprovalDecision(
                true,
                null,
                EditedPlan.Trim() != planMarkdown.Trim() ? EditedPlan.Trim() : null)));
        EditCommand = new RelayCommand(() => IsEditing = !IsEditing);
        RejectCommand = new RelayCommand(() => _completion.TrySetResult(
            new PlanApprovalDecision(false, FeedbackText.Trim().Length > 0 ? FeedbackText.Trim() : null)));
        // Enter in the comment box: only a written comment resolves the card.
        CommentCommand = new RelayCommand(() =>
        {
            if (FeedbackText.Trim().Length > 0)
            {
                _completion.TrySetResult(new PlanApprovalDecision(false, FeedbackText.Trim()));
            }
        });
    }

    /// <summary>"Add a comment" — keep planning with the comment as feedback.</summary>
    public RelayCommand CommentCommand { get; }

    /// <summary>Swaps the rendered plan for an editable copy of it.</summary>
    public RelayCommand EditCommand { get; }

    public string PlanMarkdown { get; }

    /// <summary>True while the plan is being edited in place.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    /// <summary>The plan as the user has it now; approved as edited when it differs.</summary>
    public string EditedPlan
    {
        get => _editedPlan;
        set => SetProperty(ref _editedPlan, value);
    }

    public string FeedbackText
    {
        get => _feedbackText;
        set => SetProperty(ref _feedbackText, value);
    }

    public RelayCommand ApproveCommand { get; }

    public RelayCommand RejectCommand { get; }

    public Task<PlanApprovalDecision> Task => _completion.Task;

    public void Cancel() => _completion.TrySetResult(new PlanApprovalDecision(false, "The turn was cancelled."));
}
