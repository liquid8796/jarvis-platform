using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JarvisCode.App.Views;

/// <summary>
/// The Quick Entry popup: a small always-on-top pill that appears on the
/// monitor holding the cursor, grows with multi-line input, and hides on
/// Esc or focus loss.
/// </summary>
public partial class QuickEntryWindow : Window
{
    public event EventHandler<QuickEntrySubmission>? Submitted;

    public QuickEntryWindow()
    {
        InitializeComponent();
        InitializeImages();
        Deactivated += (_, _) => Hide();
    }

    public void ShowOnCursorMonitor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;

        // WorkingArea is in device pixels; Left/Top are in DIPs.
        var source = PresentationSource.FromVisual(this);
        var dpi = source?.CompositionTarget?.TransformToDevice.M11
            ?? System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0)
        {
            dpi = 1.0;
        }

        Left = area.Left / dpi + (area.Width / dpi - Width) / 2;
        Top = area.Top / dpi + area.Height / dpi * 0.28;

        Show();
        Activate();
        PromptInput.Focus();
    }

    public void HideEntry()
    {
        PromptInput.Clear();
        _images.Clear();
        RenderChips();
        Hide();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            Submit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideEntry();
        }
        else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            TryPasteImage())
        {
            // A screenshot on the clipboard attaches; text keeps the box's own paste.
            e.Handled = true;
        }
    }

    private void OnSubmitClick(object sender, RoutedEventArgs e) => Submit();

    private void Submit()
    {
        var text = PromptInput.Text.Trim();
        if (text.Length == 0 && _images.Count == 0)
        {
            return;
        }

        var submission = new QuickEntrySubmission(text, [.. _images]);
        PromptInput.Clear();
        _images.Clear();
        RenderChips();
        Hide();
        Submitted?.Invoke(this, submission);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
        => PromptPlaceholder.Visibility = PromptInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
}
