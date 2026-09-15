using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Dialogs;

/// <summary>
/// The reference's plan-approval dialog (CLI 2.1.257, its <c>Iwt</c>): the plan
/// rendered under "Here is Claude's plan:", then the question and its rows —
/// the clear-context row when the setting asks for it, the two keep-context
/// rows, and a "No, keep planning" row that takes feedback. shift+tab approves
/// with whatever feedback has been typed; ctrl+g opens the plan in $EDITOR.
/// </summary>
internal sealed class PlanApproval(string plan, string? planFilePath, Ansi ansi, int? usedPercent = null)
{
    /// <summary>The reference's title while the plan is ready to run.</summary>
    public const string Title = "Ready to code?";

    /// <summary>Its title when the plan file is empty.</summary>
    public const string EmptyPlanTitle = "Exit plan mode?";

    /// <summary>What it says above the plan.</summary>
    public const string PlanHeading = "Here is Jarvis's plan:";

    /// <summary>Its question.</summary>
    public const string Question = "Jarvis has written up a plan and is ready to execute. Would you like to proceed?";

    /// <summary>What it says when the plan file has nothing in it.</summary>
    public const string EmptyPlanQuestion = "Jarvis wants to exit plan mode";

    /// <summary>The reference's fallback when nothing was written to the plan file.</summary>
    public const string NoPlanFound = "No plan found. Please write your plan to the plan file first.";

    /// <summary>Its cap: past this the plan is not shown and approval is withheld.</summary>
    public const int MaxPlanChars = 200000;

    /// <summary>What it says instead of an over-long plan.</summary>
    public const string PlanTooLarge =
        "(the plan is too large to be shown in full — approval is withheld; send feedback asking for a shorter " +
        "plan, or press Esc)";

    public const string KeepPlanningLabel = "No, keep planning";
    public const string KeepPlanningPlaceholder = "Tell Jarvis what to change";
    public const string KeepPlanningDescription = "shift+tab to approve with this feedback";
    public const string AutoAcceptLabel = "Yes, auto-accept edits";
    public const string ManualLabel = "Yes, manually approve edits";
    public const string SavedNotice = "Plan saved!";

    /// <summary>The clear-context row, whose percentage the reference only shows when it knows one.</summary>
    public static string ClearContextLabel(int? usedPercent) =>
        $"Yes, clear context{(usedPercent is { } pct ? $" ({pct}% used)" : "")} and auto-accept edits";

    private readonly SelectList _list = Build(usedPercent, showClearContext: usedPercent is not null);

    public string Plan { get; private set; } = plan.Length > 0 ? plan : NoPlanFound;

    public string? PlanFilePath { get; } = planFilePath;

    /// <summary>True when the plan is past the cap, which withholds the approving rows.</summary>
    public bool ApprovalWithheld => Plan.Length > MaxPlanChars;

    public string Context => "Confirmation";

    private static SelectList Build(int? usedPercent, bool showClearContext)
    {
        var options = new List<SelectOption>();
        if (showClearContext)
        {
            options.Add(new SelectOption("yes-accept-edits", ClearContextLabel(usedPercent)));
        }

        options.Add(new SelectOption("yes-accept-edits-keep-context", AutoAcceptLabel));
        options.Add(new SelectOption("yes-default-keep-context", ManualLabel));
        options.Add(new SelectOption(
            "no", KeepPlanningLabel, KeepPlanningDescription, IsInput: true, Placeholder: KeepPlanningPlaceholder));
        return new SelectList(options);
    }

    public IReadOnlyList<string> Render(int columns, string? editorName = null)
    {
        var lines = new List<string> { "", ansi.Bold(Title), "" };
        lines.Add("  " + PlanHeading);
        lines.Add("");
        if (ApprovalWithheld)
        {
            lines.Add("  " + ansi.Dim(PlanTooLarge));
        }
        else
        {
            var markdown = new MarkdownTerminal(ansi, Math.Max(20, columns - 2));
            foreach (var line in markdown.Render(Plan).Split('\n'))
            {
                lines.Add("  " + line);
            }
        }

        lines.Add("");
        lines.Add("  " + ansi.Dim(Question));
        lines.Add("");
        lines.AddRange(_list.Render(ansi));
        if (editorName is { Length: > 0 } editor)
        {
            var hint = ChordFormat.Hint("ctrl+g", $"edit in {editor}", ChordStyle.LowerKeys);
            lines.Add("");
            lines.Add("  " + ansi.Dim(hint + (PlanFilePath is { Length: > 0 } path ? $" · {path}" : "")));
        }

        return lines;
    }

    /// <summary>Replaces the plan after an external edit, which is what ctrl+g does.</summary>
    public void SetPlan(string plan) => Plan = plan.Length > 0 ? plan : NoPlanFound;

    public DialogResult Handle(string? action, KeyPress press)
    {
        // shift+tab approves with the feedback typed so far, which the reference
        // routes past the select list.
        if (press.Key == "tab" && press.Shift && !ApprovalWithheld)
        {
            return DialogResult.Accept("yes-accept-edits-keep-context", _list.InputText);
        }

        return _list.Handle(action, press);
    }

    /// <summary>Whether the chosen row is an approval.</summary>
    public static bool IsApproval(string value) => value.StartsWith("yes", StringComparison.Ordinal);
}
