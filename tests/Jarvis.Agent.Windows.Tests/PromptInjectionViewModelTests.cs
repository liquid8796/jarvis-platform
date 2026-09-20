using System.IO;
using Jarvis.Agent.Core.Prompts;
using Jarvis.Agent.Desktop.ViewModels;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class PromptInjectionViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-prompt-ui-" + Guid.NewGuid().ToString("N"));
    private PromptInjectionStore Store => new(Path.Combine(_root, "prompt-injection.json"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Crud_and_toggles_are_drafts_until_saved_and_survive_reopen()
    {
        UserPromptContext? applied = null;
        var vm = new PromptInjectionViewModel(Store, settings => applied = settings.CreateContext(), _ => true);
        Assert.Equal(7, vm.Items.Count); Assert.False(vm.Enabled); Assert.Null(applied);
        vm.AddCommand.Execute(null);
        vm.Selected!.Title = "My workflow"; vm.Selected.Text = "Explain failed steps.";
        vm.Selected.Enabled = true; vm.Enabled = true;
        var id = vm.Selected.Id;
        Assert.True(vm.IsDirty); Assert.Null(applied);
        vm.SaveCommand.Execute(null);
        Assert.False(vm.IsDirty); Assert.Equal(id, Assert.Single(applied!.Prompts).Id);
        vm.Selected!.Text = "Explain failed steps and the next check.";
        vm.SaveCommand.Execute(null);
        var reopened = new PromptInjectionViewModel(Store, confirm: _ => true);
        reopened.Selected = reopened.Items.Single(row => row.Id == id);
        Assert.Equal("Explain failed steps and the next check.", reopened.Selected.Text);
        reopened.DeleteCommand.Execute(null);
        Assert.Contains(Store.Load().Entries, row => row.Id == id);
        reopened.SaveCommand.Execute(null);
        Assert.DoesNotContain(Store.Load().Entries, row => row.Id == id);
        Assert.Null(Store.Load().CreateContext());
    }

    [Fact]
    public void Turning_off_injection_does_not_apply_until_save()
    {
        Store.Save(new() { Enabled = true, Entries = [new("one", "One", "Use this optional workflow.", true)] }, 1);
        UserPromptContext? applied = null;
        var vm = new PromptInjectionViewModel(Store, settings => applied = settings.CreateContext());
        Assert.NotNull(applied);
        vm.Enabled = false; Assert.NotNull(applied);
        vm.SaveCommand.Execute(null); Assert.Null(applied);
        Assert.True(Assert.Single(vm.Items).Enabled);
    }

    [Fact]
    public void Cancelling_delete_reload_or_restore_keeps_the_draft()
    {
        var vm = new PromptInjectionViewModel(Store, confirm: _ => false);
        vm.Selected!.Title = "Keep my edit";
        vm.DeleteCommand.Execute(null); vm.ReloadCommand.Execute(null); vm.RestoreDefaultsCommand.Execute(null);
        Assert.Equal(7, vm.Items.Count);
        Assert.Equal("Keep my edit", vm.Selected.Title);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Invalid_draft_stays_editable_and_does_not_write_or_apply()
    {
        var applied = 0;
        var vm = new PromptInjectionViewModel(Store, _ => applied++);
        var before = applied;
        vm.AddCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        Assert.True(vm.IsDirty); Assert.NotEmpty(vm.Error); Assert.Equal(before, applied);
        Assert.Equal(7, Store.Load().Entries.Count);
    }

    [Fact]
    public void Concurrent_change_requires_reload_instead_of_overwriting_newer_settings()
    {
        var vm = new PromptInjectionViewModel(Store, confirm: _ => true);
        var newer = Store.Save(new() { Entries = [] }, 1);
        vm.Selected!.Title = "Stale edit"; vm.SaveCommand.Execute(null);
        Assert.True(vm.IsDirty); Assert.Contains("Reload", vm.Error);
        Assert.Empty(Store.Load().Entries);
        vm.ReloadCommand.Execute(null);
        Assert.Empty(vm.Items); Assert.False(vm.IsDirty); Assert.Contains(newer.Revision.ToString(), vm.SavedSummary);
    }

    [Fact]
    public void Runtime_status_reports_missing_protocol_support_and_preview_is_plain_text()
    {
        var vm = new PromptInjectionViewModel(Store);
        vm.UpdateRuntimeState(true, false);
        Assert.Contains("does not support", vm.RuntimeStatus);
        vm.Enabled = true; vm.Items[0].Enabled = true;
        Assert.Equal(vm.Items[0].Text, vm.Preview);
        vm.UpdateRuntimeState(true, true);
        Assert.Contains("subsequent successful", vm.RuntimeStatus);
        Assert.Contains("not system messages", vm.RuntimeStatus);
    }

    [Fact]
    public void Restore_defaults_is_explicit_and_does_not_reenable_any_prompt()
    {
        Store.Save(new() { Enabled = true, Entries = [new("one", "One", "Custom prompt.", true)] }, 1);
        var vm = new PromptInjectionViewModel(Store, confirm: _ => true);
        vm.RestoreDefaultsCommand.Execute(null);
        Assert.Equal(7, vm.Items.Count); Assert.False(vm.Enabled);
        Assert.All(vm.Items, row => Assert.False(row.Enabled));
        Assert.True(vm.IsDirty); Assert.Single(Store.Load().Entries);
        vm.SaveCommand.Execute(null);
        Assert.Equal(7, Store.Load().Entries.Count); Assert.Null(Store.Load().CreateContext());
    }
}
