using System.Diagnostics;
using System.IO;
using JarvisCode.Cli.Repl;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Input;
using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.App.Services;
using JarvisCode.Core.Permissions;

namespace JarvisCode.Cli;

/// <summary>
/// The key layer's dispatch: which context a press is resolved in, what each
/// action does, and the readline editing the reference's input component
/// handles below the binding table.
/// </summary>
internal sealed partial class InteractiveRepl
{
    /// <summary>The reference's contexts, picked by what currently has focus.</summary>
    private string FocusContext()
    {
        if (State.Diff is not null) return "DiffDialog";
        if (State.Document is not null) return "Transcript";
        if (_choice is { } choice) return choice.Context;
        if (_trust is not null || _permission is not null || _plan is not null || _releaseNotes is not null)
        {
            return "Confirmation";
        }

        if (_question is { } question)
        {
            return question.Context;
        }

        if (_resume is not null)
        {
            return "Select";
        }

        if (_search is not null)
        {
            return "HistorySearch";
        }

        if (_completion is not null)
        {
            return "Autocomplete";
        }

        return "Chat";
    }

    /// <summary>Returns an exit code to leave the REPL, or null to keep going.</summary>
    private async Task<int?> HandleKeyAsync(KeyPress press, CancellationToken cancellationToken)
    {
        if (press.Key == "tab" && _composer.IsEmpty && _suggestedPrompt is { } suggestion &&
            FocusContext() == "Chat")
        { _composer.Set(suggestion); _suggestedPrompt = null; return null; }
        if (_overlay && press.Key == "escape")
        {
            _overlay = false;
            return null;
        }

        var context = FocusContext();
        var route = _router.Route(context, press);
        if (route.Kind == KeyRouteKind.Pending)
        {
            return null;
        }

        var action = route.Action;
        if (action?.StartsWith("strip:", StringComparison.Ordinal) == true)
        {
            var current = _sessionTabs.Values.ToList().IndexOf(_selectedSession);
            if (action == "strip:toggle") _showSessionStrip = !_showSessionStrip;
            else if (action == "strip:new") await ClearAsync(cancellationToken);
            else if (action == "strip:next") await SwitchTabAsync((current + 1) % _sessionTabs.Count, cancellationToken);
            else if (action == "strip:previous") await SwitchTabAsync((current + _sessionTabs.Count - 1) % _sessionTabs.Count, cancellationToken);
            else if (int.TryParse(action.Replace("strip:jump", "", StringComparison.Ordinal), out var index))
                await SwitchTabAsync(index - 1, cancellationToken);
            return null;
        }
        if (await HandleDialogKeyAsync(context, action, press, cancellationToken) is { } handled)
        {
            return handled ? null : null;
        }

        if (_search is not null)
        {
            await HandleSearchKeyAsync(action, press);
            return null;
        }

        if (_completion is not null)
        {
            if (HandleCompletionKey(action, press))
            {
                return null;
            }

            // The popup only claims its own four actions; everything else is
            // still chat input, so it is re-resolved in the Chat context rather
            // than swallowed — which is what keeps Enter submitting while a
            // completion is on screen.
            action = _router.Route("Chat", press).Action;
        }

        return await HandleChatKeyAsync(action, press, cancellationToken);
    }

