using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.Composition;
using JarvisCode.App.ViewModels;
using JarvisCode.App.Views.Chat;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class ComparisonComposerTests
{
    [NativeUiFact]
    public void Full_plus_menu_updates_both_comparison_conversations_without_changing_global_defaults()
    {
        WpfTestThread.Run(() =>
        {
            var paths = ProfilePaths.Create("comparison-menu-test-" + Guid.NewGuid().ToString("N"));
            using var services = new AppServices(paths, headless: true);
            services.Settings.Current.EnableWebSearch = false;
            File.WriteAllText(paths.UserMcpFile, "{\"mcpServers\":{\"fixture-connector\":{\"command\":\"never-start-fixture\"}}}");
            var sessions = new SessionViewModels(services, isCodeSurface: false);
            var view = new ComparisonView();
            view.Initialize(services, sessions);
            var project = services.Projects.Create("Comparison context");
            var window = new Window { Content = view, Width = 1000, Height = 700, Left = -30000, Top = -30000, ShowActivated = false };
            window.Show(); window.UpdateLayout();
            var anchor = Walk(view).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Add files");
            string? customize = null;
            view.CustomizeRequested += (_, page) => customize = page;
            view.OpenComposerMenu(anchor);
            var menu = anchor.ContextMenu;
            try
            {
                var rows = MenuItems(menu.Items).ToArray();
                Assert.Contains(rows, item => Equals(item.Header, ChatComposerMenu.AddFilesOrPhotos));
                Assert.Contains(rows, item => Equals(item.Header, ChatComposerMenu.TakeAScreenshot));
                rows.Single(item => Equals(item.Header, ChatComposerMenu.WebSearch)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                rows.Single(item => Equals(item.Header, "fixture-connector")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                rows.Single(item => AutomationProperties.GetName(item) == ChatComposerMenu.ToolsAlreadyLoaded).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                rows.Single(item => Equals(item.Header, "Comparison context")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                rows.Single(item => Equals(item.Header, ChatComposerMenu.ManageConnectors)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("connectors", customize);
                Assert.False(services.Settings.Current.EnableWebSearch);
                Assert.Equal(2, services.UiSettings.Current.ChatConversations.Count);
                foreach (var id in services.UiSettings.Current.ChatConversations.Keys)
                {
                    var state = services.ChatConversations.Get(id, false);
                    Assert.True(state.WebSearch);
                    Assert.Equal(ChatToolAccess.AlreadyLoaded, state.ToolAccess);
                    Assert.Contains("fixture-connector", state.Connectors);
                    Assert.Equal(project.Id, services.Projects.ForSession(id)?.Id);
                }
            }
            finally
            {
                menu.IsOpen = false;
                window.Close();
                foreach (var id in services.UiSettings.Current.ChatConversations.Keys.ToArray()) sessions.Forget(id);
            }
        });
    }

    [Fact]
    public void Attachment_only_turns_are_accepted_but_cannot_overlap_streaming()
    {
        var session = new ComparisonSession();
        Assert.False(session.BeginTurn(""));
        Assert.True(session.BeginTurn("", hasAttachments: true));
        Assert.Equal(ComparisonPhase.Streaming, session.Phase);
        Assert.False(session.BeginTurn("another", hasAttachments: true));
        session.Settle(0, null);
        session.Settle(1, null);
        Assert.True(session.BeginTurn("follow up", hasAttachments: false));
    }

    [NativeUiFact]
    public void Real_composer_accepts_deduplicated_files_and_long_pastes_with_removable_chips()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "jarvis-comparison-composer-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "notes.txt");
                File.WriteAllText(file, "notes");
                var view = new ComparisonView();
                view.AttachFiles([file, file]);
                var remove = Assert.Single(Walk(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Remove notes.txt");
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.DoesNotContain(Walk(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Remove notes.txt");
                var input = Assert.Single(Walk(view).OfType<TextBox>());
                var data = new DataObject(DataFormats.UnicodeText, new string('x', 2001));
                var paste = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
                input.RaiseEvent(paste);
                Assert.True(paste.CommandCancelled);
                Assert.Contains(Walk(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Remove Pasted text");
                Assert.Contains(Walk(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Add files");
                Assert.Contains(Walk(view).OfType<Button>(), button => AutomationProperties.GetName(button) == "Dictate");
            }
            catch (Exception ex) { error = ex; }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Walk(child)) yield return descendant;
    }

    private static IEnumerable<MenuItem> MenuItems(ItemCollection collection)
    {
        foreach (var item in collection.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in MenuItems(item.Items)) yield return child;
        }
    }
}
