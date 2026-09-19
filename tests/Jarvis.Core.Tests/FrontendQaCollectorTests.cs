using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Agent.Core.RemoteTasks;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

/// <summary>Collector contract tests. Real rendering acceptance is scripts/test-browser-live.cjs.</summary>
public sealed class FrontendQaCollectorTests
{
    [Fact]
    public async Task Normal_frontend_execution_requires_an_explicit_qa_spec()
    {
        using var fixture = new Fixture();
        var result = await fixture.RunAsync(new() { Goal = "Fix form behavior", Steps = [Step()] });
        Assert.Equal("NEEDS_VERIFICATION", result.Status);
        Assert.False(result.Verification!.Passed);
        Assert.Equal("not_run", result.Verification.State);
        Assert.Equal(0, fixture.BrowserCalls);
    }

    [Fact]
    public async Task Read_only_frontend_task_never_mutates_browser_or_claims_verified()
    {
        using var fixture = new Fixture();
        var result = await fixture.RunAsync(new() { Goal = "Inspect form behavior", ExecutionMode = "READ_ONLY", Steps = [Step()] });
        Assert.Equal("COMPLETED", result.Status);
        Assert.Equal("not_run", result.Verification!.State);
        Assert.False(result.Verification.Passed);
        Assert.Equal(0, fixture.BrowserCalls);
    }

    [Fact]
    public async Task Actual_source_changes_classify_frontend_without_an_english_goal()
    {
        using var fixture = new Fixture { EditDuringStep = true };
        var result = await fixture.RunAsync(new() { Goal = "Sửa theo yêu cầu", Steps = [Step()] });
        Assert.Equal("NEEDS_VERIFICATION", result.Status);
        Assert.True(result.Verification!.Required);
    }

    [Fact]
    public async Task Functional_qa_can_pass_only_after_measured_scenario_and_real_artifact_validation()
    {
        using var fixture = new Fixture();
        var result = await fixture.RunAsync(Plan());
        Assert.Equal("COMPLETED", result.Status);
        Assert.True(result.Verification!.Passed);
        Assert.Equal(2, result.Verification.Captures.Count);
        Assert.All(result.Verification.Evidence, item => Assert.Equal("collector", item.Provenance!.Producer));
    }

    [Fact]
    public async Task Visual_completion_waits_for_external_image_review()
    {
        using var fixture = new Fixture();
        var plan = Plan();
        var result = await fixture.RunAsync(plan with { VerificationSpec = plan.VerificationSpec! with { RequireVisualReview = true } });
        Assert.Equal("NEEDS_REVIEW", result.Status);
        Assert.False(result.Verification!.Passed);
        Assert.Equal(new[] { "VisualFidelity" }, result.Verification.Missing);
    }

    [Theory]
    [InlineData("console", "ConsoleHealth")]
    [InlineData("network", "NetworkHealth")]
    [InlineData("interaction", "Interaction")]
    [InlineData("clipping", "Overflow")]
    [InlineData("artifact", "Screenshot")]
    [InlineData("identity", "TargetIdentity")]
    public async Task Observed_failures_block_completion(string fault, string kind)
    {
        using var fixture = new Fixture { Fault = fault };
        var result = await fixture.RunAsync(Plan());
        Assert.Equal("NEEDS_REPAIR", result.Status);
        Assert.Contains(kind, result.Verification!.Failed);
    }

    [Fact]
    public async Task Editing_source_during_capture_invalidates_the_entire_run()
    {
        using var fixture = new Fixture { Fault = "source-change" };
        var result = await fixture.RunAsync(Plan());
        Assert.Equal("NEEDS_VERIFICATION", result.Status);
        Assert.Equal("stale", result.Verification!.State);
        Assert.False(result.Verification.Passed);
    }

