using System.IO;
using JarvisCode.App.Infrastructure;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.ViewModels;

/// <summary>
/// One button on the approval card: its label, the digit that picks it, and the
/// key caps printed beside it.
/// </summary>
public sealed record PermissionOption(
    string Label, string Number, IReadOnlyList<string> Keys, RelayCommand Command);

/// <summary>One pending permission request, rendered as the inline approval card.</summary>
public sealed class PermissionPromptViewModel : ObservableObject
{
    private readonly TaskCompletionSource<PermissionDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PermissionPromptViewModel(PermissionPrompt prompt)
    {
        Prompt = prompt;
        DenyCommand = new RelayCommand(() => Resolve(PermissionDecision.Deny));
        AllowOnceCommand = new RelayCommand(() => Resolve(PermissionDecision.Allow));
        AllowAlwaysCommand = new RelayCommand(() => Resolve(PermissionDecision.AllowAlways));

        DenyOption = new PermissionOption("Deny", "1", ["Esc"], DenyCommand);
        // The gate re-prompts escalated calls whatever the answer was, so the standing
        // approval is left off rather than shown doing nothing.
        AlwaysAllowOption = prompt.Risk == CallRisk.Escalated || !prompt.AllowAlwaysOffered
            ? null
            : new PermissionOption("Always allow", "2", ["Ctrl", "\u21E7", "Enter"], AllowAlwaysCommand);
        AllowOnceOption = new PermissionOption(
            "Allow once", AlwaysAllowOption is null ? "2" : "3", ["Ctrl", "Enter"], AllowOnceCommand);
        Options = AlwaysAllowOption is null
            ? [DenyOption, AllowOnceOption]
            : [DenyOption, AlwaysAllowOption, AllowOnceOption];
    }

    public PermissionPrompt Prompt { get; }

    public string Title => Prompt.ToolName switch
    {
        "PowerShell" => "Allow Jarvis to run this command?",
        "Write" => $"Allow Jarvis to write {FileName}?",
        "Edit" => $"Allow Jarvis to edit {FileName}?",
        "WebFetch" => "Allow Jarvis to fetch this URL?",
        "TaskStop" => "Allow Jarvis to kill this background task?",
        _ => $"Allow Jarvis to use {Prompt.ToolName}?",
    };

    /// <summary>The line under the title; absent for shell, whose command is in the box.</summary>
    public string? Detail => Prompt.Detail;

    public bool HasDetail => !string.IsNullOrEmpty(Prompt.Detail);

    /// <summary>The file's own name, the way the reference app titles a file prompt.</summary>
    private string FileName =>
        Prompt.SubjectText is { Length: > 0 } path && Path.GetFileName(path) is { Length: > 0 } name
            ? name
            : "this file";

    public string? Warning => Prompt.Warning;

    public bool HasWarning => Prompt.Warning is not null;

    /// <summary>The literal subject in the monospace box: a command, a path, a URL.</summary>
    public string? SubjectText => Prompt.SubjectText;

    public bool HasSubject => Prompt.SubjectText is not null;

    public ToolDiffPreview.Preview? Diff => Prompt.DiffPreview;

    public IReadOnlyList<DiffLine> DiffLines => Prompt.DiffPreview?.Lines ?? [];

    public bool HasDiff => Prompt.DiffPreview is not null;

    /// <summary>Deny sits alone on the left; the approvals are right-aligned beside it.</summary>
    public PermissionOption DenyOption { get; }

    public PermissionOption? AlwaysAllowOption { get; }

    public bool HasAlwaysAllow => AlwaysAllowOption is not null;

    public PermissionOption AllowOnceOption { get; }

    /// <summary>Every button, numbered in the order they read left to right.</summary>
    public IReadOnlyList<PermissionOption> Options { get; }

    public RelayCommand DenyCommand { get; }
    public RelayCommand AllowOnceCommand { get; }
    public RelayCommand AllowAlwaysCommand { get; }

    public Task<PermissionDecision> Task => _completion.Task;

    /// <summary>Picks the button whose printed digit was typed; null when none matches.</summary>
    public PermissionOption? OptionForNumber(string number) =>
        Options.FirstOrDefault(option => option.Number == number);

    public void Resolve(PermissionDecision decision) => _completion.TrySetResult(decision);
}
