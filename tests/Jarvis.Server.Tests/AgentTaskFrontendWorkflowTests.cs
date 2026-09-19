using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Server.Tests;

public sealed partial class AgentTaskMcpTests
{
    [Fact]
    public async Task OAuth_frontend_workflow_delivers_images_accepts_bound_review_and_completes_without_replaying_work()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var browser = new QaImageFixtureTool();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, browser);
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var spec = new FrontendQaSpec
        {
            Url = "http://localhost:4173/", RequireVisualReview = true,
            Viewports = [new() { Name = "desktop", Width = 1440, Height = 900 }, new() { Name = "mobile", Width = 390, Height = 844, Mobile = true }],
            Steps = [new() { Action = "click", Locator = new() { TestId = "open" }, Expect = new() { Kind = "visible", Locator = new() { TestId = "dialog" } } }]
        };
        var created = await CallAsync(client, "agent_task_create", new { goal = "Verify existing frontend layout", verificationSpec = spec });
        var createData = AssertOutputMatches(listed, "agent_task_create", created);
        var id = createData.GetProperty("task").GetProperty("taskId").GetString()!;
        RemoteTaskSnapshot? snapshot = null;
        for (var i = 0; i < 150; i++)
        {
            var response = await CallAsync(client, "agent_task_get", new { taskId = id });
            snapshot = AssertOutputMatches(listed, "agent_task_get", response).GetProperty("task").Deserialize<RemoteTaskSnapshot>(WireJson.Options);
            if (snapshot!.Status == "NEEDS_REVIEW") break;
            await Task.Delay(20);
        }
        Assert.Equal("NEEDS_REVIEW", snapshot!.Status);
        Assert.Equal(2, snapshot.Verification!.Captures.Count);
        foreach (var capture in snapshot.Verification.Captures)
        {
            var response = await CallAsync(client, "agent_task_capture", new { taskId = id, captureId = capture.CaptureId });
            var structured = AssertOutputMatches(listed, "agent_task_capture", response);
            var blocks = response.GetProperty("content").EnumerateArray().ToArray();
            var image = Assert.Single(blocks, block => block.GetProperty("type").GetString() == "image");
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
            Assert.Equal(capture.Sha256, Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(image.GetProperty("data").GetString()!))));
            Assert.DoesNotContain(image.GetProperty("data").GetString()!, structured.GetRawText());
        }
        var review = new FrontendQaVisualReview
        {
            VerificationRunId = snapshot.Verification.VerificationRunId!, SourceRevision = snapshot.Verification.SourceRevision!,
            Reviewer = "fixture external reviewer", Summary = "Reviewed each delivered viewport PNG.",
            Checks = snapshot.Verification.Captures.SelectMany(capture => FrontendQaRules.ReviewCategories.Select(category => new FrontendQaReviewCheck
            { CaptureId = capture.CaptureId, ScreenshotSha256 = capture.Sha256, Category = category, Passed = true, Observation = "Observed " + category + " in the delivered image." })).ToArray()
        };
        var reviewed = await CallAsync(client, "agent_task_review", new { taskId = id, attemptId = Guid.NewGuid().ToString("N"), visualReview = review });
        Assert.Equal("READY_TO_COMPLETE", AssertOutputMatches(listed, "agent_task_review", reviewed).GetProperty("task").GetProperty("status").GetString());
        var attempt = Guid.NewGuid().ToString("N");
        var completed = await CallAsync(client, "agent_task_complete", new { taskId = id, attemptId = attempt });
        var completedData = AssertOutputMatches(listed, "agent_task_complete", completed);
        Assert.Equal("COMPLETED", completedData.GetProperty("task").GetProperty("status").GetString());
        Assert.True(completedData.GetProperty("task").GetProperty("verification").GetProperty("passed").GetBoolean());
        var duplicate = await CallAsync(client, "agent_task_complete", new { taskId = id, attemptId = attempt });
        Assert.Equal(completedData.GetRawText(), AssertOutputMatches(listed, "agent_task_complete", duplicate).GetRawText());
        Assert.Equal(1, browser.Calls);
    }

    private sealed class QaImageFixtureTool : IAgentTool
    {
        public int Calls { get; private set; }
        public ToolDescriptor Descriptor { get; } = new("browser.qa", "browser__qa", "browser", "Synthetic browser QA result for transport testing.",
            WireJson.Element(new { type = "object", additionalProperties = false, required = new[] { "spec" },
                properties = new { spec = FrontendQaSchemas.Spec(), browserFamily = new { type = "string" } } }), false);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            var spec = arguments.GetProperty("spec").Deserialize<FrontendQaSpec>(WireJson.Options)!;
            var directory = Path.Combine(context.Workspace, "artifacts", "transport-qa");
            Directory.CreateDirectory(directory);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aR1sAAAAASUVORK5CYII=");
            var snapshots = spec.Viewports.Select(viewport =>
            {
                var path = Path.Combine(directory, viewport.Name + ".png");
                File.WriteAllBytes(path, png);
                return new { name = viewport.Name, url = spec.Url, title = "Fixture", width = viewport.Width, height = viewport.Height,
                    observedWidth = viewport.Width, observedHeight = viewport.Height, artifactPath = path, screenshotSha256 = Convert.ToHexString(SHA256.HashData(png)),
                    identityPassed = true, domPresent = true, frameworkOverlay = false, consoleErrors = Array.Empty<string>(), consoleWarnings = Array.Empty<string>(),
                    networkFailures = Array.Empty<string>(), overflow = new { horizontal = false, clipped = false },
                    steps = spec.Steps.Select((step, index) => new { index, action = step.Action, passed = true }).ToArray(), passed = true };
            }).ToArray();
            return Task.FromResult(new ToolReply(JsonSerializer.Serialize(new { schemaVersion = 1, passed = true, cleanedUp = true, snapshots, errors = Array.Empty<string>() }, WireJson.Options)));
        }
    }
}
