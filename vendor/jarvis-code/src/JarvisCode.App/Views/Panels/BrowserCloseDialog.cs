using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>What the user chose in the Browser pane's close dialog.</summary>
public enum BrowserCloseChoice
{
    Cancel,
    StopServers,
    KeepRunning,
}

/// <summary>
/// The Browser pane's "Close {host}?" dialog, ported from the reference's ES (ion-dist
/// chunk c360a9e1c-DUoNQd2W.js): a title, one of three bodies, and a footer split the
/// reference's way — Cancel on the left, and on the right the danger "Stop server(s)"
/// beside the primary "Keep running", which takes the initial focus.
/// </summary>
public sealed class BrowserCloseDialog : Window
{
    private BrowserCloseDialog(BrowserCloseMode mode, int serverCount, string serverLabel, string host)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(FontFamilyProperty, "UiFontFamily");

        var stack = new StackPanel();
        var title = new TextBlock
        {
            Text = BrowserCloseDialogText.Title(mode, host),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        stack.Children.Add(title);

        var body = new TextBlock
        {
            Text = BrowserCloseDialogText.Body(mode, serverCount, serverLabel, host),
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        stack.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancel = new Button { Content = BrowserCloseDialogText.Cancel, HorizontalAlignment = HorizontalAlignment.Left };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        cancel.Click += (_, _) => Choose(BrowserCloseChoice.Cancel);
        footer.Children.Add(cancel);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var stop = new Button { Content = BrowserCloseDialogText.StopLabel(serverCount) };
        stop.SetResourceReference(StyleProperty, "DangerButton");
        stop.SetValue(AutomationProperties.NameProperty, BrowserCloseDialogText.StopAccessibleName(serverCount));
        stop.Click += (_, _) => Choose(BrowserCloseChoice.StopServers);
        right.Children.Add(stop);

        var keep = new Button { Content = BrowserCloseDialogText.KeepRunning, Margin = new Thickness(8, 0, 0, 0) };
        keep.SetResourceReference(StyleProperty, "PrimaryButton");
        keep.Click += (_, _) => Choose(BrowserCloseChoice.KeepRunning);
        right.Children.Add(keep);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        stack.Children.Add(footer);

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 16, 18, 16),
            MinWidth = 340,
            MaxWidth = 448,
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        Content = card;

        // The reference gives the dialog its initial focus and answers Escape as a cancel.
        Loaded += (_, _) => keep.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Choose(BrowserCloseChoice.Cancel);
            }
        };
    }

    /// <summary>What the user picked.</summary>
    public BrowserCloseChoice Choice { get; private set; } = BrowserCloseChoice.Cancel;

    private void Choose(BrowserCloseChoice choice)
    {
        Choice = choice;
        DialogResult = choice != BrowserCloseChoice.Cancel;
    }

    /// <summary>Raises the dialog and reports the answer.</summary>
    public static BrowserCloseChoice Ask(
        Window? owner, BrowserCloseMode mode, int serverCount, string serverLabel, string host)
    {
        var dialog = new BrowserCloseDialog(mode, serverCount, serverLabel, host) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }
}
