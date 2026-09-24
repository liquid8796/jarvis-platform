using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.ImageGeneration;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.ImageGeneration;

/// <summary>Only the existing extension route is used. No HTTP client, API key, token import or browser launcher.</summary>
public sealed class ChatGptExtensionImageBackend(IBrowserRuntimeClient browser, ImageGenBrowserBindingStore bindings) : IImageGenerationBackend
{
    public async Task<ImageBrowserBinding> GetBindingAsync(AgentExecutionContext context, CancellationToken ct)
    {
        var binding = bindings.Read() ?? throw new ImageGenerationException("BROWSER_UNBOUND", "Select your signed-in Chrome/Edge in ImageGen settings or click Use this browser in the Jarvis extension.");
        var state = await SendAsync("state", new JsonObject(), context, ct).ConfigureAwait(false);
        if (state["connected"]?.GetValue<bool>() != true)
            throw new ImageGenerationException("BROWSER_NOT_CONNECTED", "The selected extension is not connected. Open the same Chrome/Edge profile; no replacement browser will be used.");
        if (bindings.Read() != binding) throw new ImageGenerationException("BINDING_CHANGED", "The selected browser changed. Check ImageGen settings.");
        return binding;
    }
    public Task<JsonObject> GetStateAsync(AgentExecutionContext context, CancellationToken ct) => SendAsync("state", new JsonObject(), context, ct);
    public async Task<JsonObject> CallAsync(string operation, ImageGenerationJob job, AgentExecutionContext context, CancellationToken ct)
    {
        if (operation is not ("prepare" or "inspect" or "upload" or "upload_status" or "submit" or "poll" or "download" or "download_status" or "cancel"))
            throw new ArgumentException("Unknown ImageGen operation.");
        if (bindings.Read() != job.Binding) throw new ImageGenerationException("BINDING_CHANGED", "ImageGen browser selection was changed or revoked. No request was routed elsewhere.");
        if (operation == "upload")
            foreach (var input in job.Inputs)
            {
                var bytes = await ImageArtifactStore.ReadBoundedAsync(input.LocalPath, ImageArtifactStore.MaxInputBytes, ct).ConfigureAwait(false);
                if (ImageArtifactStore.Hash(bytes) != input.Sha256)
                    throw new ImageGenerationException("INPUT_CHANGED", "A staged reference image changed before upload.");
            }
        var args = new JsonObject
        {
            ["jobId"] = job.JobId, ["instanceId"] = job.Binding.ExtensionInstanceId,
            ["bindingRevision"] = job.Binding.Revision
        };
        if (operation == "submit") args["prompt"] = job.Prompt;
        if (operation == "upload") args["paths"] = JsonSerializer.SerializeToNode(job.Inputs.Select(i => i.LocalPath).ToArray());
        return await SendAsync(operation, args, context, ct).ConfigureAwait(false);
    }
    private async Task<JsonObject> SendAsync(string operation, JsonObject args, AgentExecutionContext context, CancellationToken ct)
    {
        context.RequireSessionIdentity();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await browser.ExecuteAsync(new BrowserRuntimeRequest("imagegen." + operation,
            JsonSerializer.SerializeToElement(args), new BrowserRuntimeContext(context.CallId, context.SessionId,
                context.IsolationScopeId, context.Workspace, context.AdditionalDirectories, context.FullPermission, "extension")), timeout.Token).ConfigureAwait(false);
        if (response.IsError) throw new ImageGenerationException("BROWSER_ERROR", "The selected browser could not complete this step. Inspect the tab before retrying.");
        return JsonNode.Parse(response.Text)?.AsObject() ?? throw new ImageGenerationException("RESULT_UNVERIFIED", "Browser result is invalid.");
    }
}
