using System.IO;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Settings;

namespace JarvisCode.App.Views;

internal sealed class OutputStyleEditor : Window
{
    internal string? SavedName { get; private set; }

    internal OutputStyleEditor(AppServices services, string workingDirectory, string? selectedName = null)
    {
        Title = "Custom output styles";
        Width = 660;
        Height = 650;
        MinWidth = 520;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        var custom = OutputStyles.Available(workingDirectory, services.Paths.Root).Where(style => style.FilePath is not null).ToList();
        var picker = new ComboBox { MinWidth = 240, Margin = new Thickness(0, 0, 0, 16) };
        picker.Items.Add("New output style");
        foreach (var style in custom) picker.Items.Add(style.Name);
        panel.Children.Add(picker);
        var name = SettingsRows.Input("", width: 380, accessibleName: "Name");
        var description = SettingsRows.Input("", width: 380, accessibleName: "Description");
        var prompt = SettingsRows.Input("", width: 560, accessibleName: "Instructions");
        prompt.AcceptsReturn = true;
        prompt.TextWrapping = TextWrapping.Wrap;
        prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        prompt.Height = 240;
        prompt.Width = double.NaN;
        prompt.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(SettingsRows.Row("Name", null, name));
        panel.Children.Add(SettingsRows.Row("Description", null, description));
        panel.Children.Add(SettingsUi.Caption("Instructions"));
        panel.Children.Add(prompt);
        var keep = new CheckBox { Content = "Keep coding instructions", Margin = new Thickness(0, 12, 0, 12) };
        panel.Children.Add(keep);
        var scope = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        scope.Items.Add("All projects");
        if (!string.IsNullOrWhiteSpace(workingDirectory)) scope.Items.Add("This project");
        scope.SelectedIndex = 0;
        panel.Children.Add(SettingsRows.Row("Save to", null, scope));
        var error = SettingsRows.Danger("");
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = SettingsRows.SecondaryButton("Cancel", Close);
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        var save = SettingsRows.PrimaryButton("Save", () =>
        {
            try
            {
                var directory = scope.SelectedIndex == 1
                    ? Path.Combine(workingDirectory, ".jarvis", "output-styles")
                    : Path.Combine(services.Paths.Root, "output-styles");
                CustomOutputStyles.Save(directory, name.Text, description.Text, prompt.Text, keep.IsChecked == true);
                SavedName = name.Text.Trim();
                DialogResult = true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { error.Text = ex.Message; }
        });
        save.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        picker.SelectionChanged += (_, _) =>
        {
            var selected = picker.SelectedIndex > 0 ? custom[picker.SelectedIndex - 1] : null;
            name.Text = selected?.Name ?? "";
            description.Text = selected?.Description ?? "";
            prompt.Text = selected?.Prompt ?? "";
            keep.IsChecked = selected?.KeepCodingInstructions ?? false;
            scope.SelectedIndex = selected?.FilePath?.StartsWith(Path.Combine(workingDirectory, ".jarvis"), StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
        };
        picker.SelectedIndex = Math.Max(0, custom.FindIndex(style => style.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase)) + 1);
    }
}