    /// <summary>Null when no dialog is up; true when the press was consumed by one.</summary>
    private async Task<bool?> HandleDialogKeyAsync(
        string context, string? action, KeyPress press, CancellationToken cancellationToken)
    {
        if (State.Diff is { } diff)
        {
            if (!diff.Handle(action, _console.Height - 2)) State.Diff = null;
            return true;
        }
        if (State.Document is { } document)
        {
            if (action is "transcript:exit" or "app:toggleTranscript") State.Document = null;
            else if (action == "transcript:toggleShowAll") OpenTranscript(toggleDetails: true);
            else document.Scroll(action, _console.Height - 2);
            return true;
        }
        if (_choice is { } choice)
        {
            var result = choice.Handle(action, press);
            if (result.Outcome is DialogOutcome.Accepted or DialogOutcome.Cancelled)
            {
                var callback = _choiceAccepted;
                _choice = null; _choiceAccepted = null;
                if (result.Outcome == DialogOutcome.Accepted && callback is not null)
                    await callback(choice, result.Value ?? "", cancellationToken);
            }
            return true;
        }
        if (_permission is { } permission)
        {
            var result = permission.Handle(action, press);
            if (result.Outcome is DialogOutcome.Accepted or DialogOutcome.Cancelled)
            {
                var value = result.Outcome == DialogOutcome.Cancelled ? "no" : result.Value ?? "no";
                _permission = null;
                _permissionAnswer?.TrySetResult(PermissionPromptDialog.Decide(value));
                _permissionAnswer = null;
            }

            return true;
        }

        if (_plan is { } plan)
        {
            if (press.Chord == "ctrl+g")
            {
                await EditPlanExternallyAsync(plan, cancellationToken);
                return true;
            }

            var result = plan.Handle(action, press);
            if (result.Outcome is DialogOutcome.Accepted or DialogOutcome.Cancelled)
            {
                bool approved = result.Outcome == DialogOutcome.Accepted &&
                                PlanApproval.IsApproval(result.Value ?? "no");
                _plan = null;
                _planAnswer?.TrySetResult(new Core.Tools.BuiltIn.PlanApprovalDecision(
                    approved, approved ? null : result.Text, approved ? plan.Plan : null));
                _planAnswer = null;
                if (approved && _gate.Mode == PermissionMode.Plan)
                {
                    _gate.ExitPlanMode();
                }
            }

            return true;
        }

        if (_question is { } question)
        {
            var result = question.Handle(action, press);
            if (result.Outcome is DialogOutcome.Accepted or DialogOutcome.Cancelled)
            {
                bool submitted = result.Outcome == DialogOutcome.Accepted && result.Value == "submit";
                var answers = submitted ? question.ToAnswers() : null;
                _question = null;
                _questionAnswer?.TrySetResult(answers);
                _questionAnswer = null;
            }

            return true;
        }

        if (_resume is { } resume)
        {
            var result = resume.Handle(action, press);
            if (result.Outcome == DialogOutcome.Cancelled)
            {
                _resume = null;
                return true;
            }

            if (result.Outcome == DialogOutcome.Accepted)
            {
                await HandleResumeChoiceAsync(resume, result, cancellationToken);
            }

            return true;
        }

        return null;
    }

    private async Task HandleSearchKeyAsync(string? action, KeyPress press)
    {
        var search = _search!;
        switch (action)
        {
            case "historySearch:next":
                search.NextMatch();
                return;
            case "historySearch:cycleScope":
                search.CycleScope();
                return;
            case "historySearch:cancel":
                _composer.Set(search.Restore);
                _search = null;
                return;
            case "historySearch:accept":
                if (search.Current is { } accepted)
                {
                    _composer.Set(accepted.Display);
                }

                _search = null;
                return;
            case "historySearch:execute":
                if (search.Current is { } executed)
                {
                    _composer.Set(executed.Display);
                }

                _search = null;
                await SubmitComposerAsync();
                return;
        }

        if (press.Key == "backspace")
        {
            search.Backspace();
            return;
        }

        if (press.IsPrintable && press.Text is { Length: > 0 } typed)
        {
            search.Type(typed);
        }
    }

    /// <summary>True when the completion popup consumed the press.</summary>
    private bool HandleCompletionKey(string? action, KeyPress press)
    {
        var completion = _completion!;
        switch (action)
        {
            case "autocomplete:accept":
                var (text, offset) = completion.Accept(_composer.Text, _composer.Offset);
                _composer.Set(text, offset);
                _completion = null;
                RefreshCompletion();
                return true;
            case "autocomplete:dismiss":
                _completion = null;
                return true;
            case "autocomplete:next":
                _completion = completion.Next();
                return true;
            case "autocomplete:previous":
                _completion = completion.Previous();
                return true;
        }

        return false;
    }

