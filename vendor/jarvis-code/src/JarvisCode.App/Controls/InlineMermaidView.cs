using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// A selectable SVG in the transcript itself. ElectronPaneView applies the
/// ancestor ScrollViewer's native window region, so this HWND cannot paint
/// across neighbouring rows or the composer while the transcript scrolls.
/// </summary>
internal sealed class InlineMermaidView : ContentControl
{
    private readonly MermaidDiagram _diagram;
    private ElectronPaneSession? _session;
    private int _generation;

    public InlineMermaidView(MermaidDiagram diagram)
    {
        _diagram = diagram;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        MaxWidth = diagram.Width;
        Height = diagram.Height;
        Content = new Image { Source = diagram.Image, Stretch = Stretch.Uniform };
        Loaded += (_, _) => _ = StartAsync();
        Unloaded += (_, _) =>
        {
            _generation++;
            var session = _session;
            _session = null;
            if (session is not null) _ = session.DisposeAsync().AsTask();
        };
        SizeChanged += (_, _) =>
        {
            if (ActualWidth > 0) Height = _diagram.Height * Math.Min(1, ActualWidth / _diagram.Width);
            if (_session is { } session) _ = session.LayoutAsync();
        };
        System.Windows.Automation.AutomationProperties.SetName(this, MermaidDiagrams.DiagramLabel);
    }

    private async Task StartAsync()
    {
        if (_session is not null || _diagram.Svg is null) return;
        var generation = ++_generation;
        var view = new ElectronPaneView();
        Content = view;
        var session = ElectronEngine.CreateSession(Dispatcher);
        try
        {
            var window = await session.EnsureHostWindowAsync(false);
            if (generation != _generation || !IsLoaded) { await session.DisposeAsync(); return; }
            _session = session;
            view.Attach(window);
            await session.Host.RequestAsync("assets.serve", new JsonObject
            {
                ["host"] = "richtext", ["directory"] = Path.Combine(AppContext.BaseDirectory, "Assets", "RichText"),
            }, default);
            var tab = await session.CreateTabAsync(true);
            await session.Cdp(tab).SendAsync("Runtime.enable", null);
            await session.Cdp(tab).SendAsync("Runtime.addBinding", new JsonObject { ["name"] = "__jarvisDiagramPost" });
            session.CdpEvent += (_, method, parameters) =>
            {
                if (generation != _generation || method != "Runtime.bindingCalled" || parameters["name"]?.GetValue<string>() != "__jarvisDiagramPost") return;
                try
                {
                    var message = JsonNode.Parse(parameters["payload"]!.GetValue<string>());
                    if (message?["kind"]?.GetValue<string>() == "wheel")
                    {
                        var delta = message["delta"]?.GetValue<double>() ?? 0;
                        if (double.IsFinite(delta)) RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, (int)Math.Clamp(-delta, -1200, 1200))
                            { RoutedEvent = Mouse.MouseWheelEvent, Source = this });
                    }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException) { }
            };
            await session.NavigateAsync(tab, "jarvis-asset://richtext/diagram.html");
            var ink = Background is SolidColorBrush brush ? brush.Color : Colors.Transparent;
            var page = TryFindResource("Bg000Brush") is SolidColorBrush baseBrush ? baseBrush.Color : Colors.White;
            var alpha = ink.A / 255.0;
            var background = $"rgb({Math.Round(ink.R * alpha + page.R * (1 - alpha))},{Math.Round(ink.G * alpha + page.G * (1 - alpha))},{Math.Round(ink.B * alpha + page.B * (1 - alpha))})";
            var expression = "document.body.style.background=" + JsonSerializer.Serialize(background) + ";new Promise((resolve,reject)=>{let n=0;function ready(){if(window.jarvisDiagram)resolve(window.jarvisDiagram.render(" +
                JsonSerializer.Serialize(_diagram.Svg) + "));else if(++n>200)reject(new Error('Diagram renderer did not load'));else setTimeout(ready,25)}ready()})";
            var rendered = await session.Cdp(tab).SendAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true,
            });
            if (rendered["exceptionDetails"] is not null) throw new InvalidOperationException("The inline diagram could not be rendered.");
            await session.ShowAsync();
        }
        catch (Exception error)
        {
            await session.DisposeAsync();
            if (generation == _generation)
            {
                _session = null;
                Content = new Image { Source = _diagram.Image, Stretch = Stretch.Uniform };
            }
            JarvisCode.Core.Utilities.DiagnosticLog.Write("inline diagram: " + error.GetType().Name);
        }
    }
}
