using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JarvisCode.App.Views;

/// <summary>Which of the reference's four dialog types a message box is.</summary>
public enum MessageDialogType
{
    None,
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>
/// The shape the reference's main process raises every one of its dialogs in
/// (Electron's <c>dialog.showMessageBox</c>): a message, an optional detail, a list
/// of buttons in the reference's own order, the index that Enter commits and the
/// index that Escape answers with. It returns that index, so a port reads exactly
/// like the reference's <c>response === 1</c> checks.
///
/// WPF's own <c>MessageBox</c> cannot do this: it has three fixed button sets, no
/// detail line and no theme, so every dialog this app took from the reference would
/// come out with different words in a different order.
/// </summary>
public partial class MessageDialog : Window
{
    private readonly int _cancelId;
    private int _response;

    /// <summary>
    /// How long a delayed confirm stays disabled. The reference's external-link
    /// dialog uses it so a link cannot be confirmed by a click the user had
    /// already begun before the question appeared.
    /// </summary>
    public const int ActivationDelayMs = 400;

    private MessageDialog(
        string message,
        string? detail,
        IReadOnlyList<string> buttons,
        int defaultId,
        int cancelId,
        MessageDialogType type,
        int delayedButtonId)
    {
        InitializeComponent();
        MessageText.Text = message;
        Title = message;
        _cancelId = cancelId;
        _response = cancelId;

        if (!string.IsNullOrEmpty(detail))
        {
            DetailText.Text = detail;
            DetailText.Visibility = Visibility.Visible;
        }

        if (Glyph(type) is { } glyph)
        {
            TypeGlyph.Text = glyph;
            TypeGlyph.SetResourceReference(
                ForegroundProperty,
                type == MessageDialogType.Error ? "Danger100Brush" : "Text300Brush");
            TypeGlyph.Visibility = Visibility.Visible;
        }

        Button? defaultButton = null;
        Button? delayedButton = null;
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i],
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
            };
            button.SetResourceReference(
                StyleProperty, i == defaultId ? "PrimaryButton" : "SecondaryButton");
            button.Click += (_, _) =>
            {
                _response = index;
                DialogResult = true;
            };
            Buttons.Children.Add(button);
            if (i == defaultId)
            {
                defaultButton = button;
            }

            if (i == delayedButtonId)
            {
                delayedButton = button;
                button.IsEnabled = false;
            }
        }

        Loaded += (_, _) =>
        {
            defaultButton?.Focus();
            if (delayedButton is null)
            {
                return;
            }

            // A one-shot timer rather than an await: the dialog may be closed
            // before it fires, and a stopped timer leaves nothing running.
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ActivationDelayMs),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                delayedButton.IsEnabled = true;
            };
            timer.Start();
            Closed += (_, _) => timer.Stop();
        };
    }

    /// <summary>
    /// Shows the dialog and returns the index of the button pressed —
    /// <paramref name="cancelId"/> when it was dismissed without one.
    /// </summary>
    public static int Show(
        Window? owner,
        string message,
        string? detail = null,
        IReadOnlyList<string>? buttons = null,
        int defaultId = 0,
        int cancelId = -1,
        MessageDialogType type = MessageDialogType.None,
        int delayedButtonId = -1)
    {
        buttons ??= [OkLabel];
        // Electron treats an unset cancelId as "the Cancel button if there is one,
        // else 0"; every caller here names one or has a single button.
        var cancel = cancelId >= 0 ? cancelId : buttons.Count - 1;
        var dialog = new MessageDialog(
            message, detail, buttons, defaultId, cancel, type, delayedButtonId)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };
        if (dialog.Owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog._response;
    }

    /// <summary>The reference's own OK button text.</summary>
    public const string OkLabel = "OK";

    /// <summary>Segoe MDL2 Assets, the icon font the rest of the chrome uses.</summary>
    private static string? Glyph(MessageDialogType type) => type switch
    {
        MessageDialogType.Info => "\uE946",
        MessageDialogType.Warning => "\uE7BA",
        MessageDialogType.Error => "\uEA39",
        MessageDialogType.Question => "\uE897",
        _ => null,
    };

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _response = _cancelId;
            DialogResult = true;
        }
    }
}
