using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.Core.Memory;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// Customize › Memory (c71860c77-BRN4k43v): the switch that decides whether
/// sessions read and update memory, then one row per memory file, each opening
/// on its text and carrying a delete. The files are this app's own per-project
/// memory folder, which is where the memory tool writes them.
/// </summary>
public partial class CustomizeSurface
{
    public void ShowMemory()
    {
        if (_services is null)
            return;

        var page = NewPage();
        page.Children.Add(CustomizeUi.PageHeader("Memory", null, null, ""));

        var enabled = Services.UiSettings.Current.MemoryEnabled;
        var switchRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        switchRow.ColumnDefinitions.Add(new ColumnDefinition());
        switchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(CustomizeUi.Text("Use memory in sessions", 13.5, "Text100Brush", semibold: true));
        var description = CustomizeUi.Text(
            CustomizeMemory.SwitchDescription(enabled), 12, "Text400Brush", wrap: true);
        description.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(description);
        switchRow.Children.Add(text);
        var toggle = CustomizeUi.Switch(enabled, value =>
        {
            Services.UiSettings.Current.MemoryEnabled = value;
            Services.UiSettings.Save();
            ShowMemory();
        }, "Use memory in sessions");
        Grid.SetColumn(toggle, 1);
        switchRow.Children.Add(toggle);
        page.Children.Add(switchRow);

        var blurb = CustomizeUi.Text(
            "Jarvis saves what it learns about you and your work during Code sessions. " +
            "These files are stored on this device.", 12, "Text400Brush", wrap: true);
        blurb.Margin = new Thickness(0, 12, 0, 4);
        page.Children.Add(blurb);

        var directory = ProjectMemory.DirectoryFor(Services.Paths.MemoryRoot, Cwd);
        var entries = CustomizeMemory.Load(directory);
        if (entries.Count == 0)
        {
            page.Children.Add(CustomizeUi.Notice(
                "No memories yet. Jarvis will add entries here as you work together."));
        }
        else
        {
            foreach (var entry in entries)
                page.Children.Add(MemoryRow(entry, directory));
        }

        var path = CustomizeUi.Notice(directory);
        page.Children.Add(path);
        var open = CustomizeUi.Ghost("Open folder", () => OpenInShell(directory));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(open);

        Show(page);
    }

    private FrameworkElement MemoryRow(MemoryEntry entry, string directory)
    {
        var body = new StackPanel();
        body.Children.Add(CustomizeUi.Text(entry.Title, 13.5, "Text100Brush"));
        if (entry.Description is { Length: > 0 } description)
        {
            var block = CustomizeUi.Text(description, 12, "Text400Brush", wrap: true);
            block.Margin = new Thickness(0, 3, 12, 0);
            body.Children.Add(block);
        }

        var detail = new TextBox
        {
            Text = entry.Body,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 260,
            Margin = new Thickness(0, 8, 12, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
            FontSize = 11.5,
        };
        detail.SetResourceReference(StyleProperty, "CodeEditorTextBox");
        body.Children.Add(detail);

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(CustomizeUi.Ghost("Edit", () => OpenInShell(entry.Path)));
        controls.Children.Add(CustomizeUi.Kebab($"Actions for {entry.Title}",
        [
            ("Open folder", false, () => OpenInShell(directory)),
            ("Copy", false, () => CustomizeUi.Copy(entry.Body)),
            ("Delete", true, () => DeleteMemory(entry)),
        ]));

        return CustomizeUi.ListRow(
            body,
            controls,
            () => detail.Visibility = detail.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible,
            entry.Title);
    }

    private void DeleteMemory(MemoryEntry entry)
    {
        if (!Confirm("Delete memory", $"Delete memory {entry.Title}", "Delete", entry.FileName))
            return;
        if (!CustomizeMemory.Delete(entry.Path))
        {
            Warn("Couldn’t delete this memory. Please try again.");
            return;
        }
        ShowMemory();
    }
}
