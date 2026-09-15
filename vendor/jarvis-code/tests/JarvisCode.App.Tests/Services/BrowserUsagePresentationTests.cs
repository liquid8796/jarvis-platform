using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.Tests.Services;

public sealed class BrowserUsagePresentationTests
{
    [Fact]
    public void Estimated_context_is_labeled_and_never_shown_as_account_billing()
    {
        var snapshot = new ContextSnapshot(128000, 128000, AutoCompactWindowSource.ModelDefault, 24000, 104000, [])
        { ModelName = "Browser model", IsEstimated = true };
        var report = ContextReport.Render(snapshot);
        Assert.Contains("Token counts are estimates", report);
        Assert.Contains("context limit is configured locally", report);
        Assert.DoesNotContain("Token counts are estimates", ContextReport.Render(snapshot with { IsEstimated = false }));
        Assert.Null(CostCalculator.Estimate(new ModelInfo("chatgpt-web", "model", "Model", 128000, 2, 8),
            new Usage(1000, 200) { IsEstimated = true }));
    }
}
