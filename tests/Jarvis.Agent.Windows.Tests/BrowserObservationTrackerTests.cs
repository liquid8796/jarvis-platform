using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class BrowserObservationTrackerTests
{
    [Fact]
    public void Fresh_read_page_refs_are_valid_until_a_material_mutation()
    {
        var tracker = new BrowserObservationTracker();
        var readArgs = WireJson.Element(new { tabId = 1 });
        tracker.AfterTool("browser.read_page", readArgs, "button Save [ref_1] input Name [ref_2]", success: true);

        tracker.BeforeTool("browser.form_input", WireJson.Element(new { tabId = 1, @ref = "ref_2", value = "Nam" }));
        tracker.AfterTool("browser.form_input", WireJson.Element(new { tabId = 1, @ref = "ref_2", value = "Nam" }), "ok", success: true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            tracker.BeforeTool("browser.form_input", WireJson.Element(new { tabId = 1, @ref = "ref_2", value = "Tran" })));
        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void New_observation_replaces_old_refs_after_navigation()
    {
        var tracker = new BrowserObservationTracker();
        tracker.AfterTool("browser.find", WireJson.Element(new { tabId = 1, query = "save" }), "Save [ref_old]", true);
        tracker.AfterTool("browser.navigate", WireJson.Element(new { tabId = 1, url = "https://example.test/next" }), "ok", true);
        Assert.Throws<InvalidOperationException>(() =>
            tracker.BeforeTool("browser.computer", WireJson.Element(new { tabId = 1, action = "left_click", @ref = "ref_old" })));

        tracker.AfterTool("browser.read_page", WireJson.Element(new { tabId = 1 }), "Next [ref_new]", true);
        tracker.BeforeTool("browser.computer", WireJson.Element(new { tabId = 1, action = "left_click", @ref = "ref_new" }));
        Assert.Throws<InvalidOperationException>(() =>
            tracker.BeforeTool("browser.computer", WireJson.Element(new { tabId = 1, action = "left_click", @ref = "ref_old" })));
    }

    [Fact]
    public void Failed_mutation_does_not_invalidate_current_observation()
    {
        var tracker = new BrowserObservationTracker();
        tracker.AfterTool("browser.read_page", WireJson.Element(new { tabId = 1 }), "Save [ref_1]", true);
        var click = WireJson.Element(new { tabId = 1, action = "left_click", @ref = "ref_1" });

        tracker.BeforeTool("browser.computer", click);
        tracker.AfterTool("browser.computer", click, "permission denied", success: false);
        tracker.BeforeTool("browser.computer", click);
    }

    [Fact]
    public void Read_page_subtree_ref_must_come_from_current_generation()
    {
        var tracker = new BrowserObservationTracker();
        tracker.AfterTool("browser.read_page", WireJson.Element(new { tabId = 1 }), "dialog [ref_dialog]", true);
        tracker.BeforeTool("browser.read_page", WireJson.Element(new { tabId = 1, ref_id = "ref_dialog" }));
        tracker.AfterTool("browser.resize_window", WireJson.Element(new { tabId = 1, width = 390, height = 844 }), "ok", true);

        Assert.Throws<InvalidOperationException>(() =>
            tracker.BeforeTool("browser.read_page", WireJson.Element(new { tabId = 1, ref_id = "ref_dialog" })));
    }
}
