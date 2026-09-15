using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Services;
using JarvisCode.Core.Mcp;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// "Add custom connector" (ce99eb202), as the reference's two steps: the name
/// and the remote MCP server URL first, then the trust warning with the OAuth
/// client credentials behind Advanced. The transport follows the URL — a path
/// ending in /sse takes the legacy one — and the note the reference prints for
/// that case appears with it.
/// </summary>
public sealed class CustomConnectorDialog : Window
{
    private readonly IReadOnlyList<string> _existingNames;

    private readonly StackPanel _body = new() { Margin = new Thickness(22, 18, 22, 18) };
    private readonly TextBox _name;
    private readonly TextBox _url;
    private readonly TextBlock _nameError;
    private readonly TextBlock _urlError;
    private readonly TextBlock _sseNote;
    private readonly TextBlock _step;
    private readonly Button _primary;
    private readonly Button _back;
    private readonly StackPanel _stepOne = new();
    private readonly StackPanel _stepTwo = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _clientId;
    private readonly TextBox _clientSecret;

    private int _stepIndex = 1;

    /// <summary>The server the form described, once the user finished it.</summary>
    public McpServerConfig? Result { get; private set; }

    public CustomConnectorDialog(IReadOnlyList<string> existingNames)
    {
        _existingNames = existingNames;

        Title = "Add custom connector";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg100Brush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFontFamily");

        var heading = CustomizeUi.Text(Title, 17, "Text100Brush", semibold: true);
        _body.Children.Add(heading);
        _step = CustomizeUi.Text("Step 1 of 2", 11.5, "Text500Brush");
        _step.Margin = new Thickness(0, 4, 0, 6);
        _body.Children.Add(_step);

        var intro = CustomizeUi.Text(
            "Connect Jarvis to your data and tools.", 12.5, "Text400Brush", wrap: true);
        intro.Margin = new Thickness(0, 0, 0, 4);
        _body.Children.Add(intro);

        _body.Children.Add(_stepOne);
        _body.Children.Add(_stepTwo);

        _name = CustomizeUi.Field(_stepOne, "Name", "", "Shown in the connectors list.");
        _nameError = CustomizeUi.FieldError(_stepOne);
        _url = CustomizeUi.Field(
            _stepOne, "Remote MCP server URL", "",
            "The HTTPS address where the server accepts MCP requests, for example https://mcp.example.com/mcp.");
        _urlError = CustomizeUi.FieldError(_stepOne);
        _sseNote = CustomizeUi.Text(
            "This connector will use SSE transport, which is being deprecated. If you maintain this server, consider switching to streamable HTTP. You can change transport under Advanced on the next step.",
            11.5, "Warning100Brush", wrap: true);
        _sseNote.Margin = new Thickness(2, 6, 0, 0);
        _sseNote.Visibility = Visibility.Collapsed;
        _stepOne.Children.Add(_sseNote);

        var trust = CustomizeUi.Text(
            "Only use connectors from developers you trust. Anthropic does not control which tools developers make available and cannot verify that they will work as intended or that they won’t change.",
            12, "Text400Brush", wrap: true);
        trust.Margin = new Thickness(0, 6, 0, 6);
        _stepTwo.Children.Add(trust);

        var advanced = CustomizeUi.Text("Advanced settings", 12.5, "Text100Brush", semibold: true);
        advanced.Margin = new Thickness(0, 10, 0, 0);
        _stepTwo.Children.Add(advanced);
        _clientId = CustomizeUi.Field(
            _stepTwo, "OAuth Client ID (optional)", "",
            "Delete and re-add connectors to edit custom OAuth Client IDs.");
        _clientSecret = CustomizeUi.Field(
            _stepTwo, "OAuth Client Secret (optional)", "",
            "Delete and re-add connectors to edit custom OAuth Client Secrets.");

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        _back = CustomizeUi.Ghost("Back", GoBack);
        _back.Visibility = Visibility.Collapsed;
        footer.Children.Add(_back);
        var cancel = CustomizeUi.Ghost("Cancel", Close);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(cancel);
        _primary = CustomizeUi.Primary("Continue", Advance);
        _primary.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(_primary);
        _body.Children.Add(footer);

        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _name.TextChanged += (_, _) => Validate();
        _url.TextChanged += (_, _) => Validate();
        PreviewKeyDown += OnKeyDown;
        Validate();
        CustomizeUi.FocusWhenLoaded(_name);
    }

    private void Validate()
    {
        var nameError = CustomConnectorForm.NameError(_name.Text, _existingNames);
        CustomizeUi.SetError(_nameError, nameError);
        var urlError = CustomConnectorForm.UrlError(_url.Text);
        CustomizeUi.SetError(_urlError, urlError);
        _sseNote.Visibility =
            urlError is null && CustomConnectorForm.TransportFor(_url.Text) == CustomConnectorForm.Sse
                ? Visibility.Visible
                : Visibility.Collapsed;
        _primary.IsEnabled = _stepIndex == 2 ||
            CustomConnectorForm.CanContinue(_name.Text, _url.Text, _existingNames);
    }

    private void Advance()
    {
        if (_stepIndex == 1)
        {
            _stepIndex = 2;
            _step.Text = "Step 2 of 2";
            _stepOne.Visibility = Visibility.Collapsed;
            _stepTwo.Visibility = Visibility.Visible;
            _back.Visibility = Visibility.Visible;
            _primary.Content = "Add";
            Validate();
            return;
        }

        Result = CustomConnectorForm.ToConfig(
            _name.Text, _url.Text, CustomConnectorForm.TransportFor(_url.Text),
            _clientId.Text, _clientSecret.Text);
        DialogResult = true;
    }

    private void GoBack()
    {
        _stepIndex = 1;
        _step.Text = "Step 1 of 2";
        _stepOne.Visibility = Visibility.Visible;
        _stepTwo.Visibility = Visibility.Collapsed;
        _back.Visibility = Visibility.Collapsed;
        _primary.Content = "Continue";
        Validate();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && _primary.IsEnabled)
        {
            e.Handled = true;
            Advance();
        }
    }
}
