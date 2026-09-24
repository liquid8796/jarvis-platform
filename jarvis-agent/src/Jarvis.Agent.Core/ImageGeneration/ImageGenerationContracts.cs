using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.ImageGeneration;

public sealed record ImageGenRequest(string Prompt, IReadOnlyList<string> ReferencedImagePaths,
    int? NumLastImagesToInclude = null, string? RequestId = null)
{
    public static ImageGenRequest Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("ImageGen arguments must be an object.");
        foreach (var p in value.EnumerateObject())
            if (p.Name is not ("prompt" or "referenced_image_paths" or "num_last_images_to_include" or "request_id"))
                throw new ArgumentException("Unknown ImageGen argument: " + p.Name);
        var prompt = value.TryGetProperty("prompt", out var p1) && p1.ValueKind == JsonValueKind.String ? p1.GetString()! : "";
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 16000) throw new ArgumentException("Prompt must contain 1..16000 characters.");
        var paths = new List<string>();
        var hasPaths = value.TryGetProperty("referenced_image_paths", out var p2);
        if (hasPaths)
        {
            if (p2.ValueKind != JsonValueKind.Array || p2.GetArrayLength() is < 1 or > 5)
                throw new ArgumentException("Provide 1..5 local reference images.");
            foreach (var path in p2.EnumerateArray())
            {
                if (path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
                    throw new ArgumentException("Every reference image must have a local path.");
                paths.Add(path.GetString()!);
            }
        }
        int? recent = null;
        if (value.TryGetProperty("num_last_images_to_include", out var p3))
        {
            if (hasPaths) throw new ArgumentException("Use local image paths OR recent session images, never both.");
            if (!p3.TryGetInt32(out var n) || n is < 1 or > 5) throw new ArgumentException("Recent image count must be 1..5.");
            recent = n;
        }
        string? requestId = null;
        if (value.TryGetProperty("request_id", out var p4))
        {
            if (p4.ValueKind != JsonValueKind.String || p4.GetString() is not { Length: > 0 and <= 100 } s || s.Any(char.IsControl))
                throw new ArgumentException("request_id must contain 1..100 printable characters.");
            requestId = s;
        }
        return new(prompt, paths, recent, requestId);
    }
}

public sealed record ImageArtifact(string ArtifactId, string LocalPath, string MimeType, int Width, int Height,
    long FileSize, string Sha256, string? PreviewPath, IReadOnlyList<string> ParentArtifactIds);

public sealed record ImageBrowserBinding(string ExtensionInstanceId, string BrowserFamily, string Revision);

public sealed record ImageGenerationJob
{
    public required string JobId { get; init; }
    public required string OwnerId { get; init; }
    public required string DeviceId { get; init; }
    public required string SessionId { get; init; }
    public required string RequestId { get; init; }
    public required string RequestDigest { get; init; }
    public required string Prompt { get; init; }
    public required ImageBrowserBinding Binding { get; init; }
    public string Backend { get; init; } = "chatgpt-web-extension";
    public string Status { get; init; } = "queued";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ImageArtifact> Inputs { get; init; } = [];
    public IReadOnlyList<ImageArtifact> Artifacts { get; init; } = [];
    public JsonObject BrowserState { get; init; } = new();
    public bool SubmissionAttempted { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public bool IsTerminal => Status is "completed" or "cancelled" or "completion_unknown" || Status.StartsWith("failed_", StringComparison.Ordinal);
}

public interface IImageGenerationBackend
{
    Task<ImageBrowserBinding> GetBindingAsync(AgentExecutionContext context, CancellationToken ct);
    Task<JsonObject> GetStateAsync(AgentExecutionContext context, CancellationToken ct);
    // Only the fixed operations implemented by the extension backend are accepted. Never execute page-returned code.
    Task<JsonObject> CallAsync(string operation, ImageGenerationJob job, AgentExecutionContext context, CancellationToken ct);
}

public interface IImageArtifactStore
{
    Task<IReadOnlyList<ImageArtifact>> StageInputsAsync(IReadOnlyList<string> paths, IReadOnlyList<string> parents,
        string directory, CancellationToken ct);
    Task<IReadOnlyList<ImageArtifact>> ImportResultsAsync(JsonObject browserResult, IReadOnlyList<ImageArtifact> parents,
        string directory, string jobId, CancellationToken ct);
    Task<IReadOnlyList<WireImage>> ReadPreviewsAsync(IReadOnlyList<ImageArtifact> artifacts, CancellationToken ct);
}

public sealed class ImageGenerationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ImageGenerationSafety
{
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Scope(AgentExecutionContext context)
    {
        var id = context.RequireSessionIdentity();
        return Digest(id.OwnerId + "\n" + id.DeviceId + "\n" + id.SessionId);
    }
    public static bool IsJobId(string id) => id.StartsWith("ig_", StringComparison.Ordinal) && id.Length == 35 && Guid.TryParseExact(id[3..], "N", out _);
    public static void NoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("ImageGen storage and input paths must not traverse links or junctions.");
    }
    public static void AtomicWrite(string path, string content)
    {
        NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            { writer.Write(content); writer.Flush(); stream.Flush(true); }
            NoLinks(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
