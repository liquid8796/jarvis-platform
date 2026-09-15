using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Services;
using JarvisCode.Core.Mcp;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// Signing in to a remote connector without leaving the app (c752b32f8): the
/// browser is opened on the authorization URL, the loopback listener waits, and
/// when the browser does not come back the reference's paste-the-callback-URL
/// fallback takes over. Every sentence here is the reference's own.
/// </summary>
public sealed class ConnectorSignInDialog : Window
{
    private readonly McpServerConfig _config;
    private readonly HttpClient _http;
    private readonly McpTokenStore _tokens;
    private readonly CancellationTokenSource _cancel = new();

    private readonly TextBlock _headline;
    private readonly TextBlock _status;
    private readonly TextBlock _error;
    private readonly StackPanel _pastePanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _callback;
    private readonly Button _done;
    private readonly Button _retry;

    private McpOAuthFlow? _flow;
    private ConnectorSignInPhase _phase = ConnectorSignInPhase.Starting;

    public ConnectorSignInDialog(McpServerConfig config, HttpClient http, McpTokenStore tokens)
    {
        _config = config;
        _http = http;
        _tokens = tokens;

        Title = $"Connect {config.Name}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg100Brush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFontFamily");

        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        _headline = CustomizeUi.Text(ConnectorSignIn.WaitingHeadline(config.Name), 15, "Text100Brush", wrap: true);
        body.Children.Add(_headline);

        _status = CustomizeUi.Text(ConnectorSignIn.Waiting, 12.5, "Text400Brush", wrap: true);
        _status.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(_status);

        _error = CustomizeUi.Text("", 12.5, "Danger100Brush", wrap: true);
        _error.Margin = new Thickness(0, 10, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        body.Children.Add(_error);

        var pasteLabel = CustomizeUi.Text(ConnectorSignIn.PasteCallback, 12.5, "Text400Brush", wrap: true);
        pasteLabel.Margin = new Thickness(0, 12, 0, 4);
        _pastePanel.Children.Add(pasteLabel);
        _callback = new TextBox();
        _callback.SetResourceReference(StyleProperty, "InputTextBox");
        _callback.Height = CustomizeUi.ControlHeight;
        System.Windows.Automation.AutomationProperties.SetName(_callback, ConnectorSignIn.CallbackLabel(config.Name));
        _pastePanel.Children.Add(_callback);
        var placeholder = CustomizeUi.Text(ConnectorSignIn.CallbackPlaceholder, 11, "Text500Brush");
        placeholder.Margin = new Thickness(2, 4, 0, 0);
        _pastePanel.Children.Add(placeholder);
        body.Children.Add(_pastePanel);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        _retry = CustomizeUi.Ghost("Start over", StartOver);
        _retry.Visibility = Visibility.Collapsed;
        footer.Children.Add(_retry);
        var cancel = CustomizeUi.Ghost("Cancel", Close);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(cancel);
        _done = CustomizeUi.Primary("Done", Finish);
        _done.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(_done);
        body.Children.Add(footer);

        Content = body;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) =>
        {
            _cancel.Cancel();
            _ = _flow?.DisposeAsync();
        };
        Loaded += (_, _) => _ = StartAsync();
    }

    private void SetPhase(ConnectorSignInPhase phase, string? status = null)
    {
        _phase = phase;
        if (status is not null)
            _status.Text = status;
        _pastePanel.Visibility = phase == ConnectorSignInPhase.PasteCallback ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = phase is ConnectorSignInPhase.PasteCallback or ConnectorSignInPhase.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
        _done.IsEnabled = phase is not (ConnectorSignInPhase.Starting or ConnectorSignInPhase.Reconnecting);
    }

    private void Fail(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        SetPhase(ConnectorSignInPhase.Failed, "Failed to connect. Click to retry.");
    }

    private async Task StartAsync()
    {
        _error.Visibility = Visibility.Collapsed;
        SetPhase(ConnectorSignInPhase.Starting, ConnectorSignIn.Waiting);
        try
        {
            _flow = await McpOAuthFlow.StartAsync(_config, _http, _tokens, _cancel.Token);
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or TaskCanceledException)
        {
            Fail(ConnectorSignIn.StartFailed(_config.Name, ex.Message));
            return;
        }

        if (!ConnectorSignIn.IsOpenable(_flow.AuthorizeUrl))
        {
            Fail(ConnectorSignIn.RefusedNonHttps(_config.Name));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_flow.AuthorizeUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            Fail(ConnectorSignIn.StartFailed(_config.Name, ex.Message));
            return;
        }

        SetPhase(ConnectorSignInPhase.WaitingForBrowser, ConnectorSignIn.Waiting);
        var callback = await _flow.WaitForCallbackAsync(ConnectorSignIn.BrowserWait, _cancel.Token);
        if (_cancel.IsCancellationRequested)
            return;
        if (callback is null)
        {
            _headline.Text = ConnectorSignIn.BrowserDidNotReturn(_config.Name);
            SetPhase(ConnectorSignInPhase.PasteCallback, ConnectorSignIn.PasteCallback);
            return;
        }
        await CompleteAsync(callback);
    }

    /// <summary>Done means "the browser came back" or "here is the URL I pasted".</summary>
    private void Finish()
    {
        if (_phase == ConnectorSignInPhase.Connected)
        {
            DialogResult = true;
            return;
        }
        if (_phase == ConnectorSignInPhase.PasteCallback)
        {
            _ = CompleteAsync(_callback.Text);
            return;
        }
        if (_phase == ConnectorSignInPhase.Failed)
        {
            StartOver();
            return;
        }
        // The listener is still waiting; a browser that already finished lands there.
        _status.Text = ConnectorSignIn.Waiting;
    }

    private async Task CompleteAsync(string callbackUrl)
    {
        if (_flow is null)
            return;
        _error.Visibility = Visibility.Collapsed;
        SetPhase(ConnectorSignInPhase.Reconnecting, ConnectorSignIn.Reconnecting);
        try
        {
            await _flow.CompleteAsync(callbackUrl, _cancel.Token);
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or TaskCanceledException)
        {
            Fail(ConnectorSignIn.CompleteFailed(_config.Name, ex.Message));
            return;
        }
        SetPhase(ConnectorSignInPhase.Connected, ConnectorSignIn.Connected(_config.Name));
        DialogResult = true;
    }

    private void StartOver()
    {
        _ = _flow?.DisposeAsync();
        _flow = null;
        _headline.Text = ConnectorSignIn.WaitingHeadline(_config.Name);
        _callback.Text = "";
        _ = StartAsync();
    }
}
