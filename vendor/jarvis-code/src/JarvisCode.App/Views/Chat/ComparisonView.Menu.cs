using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Mcp;

namespace JarvisCode.App.Views.Chat;

public sealed partial class ComparisonView
{
    public event EventHandler<string>? CustomizeRequested;
    public event EventHandler? ProjectChanged;

    private IReadOnlyList<ChatViewModel> ComparisonModels => _panels.Select(panel => panel.ViewModel).OfType<ChatViewModel>().ToArray();

    internal void OpenComposerMenu(Button anchor)
    {
        if (_session.Phase == ComparisonPhase.Streaming) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
        var first = ComparisonModels.FirstOrDefault();
        var conversation = first is null || _services is null
            ? ChatConversationSettings.Resolve(null, false)
            : _services.ChatConversations.Get(first.Session.Id, _services.Settings.Current.EnableWebSearch);
        var connectors = new List<ChatConnectorRow>();
        if (_services is not null)
        {
            try
            {
                var plugins = Plugins.Load(_services.Paths.PluginsDirectory);
                connectors.AddRange(McpConfig.Load(first?.Session.WorkingDirectory ?? Environment.CurrentDirectory, _services.Paths.UserMcpFile,
                    DesktopExtensions.ConfigFiles(_services.Paths, plugins.McpFiles)).Select(server =>
                    new ChatConnectorRow(server.Name, _services.Mcp.ConnectedToolCounts.ContainsKey(server.Name),
                        conversation.Connectors.Contains(server.Name, StringComparer.OrdinalIgnoreCase))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _composerError.Text = error.Message; }
        }
        foreach (var item in RenderComparisonMenu(ChatComposerMenu.Plus(_services is not null, _services is not null,
                     false, connectors, conversation.ToolAccess, conversation.WebSearch))) menu.Items.Add(item);
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private IEnumerable<object> RenderComparisonMenu(IReadOnlyList<ChatMenuRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Kind == ChatMenuKind.Separator) { yield return new Separator(); continue; }
            object header = row.Label;
            if (row.Subtitle is not null)
            {
                var detail = new TextBlock { Text = row.Subtitle, FontSize = 11 };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
                header = new StackPanel { Children = { new TextBlock { Text = row.Label }, detail } };
            }
            var item = new MenuItem { Header = header, IsCheckable = row.Kind == ChatMenuKind.Checkbox,
                IsChecked = row.Checked, StaysOpenOnClick = row.Kind == ChatMenuKind.Checkbox, IsEnabled = row.Id != "none" };
            AutomationProperties.SetName(item, row.Label);
            if (row.Suffix is { } suffix) Controls.MenuItemExtras.SetTrailing(item, suffix);
            if (row.Id is "skills" or "plugins")
            {
                var page = row.Id;
                IReadOnlyList<string> names = [];
                if (_services is not null && page == "plugins")
                {
                    try { names = Plugins.Load(_services.Paths.PluginsDirectory).Installed.Select(plugin => plugin.Name).ToArray(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _composerError.Text = error.Message; }
                }
                // Chat uses the same account-capability boundary as the ordinary
                // composer; locally installed Code skills remain managed in Customize.
                foreach (var name in names.Take(12))
                {
                    var child = new MenuItem { Header = name };
                    child.Click += (_, _) => CustomizeRequested?.Invoke(this, page);
                    item.Items.Add(child);
                }
                if (names.Count == 0) item.Items.Add(new MenuItem { Header = $"No {row.Label.ToLowerInvariant()} yet", IsEnabled = false });
                item.Items.Add(new Separator());
                var manage = new MenuItem { Header = $"Manage {row.Label.ToLowerInvariant()}…" };
                manage.Click += (_, _) => CustomizeRequested?.Invoke(this, page);
                item.Items.Add(manage);
            }
            else if (row.Id == "project") BuildComparisonProjects(item);
            else if (row.Items is { Count: > 0 } children)
                foreach (var child in RenderComparisonMenu(children)) item.Items.Add(child);
            else item.Click += (_, _) => RunComparisonMenuAction(row.Id);
            yield return item;
        }
    }

    private void RunComparisonMenuAction(string? id)
    {
        if (_session.Phase == ComparisonPhase.Streaming || id is null) return;
        if (id == "files")
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Add files or photos to the conversation" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) AttachFiles(picker.FileNames);
            return;
        }
        if (_services is null) return;
        var models = ComparisonModels;
        var first = models.FirstOrDefault();
        if (first is null) return;
        var state = _services.ChatConversations.Get(first.Session.Id, _services.Settings.Current.EnableWebSearch);
        switch (id)
        {
            case "screenshot":
                try
                {
                    var capture = new ComputerUseService(() => _services.UiSettings.Current).CaptureScreenshot();
                    var directory = Path.Combine(_services.Paths.Root, "attachments");
                    Directory.CreateDirectory(directory);
                    var file = Path.Combine(directory, "screenshot-" + Guid.NewGuid().ToString("N") + ".jpg");
                    File.WriteAllBytes(file, Convert.FromBase64String(capture.Image.Base64Data));
                    AttachFiles([file]);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { _composerError.Text = ChatComposerMenu.CouldNotCaptureScreen; }
                break;
            case "web-search":
                foreach (var model in models) _services.ChatConversations.SetWebSearch(model.Session.Id, !state.WebSearch);
                break;
            case "tool-access:on":
            case "tool-access:off":
                foreach (var model in models) _services.ChatConversations.SetToolAccess(model.Session.Id,
                    id == "tool-access:on" ? ChatToolAccess.LoadWhenNeeded : ChatToolAccess.AlreadyLoaded);
                break;
            case "manage-connectors": CustomizeRequested?.Invoke(this, "connectors"); break;
            case "new-project":
                var name = InputDialog.Prompt(Window.GetWindow(this), ChatComposerMenu.StartANewProject);
                if (string.IsNullOrWhiteSpace(name)) break;
                var project = _services.Projects.Create(name);
                foreach (var model in models) _services.Projects.Assign(model.Session.Id, project.Id);
                ProjectChanged?.Invoke(this, EventArgs.Empty);
                break;
            default:
                if (id.StartsWith("connector:", StringComparison.Ordinal))
                {
                    var server = id["connector:".Length..];
                    var enabled = !state.Connectors.Contains(server, StringComparer.OrdinalIgnoreCase);
                    foreach (var model in models) _services.ChatConversations.SetConnector(model.Session.Id, server, enabled);
                }
                else if (id.StartsWith("project:", StringComparison.Ordinal))
                {
                    var selected = _services.Projects.All.FirstOrDefault(project => project.Name == id["project:".Length..]);
                    var target = selected?.Id == _services.Projects.ForSession(first.Session.Id)?.Id ? null : selected?.Id;
                    foreach (var model in models) _services.Projects.Assign(model.Session.Id, target);
                    ProjectChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
        }
    }

    private void BuildComparisonProjects(MenuItem root)
    {
        if (_services is null) return;
        var search = new TextBox { Margin = new Thickness(4, 2, 4, 4), MinWidth = 200 };
        search.SetResourceReference(StyleProperty, "InputTextBox");
        AutomationProperties.SetName(search, ChatComposerMenu.SearchProjects);
        var host = new MenuItem { Header = search, StaysOpenOnClick = true };
        void Fill()
        {
            root.Items.Clear(); root.Items.Add(host);
            var first = ComparisonModels.FirstOrDefault();
            var current = first is null ? null : _services.Projects.ForSession(first.Session.Id)?.Name;
            foreach (var row in RenderComparisonMenu(ChatComposerMenu.ProjectPicker(
                         _services.Projects.Search(search.Text).Select(project => project.Name).ToArray(), current,
                         search.Text, _services.Projects.All.Count > 0))) root.Items.Add(row);
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
    }
}
