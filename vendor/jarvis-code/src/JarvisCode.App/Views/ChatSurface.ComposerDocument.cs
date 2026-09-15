using System.Windows;
using System.Windows.Threading;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

public partial class ChatSurface
{
    private DispatcherTimer? _composerDraftTimer;
    private bool _restoringComposerDraft;
    private ComposerDocument? _historyRichDraft;

    private string? DraftKey => _vm is null ? null : (_vm.IsCodeSurface ? "code:" : "chat:") + _vm.Session.Id;

    private void SaveComposerDraft()
    {
        _composerDraftTimer?.Stop();
        if (_restoringComposerDraft || _services is null || DraftKey is not { } key) return;
        try { _services.ComposerDrafts.Save(key, InputBox.Snapshot()); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { _services.Toasts.AddError("The draft could not be saved."); }
    }

    private void ScheduleComposerDraft()
    {
        if (_restoringComposerDraft || _services is null || _vm is null) return;
        if (_composerDraftTimer is null)
        {
            _composerDraftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _composerDraftTimer.Tick += (_, _) => SaveComposerDraft();
            Unloaded += (_, _) => SaveComposerDraft();
        }
        _composerDraftTimer.Stop();
        _composerDraftTimer.Start();
    }

    private void RestoreComposerDraft()
    {
        if (_services is null || DraftKey is not { } key) return;
        _restoringComposerDraft = true;
        try
        {
            _history = null;
            _historyRichDraft = null;
            HistoryHint.Visibility = Visibility.Collapsed;
            _commandCompleting = true;
            InputBox.Restore(_services.ComposerDrafts.Get(key));
        }
        finally { _restoringComposerDraft = false; }
    }

    private ComposerSkillChip? ResolveComposerSkill(string name)
    {
        var row = _commandSource.FirstOrDefault(row => row.SkillId == name || row.Label == name);
        if (row is not null) return new(row.SkillId.Length > 0 ? row.SkillId : row.Label, row.Label, row.SkillDescription, row.ArgumentHint);
        var command = _slashCommands.FirstOrDefault(command => command.Name == name);
        return command is null ? null : new(name, name, command.Description, command.Hint ?? "");
    }

    private void UpdateSkillArgumentHint()
    {
        if (SkillArgumentHint is null || InputBox is null) return;
        var hint = InputBox.ArgumentHint;
        SkillArgumentHint.Text = hint;
        SkillArgumentHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (hint.Length == 0) return;
        SkillArgumentHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var caret = CaretRectInComposer(InputBox.Text.Length);
        SkillArgumentHintOffset.X = caret.Left + 2;
        SkillArgumentHintOffset.Y = caret.Top + (caret.Height - SkillArgumentHint.DesiredSize.Height) / 2;
        SkillArgumentHint.MaxWidth = Math.Max(0, InputBox.ActualWidth - caret.Left - 10);
    }
}