    private async Task<int?> HandleChatKeyAsync(
        string? action, KeyPress press, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        _pendingNotice = null;

        switch (action)
        {
            case "app:exit":
                if (_composer.IsEmpty)
                {
                    if (_exit.Press(now))
                    {
                        return 0;
                    }

                    _pendingNotice = $"Press {ChordFormat.Format("ctrl+d")} again to exit";
                    return null;
                }

                _composer.Editor.Delete();
                return null;

            case "app:interrupt":
                return HandleInterrupt(now);

            case "app:toggleTranscript":
                OpenTranscript();
                return null;
            case "app:toggleReplTab":
                await OpenDiffAsync("", cancellationToken);
                return null;
            case "app:diffFileListUp":
            case "app:diffFileListDown":
                if (State.Diff is null) await OpenDiffAsync("", cancellationToken);
                State.Diff?.Handle(action == "app:diffFileListUp" ? "diff:previousFile" : "diff:nextFile", _console.Height);
                return null;

            case "app:toggleTodos":
                await RunCommandAsync("tasks", "", cancellationToken);
                return null;

            case "app:toggleBrief":
                _brief = !_brief;
                EmitNotice(_brief ? "Brief mode on" : "Brief mode off");
                return null;

            case "app:openArtifact":
                EmitNotice(ReplCommandTable.NotAvailable("artifact", "artifacts published to claude.ai"));
                return null;

            case "chat:cancel":
                return await HandleEscapeAsync(now);

            case "chat:submit":
                await SubmitComposerAsync();
                return null;

            case "chat:queueSubmit":
                QueueComposer();
                return null;

            case "chat:newline":
                _composer.Newline(now);
                return null;

            case "chat:clearInput":
                _composer.Clear();
                _completion = null;
                return null;

            case "chat:clearScreen":
                _screen.ClearAll();
                return null;

            case "chat:cycleMode":
                CycleMode();
                return null;

            case "chat:modelPicker":
                await RunCommandAsync("model", "", cancellationToken);
                return null;

            case "chat:fastMode":
                EmitNotice(ReplCommandTable.NotAvailable(
                    "fast", "fast mode is a server-side inference setting on Anthropic's models"));
                return null;

            case "chat:thinkingToggle":
                ToggleThinkingKeyword();
                return null;

            case "chat:workflowKeywordToggle":
                ToggleKeyword("ultracode");
                return null;

            case "chat:undo":
                if (!_composer.Editor.Undo())
                {
                    _pendingNotice = "Nothing to undo";
                }

                return null;

            case "chat:stash":
                StashPrompt();
                return null;

            case "chat:externalEditor":
                await EditComposerExternallyAsync(cancellationToken);
                return null;

            case "chat:imagePaste":
                _composer.PasteImage(now);
                return null;

            case "chat:killAgents":
                if (_workers.KillAll() is { } killed)
                {
                    EmitNotice(killed);
                }

                return null;

            case "history:previous":
                if (_composer.Editor.OnFirstLine)
                {
                    _navigator.Previous(_composer);
                }
                else
                {
                    _composer.Editor.UpLogicalLine();
                }

                return null;

            case "history:next":
                if (_composer.Editor.OnLastLine)
                {
                    _navigator.Next(_composer);
                }
                else
                {
                    _composer.Editor.DownLogicalLine();
                }

                return null;

            case "history:search":
                _search = new HistorySearch(_history, _session.WorkingDirectory, _session.Id)
                {
                    Restore = _composer.Text,
                };
                return null;

            case "task:background":
                EmitNotice("Background work runs on its own; use /tasks to see it.");
                return null;

            // The reference binds space in Chat to push-to-talk and gates it on
            // voice being available. There is no voice engine in this front-end,
            // so a space is a space.
            case "voice:pushToTalk":
                break;
        }

        if (_composer.HandleVim(press, now))
        {
            RefreshCompletion();
            return null;
        }

        if (HandleReadline(press, now))
        {
            RefreshCompletion();
            return null;
        }

        if (press.IsPaste && press.Text is { Length: > 0 } pasted)
        {
            _composer.Paste(pasted, _console.Height, now);
            RefreshCompletion();
            return null;
        }

        if (press.IsPrintable && press.Text is { Length: > 0 } typed)
        {
            if (typed == "?" && _composer.IsEmpty)
            {
                _overlay = !_overlay;
                return null;
            }

            _composer.Type(typed, now);
            RefreshCompletion();
            return null;
        }

        return null;
    }

    /// <summary>
    /// The editing the reference's input component handles itself, below the
    /// binding table: the readline movement and kill keys, and the arrows.
    /// </summary>
    private bool HandleReadline(KeyPress press, DateTime now)
    {
        var editor = _composer.Editor;
        switch (press.Chord)
        {
            case "left": editor.Left(); return true;
            case "right": editor.Right(); return true;
            case "home" or "ctrl+a": editor.StartOfLogicalLine(); return true;
            case "end" or "ctrl+e": editor.EndOfLogicalLine(); return true;
            case "backspace": editor.PushUndo(now); editor.Backspace(); return true;
            case "delete": editor.PushUndo(now); editor.Delete(); return true;
            case "ctrl+u": editor.PushUndo(now, immediate: true); editor.KillToLineStart(); return true;
            case "ctrl+k": editor.PushUndo(now, immediate: true); editor.KillToLineEnd(); return true;
            case "ctrl+w": editor.PushUndo(now, immediate: true); editor.DeleteWordBefore(); return true;
            case "ctrl+y": editor.PushUndo(now, immediate: true); editor.Yank(); return true;
            case "alt+y": editor.YankPop(); return true;
            case "alt+f": editor.ForwardWord(); return true;
            case "alt+b": editor.BackwardWord(); return true;
            case "alt+d": editor.PushUndo(now, immediate: true); editor.KillWord(); return true;
            case "ctrl+left": editor.BackwardWord(); return true;
            case "ctrl+right": editor.ForwardWord(); return true;
            case "up": editor.UpLogicalLine(); return true;
            case "down": editor.DownLogicalLine(); return true;
        }

        return false;
    }