    private static RemoteTaskStep Step() => new() { Id = "inspect", ToolId = "test.inspect" };
    private static RemoteTaskPlan Plan() => new()
    {
        Goal = "Fix form behavior", Steps = [Step()], VerificationSpec = new()
        {
            Url = "http://localhost:43123/", RequireVisualReview = false,
            Viewports = [new() { Name = "desktop", Width = 1440, Height = 900 }, new() { Name = "mobile", Width = 390, Height = 844 }],
            Steps = [new() { Action = "click", Locator = new() { TestId = "save" },
                Expect = new() { Kind = "text", Locator = new() { TestId = "status" }, Value = WireJson.Element("Saved") } }]
        }
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-qa-collector-" + Guid.NewGuid().ToString("N"));
        private readonly string _workspace;
        private readonly string _capture;
        public string? Fault { get; init; }
        public bool EditDuringStep { get; init; }
        public int BrowserCalls { get; private set; }
        public Fixture()
        {
            _workspace = Path.Combine(_root, "project");
            Directory.CreateDirectory(_workspace);
            File.WriteAllText(Path.Combine(_workspace, "app.js"), "const version = 1;");
            _capture = Path.Combine(_root, "capture.png");
            File.WriteAllBytes(_capture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6ioAAAAASUVORK5CYII="));
        }
        public async Task<RemoteTaskSnapshot> RunAsync(RemoteTaskPlan plan)
        {
            await using var host = new RemoteTaskHost(Path.Combine(_root, "tasks"), new WorkspaceDirectories(_workspace),
                new DynamicToolRegistry([new StubTool("test.inspect", true), new StubTool("browser.qa", false)]), () => true,
                Invoke, (_, _) => Task.CompletedTask);
            var id = Guid.NewGuid().ToString("N");
            var created = await host.HandleAsync("create", new("owner", id, plan), CancellationToken.None);
            Assert.Null(created.Error);
            for (var i = 0; i < 160; i++)
            {
                var current = (await host.HandleAsync("get", new("owner", id), CancellationToken.None)).Task!;
                if (current.Status is "COMPLETED" or "FAILED" or "NEEDS_VERIFICATION" or "NEEDS_REVIEW" or "NEEDS_REPAIR") return current;
                await Task.Delay(25);
            }
            throw new TimeoutException("Collector did not settle.");
        }
        private Task<ToolReply> Invoke(string id, JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            if (id != "browser.qa")
            {
                if (EditDuringStep) File.WriteAllText(Path.Combine(_workspace, "panel.tsx"), "export const Panel = () => <button>Save</button>;");
                return Task.FromResult(new ToolReply("Step succeeded."));
            }
            BrowserCalls++;
            if (Fault == "source-change") File.AppendAllText(Path.Combine(_workspace, "app.js"), "const later = true;");
            var spec = args.GetProperty("spec").Deserialize<FrontendQaSpec>(WireJson.Options)!;
            var snapshots = spec.Viewports.Select(v => new
            {
                name = v.Name, url = spec.Url, identityPassed = Fault != "identity", domPresent = true,
                observedWidth = v.Width, observedHeight = v.Height,
                artifactPath = _capture, screenshotSha256 = Fault == "artifact" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_capture))),
                frameworkOverlay = false, consoleErrors = Fault == "console" ? new[] { "boom" } : [],
                consoleWarnings = Array.Empty<string>(),
                networkFailures = Fault == "network" ? new[] { "500 /api/save" } : [],
                overflow = new { horizontal = false, clipped = Fault == "clipping" ? new[] { "save" } : [] },
                steps = spec.Steps.Select((s, i) => new { index = i, action = s.Action, passed = Fault != "interaction" }).ToArray()
            }).ToArray();
            return Task.FromResult(new ToolReply(JsonSerializer.Serialize(new { schemaVersion = 1, passed = true, cleanedUp = true, snapshots, errors = Array.Empty<string>() }, WireJson.Options)));
        }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class StubTool(string id, bool readOnly) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(id, id.Replace('.', '_'), "test", "Fixture", WireJson.Element(new { type = "object" }), readOnly);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
