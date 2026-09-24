using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.ImageGeneration;

/// <summary>Durable, session-owned jobs. Creation is acknowledged before generation; recovery never submits a prompt.</summary>
public sealed class ImageGenerationJobs(string root, IImageGenerationBackend backend, IImageArtifactStore artifacts) : IDisposable
{
    private sealed class Running(CancellationTokenSource stop)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool ExplicitCancellation { get; set; }
    }
    private readonly SemaphoreSlim _create = new(1, 1);
    private readonly object _storageSync = new();
    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);
    private int _disposed;
    internal event Action<Exception>? WorkerFailed;
    private string ScopeDirectory(AgentExecutionContext context) => Path.Combine(root, ImageGenerationSafety.Scope(context));
    private string DirectoryFor(AgentExecutionContext context, string id)
    {
        if (!ImageGenerationSafety.IsJobId(id)) throw new ArgumentException("Invalid ImageGen job ID.");
        var path = Path.Combine(ScopeDirectory(context), id);
        ImageGenerationSafety.NoLinks(path);
        return path;
    }
    private string Key(AgentExecutionContext context, string id) => ImageGenerationSafety.Scope(context) + "/" + id;
    private void Save(AgentExecutionContext context, ImageGenerationJob job)
    {
        lock (_storageSync)
            ImageGenerationSafety.AtomicWrite(Path.Combine(DirectoryFor(context, job.JobId), "job.json"), JsonSerializer.Serialize(job, WireJson.Options));
    }
    private ImageGenerationJob Load(AgentExecutionContext context, string id)
    {
        lock (_storageSync) return LoadCore(context, id);
    }
    private ImageGenerationJob LoadCore(AgentExecutionContext context, string id)
    {
        var path = Path.Combine(DirectoryFor(context, id), "job.json");
        if (!File.Exists(path)) throw new ImageGenerationException("JOB_NOT_FOUND", "This session has no such ImageGen job.");
        if (new FileInfo(path).Length > 512 * 1024) throw new IOException("ImageGen job metadata is oversized.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(file);
        var job = JsonSerializer.Deserialize<ImageGenerationJob>(reader.ReadToEnd(), WireJson.Options)
            ?? throw new IOException("ImageGen job metadata is invalid.");
        if (job.JobId != id || job.SessionId != context.SessionId || job.OwnerId != context.OwnerId || job.DeviceId != context.AgentDeviceId)
            throw new ImageGenerationException("JOB_NOT_FOUND", "This session has no such ImageGen job.");
        return job;
    }
    public IReadOnlyList<ImageGenerationJob> List(AgentExecutionContext context)
    {
        var dir = ScopeDirectory(context);
        ImageGenerationSafety.NoLinks(dir);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir).Where(p => ImageGenerationSafety.IsJobId(Path.GetFileName(p)) && File.Exists(Path.Combine(p, "job.json"))).Take(257)
            .Select(p => Load(context, Path.GetFileName(p))).OrderByDescending(j => j.CreatedAt).ToArray();
    }
    public async Task<ImageGenerationJob> StartAsync(ImageGenRequest request, AgentExecutionContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        context.RequireSessionIdentity();
        context.SessionCancellation.ThrowIfCancellationRequested();
        // Snapshot absolute caller paths, never guess a Downloads file or another chat's image.
        var paths = request.ReferencedImagePaths.Select(p => WorkspaceDirectories.ResolvePath(p, context.Workspace)).ToArray();
        var digest = ImageGenerationSafety.Digest(JsonSerializer.Serialize(new { request.Prompt, paths, request.NumLastImagesToInclude }));
        var requestId = request.RequestId ?? context.CallId;
        await _create.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = List(context);
            var old = jobs.SingleOrDefault(j => j.RequestId == requestId);
            if (old is not null)
            {
                if (old.RequestDigest != digest) throw new ImageGenerationException("REQUEST_CONFLICT", "request_id already belongs to a different image request.");
                return old;
            }
            if (jobs.Count >= 256) throw new ImageGenerationException("SESSION_CAPACITY", "This image session has reached its 256-job capacity. Open a new Jarvis session.");
            if (_running.Count >= 16) throw new ImageGenerationException("QUEUE_FULL", "The ImageGen job queue is full.");
            if (jobs.Any(j => !j.IsTerminal || j.Status == "completion_unknown"))
                throw new ImageGenerationException("SESSION_BUSY", "Read or reconcile the previous image job before sending another prompt in this session.");
            var binding = await backend.GetBindingAsync(context, ct).ConfigureAwait(false);
            var parents = Array.Empty<string>();
            if (request.NumLastImagesToInclude is { } n)
            {
                var recent = jobs.Where(j => j.Status == "completed").OrderBy(j => j.CreatedAt).SelectMany(j => j.Artifacts).TakeLast(n).ToArray();
                if (recent.Length != n) throw new ImageGenerationException("MISSING_IMAGES", "There are not enough generated images in this Jarvis session.");
                paths = recent.Select(a => a.LocalPath).ToArray();
                parents = recent.Select(a => a.ArtifactId).ToArray();
            }
            var job = new ImageGenerationJob
            {
                JobId = "ig_" + Guid.NewGuid().ToString("N"), OwnerId = context.OwnerId!, DeviceId = context.AgentDeviceId!,
                SessionId = context.SessionId, RequestId = requestId, RequestDigest = digest, Prompt = request.Prompt, Binding = binding
            };
            var inputs = await artifacts.StageInputsAsync(paths, parents, DirectoryFor(context, job.JobId), ct).ConfigureAwait(false);
            job = job with { Inputs = inputs };
            ct.ThrowIfCancellationRequested();
            Save(context, job);
            StartWorker(job, context, false);
            return job;
        }
        finally { _create.Release(); }
    }
    private void StartWorker(ImageGenerationJob job, AgentExecutionContext context, bool recoverOnly)
    {
        var key = Key(context, job.JobId);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(context.SessionCancellation);
        stop.CancelAfter(TimeSpan.FromMinutes(20));
        var running = new Running(stop);
        if (!_running.TryAdd(key, running)) { stop.Dispose(); return; }
        IDisposable? lease = null;
        try
        {
            lease = context.RetainResources?.Invoke();
            var retained = lease;
            running.Completion = Task.Run(async () =>
            {
                try { await RunAsync(job, context, recoverOnly, stop.Token).ConfigureAwait(false); }
                catch (Exception ex) { WorkerFailed?.Invoke(ex); }
                finally { retained?.Dispose(); _running.TryRemove(key, out _); stop.Dispose(); }
            });
        }
        catch { lease?.Dispose(); _running.TryRemove(key, out _); stop.Dispose(); throw; }
    }
    private async Task RunAsync(ImageGenerationJob initial, AgentExecutionContext context, bool recoverOnly, CancellationToken ct)
    {
        var job = initial;
        void Update(string status, JsonObject? state = null, bool? attempted = null, string? code = null, string? message = null)
        {
            job = job with { Status = status, BrowserState = state is null ? job.BrowserState : (JsonObject)state.DeepClone(),
                SubmissionAttempted = attempted ?? job.SubmissionAttempted, ErrorCode = code, Message = message, UpdatedAt = DateTimeOffset.UtcNow };
            Save(context, job);
        }
        async Task<JsonObject> Call(string op)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await backend.CallAsync(op, job, context, ct).ConfigureAwait(false);
            if (reply["errorCode"]?.GetValue<string>() is { } error)
                throw new ImageGenerationException(error, reply["message"]?.GetValue<string>() ?? "The ChatGPT image request could not complete.");
            return reply;
        }
        try
        {
            if (!recoverOnly)
            {
                Update("binding_browser");
                Update("opening_chatgpt_tab", await Call("prepare").ConfigureAwait(false));
                var ready = false;
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    var state = await Call("inspect").ConfigureAwait(false);
                    Update("opening_chatgpt_tab", state);
                    if (state["ready"]?.GetValue<bool>() == true) { ready = true; break; }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                if (!ready) throw new ImageGenerationException("PAGE_NOT_READY", "ChatGPT is not ready. Check the selected browser tab.");
                if (job.Inputs.Count > 0)
                {
                    Update("uploading_inputs", await Call("upload").ConfigureAwait(false));
                    var uploaded = false;
                    for (var attempt = 0; attempt < 90; attempt++)
                    {
                        var state = await Call("upload_status").ConfigureAwait(false);
                        Update("uploading_inputs", state);
                        if (state["ready"]?.GetValue<bool>() == true) { uploaded = true; break; }
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                    }
                    if (!uploaded) throw new ImageGenerationException("UPLOAD_INCOMPLETE", "Not all reference images finished uploading. No edit prompt was sent.");
                }
                // Persist the send intent BEFORE the browser is permitted to click Send.
                Update("submitting_prompt", attempted: true);
                Update("waiting_for_generation", await Call("submit").ConfigureAwait(false));
            }
            else Update("waiting_for_generation", message: "Reconciling the original browser job; no prompt is resent.");
            JsonObject result;
            while (true)
            {
                result = await Call("poll").ConfigureAwait(false);
                Update("waiting_for_generation", result);
                if (result["ready"]?.GetValue<bool>() == true) break;
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            Update("downloading_result", await Call("download").ConfigureAwait(false));
            while (true)
            {
                result = await Call("download_status").ConfigureAwait(false);
                Update("downloading_result", result);
                if (result["ready"]?.GetValue<bool>() == true) break;
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            var output = await artifacts.ImportResultsAsync(result, job.Inputs, DirectoryFor(context, job.JobId), job.JobId, ct).ConfigureAwait(false);
            if (output.Count == 0) throw new ImageGenerationException("EMPTY_RESULT", "ChatGPT returned no downloadable image.");
            job = job with { Artifacts = output };
            Update("completed", message: "Images saved. Generation used ChatGPT Web in the selected browser.");
        }
        catch (OperationCanceledException)
        {
            // Stopping local observation does not prove server-side cancellation or quota restoration.
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await backend.CallAsync("cancel", job, context, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* Never replace the original cancellation with cleanup failure. */ }
            var explicitCancel = _running.TryGetValue(Key(context, job.JobId), out var active) && active.ExplicitCancellation;
            Update(job.SubmissionAttempted && !explicitCancel ? "completion_unknown" : "cancelled", code: "LOCAL_STOPPED",
                message: !job.SubmissionAttempted ? "Cancelled before prompt submission."
                    : explicitCancel ? "Local tracking cancelled after send. ChatGPT may still finish; its result and quota are not undone."
                    : "Local work stopped after send. ChatGPT may still finish; read with resume=true to reconcile, never resend blindly.");
        }
        catch (ImageGenerationException ex)
        {
            // Losing authentication after Send does not prove generation did not happen.
            var definite = ex.Code is "QUOTA_EXCEEDED" or "GENERATION_REJECTED";
            Update(job.SubmissionAttempted && !definite ? "completion_unknown" : "failed_" + ex.Code.ToLowerInvariant(), code: ex.Code, message: ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or JsonException or TimeoutException or UnauthorizedAccessException)
        {
            WorkerFailed?.Invoke(ex);
            Update(job.SubmissionAttempted ? "completion_unknown" : "failed_runtime", code: "RUNTIME_ERROR",
                message: "Browser connection or local storage failed. Inspect this job before retrying; no alternate browser or paid API was used.");
        }
    }
    public async Task<ToolReply> ReadAsync(string id, bool resume, bool preview, AgentExecutionContext context, CancellationToken ct)
    {
        context.RequireSessionIdentity();
        var job = Load(context, id);
        var key = Key(context, id);
        if (!_running.ContainsKey(key) && !job.IsTerminal)
        {
            // The worker may have saved a terminal result after our initial snapshot and then left
            // _running. Reload before recovery classification; never overwrite its newer terminal state.
            job = Load(context, id);
            if (!job.IsTerminal && !_running.ContainsKey(key))
            {
                job = job with { Status = job.SubmissionAttempted ? "completion_unknown" : "failed_interrupted",
                    Message = "Agent stopped before completion. No request was automatically replayed.", UpdatedAt = DateTimeOffset.UtcNow };
                Save(context, job);
            }
        }
        if (resume && job.Status == "completion_unknown")
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_running.Count >= 16) throw new ImageGenerationException("QUEUE_FULL", "The ImageGen queue is full.");
            var binding = await backend.GetBindingAsync(context, ct).ConfigureAwait(false);
            if (binding.ExtensionInstanceId != job.Binding.ExtensionInstanceId || binding.BrowserFamily != job.Binding.BrowserFamily)
                throw new ImageGenerationException("BINDING_CHANGED", "Restore this job's original selected browser before recovery.");
            // A local rebind may change the revision; only the SAME instance is eligible for recovery.
            job = job with { Binding = binding };
            Save(context, job);
            StartWorker(job, context, true);
        }
        var images = preview && job.Status == "completed" ? await artifacts.ReadPreviewsAsync(job.Artifacts, ct).ConfigureAwait(false) : null;
        return new ToolReply(Summary(job).ToJsonString(WireJson.Options), job.Status.StartsWith("failed_", StringComparison.Ordinal), images);
    }
    public ImageGenerationJob Cancel(string id, AgentExecutionContext context)
    {
        var job = Load(context, id);
        if (_running.TryGetValue(Key(context, id), out var running))
        {
            running.ExplicitCancellation = true;
            try { running.Stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        else
        {
            lock (_storageSync)
            {
                job = LoadCore(context, id); // Do not overwrite a completion that raced with Cancel.
                if (job.Status == "completion_unknown" || !job.IsTerminal)
                {
                    job = job with { Status = "cancelled", UpdatedAt = DateTimeOffset.UtcNow,
                        Message = "Local tracking explicitly cancelled. ChatGPT may already have completed; its result and quota are not undone." };
                    Save(context, job);
                }
            }
        }
        return job;
    }
    public async Task<ImageGenerationJob> CancelAsync(string id, AgentExecutionContext context, CancellationToken ct)
    {
        var notifyBrowser = !_running.ContainsKey(Key(context, id));
        var job = Cancel(id, context);
        if (notifyBrowser && job.Status != "completed")
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await backend.CallAsync("cancel", job, context, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ImageGenerationException or IOException or InvalidOperationException)
            { /* Local cancellation remains accepted; remote cancellation is never claimed. */ }
        }
        return job;
    }
    public static JsonObject Summary(ImageGenerationJob job) => new()
    {
        ["jobId"] = job.JobId, ["requestId"] = job.RequestId, ["status"] = job.Status,
        ["backend"] = job.Backend, ["message"] = job.Message, ["errorCode"] = job.ErrorCode,
        ["submissionAttempted"] = job.SubmissionAttempted, ["createdAt"] = job.CreatedAt,
        ["updatedAt"] = job.UpdatedAt, ["artifacts"] = JsonSerializer.SerializeToNode(job.Artifacts, WireJson.Options)
    };
    public void StopSession(AgentSessionIdentity identity)
    {
        var prefix = ImageGenerationSafety.Digest(identity.OwnerId + "\n" + identity.DeviceId + "\n" + identity.SessionId) + "/";
        foreach (var pair in _running.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)))
            try { pair.Value.Stop.Cancel(); } catch (ObjectDisposedException) { }
    }
    public void Pause()
    {
        foreach (var job in _running.Values) try { job.Stop.Cancel(); } catch (ObjectDisposedException) { }
    }
    public void Dispose() { Interlocked.Exchange(ref _disposed, 1); Pause(); }
}
