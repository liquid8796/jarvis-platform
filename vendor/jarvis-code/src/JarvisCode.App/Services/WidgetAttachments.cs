using System.IO;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>Files from the widget's structured-clone message, encoded by the isolated host bridge.</summary>
internal static class WidgetAttachments
{
    public const int MaxFiles = 20;
    public const int MaxFileBytes = 30 * 1024 * 1024;
    public const int MaxTotalBytes = 60 * 1024 * 1024;

    public static IReadOnlyList<string> Save(JsonArray files, string attachmentsDirectory)
    {
        if (files.Count is 0 or > MaxFiles) throw new InvalidDataException("Choose between 1 and 20 files.");
        var decoded = new List<(string Name, byte[] Bytes)>();
        long total = 0;
        foreach (var entry in files.OfType<JsonObject>())
        {
            var name = entry["name"]?.GetValue<string>() ?? "attachment";
            var data = entry["data"]?.GetValue<string>() ?? throw new InvalidDataException("The file has no data.");
            if (data.Length > (long)MaxFileBytes * 4 / 3 + 4) throw new InvalidDataException("A file is larger than 30 MB.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { throw new InvalidDataException("The file data is invalid."); }
            total += bytes.Length;
            if (bytes.Length > MaxFileBytes || total > MaxTotalBytes) throw new InvalidDataException("The selected files are too large.");
            name = Path.GetFileName(name.Replace('\\', '/'));
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
            if (name.Length == 0) name = "attachment";
            if (name.Length > 180) name = name[..180];
            var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                char.IsAsciiDigit(stem[3])) name = "_" + name;
            decoded.Add((name, bytes));
        }
        if (decoded.Count != files.Count) throw new InvalidDataException("The file list is invalid.");

        // Validate every file before creating any. Each gets its own folder,
        // preserving duplicate names without replacing an earlier attachment.
        var root = Path.Combine(Path.GetFullPath(attachmentsDirectory), "widgets", Guid.NewGuid().ToString("N"));
        var result = new List<string>();
        for (var i = 0; i < decoded.Count; i++)
        {
            var folder = Path.Combine(root, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, decoded[i].Name);
            File.WriteAllBytes(path, decoded[i].Bytes);
            result.Add(path);
        }
        return result;
    }
}
