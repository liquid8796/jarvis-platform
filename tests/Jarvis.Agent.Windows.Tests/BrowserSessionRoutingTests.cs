using System.Text.Json.Nodes;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class BrowserSessionRoutingTests
{
    [Fact]
    public void Page_calls_follow_the_observed_tab_even_after_another_family_was_selected()
    {
        var routing = new BrowserSessionRouting();
        routing.Observe(BrowserFamily.Dev, "browser.navigate", WireJson.Element(new { }), "Opened http://localhost:5173 in tab 42.");
        routing.Observe(BrowserFamily.Chrome, "browser.navigate", WireJson.Element(new { }), "Opened https://example.test in tab 9.");
        Assert.Equal(BrowserFamily.Dev, routing.Resolve(BrowserFamily.Auto, new JsonObject { ["tabId"] = 42 }));
        Assert.Equal(BrowserFamily.Chrome, routing.Resolve(BrowserFamily.Auto, new JsonObject { ["tabId"] = 9 }));
    }

    [Fact]
    public void Numeric_tab_collision_fails_closed_until_family_is_explicit()
    {
        var routing = new BrowserSessionRouting();
        routing.Observe(BrowserFamily.Dev, "browser.navigate", WireJson.Element(new { tabId = 7 }), "ok");
        routing.Observe(BrowserFamily.Edge, "browser.navigate", WireJson.Element(new { tabId = 7 }), "ok");
        Assert.Throws<InvalidOperationException>(() => routing.Resolve(BrowserFamily.Auto, new JsonObject { ["tabId"] = 7 }));
        Assert.Equal(BrowserFamily.Dev, routing.Resolve(BrowserFamily.Dev, new JsonObject { ["tabId"] = 7 }));
        routing.Observe(BrowserFamily.Dev, "browser.tabs_close_mcp", WireJson.Element(new { tabId = 7 }), "ok");
        Assert.Equal(BrowserFamily.Edge, routing.Resolve(BrowserFamily.Auto, new JsonObject { ["tabId"] = 7 }));
    }

    [Fact]
    public void Structured_qa_url_selects_the_dev_family_without_changing_explicit_routing()
    {
        var args = new JsonObject { ["spec"] = new JsonObject { ["url"] = "http://localhost:4173" } };
        Assert.Equal(BrowserFamily.Dev, new BrowserSessionRouting().Resolve(BrowserFamily.Auto, args));
        Assert.Equal(BrowserFamily.Edge, new BrowserSessionRouting().Resolve(BrowserFamily.Edge, args));
    }
}
