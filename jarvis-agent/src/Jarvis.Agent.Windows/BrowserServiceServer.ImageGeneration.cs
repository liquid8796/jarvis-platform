using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core.ImageGeneration;
using Jarvis.Agent.Windows.ImageGeneration;
using Jarvis.Protocol;
using JarvisCode.App.Services;

namespace Jarvis.Agent.Windows;

public sealed partial class BrowserServiceServer
{
    private void OnImageGenerationBindingRequested(BrowserConnectionInfo connection, bool enabled)
    {
        if (connection.Id == _devConnectionId || !connection.Ready || connection.ExtensionInstanceId is null ||
            connection.Name is not ("Chrome" or "Edge") || connection.Capabilities?.Contains("imagegen-v1") != true) return;
        try
        {
            var store = new ImageGenBrowserBindingStore(_root);
            var old = store.Read();
            if (enabled) store.Save(connection.ExtensionInstanceId, connection.Name.ToLowerInvariant(), old?.Revision);
            else if (old?.ExtensionInstanceId == connection.ExtensionInstanceId) store.Clear(old.Revision);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or JsonException)
        { /* Invalid or concurrently changed local settings are preserved; the settings UI reports them. */ }
    }
    private async Task<JsonObject> ExecuteImageGenerationAsync(string id, BrowserRuntimeRequest request, JsonObject args, CancellationToken ct)
    {
        var operation = request.ToolId["imagegen.".Length..];
        if (request.Context.BrowserFamily != "extension") throw new ArgumentException("ImageGen supports only explicitly selected external extensions.");
        var candidates = _bridge.Connections.Where(c => c.Ready && c.Id != _devConnectionId &&
            c.Name is "Chrome" or "Edge" && c.ExtensionInstanceId is not null && c.Capabilities?.Contains("imagegen-v1") == true).ToArray();
        var binding = new ImageGenBrowserBindingStore(_root).Read();
        JsonObject result;
        if (operation == "connections")
        {
            result = new JsonObject
            {
                ["binding"] = JsonSerializer.SerializeToNode(binding, WireJson.Options),
                ["connections"] = JsonSerializer.SerializeToNode(candidates, WireJson.Options)
            };
        }
        else
        {
            if (!AgentSessionRules.IsSessionId(request.Context.ApplicationSessionId)) throw new ArgumentException("An explicit ImageGen session is required.");
            var selected = binding is null ? [] : candidates.Where(c => c.ExtensionInstanceId == binding.ExtensionInstanceId &&
                c.Name.Equals(binding.BrowserFamily, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (operation == "state")
            {
                result = new JsonObject { ["backend"] = "chatgpt-web-extension", ["connected"] = selected.Length == 1,
                    ["binding"] = JsonSerializer.SerializeToNode(binding, WireJson.Options),
                    ["status"] = binding is null ? "browser_unbound" : selected.Length == 1 ? "browser_selected" : "browser_not_connected" };
                if (selected.Length == 1)
                {
                    using var session = _bridge.EnterApplicationSession(request.Context.ApplicationSessionId!);
                    result["tab"] = await _bridge.RequestForExtensionInstanceAsync(binding!.ExtensionInstanceId, "imagegen_state", new JsonObject(), ct).ConfigureAwait(false);
                }
            }
            else
            {
                if (operation is not ("prepare" or "inspect" or "upload" or "upload_status" or "submit" or "poll" or "download" or "download_status" or "cancel"))
                    throw new ArgumentException("Unknown ImageGen operation.");
                if (binding is null || selected.Length != 1 || args["instanceId"]?.GetValue<string>() != binding.ExtensionInstanceId ||
                    args["bindingRevision"]?.GetValue<string>() != binding.Revision)
                    throw new InvalidOperationException("The exact selected ImageGen browser is unavailable or its binding changed.");
                if (args["jobId"]?.GetValue<string>() is not { } job || !ImageGenerationSafety.IsJobId(job)) throw new ArgumentException("Invalid ImageGen job.");
                args.Remove("instanceId"); args.Remove("bindingRevision");
                using var session = _bridge.EnterApplicationSession(request.Context.ApplicationSessionId!);
                result = await _bridge.RequestForExtensionInstanceAsync(binding.ExtensionInstanceId, "imagegen_" + operation, args, ct).ConfigureAwait(false);
            }
        }
        var reply = new BrowserRuntimeReply(result.ToJsonString(WireJson.Options), false);
        return Ok(id, new JsonObject { ["reply"] = JsonSerializer.SerializeToNode(reply, BrowserRuntimeProtocol.Json) });
    }
}
