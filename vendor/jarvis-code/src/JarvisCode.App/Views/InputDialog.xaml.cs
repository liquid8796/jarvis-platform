using System.Windows;
using System.Windows.Input;

namespace JarvisCode.App.Views;

/// <summary>Small themed text prompt (rename session, new group, …).</summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string initialValue = "", string okLabel = "OK")
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        ValueBox.Text = initialValue;
        OkButton.Content = okLabel;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    /// <summary>Shows the prompt; null when cancelled or left empty.</summary>
    public static string? Prompt(Window? owner, string title, string initialValue = "", string okLabel = "OK")
    {
        var dialog = new InputDialog(title, initialValue, okLabel) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Value.Length > 0 ? dialog.Value : null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DialogResult = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
