using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JarvisCode.App.Services;

internal sealed record RichSyntaxSpan(string Text, string? Color = null, int FontStyle = 0);
internal sealed record RenderedMath(BitmapSource Image, double Width, double Height, string Html, bool IsError);

/// <summary>
/// The grammar/math libraries run in the shared Chromium engine. Their output
/// is consumed by native text/image controls, retaining native code selection.
/// No CDN, external account or page navigation is involved.
/// </summary>
internal sealed class RichTextRenderer(ElectronPaneHost? testHost = null) : IAsyncDisposable
{
    public static RichTextRenderer Shared { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ElectronPaneSession? _session;
    private string? _tab;
    private readonly Dictionary<string, IReadOnlyList<RichSyntaxSpan>> _highlightCache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private async Task EnsureAsync()
    {
        if (_session is not null) return;
        var session = testHost is null ? ElectronEngine.CreateSession(Dispatcher.CurrentDispatcher, "jarvis-richtext")
            : new ElectronPaneSession(testHost, Dispatcher.CurrentDispatcher, ownsHost: false, storagePartition: "jarvis-richtext");
        try
        {
            await session.EnsureHostWindowAsync(false, offscreen: true).ConfigureAwait(false);
            await session.ShowAsync().ConfigureAwait(false);
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "RichText");
            await session.Host.RequestAsync("assets.serve", new JsonObject { ["host"] = "richtext", ["directory"] = assets }, default).ConfigureAwait(false);
            var tab = await session.CreateTabAsync(true).ConfigureAwait(false);
            await session.NavigateAsync(tab, "jarvis-asset://richtext/host.html").ConfigureAwait(false);
            await session.Cdp(tab).SendAsync("Emulation.setDefaultBackgroundColorOverride", new JsonObject
            {
                ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 0, ["a"] = 0 },
            }).ConfigureAwait(false);
            var response = await session.Cdp(tab).SendAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = "new Promise((resolve,reject)=>{let attempts=0;function ready(){if(window.jarvisRichText)resolve(true);else if(++attempts>200)reject(new Error('Renderer modules did not load'));else setTimeout(ready,25)}ready()})",
                ["awaitPromise"] = true, ["returnByValue"] = true,
            }).ConfigureAwait(false);
            if (response["exceptionDetails"] is not null) throw new InvalidOperationException("The text renderer could not load its bundled libraries.");
            _session = session;
            _tab = tab;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    private async Task<JsonNode?> EvaluateAsync(string expression)
    {
        var response = await _session!.Cdp(_tab!).SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true,
        }).ConfigureAwait(false);
        if (response["exceptionDetails"] is { } error)
            throw new InvalidOperationException(error["exception"]?["description"]?.GetValue<string>() ?? "The text renderer rejected its input.");
        return response["result"]?["value"];
    }

    public async Task<IReadOnlyList<RichSyntaxSpan>> HighlightAsync(string code, string? language, CodeTheme theme)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var key = theme.Id + "\n" + language + "\n" + code;
            if (_highlightCache.TryGetValue(key, out var cached)) return cached;
            await EnsureAsync().ConfigureAwait(false);
            var args = JsonSerializer.Serialize(new { code, language, theme });
            var tokens = await EvaluateAsync("window.jarvisRichText.highlight(" + args + ")").ConfigureAwait(false);
            var spans = JsonSerializer.Deserialize<List<RichSyntaxSpan>>(tokens!.ToJsonString(), Json) ?? [];
            if (string.Concat(spans.Select(span => span.Text)) != code)
                throw new InvalidDataException("The highlighter did not preserve the source text.");
            if (_highlightCache.Count >= 128) _highlightCache.Remove(_highlightCache.Keys.First());
            _highlightCache[key] = spans;
            return spans;
        }
        finally { _gate.Release(); }
    }

    public async Task<RenderedMath> MathAsync(string latex, bool display, double size, string color, double scale = 2)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureAsync().ConfigureAwait(false);
            var result = await EvaluateAsync("window.jarvisRichText.math(" + JsonSerializer.Serialize(new { latex, display, size, color }) + ")").ConfigureAwait(false);
            var width = result!["width"]!.GetValue<double>();
            var height = result["height"]!.GetValue<double>();
            if (width is <= 0 or > 8192 || height is <= 0 or > 8192) throw new InvalidDataException("The formula is too large to display.");
            var capture = await _session!.Cdp(_tab!).SendAsync("Page.captureScreenshot", new JsonObject
            {
                ["format"] = "png", ["captureBeyondViewport"] = true,
                ["clip"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = width, ["height"] = height, ["scale"] = Math.Clamp(scale, 1, 4) },
            }).ConfigureAwait(false);
            using var bytes = new MemoryStream(Convert.FromBase64String(capture["data"]!.GetValue<string>()));
            var image = BitmapFrame.Create(bytes, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze();
            return new RenderedMath(image, width, height, result["html"]!.GetValue<string>(), result["error"]!.GetValue<bool>());
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session) await session.DisposeAsync();
        _session = null;
        _tab = null;
    }
}
