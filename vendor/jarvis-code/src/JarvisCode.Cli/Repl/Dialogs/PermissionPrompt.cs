using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Permissions;

namespace JarvisCode.Cli.Repl.Dialogs;

/// <summary>
/// The reference's permission prompt (CLI 2.1.257, its <c>Lz</c> and the
/// consent-row factories beside it): a title, the subject, and a select list
/// whose first row is a bare "Yes", whose middle rows are whatever standing
/// permission this call could earn, and whose last row is the reference's
/// refusal — the one that offers to tell the model what to do instead.
/// </summary>
internal sealed class PermissionPromptDialog(PermissionPrompt prompt, string? alwaysAllowLabel, Ansi ansi)
{
    /// <summary>The reference's own question.</summary>
    public const string Title = "Do you want to proceed?";

    /// <summary>The reference's bare approval.</summary>
    public const string Yes = "Yes";

    /// <summary>The reference's refusal, whose "(esc)" it draws in bold.</summary>
    public const string NoLabel = "No, and tell Jarvis what to do differently";
    public const string NoSuffix = "(esc)";

    /// <summary>The reference's <c>eD</c>: what the feedback box invites once the refusal is chosen.</summary>
    public const string RejectPlaceholder = "tell Jarvis what to do differently";
    public const string AcceptPlaceholder = "tell Jarvis what to do next";

    /// <summary>The reference's <c>vD</c>: how each mode reads in a "switch to …" row.</summary>
    public static readonly IReadOnlyDictionary<string, string> ModeLabels = new Dictionary<string, string>
    {
        ["default"] = "default (ask each time)",
        ["acceptEdits"] = "accept edits (auto-approve file edits and common file commands)",
        ["auto"] = "auto (no routine prompts; a reviewer model screens actions)",
        ["dontAsk"] = "don't ask (auto-deny anything that would prompt)",
        ["plan"] = "plan mode (research and propose changes without making them)",
        ["bypassPermissions"] = "BYPASS PERMISSIONS (no further prompts)",
    };

    /// <summary>The reference's <c>_5</c>: the row that switches the session's mode.</summary>
    public static string SwitchModeLabel(string mode) =>
        $"Yes, and switch to {ModeLabels.GetValueOrDefault(mode, mode)} for this session";

    /// <summary>The reference's shell-rule row.</summary>
    public static string DontAskShellLabel(string tool, string workingDirectory) =>
        $"Yes, and don't ask again for {tool} commands in {workingDirectory}";

    /// <summary>Its any-command variant, when the rule would cover the whole tool.</summary>
    public static string DontAskAnyLabel(string tool) => $"Yes, and don’t ask again for any {tool} command";

    /// <summary>Its named-rule variant.</summary>
    public static string DontAskRuleLabel(string rule) => $"Yes, and don’t ask again for: {rule}";

    /// <summary>The reference's read-grant row.</summary>
    public static string AllowReadingLabel(string paths) => $"Yes, allow reading from {paths} from this project";

    /// <summary>Its write-grant row.</summary>
    public static string AllowAccessLabel(string paths) => $"Yes, and always allow access to {paths} from this project";

    private readonly SelectList _list = Build(prompt, alwaysAllowLabel);
    private bool _feedback;

    public PermissionPrompt Prompt { get; } = prompt;

    /// <summary>The context the key router resolves in while this is up.</summary>
    public string Context => "Confirmation";

    private static SelectList Build(PermissionPrompt prompt, string? alwaysAllowLabel)
    {
        var options = new List<SelectOption> { new("yes", Yes) };
        // An escalated call never earns a standing permission, which is why the
        // gate would refuse an "always" answer for one anyway.
        if (alwaysAllowLabel is { Length: > 0 } always && prompt.Risk != CallRisk.Escalated)
        {
            options.Add(new SelectOption("yes-dont-ask-again", always));
        }

        options.Add(new SelectOption("no", $"{NoLabel} {NoSuffix}"));
        return new SelectList(options);
    }

    public IReadOnlyList<string> Render(int columns)
    {
        var lines = new List<string> { "", ansi.Bold(Title), "" };
        if (Prompt.Detail is { Length: > 0 } detail)
        {
            lines.Add("  " + detail);
            lines.Add("");
        }

        if (Prompt.SubjectText is { Length: > 0 } subject)
        {
            foreach (var line in ResultFold.Render(subject, columns).Split('\n'))
            {
                lines.Add("  " + line);
            }

            lines.Add("");
        }

        if (Prompt.Warning is { Length: > 0 } warning)
        {
            lines.Add("  " + ansi.Color("warning", warning));
            lines.Add("");
        }

        lines.AddRange(_list.Render(ansi));
        if (_feedback)
        {
            lines.Add("");
            lines.Add("  " + ansi.Dim(RejectPlaceholder + ": ") + _list.InputText);
        }

        return lines;
    }

    public DialogResult Handle(string? action, KeyPress press)
    {
        var result = _list.Handle(action, press);
        if (result.Outcome == DialogOutcome.Accepted && result.Value == "no")
        {
            _feedback = true;
        }

        return result;
    }

    /// <summary>Maps the chosen row onto the gate's own answer.</summary>
    public static PermissionDecision Decide(string value) => value switch
    {
        "yes" => PermissionDecision.Allow,
        "yes-dont-ask-again" => PermissionDecision.AllowAlways,
        _ => PermissionDecision.Deny,
    };
}
