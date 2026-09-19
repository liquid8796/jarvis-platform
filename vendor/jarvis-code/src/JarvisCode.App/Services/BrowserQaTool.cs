using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>Executes a bounded structured QA scenario through the shipping browser extension.</summary>
public sealed class BrowserQaTool(BrowserBridge bridge, string? imageDirectory = null) : ITool
{
    public string Name => "qa";
    public string Description => "Verify a frontend in a fresh owned browser tab. Supply a structured spec with url, expectedUrl, ready locator, viewports and ordered action/assertion steps. Captures console/network before navigation, waits for readiness and postconditions, and returns measured JSON plus real screenshot artifacts. Visual review remains explicit.";
    public bool IsReadOnly => false;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JsonArray("spec"),
        ["properties"] = new JsonObject
        {
            ["spec"] = new JsonObject { ["type"] = "object", ["description"] = "FrontendQaSpec: url, expectedUrl, ready, viewports, steps, timeoutMs, fullPage, requireVisualReview, referenceId." }
        }
    };
    public string DescribeCall(JsonObject arguments) => "BrowserQA(" + arguments["spec"]?["url"]?.GetValue<string>() + ")";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (arguments["spec"] is not JsonObject spec) return ToolResult.Error("spec must be an object.");
        try
        {
            var result = await bridge.RequestAsync("qa", new JsonObject { ["spec"] = spec.DeepClone() }, cancellationToken);
            var images = new List<ImageBlock>();
            long imageBytes = 0;
            var directory = imageDirectory ?? Path.Combine(Path.GetTempPath(), "jarvis-code", "qa-screenshots");
            Directory.CreateDirectory(directory);
            foreach (var snapshot in (result["snapshots"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (snapshot.Remove("image", out var imageNode) && imageNode?.GetValue<string>() is { Length: > 0 } base64)
                {
                    var bytes = Convert.FromBase64String(base64);
                    imageBytes += bytes.Length;
                    if (imageBytes > 4 * 1024 * 1024) throw new InvalidOperationException("QA screenshots exceed the aggregate 4 MiB transport budget.");
                    if (bytes.Length < 8 || bytes.Length > 4 * 1024 * 1024 ||
                        !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        throw new InvalidOperationException("Browser QA returned an invalid PNG artifact.");
                    var path = Path.GetFullPath(Path.Combine(directory, $"qa-{Guid.NewGuid():N}.png"));
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                    snapshot["artifactPath"] = path;
                    snapshot["screenshotSha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    images.Add(new ImageBlock("image/png", base64));
                }
                else
                {
                    snapshot["passed"] = false;
                    result["passed"] = false;
                    (result["errors"] as JsonArray)?.Add("Screenshot artifact missing for viewport " + snapshot["name"]);
                }
            }
            return new ToolResult(result.ToJsonString(), IsError: false, images.Count == 0 ? null : images);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or FormatException)
        {
            return ToolResult.Error("Browser QA failed: " + ex.Message);
        }
    }
}
