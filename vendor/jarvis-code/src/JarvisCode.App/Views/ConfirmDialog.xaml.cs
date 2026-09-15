using System.Windows;
using System.Windows.Input;

namespace JarvisCode.App.Views;

/// <summary>
/// The reference's alert dialog: a title, a description, an optional children
/// slot (a path, a scrolling list of files) and a footnote, over Cancel and a
/// confirm button that is danger-styled unless the caller says otherwise.
/// A third, middle button is offered for the one dialog that has three answers.
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>Which button the user pressed. <see cref="Middle"/> is the third answer.</summary>
    public enum Answer
    {
        Cancel,
        Confirm,
        Middle,
    }

    public Answer Result { get; private set; } = Answer.Cancel;

    public ConfirmDialog(
        string title,
        string message,
        string confirmLabel,
        string? subject = null,
        string? footnote = null,
        bool focusCancel = false,
        bool danger = true,
        string? preformatted = null,
        string? middleLabel = null,
        string cancelLabel = Services.SessionDialogs.Cancel)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        CancelButton.Content = cancelLabel;
        Title = title;

        if (!danger)
        {
            // The reference's confirmVariant: "primary" while a dialog is still
            // finding out whether the action is destructive at all.
            ConfirmButton.SetResourceReference(StyleProperty, "PrimaryButton");
        }

        if (!string.IsNullOrEmpty(subject))
        {
            SubjectText.Text = subject;
            SubjectText.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrEmpty(preformatted))
        {
            PreformattedText.Text = preformatted;
            PreformattedBox.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrEmpty(footnote))
        {
            FootnoteText.Text = footnote;
            FootnoteText.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrEmpty(middleLabel))
        {
            MiddleButton.Content = middleLabel;
            MiddleButton.Visibility = Visibility.Visible;
        }

        // The confirm button carries focus so Enter commits and Escape cancels, the
        // way the reference app presents the same dialog — except where the reference
        // asks for initialFocus "cancel", which is what it does before a mode that
        // takes permissions away and before anything it is about to discard.
        Loaded += (_, _) =>
        {
            if (focusCancel)
            {
                CancelButton.Focus();
            }
            else
            {
                ConfirmButton.Focus();
            }
        };
    }

    /// <summary>Shows the dialog; true only when the user confirmed.</summary>
    public static bool Ask(
        Window? owner,
        string title,
        string message,
        string confirmLabel,
        string? subject = null,
        string? footnote = null,
        bool focusCancel = false,
        bool danger = true,
        string? preformatted = null,
        string? cancelLabel = null)
    {
        var dialog = new ConfirmDialog(
            title, message, confirmLabel, subject, footnote, focusCancel, danger, preformatted,
            cancelLabel: cancelLabel ?? Services.SessionDialogs.Cancel)
        {
            Owner = owner,
        };
        return dialog.ShowDialog() == true;
    }

    /// <summary>Shows one of the reference's confirm dialogs, described by its copy.</summary>
    public static bool Ask(Window? owner, Services.DialogCopy copy, bool focusCancel = true) =>
        Ask(owner, copy.Title, copy.Description, copy.ConfirmLabel,
            footnote: copy.Footnote, focusCancel: focusCancel, danger: copy.Danger,
            preformatted: copy.Preformatted);

    /// <summary>Shows a three-answer dialog and reports which button was pressed.</summary>
    public static Answer Ask3(
        Window? owner,
        string title,
        string message,
        string confirmLabel,
        string middleLabel,
        string cancelLabel)
    {
        var dialog = new ConfirmDialog(
            title, message, confirmLabel, middleLabel: middleLabel, cancelLabel: cancelLabel, focusCancel: true)
        {
            Owner = owner,
        };
        dialog.ShowDialog();
        return dialog.Result;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Result = Answer.Confirm;
        DialogResult = true;
    }

    private void OnMiddleClick(object sender, RoutedEventArgs e)
    {
        Result = Answer.Middle;
        DialogResult = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = Answer.Cancel;
        DialogResult = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Result = Answer.Cancel;
            DialogResult = false;
        }
        else if (e.Key == Key.Enter)
        {
            // Enter commits whichever button holds focus, so tabbing to Cancel and
            // pressing it backs out instead of running the destructive action.
            e.Handled = true;
            if (MiddleButton.IsKeyboardFocused)
            {
                Result = Answer.Middle;
                DialogResult = false;
                return;
            }

            var confirmed = !CancelButton.IsKeyboardFocused;
            Result = confirmed ? Answer.Confirm : Answer.Cancel;
            DialogResult = confirmed;
        }
    }
}
