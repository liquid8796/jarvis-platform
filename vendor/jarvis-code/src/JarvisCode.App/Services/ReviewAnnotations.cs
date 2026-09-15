using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JarvisCode.App.Services;

public sealed record ReviewAnnotation(string File, string Line, bool Removed, string SourceHash, string Text, bool Comment, DateTimeOffset CreatedAt);

/// <summary>Session review anchors survive a diff reload without attaching to changed source text.</summary>
public sealed class ReviewAnnotationStore(string filePath)
{
    public IReadOnlyList<ReviewAnnotation> Load()
    {
        try { return File.Exists(filePath) ? JsonSerializer.Deserialize<List<ReviewAnnotation>>(File.ReadAllText(filePath)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }
    public void Add(ReviewAnnotation annotation)
    {
        var rows = Load().Where(a => !SamePosition(a, annotation)).Append(annotation).TakeLast(500).ToArray();
        Save(rows);
    }
    public void Remove(ReviewAnnotation annotation) => Save(Load().Where(a => !SamePosition(a, annotation)).ToArray());
    private void Save(IReadOnlyList<ReviewAnnotation> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(rows)); File.Move(temp, filePath, true);
    }
    public static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    public static bool SamePosition(ReviewAnnotation a, ReviewAnnotation b) =>
        a.File.Equals(b.File, StringComparison.OrdinalIgnoreCase) && a.Line == b.Line && a.Removed == b.Removed;
    public static bool Matches(ReviewAnnotation annotation, string file, string line, bool removed, string source) =>
        annotation.File.Equals(file, StringComparison.OrdinalIgnoreCase) && annotation.Line == line && annotation.Removed == removed && annotation.SourceHash == Hash(source);
}
