using System.IO;
using Jarvis.Agent.Core.Execution;
using Jarvis.Agent.Desktop.ViewModels;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class ExecutionSettingsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-settings-ui-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "execution-settings.json");

    [Fact]
    public void Default_is_five_and_draft_does_not_apply_until_saved()
    {
        AgentExecutionSettings? applied = null;
        var vm = new ExecutionSettingsViewModel(new ExecutionSettingsStore(FilePath), s => applied = s);
        Assert.Equal("5", vm.ConcurrentCalls.Text); Assert.Equal("5", vm.ProcessJobs.Text); Assert.Equal("5", vm.DurableTasks.Text);
        vm.ConcurrentCalls.Text = "17";
        Assert.Null(applied); Assert.False(File.Exists(FilePath)); Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);
        Assert.Equal(17, applied!.MaxConcurrentCalls); Assert.Equal(2, applied.Revision);
        Assert.Equal(applied, new ExecutionSettingsStore(FilePath).Load());
        Assert.False(vm.SaveCommand.CanExecute(null));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("99999999999999999999")]
    public void Invalid_numbers_have_inline_errors_and_cannot_save(string text)
    {
        var vm = new ExecutionSettingsViewModel(new ExecutionSettingsStore(FilePath));
        vm.ConcurrentCalls.Text = text;
        Assert.True(vm.ConcurrentCalls.HasErrors); Assert.NotEmpty(vm.ConcurrentCalls.Error);
        Assert.False(vm.SaveCommand.CanExecute(null)); vm.SaveCommand.Execute(null);
        Assert.False(File.Exists(FilePath));
    }
    [Fact]
    public void Positive_limits_are_not_silently_clamped_and_acknowledgement_is_explicit()
    {
        var vm = new ExecutionSettingsViewModel(new ExecutionSettingsStore(FilePath));
        vm.ConcurrentCalls.Text = "120"; vm.SaveCommand.Execute(null);
        Assert.Equal(120, vm.Saved.MaxConcurrentCalls); Assert.NotEmpty(vm.HighLoadWarning);
        vm.UpdateRuntimeState(true, vm.Saved.Revision, 0, true);
        Assert.Contains("pending", vm.SyncSummary, StringComparison.OrdinalIgnoreCase);
        vm.UpdateRuntimeState(true, vm.Saved.Revision, vm.Saved.Revision, true);
        Assert.Contains("acknowledged", vm.SyncSummary, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Concurrent_local_settings_change_requires_reload_not_overwrite()
    {
        var store = new ExecutionSettingsStore(FilePath);
        var vm = new ExecutionSettingsViewModel(store);
        var other = store.Save(new AgentExecutionSettings { MaxConcurrentCalls = 7 }, 1);
        vm.ConcurrentCalls.Text = "8"; vm.SaveCommand.Execute(null);
        Assert.Contains("changed", vm.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(other, store.Load());
        vm.ReloadCommand.Execute(null);
        Assert.Equal("7", vm.ConcurrentCalls.Text); Assert.Empty(vm.Error);
    }
    [Fact]
    public void Corrupt_settings_are_not_replaced_by_defaults()
    {
        Directory.CreateDirectory(_root); File.WriteAllText(FilePath, "not json");
        var vm = new ExecutionSettingsViewModel(new ExecutionSettingsStore(FilePath));
        vm.ConcurrentCalls.Text = "9";
        Assert.NotEmpty(vm.Error); Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal("not json", File.ReadAllText(FilePath));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
