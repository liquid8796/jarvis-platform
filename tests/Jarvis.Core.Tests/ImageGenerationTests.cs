using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Agent.Core.ImageGeneration;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ImageGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-imagegen-tests-" + Guid.NewGuid().ToString("N"));
    private static AgentExecutionContext Context(string? session = null, string? call = null) => new(Path.GetTempPath(), call ?? Guid.NewGuid().ToString("N"), session ?? "js_" + new string('a', 32))
    { OwnerId = "owner", AgentDeviceId = "device" };
    private static ImageGenRequest Request(string id = "request") => ImageGenRequest.Parse(WireJson.Element(new { prompt = "Create an icon", request_id = id }));
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"prompt\":\" \"}")]
    [InlineData("{\"prompt\":\"x\",\"model\":\"api\"}")]
    [InlineData("{\"prompt\":\"x\",\"referenced_image_paths\":[]}")]
    [InlineData("{\"prompt\":\"x\",\"referenced_image_paths\":[\"a\"],\"num_last_images_to_include\":1}")]
    [InlineData("{\"prompt\":\"x\",\"num_last_images_to_include\":0}")]
    [InlineData("{\"prompt\":\"x\",\"num_last_images_to_include\":6}")]
    [InlineData("{\"prompt\":\"x\",\"request_id\":\"\"}")]
    public void InvalidRequestsFailBeforeAnyBackendWork(string json) =>
        Assert.ThrowsAny<ArgumentException>(() => ImageGenRequest.Parse(JsonSerializer.Deserialize<JsonElement>(json)));

    [Theory]
    [InlineData(1)] [InlineData(5)]
    public void RecentWindowAcceptsOnlySupportedBounds(int count) => Assert.Equal(count,
        ImageGenRequest.Parse(WireJson.Element(new { prompt = "Edit", num_last_images_to_include = count })).NumLastImagesToInclude);

    [Fact]
    public async Task GenerateRunsOneSubmitAndReturnsDurableArtifacts()
    {
        var backend = new Backend(); var store = new Artifacts(); using var jobs = new ImageGenerationJobs(_root, backend, store);
        var context = Context();
        var job = await jobs.StartAsync(Request(), context, default);
        Assert.Equal("queued", job.Status);
        var result = await Wait(jobs, context, job.JobId, "completed");
        Assert.Equal(1, backend.Count("submit"));
        Assert.DoesNotContain("upload", backend.Calls);
        Assert.Single(result["artifacts"]!.AsArray());
        Assert.DoesNotContain("Create an icon", result.ToJsonString());
        using var reopened = new ImageGenerationJobs(_root, backend, store);
        Assert.Equal("completed", ReadJson(await reopened.ReadAsync(job.JobId, false, false, context, default))["status"]!.GetValue<string>());
    }
    [Fact]
    public async Task IdenticalIdempotencyKeyReturnsOriginalAndConflictingPromptFails()
    {
        var backend = new Backend(); using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts()); var context = Context();
        var job = await jobs.StartAsync(Request(), context, default);
        var second = await jobs.StartAsync(Request(), context with { CallId = "other-call" }, default);
        Assert.Equal(job.JobId, second.JobId);
        await Assert.ThrowsAsync<ImageGenerationException>(() => jobs.StartAsync(Request() with { Prompt = "Different image" }, context, default));
        await Wait(jobs, context, job.JobId, "completed"); Assert.Equal(1, backend.Count("submit"));
    }
    [Fact]
    public async Task ExplicitSessionAndOwnershipAreRequired()
    {
        var backend = new Backend(); using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts());
        await Assert.ThrowsAsync<AgentRequestException>(() => jobs.StartAsync(Request(), Context("call_" + Guid.NewGuid().ToString("N")), default));
        var context = Context(); var job = await jobs.StartAsync(Request(), context, default); await Wait(jobs, context, job.JobId, "completed");
        await Assert.ThrowsAsync<ImageGenerationException>(() => jobs.ReadAsync(job.JobId, false, true, Context("js_" + new string('b', 32)), default));
        await Assert.ThrowsAsync<ImageGenerationException>(() => jobs.ReadAsync(job.JobId, false, true, context with { OwnerId = "other-owner" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => jobs.ReadAsync("../../job", false, false, context, default));
    }
    [Fact]
    public async Task MissingRecentImagesNeverCallPrepareOrSubmit()
    {
        var backend = new Backend(); using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts());
        await Assert.ThrowsAsync<ImageGenerationException>(() => jobs.StartAsync(Request() with { NumLastImagesToInclude = 1 }, Context(), default));
        Assert.Empty(backend.Calls);
    }
    [Fact]
    public async Task RecentEditUsesOnlyItsOwnCompletedArtifacts()
    {
        var backend = new Backend(); var store = new Artifacts(); using var jobs = new ImageGenerationJobs(_root, backend, store); var context = Context();
        var first = await jobs.StartAsync(Request(), context, default); await Wait(jobs, context, first.JobId, "completed");
        var latest = jobs.List(context).Single().Artifacts.Single();
        var edit = await jobs.StartAsync(Request("edit") with { NumLastImagesToInclude = 1 }, context, default); await Wait(jobs, context, edit.JobId, "completed");
        Assert.Equal(latest.LocalPath, store.LastPaths.Single());
        Assert.Equal(latest.ArtifactId, store.LastParents.Single());
        Assert.Equal(1, backend.Count("upload"));
        await Assert.ThrowsAsync<ImageGenerationException>(() => jobs.StartAsync(Request("foreign-edit") with { NumLastImagesToInclude = 1 }, Context("js_" + new string('b', 32)), default));
    }
    [Fact]
    public async Task IncompleteUploadNeverSendsPrompt()
    {
        var backend = new Backend { FailOperation = "upload_status", Failure = "UPLOAD_INCOMPLETE" };
        using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts()); var context = Context();
        var request = Request() with { ReferencedImagePaths = ["source.png"] };
        var job = await jobs.StartAsync(request, context, default);
        await Wait(jobs, context, job.JobId, "failed_upload_incomplete"); Assert.Equal(0, backend.Count("submit"));
    }
    [Fact]
    public async Task LostSubmitAcknowledgementIsNotReplayedOnResume()
    {
        var backend = new Backend { FailOperation = "submit", Failure = "CONNECTION_LOST" };
        using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts()); var context = Context();
        Exception? workerFailure = null; jobs.WorkerFailed += ex => workerFailure = ex;
        var job = await jobs.StartAsync(Request(), context, default);
        try { await Wait(jobs, context, job.JobId, "completion_unknown"); }
        catch (Exception ex) { throw new Xunit.Sdk.XunitException(ex.Message + "\nCalls: " + string.Join(",", backend.Calls) + "\nWorker: " + workerFailure); }
        await Assert.ThrowsAsync<ImageGenerationException>(() => jobs.StartAsync(Request("new-request"), context, default));
        backend.FailOperation = null;
        await jobs.ReadAsync(job.JobId, true, false, context, default);
        await Wait(jobs, context, job.JobId, "completed");
        Assert.Equal(1, backend.Count("submit")); Assert.Equal(1, backend.Count("prepare"));
    }
    [Fact]
    public async Task RecoveryRejectsDifferentBrowserButAllowsExplicitSameInstanceRebind()
    {
        var backend = new Backend { FailOperation = "submit", Failure = "CONNECTION_LOST" };
        using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts()); var context = Context();
        var binding = backend.Binding;
        var job = await jobs.StartAsync(Request(), context, default); await Wait(jobs, context, job.JobId, "completion_unknown");
        backend.Binding = binding with { ExtensionInstanceId = Guid.NewGuid().ToString() };
        await Assert.ThrowsAsync<ImageGenerationException>(() => jobs.ReadAsync(job.JobId, true, false, context, default));
        backend.Binding = binding with { Revision = Guid.NewGuid().ToString("N") }; backend.FailOperation = null;
        await jobs.ReadAsync(job.JobId, true, false, context, default); await Wait(jobs, context, job.JobId, "completed");
        Assert.Equal(1, backend.Count("submit"));
    }
    [Fact]
    public async Task CancelBeforeSendCancelsOnlyThatJobAndReleasesRetainedResources()
    {
        var backend = new Backend { BlockOperation = "inspect" }; var context = Context(); var released = 0;
        context = context with { RetainResources = () => new Lease(() => Interlocked.Increment(ref released)) };
        using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts());
        var job = await jobs.StartAsync(Request(), context, default); await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        jobs.Cancel(job.JobId, context); await Wait(jobs, context, job.JobId, "cancelled");
        for (var i = 0; i < 100 && released == 0; i++) await Task.Delay(10);
        Assert.Equal(1, released); Assert.Equal(0, backend.Count("submit"));
    }
    [Fact]
    public async Task PauseAfterSendPreservesUncertaintyRatherThanClaimingRefund()
    {
        var backend = new Backend { BlockOperation = "poll" }; var context = Context();
        using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts());
        var job = await jobs.StartAsync(Request(), context, default); await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        jobs.Pause(); var final = await Wait(jobs, context, job.JobId, "completion_unknown");
        Assert.True(final["submissionAttempted"]!.GetValue<bool>());
        Assert.Contains("may still finish", final["message"]!.GetValue<string>());
        jobs.Cancel(job.JobId, context);
        await Wait(jobs, context, job.JobId, "cancelled");
    }
    [Theory]
    [InlineData("NOT_SIGNED_IN")]
    [InlineData("CHALLENGE")]
    public async Task AuthenticationLostAfterSendRemainsUncertainAndCanBeExplicitlyCancelled(string code)
    {
        var backend = new Backend { FailOperation = "poll", Failure = code };
        using var jobs = new ImageGenerationJobs(_root, backend, new Artifacts()); var context = Context();
        var job = await jobs.StartAsync(Request(), context, default); await Wait(jobs, context, job.JobId, "completion_unknown");
        await jobs.CancelAsync(job.JobId, context, default); await Wait(jobs, context, job.JobId, "cancelled");
        Assert.Equal(1, backend.Count("submit")); Assert.Equal(1, backend.Count("cancel"));
    }

    [Fact]
    public void FourToolsAreSensitiveAndReadCancelDoNotDeadlockBehindGeneration()
    {
        var context = Context(); var empty = WireJson.Element(new { });
        foreach (var name in new[] { "imagegen", "read", "cancel", "get_state" })
        { var descriptor = ImageGenerationToolSet.Describe(name); Assert.True(descriptor.Sensitive); Assert.Equal("image_gen__" + name, descriptor.Name); }
        Assert.Contains("browser|" + context.SessionId, ToolExecutionResources.For(ImageGenerationToolSet.Describe("imagegen"), empty, context).Resources);
        Assert.Empty(ToolExecutionResources.For(ImageGenerationToolSet.Describe("cancel"), empty, context).Resources);
        Assert.Empty(ToolExecutionResources.For(ImageGenerationToolSet.Describe("read"), empty, context).Resources);
        Assert.NotEmpty(ToolExecutionResources.For(ImageGenerationToolSet.Describe("read"), WireJson.Element(new { resume = true }), context).Resources);
    }
    private static JsonObject ReadJson(ToolReply reply) => JsonNode.Parse(reply.Text)!.AsObject();
    private static async Task<JsonObject> Wait(ImageGenerationJobs jobs, AgentExecutionContext context, string id, string expected)
    {
        JsonObject result = new();
        for (var i = 0; i < 300; i++)
        {
            result = ReadJson(await jobs.ReadAsync(id, false, false, context, default));
            if (result["status"]?.GetValue<string>() == expected) { await Task.Delay(20); return result; }
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException("Expected " + expected + "; actual " + result.ToJsonString());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Lease(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class Backend : IImageGenerationBackend
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public string? FailOperation { get; set; }
        public string Failure { get; set; } = "FAILURE";
        public string? BlockOperation { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ImageBrowserBinding Binding { get; set; } = new(Guid.NewGuid().ToString(), "chrome", Guid.NewGuid().ToString("N"));
        public int Count(string operation) => Calls.Count(c => c == operation);
        public Task<ImageBrowserBinding> GetBindingAsync(AgentExecutionContext context, CancellationToken ct) => Task.FromResult(Binding);
        public Task<JsonObject> GetStateAsync(AgentExecutionContext context, CancellationToken ct) => Task.FromResult(new JsonObject { ["connected"] = true });
        public async Task<JsonObject> CallAsync(string operation, ImageGenerationJob job, AgentExecutionContext context, CancellationToken ct)
        {
            Calls.Enqueue(operation);
            if (operation == BlockOperation) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            if (operation == FailOperation) return new JsonObject { ["errorCode"] = Failure, ["message"] = "Synthetic failure" };
            return new JsonObject { ["ready"] = true, ["downloads"] = new JsonArray() };
        }
    }
    private sealed class Artifacts : IImageArtifactStore
    {
        public IReadOnlyList<string> LastPaths { get; private set; } = [];
        public IReadOnlyList<string> LastParents { get; private set; } = [];
        public Task<IReadOnlyList<ImageArtifact>> StageInputsAsync(IReadOnlyList<string> paths, IReadOnlyList<string> parents, string directory, CancellationToken ct)
        {
            LastPaths = paths; LastParents = parents;
            return Task.FromResult<IReadOnlyList<ImageArtifact>>(paths.Select((p, i) => new ImageArtifact("input-" + i, p, "image/png", 8, 8, 100, "hash", null, parents)).ToArray());
        }
        public Task<IReadOnlyList<ImageArtifact>> ImportResultsAsync(JsonObject result, IReadOnlyList<ImageArtifact> parents, string directory, string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImageArtifact>>([new("artifact-" + id, Path.Combine(directory, "result.png"), "image/png", 8, 8, 100, "hash", null, parents.Select(p => p.ArtifactId).ToArray())]);
        public Task<IReadOnlyList<WireImage>> ReadPreviewsAsync(IReadOnlyList<ImageArtifact> artifacts, CancellationToken ct) => Task.FromResult<IReadOnlyList<WireImage>>([]);
    }
}
