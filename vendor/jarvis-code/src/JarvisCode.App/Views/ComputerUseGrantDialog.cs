using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The card request_access raises, as the Code surface draws it — ported from
/// desktop 1.44121.2.0's <c>cd5a31703-DPCARDPv.js</c>: a title that names the one
/// app or counts them, a "Reason" line carrying what the model said it needs the
/// access for, one row per application (20px icon, name, the tier on the right,
/// and a warning line under anything that can run commands, reach every file or
/// change system settings), a row per grant flag, the sentence naming what will
/// be hidden, and Deny / Allow for this session.
///
/// The reference's answer is per app; its buttons are not, so allowing takes the
/// whole set, as the tool's own description says it does.
/// </summary>
public sealed class ComputerUseGrantDialog : Window
{
    private const double IconSize = 20;

    private ComputerUseGrantDialog(GrantRequest request)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(FontFamilyProperty, "UiFontFamily");
        PreviewKeyDown += OnKey;

        var body = new StackPanel();
        body.Children.Add(TitleBlock(ComputerUseGrantPrompt.Title(request)));

        if (request.Reason.Length > 0)
        {
            body.Children.Add(Label(ComputerUseGrantPrompt.ReasonLabel));
            body.Children.Add(Secondary(request.Reason));
        }

        var rows = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var app in request.Apps)
        {
            rows.Children.Add(AppRow(app));
        }

        foreach (var flag in ComputerUseGrantPrompt.FlagRows(request))
        {
            rows.Children.Add(FlagRow(flag));
        }

        if (rows.Children.Count > 0)
        {
            body.Children.Add(rows);
        }

        if (ComputerUseGrantPrompt.HideNotice(request) is { } hide)
        {
            body.Children.Add(Secondary(hide, top: 12));
        }

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var deny = new Button { Content = ComputerUseGrantPrompt.Deny };
        deny.SetResourceReference(StyleProperty, "SecondaryButton");
        deny.Click += (_, _) => DialogResult = false;
        footer.Children.Add(deny);

        var allow = new Button
        {
            Content = ComputerUseGrantPrompt.AllowForThisSession,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
        };
        allow.Click += (_, _) => DialogResult = true;
        footer.Children.Add(allow);
        body.Children.Add(footer);

        var card = new Border
        {
            Child = body,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 16),
            MinWidth = 380,
            MaxWidth = 480,
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
    }

    /// <summary>
    /// Raises the card on the UI thread and answers whether the set was allowed.
    /// Null where there is no window to raise it in — a headless run — so the
    /// caller can fall back to the question card the transcript can show.
    /// </summary>
    internal static Task<bool?> AskAsync(GrantRequest request, CancellationToken cancellationToken)
    {
        var application = Application.Current;
        if (application is null)
        {
            return Task.FromResult<bool?>(null);
        }

        return application.Dispatcher.InvokeAsync(() =>
        {
            var owner = application.Windows.OfType<Window>().FirstOrDefault(static w => w.IsActive)
                ?? application.MainWindow;
            if (owner is null || !owner.IsVisible)
            {
                return (bool?)null;
            }

            var dialog = new ComputerUseGrantDialog(request) { Owner = owner };
            using var registration = cancellationToken.Register(() =>
                application.Dispatcher.BeginInvoke(() =>
                {
                    if (dialog.IsVisible)
                    {
                        dialog.DialogResult = false;
                    }
                }));
            return dialog.ShowDialog() == true;
        }).Task;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    private static TextBlock TitleBlock(GrantTitle title)
    {
        var block = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        block.Inlines.Add(new Run(title.Before));
        block.Inlines.Add(new Run(title.Bold) { FontWeight = FontWeights.SemiBold });
        block.Inlines.Add(new Run(title.After));
        return block;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 12, 0, 2) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return block;
    }

    private static TextBlock Secondary(string text, double top = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        return block;
    }

    /// <summary>The reference's row: icon, name over its warning, tier on the right.</summary>
    private static UIElement AppRow(GrantRow app)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = AppIcon(app.App);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var names = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        name.Inlines.Add(new Run(app.App));
        if (!app.Installed)
        {
            var muted = new Run(" " + ComputerUseGrantPrompt.NotInstalled);
            muted.SetResourceReference(TextElement.ForegroundProperty, "Text500Brush");
            name.Inlines.Add(muted);
        }

        names.Children.Add(name);

        if (ComputerUseGrantPrompt.SentinelWarning(app.App) is { } warning)
        {
            var line = new TextBlock { Text = warning, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Warning100Brush");
            names.Children.Add(line);
        }

        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        var tier = new TextBlock
        {
            Text = app.AlreadyGranted ? AlreadyAllowed : ComputerUseGrantPrompt.TierLabel(app.Tier),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tier.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        Grid.SetColumn(tier, 2);
        grid.Children.Add(tier);

        return Tile(grid, app.Installed ? 1.0 : 0.5);
    }

    /// <summary>The reference's badge for an app this session already holds.</summary>
    internal const string AlreadyAllowed = "Already allowed";

    private static UIElement FlagRow(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var square = new Border
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        square.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");
        row.Children.Add(square);

        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        row.Children.Add(text);
        return Tile(row, 1.0);
    }

    private static Border Tile(UIElement child, double opacity)
    {
        var tile = new Border
        {
            Child = child,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Opacity = opacity,
        };
        tile.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        return tile;
    }

    /// <summary>
    /// The application's own icon, taken from the running process's executable.
    /// The reference asks the shell for one by bundle id and draws a blank square
    /// when it has none; here the blank square is what an application that is not
    /// running gets, since a process is the only handle this port has on a path.
    /// </summary>
    private static UIElement AppIcon(string app)
    {
        if (ExecutablePath(app) is { } path)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return new Image
                    {
                        Source = source,
                        Width = IconSize,
                        Height = IconSize,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
            }
            catch (Exception ex) when (ex is ArgumentException or System.IO.IOException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // No icon to read; the blank square below stands in for it.
            }
        }

        var blank = new Border
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        blank.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");
        return blank;
    }

    private static string? ExecutablePath(string app)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(ComputerUseGrants.Normalize(app)))
            {
                using (process)
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // An elevated or exited process will not say where it lives.
        }

        return null;
    }
}
