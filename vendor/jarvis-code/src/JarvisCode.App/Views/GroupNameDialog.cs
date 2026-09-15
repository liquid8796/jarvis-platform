using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// Naming a session group, in the reference's own shape (<c>shared-14-CYxe7_Hl.js</c>):
/// a header that reads "Rename group" or "New group", a single field whose
/// placeholder and accessible name are both "Group name", and Cancel over a
/// Save that stays disabled while the field is blank.
/// </summary>
public sealed class GroupNameDialog : Window
{
    private readonly TextBox _field;
    private readonly Button _save;

    private GroupNameDialog(string title, string initialName)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Title = title;
        SetResourceReference(FontFamilyProperty, "UiFontFamily");

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        _field = new TextBox { Text = initialName };
        _field.SetResourceReference(StyleProperty, "InputTextBox");
        _field.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            SessionDialogs.GroupNamePlaceholder);
        _field.SetValue(System.Windows.Controls.TextBox.TagProperty, SessionDialogs.GroupNamePlaceholder);
        _field.TextChanged += (_, _) => _save.IsEnabled = _field.Text.Trim().Length > 0;
        _field.PreviewKeyDown += OnFieldKey;

        var cancel = new Button { Content = SessionDialogs.Cancel };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        cancel.Click += (_, _) => DialogResult = false;

        _save = new Button
        {
            Content = SessionDialogs.Save,
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = initialName.Trim().Length > 0,
        };
        _save.SetResourceReference(StyleProperty, "PrimaryButton");
        _save.Click += (_, _) => Commit();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);

        var body = new StackPanel();
        body.Children.Add(heading);
        body.Children.Add(_field);
        body.Children.Add(buttons);

        var card = new Border
        {
            Child = body,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 18, 14),
            MinWidth = 340,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 4,
                Opacity = 0.4,
                Color = Colors.Black,
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        Content = card;

        Loaded += (_, _) =>
        {
            _field.Focus();
            _field.SelectAll();
        };
    }

    private string Value => _field.Text.Trim();

    /// <summary>Asks for a group name; null when cancelled or left blank.</summary>
    public static string? Prompt(Window? owner, string title, string initialName = "")
    {
        var dialog = new GroupNameDialog(title, initialName) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Value.Length > 0 ? dialog.Value : null;
    }

    private void Commit()
    {
        if (Value.Length > 0)
        {
            DialogResult = true;
        }
    }

    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }
}
