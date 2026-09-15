using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// The reference's "Allowed sites" modal (ion-dist <c>c25b1be4a-DyxWGdpX.js</c>,
/// <c>PreviewAllowedSitesModal</c>): an Add URL box with its six refusals, one
/// chip-row per origin with a Remove button, the empty line, and a footer of
/// Clear all (danger) and Done. Origins are stored in
/// <see cref="UiSettings.BrowserAllowedOrigins"/> and read by the Browser tools'
/// per-site gate.
/// </summary>
public sealed class AllowedSitesDialog : Window
{
    public const string Title_ = "Allowed sites";
    public const string Description =
        "Jarvis can use its Browser tools on these sites without a permission prompt. Remove a site to be asked again. Only Manual and Accept edits modes, and financial sites, prompt per site.";
    public const string AddUrlLabel = "Add URL";
    public const string AddUrlPlaceholder = "example.com";
    public const string AddLabel = "Add";
    public const string EmptyLine = "No allowed sites yet.";
    public const string ClearAllLabel = "Clear all";
    public const string DoneLabel = "Done";
    public const string InvalidMessage = "Enter a valid web address.";
    public const string LocalhostMessage = "Localhost is always allowed.";
    public const string DuplicateMessage = "That site is already allowed.";

    private readonly UiSettingsStore _store;
    private readonly StackPanel _list = new();
    private readonly TextBlock _error;
    private readonly TextBox _input;

    public AllowedSitesDialog(UiSettingsStore store)
    {
        _store = store;
        Title = Title_;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg100Brush");
        SetResourceReference(ForegroundProperty, "Text100Brush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFontFamily"];

        var root = new StackPanel { Margin = new Thickness(16) };
        var heading = new TextBlock { Text = Title_, FontSize = 16, FontWeight = FontWeights.SemiBold };
        root.Children.Add(heading);
        var description = SettingsRows.Muted(Description);
        description.Margin = new Thickness(0, 4, 0, 16);
        root.Children.Add(description);

        var addLabel = new TextBlock { Text = AddUrlLabel, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        addLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        root.Children.Add(addLabel);

        var addRow = new Grid();
        addRow.ColumnDefinitions.Add(new ColumnDefinition());
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input = SettingsRows.Input("", width: double.NaN, placeholder: AddUrlPlaceholder, accessibleName: AddUrlLabel);
        _input.HorizontalAlignment = HorizontalAlignment.Stretch;
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Add();
                e.Handled = true;
            }
        };
        addRow.Children.Add(_input);
        var add = SettingsRows.SecondaryButton(AddLabel, Add);
        add.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(add, 1);
        addRow.Children.Add(add);
        root.Children.Add(addRow);

        _error = SettingsRows.Danger("");
        _error.FontSize = 12;
        _error.Margin = new Thickness(0, 4, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        root.Children.Add(_error);

        var scroller = new ScrollViewer
        {
            Content = _list,
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 16, 0, 0),
        };
        root.Children.Add(scroller);

        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var clear = SettingsRows.SecondaryButton(ClearAllLabel, () =>
        {
            _store.Current.BrowserAllowedOrigins.Clear();
            _store.Save();
            Rebuild();
        });
        clear.SetResourceReference(ForegroundProperty, "Danger100Brush");
        footer.Children.Add(clear);
        var done = SettingsRows.PrimaryButton(DoneLabel, Close);
        Grid.SetColumn(done, 1);
        footer.Children.Add(done);
        root.Children.Add(footer);

        Content = root;
        Rebuild();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>
    /// The reference's add rules in its order: a usable address, never localhost
    /// (always allowed anyway), never one already listed. Returns the refusal, or
    /// null with the origin to store.
    /// </summary>
    public static (string? Error, string? Origin) Validate(string text, IEnumerable<string> existing)
    {
        var origin = BrowserOriginGate.OriginOf(text.Trim());
        if (origin is null)
        {
            return (InvalidMessage, null);
        }

        var host = new Uri(origin).Host;
        if (host is "localhost" or "127.0.0.1" or "[::1]" || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return (LocalhostMessage, null);
        }

        if (existing.Any(e => string.Equals(e, origin, StringComparison.OrdinalIgnoreCase)))
        {
            return (DuplicateMessage, null);
        }

        return (null, origin);
    }

    private void Add()
    {
        var (error, origin) = Validate(_input.Text, _store.Current.BrowserAllowedOrigins);
        if (error is not null)
        {
            _error.Text = error;
            _error.Visibility = Visibility.Visible;
            return;
        }

        _error.Visibility = Visibility.Collapsed;
        _store.Current.BrowserAllowedOrigins.Add(origin!);
        _store.Save();
        _input.Clear();
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var origins = _store.Current.BrowserAllowedOrigins;
        if (origins.Count == 0)
        {
            var empty = SettingsRows.Muted(EmptyLine);
            empty.FontSize = 12;
            _list.Children.Add(empty);
            return;
        }

        foreach (var origin in origins.ToList())
        {
            var row = new Grid { Height = 28, Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var host = new TextBlock
            {
                Text = new Uri(origin).Authority,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            host.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
            row.Children.Add(host);
            var remove = new Button { Content = "", Width = 24, Height = 24, ToolTip = $"Remove {host.Text}" };
            remove.SetResourceReference(StyleProperty, "IconButton");
            remove.FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFontFamily"];
            remove.FontSize = 10;
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove {host.Text}");
            var captured = origin;
            remove.Click += (_, _) =>
            {
                _store.Current.BrowserAllowedOrigins.Remove(captured);
                _store.Save();
                Rebuild();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            var chip = new Border
            {
                Child = row,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(0, 0, 2, 0),
            };
            chip.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
            _list.Children.Add(chip);
        }
    }
}
