using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.Services;

/// <summary>What a fence's diagram came back as.</summary>
/// <param name="Image">The rendered diagram, or null when it failed.</param>
/// <param name="Width">Its intrinsic width in device-independent pixels.</param>
/// <param name="Height">Its intrinsic height.</param>
/// <param name="Error">Why it failed, for the log; the user sees the code block instead.</param>
internal sealed record MermaidDiagram(BitmapSource? Image, double Width, double Height, string? Error, string? Svg = null)
{
    public bool Ok => Image is not null;
}

/// <summary>
/// The half of the reference's mermaid renderer that needs a browser: mermaid
/// itself, its sandbox iframe, and the SVG that comes back.
///
/// Diagram generation is shared by every block. The SVG is displayed inline by
/// InlineMermaidView in a viewport-clipped native engine window; the bitmap is
/// retained as a startup/error fallback and for export. What
/// happens inside it is the reference's own module: the same mermaid 11.16.1
/// build, its config, the sandbox iframe with the CSP forced onto it, the 5s
/// race, and the two extraction refusals (<see cref="MermaidDiagrams"/> and
/// <c>Assets/Mermaid/host.html</c>).
/// </summary>
internal sealed class MermaidRenderer : IDisposable
{
    /// <summary>
    /// The reference reuses a cached diagram while the window width has not moved
    /// more than this, and re-renders past it — a diagram that lays out against
    /// its container is a different diagram at a different width.
    /// </summary>
    public const double WidthTolerance = 120;

    /// <summary>The reference keeps at most 48 rendered diagrams.</summary>
    public const int CacheEntries = 48;

    /// <summary>
    /// The reference declines to cache an SVG past 512,000 characters. A bitmap
    /// has no character count, so the same ceiling is applied to its bytes.
    /// </summary>
    public const int CacheEntryByteLimit = 512_000;

    /// <summary>The virtual host the assets folder is mapped to; nothing resolves it on the network.</summary>
    /// <summary>
    /// The host segment the engine serves <c>Assets/Mermaid</c> at. It needs a
    /// real origin rather than file://: the page runs mermaid in a sandboxed
    /// iframe, which an opaque origin breaks.
    /// </summary>
    private const string AssetHost = "mermaid";

    /// <summary>The page's channel back, exposed on it as a DevTools binding.</summary>
    private const string PostBinding = "__jarvisMermaidPost";

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ElectronPaneSession? _session;
    private JarvisCode.App.Controls.ElectronPaneView? _view;
    private string? _tabId;
    private ContentControl? _host;
    private TaskCompletionSource<bool>? _ready;
    private bool _unavailable;
    private int _nextId;

    private sealed record CacheEntry(BitmapSource Image, double Width, double Height, double ViewportWidth, string? Svg);

    /// <summary>
    /// The renderer the app shares. It is created with the main window and lives
    /// as long as it does; without one (a test host, the CLI) there is none and
    /// every fence stays a code block.
    /// </summary>
    public static MermaidRenderer? Current { get; set; }

    /// <summary>
    /// Where the view will go. It is not created until the first diagram asks for
    /// one: a run that renders no diagram — a screenshot pose, a self-test —
    /// would otherwise pay for a browser it never uses.
    /// </summary>
    public void Attach(ContentControl host) => _host = host;

