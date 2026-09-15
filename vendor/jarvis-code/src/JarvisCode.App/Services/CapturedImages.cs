using System.IO;

namespace JarvisCode.App.Services;

/// <summary>
/// Screenshots the browser tools have taken, by the id <c>upload_image</c> names
/// them with.
///
/// The reference's upload_image takes "ID of a previously captured screenshot
/// (from the computer tool's screenshot action)", so a capture has to be
/// findable again after the model has seen it. Every screenshot is remembered
/// here and its id printed in the screenshot's own result, which is what makes
/// the id something the model can pass back.
///
/// The id format is this app's (<c>img_1</c>, <c>img_2</c>, …): the reference
/// never prints one in a result this session could read, so the shape is
/// declared rather than guessed at. Everything else about the contract — that an
/// id names a capture, and that upload_image resolves one — is the reference's.
///
/// Captures are held in memory, not written: <c>save_to_disk</c> is the argument
/// that puts a screenshot on disk, and taking one should not leave a file behind
/// when it was not asked for. A file is materialised only when an upload needs a
/// path, into the temp folder, and the last <see cref="Keep"/> captures stay
/// resolvable so the map cannot grow without limit.
/// </summary>
public static class CapturedImages
{
    /// <summary>How many captures stay resolvable. Older ids answer null.</summary>
    public const int Keep = 20;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Base64 = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private static int _next;

    /// <summary>Remembers one capture and returns the id that names it.</summary>
    public static string Register(string base64)
    {
        lock (Gate)
        {
            var id = $"img_{++_next}";
            Base64[id] = base64;
            Order.Enqueue(id);
            while (Order.Count > Keep)
            {
                Base64.Remove(Order.Dequeue());
            }

            return id;
        }
    }

    /// <summary>The capture an id names, or null when it was never registered or has aged out.</summary>
    public static string? Find(string id)
    {
        lock (Gate)
        {
            return Base64.TryGetValue(id, out var data) ? data : null;
        }
    }

    /// <summary>
    /// The capture written where the page can be handed a path, or null when the
    /// id is unknown or the write failed.
    /// </summary>
    public static string? WriteToDisk(string id, string fileName)
    {
        if (Find(id) is not { } data)
        {
            return null;
        }

        var directory = Path.Combine(Path.GetTempPath(), "jarvis-code", "captures", id);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, Convert.FromBase64String(data));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The refusal for an id nothing answers to; the reference's own wording is unmeasured.</summary>
    public static string NotFound(string id) =>
        $"No captured image with id \"{id}\". Take a screenshot with the computer tool first and pass the " +
        "imageId it reports.";

    /// <summary>Drops everything, for tests.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Base64.Clear();
            Order.Clear();
            _next = 0;
        }
    }
}
