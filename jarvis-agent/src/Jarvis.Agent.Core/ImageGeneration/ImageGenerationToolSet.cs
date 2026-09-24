using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.ImageGeneration;

public sealed class ImageGenerationToolSet : IDisposable
{
    private readonly IImageGenerationBackend _backend;
    public ImageGenerationJobs Jobs { get; }
    public IReadOnlyList<IAgentTool> Tools { get; }
    public ImageGenerationToolSet(string root, IImageGenerationBackend backend, IImageArtifactStore artifacts)
    {
        _backend = backend;
        Jobs = new ImageGenerationJobs(root, backend, artifacts);
        Tools = new[] { "imagegen", "read", "cancel", "get_state" }.Select(name => (IAgentTool)new Tool(this, name)).ToArray();
    }
    private sealed class Tool(ImageGenerationToolSet owner, string name) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = Describe(name);
        public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken ct)
        {
            try
            {
                context.RequireSessionIdentity();
                context.SessionCancellation.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                switch (name)
                {
                    case "imagegen":
                        var job = await owner.Jobs.StartAsync(ImageGenRequest.Parse(arguments), context, ct).ConfigureAwait(false);
                        return new(ImageGenerationJobs.Summary(job).ToJsonString(WireJson.Options));
                    case "read":
                        return await owner.Jobs.ReadAsync(arguments.GetProperty("jobId").GetString()!,
                            arguments.TryGetProperty("resume", out var resume) && resume.GetBoolean(),
                            !arguments.TryGetProperty("preview", out var preview) || preview.GetBoolean(), context, ct).ConfigureAwait(false);
                    case "cancel":
                        var cancel = await owner.Jobs.CancelAsync(arguments.GetProperty("jobId").GetString()!, context, ct).ConfigureAwait(false);
                        return new(JsonSerializer.Serialize(new { jobId = cancel.JobId, status = cancel.Status,
                            cancelRequested = true, message = "Local cancellation requested. Read the job for its final state; ChatGPT-side completion/quota restoration is not guaranteed." }, WireJson.Options));
                    default:
                        var state = await owner._backend.GetStateAsync(context, ct).ConfigureAwait(false);
                        state["jobs"] = new JsonArray(owner.Jobs.List(context).Take(25).Select(j => (JsonNode?)ImageGenerationJobs.Summary(j)).ToArray());
                        return new(state.ToJsonString(WireJson.Options));
                }
            }
            catch (ImageGenerationException ex) { return ToolReply.Error(JsonSerializer.Serialize(new { errorCode = ex.Code, message = ex.Message }, WireJson.Options)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
            { return ToolReply.Error("ImageGen could not accept this request. " + ex.Message); }
        }
    }
    public static ToolDescriptor Describe(string name)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        string description;
        if (name == "imagegen")
        {
            properties["prompt"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 16000 };
            properties["referenced_image_paths"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 5,
                ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }, ["description"] = "Local images shared with this session. All must upload before an edit is sent. Mutually exclusive with recent count." };
            properties["num_last_images_to_include"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5,
                ["description"] = "Select the latest N generated artifacts in this Jarvis session, not arbitrary client-chat attachments." };
            properties["request_id"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 100,
                ["description"] = "Caller idempotency key. Reuse only for the identical request; retries return the original job, never generate twice." };
            required.Add("prompt");
            description = "Create or edit images using ChatGPT Web in the user's explicitly selected Chrome/Edge extension instance. Requires an explicit Jarvis session and local browser binding. No standalone Chromium, cookies import, API key, Codex backend or paid API fallback. Returns a durable jobId immediately; poll image_gen__read. Never resend after timeout: inspect get_state/read first. For edits supply local paths OR 1..5 recent session images; inspect the images first. Client attachments must first be available locally. Preserve originals. The account's ChatGPT limits apply.";
        }
        else if (name is "read" or "cancel")
        {
            properties["jobId"] = new JsonObject { ["type"] = "string", ["pattern"] = "^ig_[a-f0-9]{32}$" }; required.Add("jobId");
            if (name == "read")
            {
                properties["preview"] = new JsonObject { ["type"] = "boolean", ["default"] = true };
                properties["resume"] = new JsonObject { ["type"] = "boolean", ["default"] = false,
                    ["description"] = "Explicitly reconcile an uncertain existing browser job. Only observes/downloads that job; never resends its prompt." };
            }
            description = name == "read" ? "Read a session-owned image job and bounded image previews. Local original paths and artifact metadata are retained. resume=true only reconciles the original job after interruption, without sending another prompt. A browser restart may require operator recovery; never guess a replacement tab." : "Request local cancellation of a session-owned ImageGen job. This does not guarantee server-side cancellation or quota restoration. Read the job to verify its eventual status.";
        }
        else description = "Read the exact selected browser binding, extension readiness and the latest 25 ImageGen jobs belonging to this session. Does not launch a browser, bind a different account, submit a prompt or enable any permission.";
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        return new("image_gen." + name, "image_gen__" + name, "image_gen", description,
            JsonSerializer.SerializeToElement(schema), name == "get_state", Sensitive: true);
    }
    public void Dispose() => Jobs.Dispose();
}
