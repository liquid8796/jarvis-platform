using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Models;

namespace JarvisCode.App.Controls;

/// <summary>
/// The widget a <c>mcp__visualize__show_widget</c> call renders, inline in the
/// transcript.
///
/// The reference's host is its <c>AppRenderer</c> (ion-dist
/// <c>cd9b7fccf-BIkz8mbE.js</c>): it reads the tool's <c>ui://</c> resource,
/// loads it into a sandboxed iframe on a separate origin, speaks MCP Apps
/// JSON-RPC to it over <c>postMessage</c> and hands it the tool's arguments as
/// <c>ui/notifications/tool-input</c>. This is that host over this app's engine
/// in place of the sandbox proxy's origin — see <see cref="VisualizeWidgetPage"/>
/// for the two documents and the CSP.
///
/// The channel is the engine's, not a browser control's: the page answers
/// through a DevTools binding (<c>Runtime.addBinding</c>, arriving as
/// <c>Runtime.bindingCalled</c>) and the host answers back with
/// <c>Runtime.evaluate</c>, which is what the diagram renderer does since
/// <c>window.chrome.webview</c> went away with WebView2.
/// </summary>
public sealed class WidgetBlock : ContentControl
{
    /// <summary>The protocol version the reference's runtime announces.</summary>
    private const string ProtocolVersion = "2025-11-21";

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(WidgetItem), typeof(WidgetBlock),
        new PropertyMetadata(null, static (d, _) => ((WidgetBlock)d).Rebind()));

    /// <summary>The page's channel back, exposed on it as a DevTools binding.</summary>
    private const string PostBinding = "__jarvisWidgetPost";

    private readonly Border _card;
    private ElectronPaneSession? _session;
    private ElectronPaneView? _view;
    private string? _tabId;
    private int _hostContextId;
    private bool _initStarted;
    private bool _failed;
    private WidgetItem? _boundItem;
    private int _generation;
    private double _reportedHeight;

    public WidgetBlock()
    {
        Focusable = false;
        IsTabStop = false;
        _card = new Border
        {
            CornerRadius = new CornerRadius(VisualizeWidgetStrings.BodyRadius),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(VisualizeWidgetStrings.BodyPadding),
            Margin = new Thickness(0, VisualizeWidgetStrings.BodyMarginTop, 0, 0),
            SnapsToDevicePixels = true,
        };
        _card.SetResourceReference(Border.BorderBrushProperty, "Tint3Brush");
        Content = _card;

        // Until the page answers the handshake the reference keeps the container
        // hidden with no height, border or padding, so nothing flashes; the view
        // itself must stay in the tree for WebView2 to finish initializing.
        Collapse();

        Loaded += (_, _) => Rebind();
        Unloaded += (_, _) => Release();
    }

    public WidgetItem? Item
    {
        get => (WidgetItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void Rebind()
    {
        if (Item is not { } item || !IsLoaded)
        {
            return;
        }

        if (!ReferenceEquals(_boundItem, item))
        {
            Release();
            _failed = false;
            _boundItem = item;
        }

        item.PropertyChanged -= OnItemChanged;
        item.PropertyChanged += OnItemChanged;
        ApplyLayout();
        _ = StartAsync(item);
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetItem.InputRevision) && Item is { IsInitialized: true } item)
            SendToolInput(item);
        if (e.PropertyName is nameof(WidgetItem.IsExpanded) or nameof(WidgetItem.IsInitialized))
        {
            ApplyLayout();
        }
    }

    /// <summary>
    /// The reference's own two states for the container: shown at its height
    /// once the app initialized, and hidden with every box property zeroed
    /// before that. A folded row is the same hidden state, which keeps the page
    /// alive rather than tearing the widget down and losing what it holds.
    /// </summary>
    private void ApplyLayout()
    {
        if (Item is { IsInitialized: true, IsExpanded: true })
        {
            _card.BorderThickness = new Thickness(1);
            _card.Padding = new Thickness(VisualizeWidgetStrings.BodyPadding);
            _card.Margin = new Thickness(0, VisualizeWidgetStrings.BodyMarginTop, 0, 0);
            if (_view is not null && _reportedHeight > 0)
            {
                _view.Height = _reportedHeight;
            }

            // The engine lays its view out from the window's own content size,
            // and the window was just resized from this side: without asking for
            // a layout the view keeps the rectangle it had, and the window paints
            // its own background in the difference.
            RequestEngineLayout();
            return;
        }

        Collapse();
    }

    private void Collapse()
    {
        _card.BorderThickness = default;
        _card.Padding = default;
        _card.Margin = default;
        if (_view is not null)
        {
            _view.Height = 0;
        }
    }

    /// <summary>
    /// Asks the engine to re-lay the tab's view over the window it now has. The
    /// window is moved from this side, and the engine sizes its view from the
    /// window's content size when it is told to.
    /// </summary>
    private void RequestEngineLayout()
    {
        if (_session is { } session)
        {
            // After the layout pass that gave the element its new size, so the
            // window has already been moved when the engine reads it.
            _ = Dispatcher.InvokeAsync(
                () => _ = session.LayoutAsync(),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void Release()
    {
        _generation++;
        if (_boundItem is { } item)
        {
            item.PropertyChanged -= OnItemChanged;
            item.IsInitialized = false;
        }
        _boundItem = null;

        var session = _session;
        _session = null;
        _tabId = null;
        _hostContextId = 0;
        _view = null;
        _card.Child = null;
        _initStarted = false;
        if (session is not null)
        {
            _ = session.DisposeAsync().AsTask();
        }
    }

    private async Task StartAsync(WidgetItem item)
    {
        if (_failed || _initStarted || _session is not null)
        {
            return;
        }

        _initStarted = true;
        var generation = _generation;
        var view = new ElectronPaneView { Height = 0 };

        // The element has to be in a loaded tree before the engine's window is
        // reparented into it, or there is no container to reparent into.
        _card.Child = view;
        try
        {
            var session = ElectronEngine.CreateSession(Dispatcher);
            session.CdpEvent += (_, method, parameters) =>
            {
                if (generation == _generation) OnEngineMessage(item, method, parameters);
            };
            var window = await session.EnsureHostWindowAsync(persistSessions: false);
            if (generation != _generation || !IsLoaded)
            {
                await session.DisposeAsync();
                return;
            }
            view.Attach(window);
            await session.ShowAsync();
            var tabId = await session.CreateTabAsync(foreground: true);

            // Runtime has to be enabled before the binding is added, or the
            // page's call to it produces no bindingCalled event and the host
            // waits for a widget that is answering into nothing.
            await BrowserPaneCdp.CallAsync(session.Cdp(tabId), "Runtime.enable");

            if (generation != _generation || !IsLoaded)
            {
                await session.DisposeAsync();
                return;
            }

            _session = session;
            _tabId = tabId;
            _view = view;

            // HwndHost moves the engine's window on this event; the handler is
            // registered by the control's own constructor, so this one runs
            // after it and the engine reads a window that has already moved.
            view.SizeChanged += (_, _) => RequestEngineLayout();
            await session.NavigateAsync(tabId, DataUrl(VisualizeWidgetPage.WrapperHtml(
                VisualizeTools.RuntimeSecurity.ClipboardWrite, PostBinding, CardBackground())));
            var tree = await BrowserPaneCdp.CallAsync(session.Cdp(tabId), "Page.getFrameTree");
            var world = await BrowserPaneCdp.CallAsync(session.Cdp(tabId), "Page.createIsolatedWorld", new JsonObject
            {
                ["frameId"] = tree["frameTree"]?["frame"]?["id"]?.GetValue<string>(),
                ["worldName"] = "jarvis-widget-host",
            });
            _hostContextId = world["executionContextId"]!.GetValue<int>();
            // Only the isolated top-frame world receives the binding. Neither
            // widget scripts nor their child frame can forge a host-authorized
            // file/connector request by calling the DevTools binding directly.
            await BrowserPaneCdp.CallAsync(session.Cdp(tabId), "Runtime.addBinding", new JsonObject
            {
                ["name"] = PostBinding, ["executionContextId"] = _hostContextId,
            });
            await BrowserPaneCdp.CallAsync(session.Cdp(tabId), "Runtime.evaluate", new JsonObject
            {
                ["expression"] = VisualizeWidgetPage.WrapperBootstrapScript(PostBinding),
                ["contextId"] = _hostContextId,
            });
        }
        catch (Exception ex)
        {
            // A widget must never take the app down; the row keeps its header
            // and simply never opens.
            JarvisCode.Core.Utilities.DiagnosticLog.Write(
                $"widget: init failed — {ex.GetType().Name}: {ex.Message}");
            _card.Child = null;
            _failed = true;
            _initStarted = false;
            Release();
            return;
        }
    }

    /// <summary>
    /// The card's own fill, as CSS. An engine window paints black where its page
    /// does not, so the proxy is opaque in the colour the transcript is rather
    /// than transparent over it.
    /// </summary>
    private static string CardBackground()
    {
        foreach (var key in new[] { "Bg000Brush", "Bg100Brush" })
        {
            if (Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
            {
                return $"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}";
            }
        }

        return "transparent";
    }

    /// <summary>
    /// The proxy page reaches the engine as a document rather than a string:
    /// WebView2 had NavigateToString, and the engine navigates.
    /// </summary>
    private static string DataUrl(string html) =>
        "data:text/html;charset=utf-8;base64," +
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(html));

    /// <summary>
    /// Everything the page posts arrives as one <c>Runtime.bindingCalled</c>
    /// event carrying the JSON it handed the binding.
    /// </summary>
    private void OnEngineMessage(WidgetItem item, string method, JsonObject parameters)
    {
        if (method != "Runtime.bindingCalled" ||
            parameters["name"]?.GetValue<string>() != PostBinding ||
            parameters["executionContextId"]?.GetValue<int>() != _hostContextId)
        {
            return;
        }

        OnMessage(item, parameters["payload"]?.GetValue<string>());
    }

    private void OnMessage(WidgetItem item, string? payload)
    {
        JsonObject message;
        try
        {
            message = JsonNode.Parse(payload ?? "") as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return;
        }

        if (message["__host"] is JsonValue marker &&
            marker.TryGetValue<string>(out var kind) && kind == "ready")
        {
            SendResource(item);
            return;
        }

        if (message["type"] is JsonValue type && type.TryGetValue<string>(out var bridgeType))
        {
            HandleWidgetAction(bridgeType, message);
            return;
        }

        var method = message["method"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : null;
        var id = message["id"];
        switch (method)
        {
            case "ui/initialize":
                Reply(id, HostHandshake());
                break;

            case "ui/notifications/initialized":
                item.IsInitialized = true;
                SendToolInput(item);
                break;

            case "ui/notifications/size-changed":
                if (message["params"]?["height"] is JsonValue height &&
                    height.TryGetValue<double>(out var pixels) && pixels > 0)
                {
                    _reportedHeight = pixels;
                    ApplyLayout();
                }

                break;

            case "ui/message":
                Reply(id, Prefill(TextOf(message["params"]?["content"] as JsonArray)));
                break;

            case "ui/open-link":
                Reply(id, OpenLink(Str(message["params"]?["url"])));
                break;

            // The reference answers this one with an error too: its host logs
            // "download-file not yet wired (no confirmation modal)" and refuses.
            case "ui/download-file":
                Reply(id, new JsonObject { ["isError"] = true });
                break;

            case "ui/request-display-mode":
                Reply(id, new JsonObject { ["mode"] = "inline" });
                break;

            case "ui/update-model-context":
                item.ModelContext = Blocks(message["params"]?["content"] as JsonArray);
                Reply(id, []);
                break;

            case "notifications/message":
                JarvisCode.Core.Utilities.DiagnosticLog.Write(
                    $"[MCP App: {item.ServerName}] {message["params"]?.ToJsonString()}");
                break;
        }
    }

    /// <summary>
    /// The reference's initialize result: the protocol version, who the host is,
    /// what it can do, and the context the page themes itself from. The runtime
    /// reads <c>hostContext.theme</c> and applies it to its own document.
    /// </summary>
    private static JsonObject HostHandshake()
    {
        var dark = Application.Current is App app && app.TryGetServices(out var services) &&
            services.Theme.IsDark;
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["hostInfo"] = new JsonObject
            {
                ["name"] = "Jarvis",
                ["version"] = typeof(WidgetBlock).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            },
            ["hostCapabilities"] = new JsonObject
            {
                ["openLinks"] = new JsonObject(),
                ["logging"] = new JsonObject(),
                ["message"] = new JsonObject { ["text"] = new JsonObject() },
                ["updateModelContext"] = new JsonObject
                {
                    ["text"] = new JsonObject(),
                    ["image"] = new JsonObject(),
                },
                ["sandbox"] = new JsonObject
                {
                    ["permissions"] = new JsonObject { ["clipboardWrite"] = new JsonObject() },
                    ["csp"] = new JsonObject
                    {
                        ["connectDomains"] = new JsonArray(
                            [.. VisualizeTools.RuntimeSecurity.ConnectDomains.Select(static d => (JsonNode)d)]),
                        ["resourceDomains"] = new JsonArray(
                            [.. VisualizeTools.RuntimeSecurity.ResourceDomains.Select(static d => (JsonNode)d)]),
                    },
                },
            },
            ["hostContext"] = new JsonObject
            {
                ["theme"] = dark ? "dark" : "light",
                ["displayMode"] = "inline",
                ["availableDisplayModes"] = new JsonArray("inline"),
                ["platform"] = "desktop",
                ["locale"] = System.Globalization.CultureInfo.CurrentUICulture.Name,
                ["timeZone"] = TimeZoneInfo.Local.Id,
                ["safeAreaInsets"] = new JsonObject
                {
                    ["top"] = 0,
                    ["right"] = 0,
                    ["bottom"] = 0,
                    ["left"] = 0,
                },
            },
        };
    }

    /// <summary>
    /// Hands the wrapper the resource the tool's <c>_meta.ui.resourceUri</c>
    /// names, read out of the in-process MCP shell, with the CSP its own
    /// <c>_meta.ui.csp</c> declared already applied.
    /// </summary>
    private void SendResource(WidgetItem item)
    {
        var html = item.ResourceReader?.Invoke(item.ResourceUri)?.Text ?? VisualizeCorpus.WidgetRuntimeHtml;
        Post(new JsonObject
        {
            ["__host"] = "resource",
            ["html"] = VisualizeWidgetPage.WithSecurityPolicy(html, VisualizeTools.RuntimeSecurity),
        });
    }

    /// <summary>
    /// The tool call's own arguments, as the reference's <c>sendToolInput</c>
    /// delivers them. Partial arguments use the runtime's streaming renderer;
    /// only an approved tool execution sends the final input that runs scripts.
    /// </summary>
    private void SendToolInput(WidgetItem item)
    {
        Post(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = item.IsInputComplete ? "ui/notifications/tool-input" : "ui/notifications/tool-input-partial",
            ["params"] = new JsonObject
            {
                ["arguments"] = new JsonObject
                {
                    ["title"] = item.Title,
                    ["widget_code"] = item.WidgetCode,
                    ["loading_messages"] = new JsonArray(
                        [.. item.LoadingMessages.Select(static m => (JsonNode)m)]),
                },
            },
        });
    }

    /// <summary>
    /// <c>sendPrompt</c>. The reference's Code surface does not send it: its
    /// <c>onmessage</c> is <c>onPrefillComposer</c>, so the text lands in the
    /// composer and the user presses enter.
    /// </summary>
    private JsonObject Prefill(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject { ["isError"] = true };
        }

        if (OwningSurface() is not { } surface)
        {
            return new JsonObject { ["isError"] = true };
        }

        surface.PrefillInput(text);
        return [];
    }

    private Views.ChatSurface? OwningSurface()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is Views.ChatSurface surface) return surface;
            node = node is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void HandleWidgetAction(string type, JsonObject message)
    {
        if (message["__userActivated"]?.GetValue<bool>() != true || OwningSurface() is not { } surface) return;
        try
        {
            if (type == "anthropic:connect-connector" && Str(message["connectorId"]) is { } connector)
            {
                surface.FocusInput();
                if (Window.GetWindow(this) is Views.MainWindow window) window.OpenWidgetConnector(connector);
            }
            else if (type is "anthropic:attach-files" or "anthropic:elicit-submit" && message["files"] is JsonArray files)
            {
                surface.AttachWidgetFiles(files, type == "anthropic:elicit-submit" ? Str(message["text"]) : null);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            ToastQueue.Current?.Add("The widget could not attach the selected files: " + error.Message);
        }
    }

    /// <summary>
    /// <c>ui/open-link</c>. The reference refuses anything that is not https and
    /// asks the user before following the rest.
    /// </summary>
    private JsonObject OpenLink(string? url)
    {
        if (!WidgetLinkPrompt.IsFollowable(url, out var uri) || uri is null)
        {
            return new JsonObject { ["isError"] = true };
        }

        var answer = Views.MessageDialog.Show(
            Window.GetWindow(this),
            WidgetLinkPrompt.Title,
            WidgetLinkPrompt.Detail(uri),
            [WidgetLinkPrompt.OpenLabel, WidgetLinkPrompt.CancelLabel],
            defaultId: 1,
            cancelId: 1,
            Views.MessageDialogType.Question,
            // The reference leaves its confirm disabled briefly, so a link
            // cannot be opened by a click begun before the question appeared.
            delayedButtonId: 0);
        if (answer != 0)
        {
            return new JsonObject { ["isError"] = true };
        }

        ExternalLinks.Open(Window.GetWindow(this), uri.AbsoluteUri);
        return [];
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string TextOf(JsonArray? content) =>
        content is null
            ? ""
            : string.Join("\n", content
                .OfType<JsonObject>()
                .Where(static block => Str(block["type"]) == "text")
                .Select(static block => Str(block["text"]) ?? ""));

    private static IReadOnlyList<ContentBlock> Blocks(JsonArray? content)
    {
        if (content is null)
        {
            return [];
        }

        List<ContentBlock> blocks = [];
        foreach (var block in content.OfType<JsonObject>())
        {
            switch (Str(block["type"]))
            {
                case "text" when Str(block["text"]) is { } text:
                    blocks.Add(new JarvisCode.Core.Models.TextBlock(text));
                    break;
                case "image" when Str(block["data"]) is { } data:
                    blocks.Add(new ImageBlock(Str(block["mimeType"]) ?? "image/png", data));
                    break;
            }
        }

        return blocks;
    }

    private void Reply(JsonNode? id, JsonObject result)
    {
        if (id is null)
        {
            return;
        }

        Post(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        });
    }

    /// <summary>
    /// The host's half of the channel. The wrapper exposes one function and
    /// everything the host sends is one call to it, so nothing but JSON crosses.
    /// </summary>
    private void Post(JsonObject message)
    {
        if (_session is not { } session || _tabId is not { } tabId)
        {
            return;
        }

        var call = VisualizeWidgetPage.HostFunction + "(" +
            JsonSerializer.Serialize(message.ToJsonString()) + ")";
        _ = BrowserPaneCdp.CallAsync(session.Cdp(tabId), "Runtime.evaluate", new JsonObject
        {
            ["expression"] = call,
            ["contextId"] = _hostContextId,
            ["awaitPromise"] = false,
            ["returnByValue"] = true,
        });
    }
}
