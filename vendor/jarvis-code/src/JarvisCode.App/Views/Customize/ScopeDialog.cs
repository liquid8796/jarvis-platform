using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// The reference's install-scope split button, asked as a dialog: install for
/// me, for the project and shared through git, or for the project and kept out
/// of it. Each row carries the folder this app really writes to, which is the
/// whole point of the line under the label.
/// </summary>
public sealed class ScopeDialog : Window
{
    private InstallScope? _picked;

    private ScopeDialog(string what, string userDirectory)
    {
        Title = $"Install {what}";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg100Brush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFontFamily");

        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        var heading = CustomizeUi.Text(Title, 16, "Text100Brush", semibold: true);
        heading.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(heading);

        foreach (var scope in InstallScopes.All)
        {
            var text = new StackPanel();
            text.Children.Add(CustomizeUi.Text(InstallScopes.Label(scope), 13.5, "Text100Brush", semibold: true));
            var description = CustomizeUi.Text(
                InstallScopes.Description(scope, userDirectory), 12, "Text400Brush", wrap: true);
            description.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(description);

            var button = new Button
            {
                Content = text,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
            };
            button.SetResourceReference(StyleProperty, "RowButton");
            System.Windows.Automation.AutomationProperties.SetName(button, InstallScopes.Label(scope));
            var captured = scope;
            button.Click += (_, _) =>
            {
                _picked = captured;
                DialogResult = true;
            };
            body.Children.Add(button);
        }

        var cancel = CustomizeUi.Ghost("Cancel", Close);
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(cancel);

        Content = body;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>Asks where to install; null when the user backed out.</summary>
    public static InstallScope? Ask(Window? owner, string what, string userDirectory)
    {
        var dialog = new ScopeDialog(what, userDirectory) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._picked : null;
    }
}