    /// <summary>
    /// The reference's ctrl+c: it interrupts a running turn, then clears the
    /// input, and only a second press inside the window exits.
    /// </summary>
    private int? HandleInterrupt(DateTime now)
    {
        if (_turn is { IsCompleted: false })
        {
            _turnCts?.Cancel();
            return null;
        }

        if (!_composer.IsEmpty)
        {
            _composer.Clear();
            _completion = null;
            return null;
        }

        if (_interrupt.Press(now))
        {
            return 0;
        }

        _pendingNotice = $"Press {ChordFormat.Format("ctrl+c")} again to exit";
        return null;
    }

    /// <summary>
    /// The reference's escape: it interrupts a running turn, clears a non-empty
    /// box, and on a second press with nothing to clear opens the rewind
    /// selector.
    /// </summary>
    private async Task<int?> HandleEscapeAsync(DateTime now)
    {
        if (_turn is { IsCompleted: false })
        {
            _turnCts?.Cancel();
            return null;
        }

        if (_overlay)
        {
            _overlay = false;
            return null;
        }

        if (!_composer.IsEmpty)
        {
            _composer.Clear();
            _completion = null;
            _escape.Reset();
            return null;
        }

        if (_escape.Press(now))
        {
            await RunCommandAsync("rewind", "", CancellationToken.None);
            return null;
        }

        _pendingNotice = "Press esc again to rewind";
        return null;
    }

    private void CycleMode()
    {
        _gate.Mode = ModeDescriptors.Next(_gate.Mode, bypassAvailable: false);
        if (_gate.Mode == PermissionMode.Plan)
        {
            _gate.EnterPlanMode([PlanModeTools.PlanFilePath(_session.WorkingDirectory, _session.Id)]);
        }
        else
        {
            _gate.ExitPlanMode();
        }
    }

    /// <summary>The reference's alt+t: it toggles the deeper-reasoning keyword on the prompt.</summary>
    private void ToggleThinkingKeyword() => ToggleKeyword("ultrathink");

    private void ToggleKeyword(string keyword)
    {
        var text = _composer.Text;
        if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            _composer.Set(text.Replace(keyword, "", StringComparison.OrdinalIgnoreCase).TrimEnd());
            return;
        }

        _composer.Set(text.Length == 0 ? keyword : text.TrimEnd() + " " + keyword);
    }

    /// <summary>The reference's ctrl+s: the prompt is put aside and the box cleared.</summary>
    private void StashPrompt()
    {
        if (_composer.IsEmpty)
        {
            if (_stash is { Length: > 0 } stash)
            {
                _composer.Set(stash);
                _stash = null;
            }

            return;
        }

        _stash = _composer.Text;
        _composer.Clear();
        _pendingNotice = "Prompt stashed · ctrl+s to bring it back";
    }

    private string? _stash;

    /// <summary>Rebuilds the completion popup for wherever the caret now is.</summary>
    private void RefreshCompletion()
    {
        if (options.DisableSlashCommands && _composer.IsCommandLine)
        {
            _completion = null;
            return;
        }

        _completion = AutocompleteEngine.For(
            _composer.Text, _composer.Offset, ComposerCommands(),
            prefix => AutocompleteEngine.ListPaths(_session.WorkingDirectory, prefix));
    }

    /// <summary>ctrl+g / ctrl+x ctrl+e: the prompt in $EDITOR, and back.</summary>
    private async Task EditComposerExternallyAsync(CancellationToken cancellationToken)
    {
        if (await EditInEditorAsync(_composer.Text, ".md", cancellationToken) is { } edited)
        {
            _composer.Set(edited.TrimEnd('\n'));
        }
    }

    private async Task EditPlanExternallyAsync(PlanApproval plan, CancellationToken cancellationToken)
    {
        if (await EditInEditorAsync(plan.Plan, ".md", cancellationToken) is { } edited)
        {
            plan.SetPlan(edited);
            if (plan.PlanFilePath is { Length: > 0 } path)
            {
                try
                {
                    await File.WriteAllTextAsync(path, edited, cancellationToken);
                    EmitNotice(PlanApproval.SavedNotice);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    EmitError($"Could not save the plan: {ex.Message}");
                }
            }
        }
    }

    private async Task<string?> EditInEditorAsync(string text, string extension, CancellationToken cancellationToken)
    {
        var editor = Environment.GetEnvironmentVariable("EDITOR")
                     ?? Environment.GetEnvironmentVariable("VISUAL");
        if (editor is not { Length: > 0 })
        {
            EmitError("Set $EDITOR to edit here.");
            return null;
        }

        var file = Path.Combine(Path.GetTempPath(), $"jarvis-{Guid.NewGuid():N}{extension}");
        try
        {
            await File.WriteAllTextAsync(file, text, cancellationToken);
            _screen.ClearLive();
            var info = new ProcessStartInfo(editor) { UseShellExecute = false };
            info.ArgumentList.Add(file);
            using var process = Process.Start(info);
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken);
            }

            return await File.ReadAllTextAsync(file, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.ComponentModel.Win32Exception)
        {
            EmitError($"Could not open {editor}: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
