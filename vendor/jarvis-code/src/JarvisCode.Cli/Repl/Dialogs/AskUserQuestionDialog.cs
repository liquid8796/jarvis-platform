using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Cli.Repl.Dialogs;

/// <summary>
/// The reference's AskUserQuestion card (CLI 2.1.257, its <c>Foe</c>/<c>Lx</c>/
/// <c>Boe</c>): a tab strip of the questions with a checkbox each and a Submit
/// tab at the end, the current question's options as a select list with an
/// "Other" row that takes typed text, a "Chat about this" row under them, and
/// a review step that asks "Ready to submit your answers?".
/// </summary>
internal sealed class AskUserQuestionDialog(IReadOnlyList<UserQuestion> questions, Ansi ansi)
{
    public const string ChatAboutThis = "Chat about this";
    public const string Other = "Other";
    public const string OtherPlaceholder = "Type something.";
    public const string OtherPlaceholderMulti = "Type something";
    public const string ReviewTitle = "Review your answers";
    public const string ReviewQuestion = "Ready to submit your answers?";
    public const string SubmitLabel = "Submit answers";
    public const string CancelLabel = "Cancel";
    public const string SubmitTab = "Submit";
    public const string NextLabel = "Next";
    public const string NotAllAnswered = "You have not answered all questions";

    /// <summary>The reference's value for the free-text row.</summary>
    public const string OtherValue = "__other__";

    /// <summary>Its value for the row that hands the question back to the model as chat.</summary>
    public const string ChatValue = "__chat__";

    private readonly Dictionary<string, string> _answers = new(StringComparer.Ordinal);
    private SelectList _list = null!;
    private int _questionIndex;
    private bool _reviewing;

    public IReadOnlyList<UserQuestion> Questions { get; } = questions;

    public IReadOnlyDictionary<string, string> Answers => _answers;

    public string Context => _reviewing ? "Confirmation" : "Select";

    /// <summary>Whether the tab strip's Submit tab is reachable, which needs every question answered.</summary>
    public bool AllAnswered => Questions.All(q => _answers.ContainsKey(q.Question));

    public AskUserQuestionDialog Start()
    {
        _list = BuildList(Questions[0]);
        return this;
    }

    private static SelectList BuildList(UserQuestion question)
    {
        var options = new List<SelectOption>();
        foreach (var option in question.Options)
        {
            options.Add(new SelectOption(option.Label, option.Label, option.Description));
        }

        options.Add(new SelectOption(
            OtherValue, Other, IsInput: true,
            Placeholder: question.MultiSelect ? OtherPlaceholderMulti : OtherPlaceholder));
        options.Add(new SelectOption(ChatValue, ChatAboutThis));
        return new SelectList(options);
    }

    /// <summary>The reference's <c>Lx</c>: the tab strip over the questions.</summary>
    public string TabStrip()
    {
        var cells = new List<string>();
        for (int i = 0; i < Questions.Count; i++)
        {
            var box = _answers.ContainsKey(Questions[i].Question) ? "[x]" : "[ ]";
            var header = Questions[i].Header is { Length: > 0 } h ? h : $"Q{i + 1}";
            var cell = $"{box} {header}";
            cells.Add(i == _questionIndex ? ansi.Color("permission", cell) : cell);
        }

        cells.Add(_reviewing ? ansi.Color("permission", $"{Glyphs.Tick} {SubmitTab}") : $"{Glyphs.Tick} {SubmitTab}");
        var left = _questionIndex == 0 ? ansi.Dim(Glyphs.ArrowLeft) : Glyphs.ArrowLeft;
        var right = _reviewing ? ansi.Dim(Glyphs.ArrowRight) : Glyphs.ArrowRight;
        return $"{left} {string.Join("  ", cells)} {right}";
    }

    public IReadOnlyList<string> Render(int columns)
    {
        var lines = new List<string> { "", TabStrip(), "" };
        if (_reviewing)
        {
            lines.Add(ansi.Bold(ReviewTitle));
            lines.Add("");
            if (!AllAnswered)
            {
                lines.Add("  " + ansi.Color("warning", $"{Glyphs.Warning} {NotAllAnswered}"));
                lines.Add("");
            }

            foreach (var question in Questions)
            {
                if (_answers.TryGetValue(question.Question, out var answer))
                {
                    lines.Add("  " + question.Question);
                    lines.Add("    " + ansi.Color("success", $"{Glyphs.ArrowRight} {answer}"));
                }
            }

            lines.Add("");
            lines.Add("  " + ansi.Dim(ReviewQuestion));
            lines.Add("  " + $"1. {SubmitLabel}   2. {CancelLabel}");
            return lines;
        }

        var current = Questions[_questionIndex];
        foreach (var row in TextWidth.Wrap(current.Question, Math.Max(20, columns - 2)))
        {
            lines.Add(ansi.Bold(row));
        }

        lines.Add("");
        lines.AddRange(_list.Render(ansi));
        return lines;
    }

    public DialogResult Handle(string? action, KeyPress press)
    {
        if (_reviewing)
        {
            if (action is "select:cancel" or "confirm:no")
            {
                return DialogResult.Cancelled;
            }

            if (action is "select:accept" or "confirm:yes" || press.Text == "1")
            {
                return DialogResult.Accept("submit");
            }

            if (press.Text == "2")
            {
                return DialogResult.Cancelled;
            }

            return DialogResult.Handled;
        }

        if (action is "tabs:next" || (press.Key == "tab" && !press.Shift))
        {
            Advance(1);
            return DialogResult.Handled;
        }

        if (action is "tabs:previous" || (press.Key == "tab" && press.Shift))
        {
            Advance(-1);
            return DialogResult.Handled;
        }

        var result = _list.Handle(action, press);
        if (result.Outcome != DialogOutcome.Accepted)
        {
            return result;
        }

        if (result.Value == ChatValue)
        {
            return DialogResult.Accept(ChatValue);
        }

        var question = Questions[_questionIndex].Question;
        _answers[question] = result.Value == OtherValue ? result.Text ?? "" : result.Value ?? "";
        Advance(1);
        return DialogResult.Handled;
    }

    private void Advance(int delta)
    {
        int next = _questionIndex + delta;
        if (next >= Questions.Count)
        {
            _reviewing = true;
            return;
        }

        _reviewing = false;
        _questionIndex = Math.Clamp(next, 0, Questions.Count - 1);
        _list = BuildList(Questions[_questionIndex]);
    }

    /// <summary>The answers in the shape the tool expects.</summary>
    public UserQuestionAnswers ToAnswers() => new(new Dictionary<string, string>(_answers, StringComparer.Ordinal));
}
