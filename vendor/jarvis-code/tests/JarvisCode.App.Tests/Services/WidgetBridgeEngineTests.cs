using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed partial class ElectronPaneHostTests
{
    [ElectronFact]
    public async Task WidgetBridgeKeepsBinaryFilesAndConnectorActionsBehindItsIsolatedHostWorld()
    {
        using var deadline = Deadline();
        var token = deadline.Token;
        await using var host = NewHost(out _);
        await host.RequestAsync("host.create", new JsonObject { ["show"] = true, ["x"] = -10000, ["y"] = -10000 }, token);
        var tab = (await host.RequestAsync("tab.create", null, token))["tabId"]!.GetValue<string>();
        await Cdp(host, tab, "Runtime.enable", token);
        var wrapper = VisualizeWidgetPage.WrapperHtml(false);
        await host.RequestAsync("tab.navigate", new JsonObject
        {
            ["tabId"] = tab, ["url"] = "data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapper)),
        }, token);
        var tree = await Cdp(host, tab, "Page.getFrameTree", token);
        var rootFrame = tree["frameTree"]!["frame"]!["id"]!.GetValue<string>();
        var world = await Cdp(host, tab, "Page.createIsolatedWorld", token, new JsonObject
        {
            ["frameId"] = rootFrame, ["worldName"] = "jarvis-widget-host",
        });
        var context = world["executionContextId"]!.GetValue<int>();
        var ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attached = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicked = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.EventReceived += evt =>
        {
            if (evt.Name != "cdp" || evt.Body["method"]?.GetValue<string>() != "Runtime.bindingCalled") return;
            var body = evt.Body["params"];
            if (body?["executionContextId"]?.GetValue<int>() != context) return;
            var message = JsonNode.Parse(body["payload"]!.GetValue<string>()) as JsonObject;
            if (message?["method"]?.GetValue<string>() == "widget-test-ready") ready.TrySetResult(message);
            if (message?["method"]?.GetValue<string>() == "widget-test-clicked") clicked.TrySetResult(message);
            if (message?["type"]?.GetValue<string>() == "anthropic:attach-files") attached.TrySetResult(message);
            if (message?["type"]?.GetValue<string>() == "anthropic:connect-connector") connected.TrySetResult(message);
            if (message?["type"]?.GetValue<string>() == "anthropic:elicit-submit") submitted.TrySetResult(message);
        };
        await Cdp(host, tab, "Runtime.addBinding", token, new JsonObject
        {
            ["name"] = VisualizeWidgetPage.DefaultPostBinding, ["executionContextId"] = context,
        });
        await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = VisualizeWidgetPage.WrapperBootstrapScript(), ["contextId"] = context,
        });
        const string resource = """
            <html><body><button id="send" onclick="parent.postMessage({method:'widget-test-clicked',params:{active:navigator.userActivation.isActive}},'*');parent.postMessage({type:'anthropic:attach-files',files:[new File([new Uint8Array([0,1,2,127,128,255])],'bytes.bin',{type:'application/octet-stream'})]},'*');parent.postMessage({type:'anthropic:elicit-submit',text:'Form answers',files:[new File(['form-file'],'form.txt')]},'*');parent.postMessage({type:'anthropic:connect-connector',connectorId:'sample-connector'},'*')">Attach</button><script>
            parent.postMessage({type:'anthropic:attach-files',__userActivated:true,files:[new File(['not selected'],'forged.txt')]},'*');
            parent.postMessage({type:'anthropic:connect-connector',__userActivated:true,connectorId:'not-selected'},'*');
            parent.postMessage({method:'ui/notifications/size-changed',params:{height:120}},'*');
            parent.postMessage({method:'widget-test-ready',params:{binding:typeof window.__jarvisWidgetPost}},'*');
            </script></body></html>
            """;
        var resourceMessage = new JsonObject { ["__host"] = "resource", ["html"] = resource }.ToJsonString();
        await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = VisualizeWidgetPage.HostFunction + "(" + JsonSerializer.Serialize(resourceMessage) + ")",
            ["contextId"] = context,
        });
        Assert.Equal("undefined", (await ready.Task.WaitAsync(token))["params"]?["binding"]?.GetValue<string>());
        var binding = await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = "typeof window.__jarvisWidgetPost", ["returnByValue"] = true,
        });
        Assert.Equal("undefined", binding["result"]?["value"]?.GetValue<string>());
        Assert.False(attached.Task.IsCompleted);
        Assert.False(connected.Task.IsCompleted);

        var layout = await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = "JSON.stringify({width:innerWidth,height:innerHeight,hit:document.elementFromPoint(25,18)?.tagName,frameHeight:document.querySelector('iframe').getBoundingClientRect().height})",
            ["returnByValue"] = true,
        });
        Assert.Contains("\"hit\":\"IFRAME\"", layout["result"]!["value"]!.GetValue<string>(), StringComparison.Ordinal);
        await Cdp(host, tab, "Page.captureScreenshot", token, new JsonObject { ["format"] = "png" });

        // CDP trusted input supplies the same user-activation signal a real
        // click does; scripts cannot supply it as a field in their message.
        await Cdp(host, tab, "Input.dispatchMouseEvent", token, new JsonObject
        {
            ["type"] = "mouseMoved", ["x"] = 25, ["y"] = 18,
        });
        await Cdp(host, tab, "Input.dispatchMouseEvent", token, new JsonObject
        {
            ["type"] = "mousePressed", ["x"] = 25, ["y"] = 18, ["button"] = "left", ["clickCount"] = 1,
        });
        await Cdp(host, tab, "Input.dispatchMouseEvent", token, new JsonObject
        {
            ["type"] = "mouseReleased", ["x"] = 25, ["y"] = 18, ["button"] = "left", ["clickCount"] = 1,
        });
        Assert.True((await clicked.Task.WaitAsync(TimeSpan.FromSeconds(10), token))["params"]!["active"]!.GetValue<bool>());
        var message = await attached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.True(message["__userActivated"]!.GetValue<bool>());
        var file = Assert.Single((message["files"] as JsonArray)!);
        Assert.Equal("bytes.bin", file!["name"]!.GetValue<string>());
        Assert.Equal(new byte[] { 0, 1, 2, 127, 128, 255 }, Convert.FromBase64String(file["data"]!.GetValue<string>()));
        Assert.Equal("sample-connector", (await connected.Task.WaitAsync(token))["connectorId"]?.GetValue<string>());
        var form = await submitted.Task.WaitAsync(token);
        Assert.Equal("Form answers", form["text"]?.GetValue<string>());
        Assert.Equal("form-file", Encoding.UTF8.GetString(Convert.FromBase64String(form["files"]![0]!["data"]!.GetValue<string>())));
    }
}
