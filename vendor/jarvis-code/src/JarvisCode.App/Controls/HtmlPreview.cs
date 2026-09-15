using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// Renders a self-contained HTML fragment (an AskUserQuestion option preview)
/// in the app's engine. The fragment is wrapped in a minimal skeleton; when the
/// engine cannot be started the control degrades to showing the fragment as
/// text, which is what keeps a preview from ever taking a turn down.
/// </summary>
public sealed class HtmlPreview : ContentControl
{
    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html), typeof(string), typeof(HtmlPreview),
        new PropertyMetadata(null, static (d, _) => ((HtmlPreview)d).Render()));

    private ElectronPaneSession? _session;
    private ElectronPaneView? _view;
    private string? _tabId;
    private bool _initFailed;
    private bool _initStarted;
    private string? _pendingHtml;

    public HtmlPreview()
    {
        Focusable = false;
        Unloaded += (_, _) => Release();
    }

    public string? Html
    {
        get => (string?)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    private async void Render()
    {
        var html = Html;
        if (string.IsNullOrWhiteSpace(html))
        {
            if (_session is not null && _tabId is not null)
            {
                await NavigateAsync("");
            }

            return;
        }

        if (_initFailed)
        {
            Content = FallbackText(html);
            return;
        }

        if (_session is null)
        {
            _pendingHtml = html;
            if (_initStarted)
            {
                return; // the first caller finishes initialization and renders _pendingHtml
            }

            _initStarted = true;
            var view = new ElectronPaneView();

            // The element has to be in the visual tree before its window is
            // reparented into it, so the host container exists to reparent into.
            Content = view;

            try
            {
                var session = ElectronEngine.CreateSession(Dispatcher);
                var window = await session.EnsureHostWindowAsync(persistSessions: false);
                view.Attach(window);
                await session.ShowAsync();
                _tabId = await session.CreateTabAsync(foreground: true);
                _session = session;
                _view = view;
            }
            catch (Exception ex)
            {
                // A preview must never take the app down; degrade to text.
                JarvisCode.Core.Utilities.DiagnosticLog.Write(
                    $"htmlpreview: init failed — {ex.GetType().Name}: {ex.Message}");
                _initFailed = true;
                _initStarted = false;
                Content = FallbackText(_pendingHtml ?? html);
                return;
            }

            html = _pendingHtml ?? html;
            _pendingHtml = null;
        }

        await NavigateAsync(html);
    }

    /// <summary>
    /// The fragment reaches the page as a data: URL. WebView2 had
    /// NavigateToString for this; the engine navigates, so the document is
    /// built here and encoded rather than handed over as a string.
    /// </summary>
    private async Task NavigateAsync(string html)
    {
        if (_session is not { } session || _tabId is not { } tabId)
        {
            return;
        }

        var document =
            "<!doctype html><html><head><meta charset=\"utf-8\"></head>" +
            "<body style=\"margin:10px;font-family:Segoe UI,sans-serif;font-size:13px\">" +
            html + "</body></html>";

        try
        {
            await session.NavigateAsync(
                tabId, "data:text/html;charset=utf-8;base64," +
                       Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(document)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            _initFailed = true;
            Content = FallbackText(html);
        }
    }

    private void Release()
    {
        var session = _session;
        _session = null;
        _tabId = null;
        _view = null;
        _initStarted = false;
        Content = null;

        if (session is not null)
        {
            _ = session.DisposeAsync().AsTask();
        }
    }

    private static TextBox FallbackText(string html) => new()
    {
        Text = html,
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = System.Windows.Media.Brushes.Transparent,
        FontSize = 12,
    };
}
