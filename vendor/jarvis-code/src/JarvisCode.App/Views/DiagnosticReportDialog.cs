using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The reference's diagnostic-report modal (desktop 1.40609.1.0, ion-dist
/// <c>cd53438bb-zMSmatZM.js</c>): a packaging state with a spinner and a step
/// label, a ready state listing what the report contains over the scrub note,
/// a "Preview contents" disclosure carrying the bundle's size and its first
/// lines, and Export to file; then the exported state with Show in Explorer and
/// Done. Its titles are the reference's four, of which three are reachable here.
///
/// The reference's Send to Anthropic half is not built: there is no endpoint to
/// send to, and its own <c>showSendUi</c> already renders exactly this shape
/// when there is none — no Reference ID row, no Send button.
/// </summary>
public sealed class DiagnosticReportDialog : Window
{
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold };
    private readonly SpinnerGlyph _spinner = new() { Width = 20, Height = 20, IsSpinning = true };
    private readonly TextBlock _step = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _body = new();
    private readonly StackPanel _footer = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 14, 0, 0),
    };

    private readonly Border _packaging;
    private readonly DiagnosticContext _context;
    private readonly HttpClient _http;

    private DiagnosticBundle? _bundle;
    private string? _savedPath;
    private bool _busy = true;

    private DiagnosticReportDialog(DiagnosticContext context, HttpClient http)
    {
        _context = context;
        _http = http;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Title = DiagnosticReportText.MenuLabel;
        SetResourceReference(FontFamilyProperty, "UiFontFamily");

        _title.Text = DiagnosticReportText.MenuLabel;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        _step.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");

        var collecting = new TextBlock
        {
            Text = DiagnosticReportText.Collecting,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
        };
        collecting.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        _step.Text = DiagnosticReportText.CollectingHint;

        var labels = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        labels.Children.Add(collecting);
        labels.Children.Add(_step);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_spinner);
        row.Children.Add(labels);
        _packaging = new Border { Child = row, Margin = new Thickness(0, 4, 0, 4) };

        _body.Children.Add(_packaging);

        var content = new StackPanel();
        content.Children.Add(_title);
        content.Children.Add(new Border { Height = 12 });
        content.Children.Add(_body);
        content.Children.Add(_footer);

        var card = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 16),
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

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy)
            {
                e.Handled = true;
                Close();
            }
        };

        Loaded += async (_, _) => await CollectAsync();
    }

    /// <summary>Opens the modal over its owner and packages the report.</summary>
    public static void Show(Window? owner, DiagnosticContext context, HttpClient http)
    {
        var dialog = new DiagnosticReportDialog(context, http) { Owner = owner };
        dialog.ShowDialog();
    }

    /// <summary>
    /// The same window without blocking, for <c>--open=diagnostics</c>: a
    /// screenshot run has to keep driving the dispatcher while the report is
    /// collected.
    /// </summary>
    public static Window Pose(Window? owner, DiagnosticContext context, HttpClient http)
    {
        var dialog = new DiagnosticReportDialog(context, http) { Owner = owner };
        dialog.Show();
        return dialog;
    }

    private async Task CollectAsync()
    {
        try
        {
            _bundle = await DiagnosticReport.BuildAsync(
                _context,
                _http,
                step => Dispatcher.Invoke(() => _step.Text = step));
            ShowReady(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            ShowReady(ex.Message);
        }
    }

    private void ShowReady(string? error)
    {
        _busy = false;
        _title.Text = DiagnosticReportText.Title;
        _body.Children.Clear();
        _footer.Children.Clear();

        _body.Children.Add(Heading(DiagnosticReportText.Contains));
        foreach (var line in DiagnosticReportText.ContentRows)
        {
            _body.Children.Add(Bullet(line));
        }

        _body.Children.Add(Note(DiagnosticReportText.ScrubNote));
        if (error is { Length: > 0 })
        {
            _body.Children.Add(Danger(error));
        }

        if (_bundle is { } bundle)
        {
            _body.Children.Add(Preview(bundle));
        }

        var export = new Button
        {
            Content = DiagnosticReportText.ExportToFile,
            IsEnabled = _bundle is not null,
        };
        export.SetResourceReference(StyleProperty, "SecondaryButton");
        export.Click += (_, _) => Export();
        _footer.Children.Add(export);
    }

    private void ShowSaved(string path)
    {
        _savedPath = path;
        _title.Text = DiagnosticReportText.ExportedTitle;
        _body.Children.Clear();
        _footer.Children.Clear();

        var saved = new TextBlock
        {
            Text = DiagnosticReportText.SavedTo(path),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        saved.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        _body.Children.Add(saved);

        var reveal = new Button { Content = DiagnosticReportText.ShowInExplorer };
        reveal.SetResourceReference(StyleProperty, "SecondaryButton");
        reveal.Click += (_, _) => Reveal();
        _footer.Children.Add(reveal);

        var done = new Button { Content = DiagnosticReportText.Done, Margin = new Thickness(8, 0, 0, 0) };
        done.SetResourceReference(StyleProperty, "PrimaryButton");
        done.Click += (_, _) => Close();
        _footer.Children.Add(done);
    }

    private void Export()
    {
        if (_bundle is not { } bundle)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = DiagnosticReport.FileName(bundle.BundleId, DateTimeOffset.Now),
            Filter = "Zip archive|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, bundle.Zip);
            ShowSaved(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowReady(ex.Message);
        }
    }

    private void Reveal()
    {
        if (_savedPath is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_savedPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The report is already written; failing to front Explorer is not worth a second dialog.
        }
    }

    private static TextBlock Heading(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 6),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        return block;
    }

    private static UIElement Bullet(string text)
    {
        var dot = new Border
        {
            Width = 3,
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(2, 8, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        dot.SetResourceReference(Border.BackgroundProperty, "Text300Brush");

        var label = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(dot);
        row.Children.Add(label);
        return row;
    }

    private static UIElement Note(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        var box = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 10, 0, 0),
        };
        box.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        return box;
    }

    private static UIElement Danger(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Danger100Brush");
        return new Border { Child = label, Margin = new Thickness(0, 10, 0, 0) };
    }

    private UIElement Preview(DiagnosticBundle bundle)
    {
        var caret = new TextBlock { Text = "›", FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
        caret.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");

        var label = new TextBlock
        {
            Text = DiagnosticReportText.PreviewContents,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        var size = new TextBlock
        {
            Text = DiagnosticReportText.FormatBytes(bundle.SizeBytes),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        size.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        size.SetResourceReference(TextBlock.FontFamilyProperty, "CodeFontFamily");

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(caret);
        left.Children.Add(label);

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 0) };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(size, Dock.Right);
        header.Children.Add(left);
        header.Children.Add(size);

        var toggle = new Button
        {
            Content = header,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, DiagnosticReportText.PreviewContents);

        var lines = new TextBlock
        {
            Text = string.Join('\n', bundle.PreviewLines),
            FontSize = 10,
            TextWrapping = TextWrapping.NoWrap,
        };
        lines.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        lines.SetResourceReference(TextBlock.FontFamilyProperty, "CodeFontFamily");

        var scroller = new ScrollViewer
        {
            Content = lines,
            MaxHeight = 168,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 10, 12, 10),
        };

        var box = new Border
        {
            Child = scroller,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        box.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");

        toggle.Click += (_, _) =>
        {
            var open = box.Visibility != Visibility.Visible;
            box.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            caret.Text = open ? "⌄" : "›";
            System.Windows.Automation.AutomationProperties.SetHelpText(toggle, open ? "expanded" : "collapsed");
        };

        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(toggle);
        panel.Children.Add(box);
        return panel;
    }
}
