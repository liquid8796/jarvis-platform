using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;
using JarvisCode.App.Views;
using JarvisCode.Core.Models;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class ChatGptControlsNativeTests
{
    [NativeUiFact]
    public void LiveChatGptEffortUsesItsDynamicLabelAndRawKeyWhileApiEffortStaysFixed()
    {
        var paths = ProfilePaths.Create("chatgpt-controls-" + Guid.NewGuid().ToString("N"));
        try
        {
            WpfTestThread.Run(() =>
            {
                using var services = new AppServices(paths, headless: true);
                const string browserModel = "chatgpt-web:Latest";
                services.Settings.Current.CustomModels.Add(new ModelInfo(
                    ChatGptWebProvider.ProviderId, browserModel, "Latest · 6 Pro", 128000));
                services.Settings.Current.CustomModels.Add(new ModelInfo(
                    ChatGptWebProvider.ProviderId, ChatGptWebProvider.AutoModelSlug, "ChatGPT Auto", 128000));
                services.Settings.Current.CustomModels.Add(new ModelInfo("openai", "api-test", "API", 128000));
                services.Settings.Current.ThinkingEffortName = "Low";
                var vm = new ChatViewModel(services, isCodeSurface: true);
                try
                {
                    vm.Session.ModelId = browserModel;
                    var surface = new ChatSurface();
                    Field("_services").SetValue(surface, services);
                    Field("_vm").SetValue(surface, vm);
                    var controls = new ChatGptComposerControls(
                        "6 Pro",
                        [new ChatGptPickerOption("Latest", "Latest", Selected: true)],
                        [
                            new ChatGptPickerOption("0", "Instant"),
                            new ChatGptPickerOption("17", "Warp drive", Selected: true),
                            new ChatGptPickerOption("9001", "Beyond", Selected: false),
                        ]);
                    var cache = Assert.IsType<Dictionary<string, ChatGptComposerControls>>(
                        Field("_chatGptControlsByContext").GetValue(surface));
                    var cacheKey = Assert.IsType<string>(StaticCall(
                        "ChatGptControlsKey", vm.Session.Id, browserModel));
                    cache[cacheKey] = controls;

                    Call("BuildSlashCommands");
                    Call("UpdateModelChip");
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)surface.FindName("EffortChip")).Visibility);
                    Assert.Equal(
                        new[] { "Instant", "Warp drive", "Beyond" },
                        Assert.IsAssignableFrom<IReadOnlyList<string>>(Call("CurrentEffortLevels")));

                    Command("effort", "Warp drive");
                    Assert.Equal("Warp drive", services.Settings.Current.EffortByModel[browserModel]);
                    Assert.Equal(
                        "17",
                        services.Settings.Current.ProviderOptionsByModel[browserModel][ChatGptWebProvider.PowerOptionKey]);
                    Assert.Equal(
                        "Warp drive",
                        ((System.Windows.Controls.TextBlock)surface.FindName("EffortChipText")).Text);

                    var autoModel = services.Settings.Models.Single(model =>
                        model.ModelId == ChatGptWebProvider.AutoModelSlug);
                    Assert.True(vm.SetModelAsync(autoModel, JarvisCode.Core.Hooks.ModelSwitchSource.Picker)
                        .GetAwaiter().GetResult());
                    Call("UpdateModelChip");
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)surface.FindName("EffortChip")).Visibility);
                    Command("effort", "Pro");
                    Assert.Contains("Choose an explicit model", Assert.IsType<NoticeItem>(vm.Transcript.Last()).Text);
                    Assert.Equal("Low", services.Settings.Current.ThinkingEffortName);

                    var apiModel = services.Settings.Models.Single(model => model.ModelId == "api-test");
                    Assert.True(vm.SetModelAsync(apiModel, JarvisCode.Core.Hooks.ModelSwitchSource.Picker)
                        .GetAwaiter().GetResult());
                    Call("UpdateModelChip");
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)surface.FindName("EffortChip")).Visibility);
                    Assert.Equal(
                        EffortLevels.Names,
                        Assert.IsAssignableFrom<IReadOnlyList<string>>(Call("CurrentEffortLevels")));
                    Command("effort", "max");
                    Assert.Equal("Max", services.Settings.Current.ThinkingEffortName);

                    var liveModel = services.Settings.Models.Single(model => model.ModelId == browserModel);
                    Assert.True(vm.SetModelAsync(liveModel, JarvisCode.Core.Hooks.ModelSwitchSource.Picker)
                        .GetAwaiter().GetResult());
                    Call("UpdateModelChip");
                    Assert.Equal("Max", services.Settings.Current.ThinkingEffortName);
                    Assert.Equal(
                        "Warp drive",
                        ((System.Windows.Controls.TextBlock)surface.FindName("EffortChipText")).Text);

                    object? Call(string method, params object[] args) => typeof(ChatSurface)
                        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, args);
                    static object? StaticCall(string method, params object[] args) => typeof(ChatSurface)
                        .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
                    void Command(string name, string argument)
                    {
                        var commands = ((IEnumerable)Field("_slashCommands").GetValue(surface)!).Cast<object>();
                        var command = commands.Single(item => (string)item.GetType().GetProperty("Name")!.GetValue(item)! == name);
                        ((Func<string, Task>)command.GetType().GetProperty("Execute")!.GetValue(command)!)(argument).GetAwaiter().GetResult();
                    }
                }
                finally { vm.Discard(); }
            });
        }
        finally
        {
            var target = Path.GetFullPath(paths.Root);
            var parent = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            if (Path.GetDirectoryName(target) == parent && Path.GetFileName(target).StartsWith("JarvisCode-chatgpt-controls-", StringComparison.Ordinal)
                && Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private static FieldInfo Field(string name) => typeof(ChatSurface).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