    /// <summary>
    /// The view itself: one device-independent pixel painting nothing, because
    /// an engine view is an HWND island that no WPF element can cover — and it
    /// stays visible rather than hidden, since Chromium throttles a hidden page
    /// and mermaid's layout would stall in one. Measured on Electron 42.10.0: a
    /// window that is never shown does not paint at all.
    /// </summary>
    private static JarvisCode.App.Controls.ElectronPaneView CreateView() => new()
    {
        Width = 1,
        Height = 1,
        IsHitTestVisible = false,
        Focusable = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>
    /// Renders one fence, or answers why it could not be. Callers are on the UI
    /// thread; renders are serialized because <c>mermaid.initialize</c> is global
    /// and two themes at once would race.
    /// </summary>
    public async Task<MermaidDiagram> RenderAsync(
        string code, bool dark, string fontFamily, double scale, double viewportWidth)
    {
        var key = dark + "\n" + code;
        if (_cache.TryGetValue(key, out var hit)
            && Math.Abs(hit.ViewportWidth - viewportWidth) <= WidthTolerance)
        {
            Touch(key);
            return new MermaidDiagram(hit.Image, hit.Width, hit.Height, null, hit.Svg);
        }

        string prepared;
        try
        {
            prepared = MermaidDiagrams.Preprocess(code);
        }
        catch (MermaidDiagrams.RefusedException ex)
        {
            return new MermaidDiagram(null, 0, 0, ex.Message);
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!await ReadyAsync().ConfigureAwait(true) || _session is not { } session || _tabId is not { } tabId)
            {
                return new MermaidDiagram(null, 0, 0, "the mermaid renderer is unavailable");
            }

            var id = "d" + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = JsonSerializer.Serialize(new
            {
                id,
                code = prepared,
                config = MermaidDiagrams.ConfigJson(dark, fontFamily),
                scopeId = "mermaid-" + id,
                scale = Math.Clamp(scale, 1, 4),
                timeoutMs = (int)MermaidDiagrams.RenderTimeout.TotalMilliseconds,
                viewportWidth = (int)Math.Round(viewportWidth),
            });

            var completion = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                await BrowserPaneCdp.EvaluateAsync(
                    session.Cdp(tabId), "window.__jarvisMermaidRender(" + request + ")", replMode: false)
                    .ConfigureAwait(true);
                // The page's own race answers first; this one only covers a page
                // that stopped answering at all (a crashed renderer process).
                var answered = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(MermaidDiagrams.RenderTimeout + TimeSpan.FromSeconds(5)))
                    .ConfigureAwait(true);
                if (answered != completion.Task)
                {
                    return new MermaidDiagram(null, 0, 0, "the mermaid renderer stopped answering");
                }

                var result = await completion.Task.ConfigureAwait(true);
                if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                {
                    var error = result.TryGetProperty("error", out var e) ? e.GetString() : null;
                    return new MermaidDiagram(null, 0, 0, error ?? "mermaid could not render the diagram");
                }

                var png = Convert.FromBase64String(result.GetProperty("png").GetString() ?? "");
                var svg = result.TryGetProperty("svg", out var markup) ? markup.GetString() : null;
                var width = result.GetProperty("width").GetDouble();
                var height = result.GetProperty("height").GetDouble();
                var image = Decode(png);
                if (image is null)
                {
                    return new MermaidDiagram(null, 0, 0, "the rendered diagram would not decode");
                }

                Store(key, new CacheEntry(image, width, height, viewportWidth, svg), svg?.Length ?? png.Length);
                return new MermaidDiagram(image, width, height, null, svg);
            }
            finally
            {
                _pending.Remove(id);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"mermaid: render failed — {ex.GetType().Name}: {ex.Message}");
            return new MermaidDiagram(null, 0, 0, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BitmapSource? Decode(byte[] png)
    {
        try
        {
            var decoder = new PngBitmapDecoder(
                new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"mermaid: decode failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void Touch(string key)
    {
        _order.Remove(key);
        _order.Add(key);
    }

    private void Store(string key, CacheEntry entry, int bytes)
    {
        if (bytes > CacheEntryByteLimit)
        {
            return;
        }

        _cache[key] = entry;
        Touch(key);
        while (_order.Count > CacheEntries)
        {
            _cache.Remove(_order[0]);
            _order.RemoveAt(0);
        }
    }

    private async Task<bool> ReadyAsync()
    {
        if (_unavailable || _host is null)
        {
            return false;
        }

        if (_ready is not null)
        {
            return await _ready.Task.ConfigureAwait(true);
        }

        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Mermaid");
            if (!File.Exists(Path.Combine(assets, "mermaid.min.js")))
            {
                throw new FileNotFoundException("Assets/Mermaid/mermaid.min.js is not beside the app");
            }

            _view = CreateView();
            // The element has to be in a loaded tree before its window is
            // reparented into it, or there is no container to reparent into.
            _host.Content = _view;

            var session = ElectronEngine.CreateSession(System.Windows.Application.Current.Dispatcher);
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.CdpEvent += (_, method, parameters) => OnEngineMessage(method, parameters, loaded);

            var window = await session.EnsureHostWindowAsync(persistSessions: false).ConfigureAwait(true);
            _view.Attach(window);
            await session.ShowAsync().ConfigureAwait(true);

            await session.Host.RequestAsync("assets.serve", new System.Text.Json.Nodes.JsonObject
            {
                ["host"] = AssetHost,
                ["directory"] = assets,
            }, CancellationToken.None).ConfigureAwait(true);

            _tabId = await session.CreateTabAsync(foreground: true).ConfigureAwait(true);

            // Runtime has to be enabled before a binding is added, or the page's
            // call to it produces no bindingCalled event and the renderer waits
            // for a page that is answering into nothing.
            await BrowserPaneCdp.CallAsync(session.Cdp(_tabId), "Runtime.enable", null).ConfigureAwait(true);
            await BrowserPaneCdp.CallAsync(session.Cdp(_tabId), "Runtime.addBinding",
                new System.Text.Json.Nodes.JsonObject { ["name"] = PostBinding }).ConfigureAwait(true);
            _session = session;

            await session.NavigateAsync(_tabId, "jarvis-asset://" + AssetHost + "/host.html")
                .ConfigureAwait(true);

            var settled = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)))
                .ConfigureAwait(true);
            _unavailable = settled != loaded.Task;
            _ready.SetResult(!_unavailable);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"mermaid: renderer unavailable — {ex.GetType().Name}: {ex.Message}");
            _unavailable = true;
            _ready.SetResult(false);
        }

        return await _ready.Task.ConfigureAwait(true);
    }

    /// <summary>
    /// The page answers through a DevTools binding, which arrives as a
    /// Runtime.bindingCalled event carrying the JSON it posted.
    /// </summary>
    private void OnEngineMessage(
        string method, System.Text.Json.Nodes.JsonObject parameters, TaskCompletionSource<bool> loaded)
    {
        if (method != "Runtime.bindingCalled" ||
            parameters["name"]?.GetValue<string>() != PostBinding)
        {
            return;
        }

        try
        {
            var payload = JsonDocument
                .Parse(parameters["payload"]?.GetValue<string>() ?? "{}").RootElement;
            var id = payload.TryGetProperty("id", out var value) ? value.GetString() : null;
            if (id == "ready")
            {
                loaded.TrySetResult(true);
                return;
            }

            if (id is not null && _pending.TryGetValue(id, out var completion))
            {
                completion.TrySetResult(payload.Clone());
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"mermaid: bad renderer message — {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        var session = _session;
        _session = null;
        _view = null;
        _tabId = null;
        if (session is not null)
        {
            _ = session.DisposeAsync().AsTask();
        }

        _gate.Dispose();
    }
}
