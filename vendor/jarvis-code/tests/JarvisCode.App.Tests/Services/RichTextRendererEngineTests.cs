using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed partial class ElectronPaneHostTests
{
    [ElectronFact]
    public async Task InlineDiagramTextIsSelectableAndItsSvgRespondsToTheViewport()
    {
        using var deadline = Deadline();
        var token = deadline.Token;
        await using var host = NewHost(out _);
        await host.RequestAsync("host.create", new JsonObject { ["show"] = true, ["x"] = -10000, ["y"] = -10000 }, token);
        await host.RequestAsync("assets.serve", new JsonObject { ["host"] = "richtext", ["directory"] = Path.Combine(AppContext.BaseDirectory, "Assets", "RichText") }, token);
        var tab = (await host.RequestAsync("tab.create", null, token))["tabId"]!.GetValue<string>();
        await host.RequestAsync("tab.navigate", new JsonObject { ["tabId"] = tab, ["url"] = "jarvis-asset://richtext/diagram.html" }, token);
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 100\"><text x=\"10\" y=\"50\">Selectable diagram</text><a href=\"https://untrusted.invalid/\"><text x=\"10\" y=\"80\">Link</text></a></svg>";
        await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = "new Promise((resolve,reject)=>{let n=0;function ready(){if(window.jarvisDiagram)resolve(window.jarvisDiagram.render(" + System.Text.Json.JsonSerializer.Serialize(svg) + "));else if(++n>200)reject(new Error('not ready'));else setTimeout(ready,25)}ready()})",
            ["awaitPromise"] = true, ["returnByValue"] = true,
        });
        var selected = await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = "(()=>{const root=document.getElementById('diagram').shadowRoot;const range=document.createRange();range.selectNodeContents(root.querySelector('text'));const selection=getSelection();selection.removeAllRanges();selection.addRange(range);return {text:selection.toString(),externalHref:root.querySelector('a')?.getAttribute('href')}})()",
            ["returnByValue"] = true,
        });
        Assert.Equal("Selectable diagram", selected["result"]?["value"]?["text"]?.GetValue<string>());
        Assert.Null(selected["result"]?["value"]?["externalHref"]);
        var heights = new List<double>();
        foreach (var width in new[] { 400, 200 })
        {
            await Cdp(host, tab, "Emulation.setDeviceMetricsOverride", token, new JsonObject
            {
                ["width"] = width, ["height"] = 200, ["deviceScaleFactor"] = 1, ["mobile"] = false,
            });
            var measured = await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
            {
                ["expression"] = "document.getElementById('diagram').shadowRoot.querySelector('svg').getBoundingClientRect().height",
                ["returnByValue"] = true,
            });
            heights.Add(measured["result"]!["value"]!.GetValue<double>());
        }
        Assert.Equal(heights[0] / 2, heights[1], precision: 1);
    }

    [ElectronFact]
    public async Task ShikiMatchesInstalledWorkerColorAndStyleFixturesAcrossTenGrammars()
    {
        await using var host = NewHost(out _);
        await using var renderer = new RichTextRenderer(host);
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference-highlight-1.46388.3.0.json"))) as JsonArray;
        foreach (var fixture in fixtures!)
        {
            var theme = CodeThemes.All.Single(theme => theme.Id == (fixture!["theme"]?.GetValue<string>() ?? "github-dark"));
            var text = fixture!["text"]!.GetValue<string>();
            var spans = await renderer.HighlightAsync(text, fixture["lang"]!.GetValue<string>(), theme);
            Assert.Equal(text, string.Concat(spans.Select(span => span.Text)));
            static IEnumerable<string> Characters(string text, string? color, int style) => text.EnumerateRunes()
                .Where(character => character.Value is not 10 and not 13)
                .Select(character => character + "|" + color + "|" + style);
            var expected = (fixture["tokens"] as JsonArray)!.SelectMany(token => Characters(
                token!["content"]!.GetValue<string>(), token["color"]?.GetValue<string>(), token["fontStyle"]?.GetValue<int>() ?? 0));
            var actual = spans.SelectMany(span => Characters(span.Text, span.Color, span.FontStyle));
            Assert.Equal(expected, actual);
        }
        var unchanged = await renderer.HighlightAsync("one\r\ntwo", "no-such-language", CodeThemes.All.Single(theme => theme.Id == "github-dark"));
        Assert.Equal("one\r\ntwo", string.Concat(unchanged.Select(span => span.Text)));
    }

    [ElectronFact]
    public async Task KatexRendersAdvancedMacrosAndKeepsItsOwnErrorFallback()
    {
        await using var host = NewHost(out _);
        await using var renderer = new RichTextRenderer(host);
        var formula = await renderer.MathAsync(@"\begin{aligned}f(x)&=\underbrace{x+\cdots+x}_{n}\\A&=\begin{pmatrix}1&2\\3&4\end{pmatrix}\end{aligned}", true, 16, "#abcdef");
        Assert.False(formula.IsError);
        Assert.Contains("<math", formula.Html, StringComparison.Ordinal);
        Assert.True(formula.Image.PixelWidth > 20);
        Assert.True(formula.Height > 20);
        var broken = await renderer.MathAsync(@"\thisMacroDoesNotExist{", false, 16, "#abcdef");
        Assert.True(broken.IsError);
        Assert.Contains("katex-error", broken.Html, StringComparison.Ordinal);
        Assert.Contains("color:inherit", broken.Html, StringComparison.Ordinal);
    }
}
