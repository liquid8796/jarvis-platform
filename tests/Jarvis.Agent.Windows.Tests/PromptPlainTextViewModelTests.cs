using System.IO;
using Jarvis.Agent.Core.Prompts;
using Jarvis.Agent.Desktop.ViewModels;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class PromptPlainTextViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-plain-prompts-" + Guid.NewGuid().ToString("N"));
    private PromptInjectionStore Store => new(Path.Combine(_root, "prompt-injection.json"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Preview_save_apply_and_reload_preserve_the_exact_body()
    {
        const string body = " \r\n    Ghi r\u00f5 \u0111\u1ed9 tin c\u1eady. \U0001F9EA\r\n\t{\"literal\":true}  \r\n";
        Store.Save(new() { Enabled = true, Entries = [new("one", "One", "Old body", true)] }, 1);
        UserPromptContext? applied = null;
        var vm = new PromptInjectionViewModel(Store, settings => applied = settings.CreateContext(), _ => true);
        vm.Selected!.Text = body;
        Assert.Equal(body, vm.Preview);
        Assert.Equal("Old body", applied!.ToContextText());
        vm.SaveCommand.Execute(null);
        Assert.Empty(vm.Error);
        Assert.False(vm.IsDirty);
        Assert.Equal(body, vm.Selected!.Text);
        Assert.Equal(body, applied!.ToContextText());
        Assert.Equal(body, Assert.Single(Store.Load().Entries).Text);
        vm.ReloadCommand.Execute(null);
        Assert.Equal(body, vm.Selected!.Text);
        Assert.Equal(body, vm.Preview);
    }

    [Fact]
    public void A_rendered_separator_overflow_is_reported_without_saving_or_applying()
    {
        var entries = Enumerable.Range(0, 4).Select(i => new PromptInjectionEntry("p" + i, "T",
            new string('x', i == 3 ? 3994 : 4000), true)).ToArray();
        var saved = Store.Save(new() { Enabled = true, Entries = entries }, 1);
        UserPromptContext? applied = null;
        var vm = new PromptInjectionViewModel(Store, settings => applied = settings.CreateContext());
        vm.Selected = vm.Items[3];
        vm.Selected.Text += "x";
        Assert.StartsWith("Complete the draft", vm.Preview);
        vm.SaveCommand.Execute(null);
        Assert.True(vm.IsDirty);
        Assert.NotEmpty(vm.Error);
        Assert.Equal(saved.Revision, Store.Load().Revision);
        Assert.Equal(UserPromptContext.MaxContextLength, applied!.ToContextText().Length);
    }

    [Fact]
    public void Multiple_saved_prompts_match_preview_order_and_omit_disabled_bodies()
    {
        Store.Save(new() { Enabled = true, Entries = [
            new("second", "Z title", "\tfirst ", true),
            new("hidden", "Hidden title", "Disabled body", false),
            new("first", "A title", " second\r\n", true)] }, 1);
        var vm = new PromptInjectionViewModel(Store);
        Assert.Equal("\tfirst \n\n second\r\n", vm.Preview);
        Assert.Equal(Store.Load().CreateContext()!.ToContextText(), vm.Preview);
    }
}
