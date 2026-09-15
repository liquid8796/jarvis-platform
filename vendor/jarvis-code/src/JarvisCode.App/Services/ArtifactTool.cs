using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

public sealed record ArtifactDocument(string Title, string Html)
{
    /// <summary>The title as a file name, for the tile's Download action.</summary>
    public static string SafeFileName(string title)
    {
        var cleaned = new string([.. title.Select(c =>
            System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)]).Trim();
        return cleaned.Length > 0 ? cleaned : "artifact";
    }
}

/// <summary>
/// App-level tool: lets the agent publish a live HTML artifact that the
/// workspace renders in the Artifact tile. Read-only for the gate — it only
/// changes what the panel shows.
/// </summary>
public sealed class ArtifactTool : ITool
{
    public const string ToolName = "Artifact";

    private readonly Action<ArtifactDocument> _publish;

    public ArtifactTool(Action<ArtifactDocument> publish)
    {
        _publish = publish;
    }

    public string Name => ToolName;

    public string Description =>
        "Renders a live HTML artifact in the side panel — a report, chart, mockup, or interactive page the user " +
        "can see immediately. Pass a complete self-contained HTML document (inline CSS/JS only; no external " +
        "network resources). Calling it again replaces the previous artifact.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Short artifact title" },
            ["html"] = new JsonObject { ["type"] = "string", ["description"] = "Complete self-contained HTML document" },
        },
        ["required"] = new JsonArray("title", "html"),
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments)
    {
        var title = JsonArgs.GetString(arguments, "title") ?? "untitled";
        return $"Artifact({title})";
    }

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var title = JsonArgs.GetString(arguments, "title")?.Trim();
        var html = JsonArgs.GetString(arguments, "html");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(html))
        {
            return Task.FromResult(ToolResult.Error("Both title and html are required."));
        }

        if (html.Length > 2_000_000)
        {
            return Task.FromResult(ToolResult.Error("The artifact is too large (max 2 MB of HTML)."));
        }

        _publish(new ArtifactDocument(title, html));
        return Task.FromResult(ToolResult.Success(
            $"The artifact \"{title}\" is now displayed in the side panel. Update it by calling this tool again."));
    }
}
