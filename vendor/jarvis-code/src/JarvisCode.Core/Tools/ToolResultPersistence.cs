using System.Text;

namespace JarvisCode.Core.Tools;

/// <summary>
/// The reference's oversize-result policy (CLI 2.1.257, <c>J</c>/<c>NV</c>/<c>Bfe</c>
/// at 184899134): a tool result longer than its tool's threshold is written whole
/// to <c>{session}/tool-results/{tool_use_id}.txt</c> and the model gets a
/// <c>&lt;persisted-output&gt;</c> block naming the file with the first 2KB as a
/// preview; an empty result becomes <c>({tool} completed with no output)</c>.
/// The threshold is the tool's <c>maxResultSizeChars</c> capped at 50,000
/// (<c>r7</c>), 400,000 (<c>aQn</c>) for a tool that declares none; a result
/// carrying images is never persisted.
/// </summary>
public sealed class ToolResultPersistence(string directory)
{
    public const string OpenTag = "<persisted-output>";
    public const string CloseTag = "</persisted-output>";

    /// <summary>The reference's <c>aQn</c>: the threshold for a tool without <c>maxResultSizeChars</c>.</summary>
    public const int DefaultThresholdChars = 400_000;

    /// <summary>The reference's <c>r7</c>: the most a declared <c>maxResultSizeChars</c> can raise the threshold to.</summary>
    public const int ThresholdCeiling = 50_000;

    /// <summary>The reference's <c>ENe</c>: the preview's length.</summary>
    public const int PreviewChars = 2000;

    /// <summary>
    /// The reference tools whose <c>maxResultSizeChars</c> differs from the
    /// 100,000 most declare (read off their definitions); a null means the tool
    /// never persists (its value is Infinity).
    /// </summary>
    private static readonly Dictionary<string, int?> DeclaredSizes = new(StringComparer.Ordinal)
    {
        ["Grep"] = 20_000,
        ["WebFetch"] = 50_000,
        ["Monitor"] = 10_000,
        ["ReportFindings"] = 256,
        ["ScheduleWakeup"] = 1_000,
        ["memory_list"] = null,
        ["memory_read"] = null,
        ["memory_write"] = null,
        ["memory"] = null,
    };

    /// <summary>The directory results are written to; created on first use.</summary>
    public string Directory { get; } = directory;

    /// <summary>
    /// The tool's threshold: its declared size capped at the ceiling, which for
    /// the reference's ordinary 100,000 declaration is the ceiling itself.
    /// </summary>
    public static int? ThresholdFor(string toolName)
    {
        if (DeclaredSizes.TryGetValue(toolName, out var declared))
        {
            return declared is int size ? Math.Min(size, ThresholdCeiling) : null;
        }

        return ThresholdCeiling;
    }

    /// <summary>The reference's <c>Ut</c>: bytes, KB, MB or GB with one decimal and a dropped ".0".</summary>
    public static string FormatSize(long chars)
    {
        var kb = chars / 1024.0;
        if (kb < 1)
        {
            return $"{chars} bytes";
        }

        if (kb < 1024)
        {
            return Trim(kb) + "KB";
        }

        var mb = kb / 1024.0;
        if (mb < 1024)
        {
            return Trim(mb) + "MB";
        }

        return Trim(mb / 1024.0) + "GB";

        static string Trim(double value)
        {
            var text = value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
        }
    }

    /// <summary>
    /// The reference's <c>G5e</c>: the first 2,000 characters, cut back to the last
    /// newline when that keeps more than half.
    /// </summary>
    public static (string Preview, bool HasMore) Preview(string content, int previewChars = PreviewChars)
    {
        if (content.Length <= previewChars)
        {
            return (content, false);
        }

        var head = content[..previewChars];
        var lastNewline = head.LastIndexOf('\n');
        var cut = lastNewline > previewChars * 0.5 ? lastNewline : previewChars;
        return (content[..cut], true);
    }

    /// <summary>The reference's <c>Bfe</c>: the block the model receives instead of the content.</summary>
    public static string Render(long originalSize, string filePath, string preview, bool hasMore)
    {
        var builder = new StringBuilder();
        builder.Append(OpenTag).Append('\n');
        builder.Append($"Output too large ({FormatSize(originalSize)}). Full output saved to: {filePath}\n\n");
        builder.Append($"Preview (first {FormatSize(PreviewChars)}):\n");
        builder.Append(preview);
        builder.Append(hasMore ? "\n...\n" : "\n");
        builder.Append(CloseTag);
        return builder.ToString();
    }

    /// <summary>The reference's stand-in for a result with nothing in it.</summary>
    public static string EmptyResult(string toolName) => $"({toolName} completed with no output)";

    /// <summary>True for content that came back from this policy rather than the tool.</summary>
    public static bool IsPersisted(string content) => content.StartsWith(OpenTag, StringComparison.Ordinal);

    /// <summary>
    /// Applies the policy to one result: the empty stand-in, the content itself
    /// when it fits or carries images, or the persisted block once the file is
    /// written. A write that fails leaves the content as it was, which is what
    /// the reference does with a persistence error.
    /// </summary>
    /// <param name="declaredMaxResultSizeChars">
    /// The tool's own <c>anthropic/maxResultSizeChars</c>, when it declared one;
    /// the reference caps it at the same ceiling as its built-in declarations.
    /// </param>
    public string Apply(
        string toolName, string toolUseId, string content, bool hasImages,
        int? declaredMaxResultSizeChars = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return hasImages ? content : EmptyResult(toolName);
        }

        if (hasImages || IsPersisted(content))
        {
            return content;
        }

        var threshold = declaredMaxResultSizeChars is { } declared
            ? Math.Min(declared, ThresholdCeiling)
            : ThresholdFor(toolName);
        if (threshold is null || content.Length <= threshold)
        {
            return content;
        }

        string path;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            path = Path.Combine(Directory, SafeFileName(toolUseId) + ".txt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return content;
        }

        var (preview, hasMore) = Preview(content);
        return Render(content.Length, path, preview, hasMore);
    }

    private static string SafeFileName(string toolUseId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = toolUseId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars);
        return name.Length == 0 ? "result" : name;
    }
}
