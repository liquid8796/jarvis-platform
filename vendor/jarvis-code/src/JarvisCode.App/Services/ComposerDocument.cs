using System.IO;
using System.Text.Json;

namespace JarvisCode.App.Services;

public sealed record ComposerSkillChip(string SkillId, string DisplayName, string Description = "", string ArgumentHint = "");
public sealed record ComposerNode(string? Text = null, ComposerSkillChip? Skill = null);
public sealed record ComposerDocument(IReadOnlyList<ComposerNode> Nodes, int? Caret = null)
{
    public static ComposerDocument Empty { get; } = new([]);
    [System.Text.Json.Serialization.JsonIgnore]
    public string Text => string.Concat(NormalizedNodes().Select(node => node.Skill is { } chip ? "/" + chip.SkillId : node.Text));

    public IReadOnlyList<ComposerNode> NormalizedNodes()
    {
        var result = new List<ComposerNode>();
        var afterChip = false;
        foreach (var node in Nodes)
        {
            if (node.Skill is { } skill)
            {
                if (afterChip) result.Add(new(" "));
                result.Add(node);
                afterChip = true;
            }
            else if (node.Text is { Length: > 0 } text)
            {
                var visible = text.TrimStart('\u200b');
                if (afterChip && visible.Length > 0 && visible[0] is not ' ' and not '\u00a0') text = " " + text;
                result.Add(new(text));
                afterChip = false;
            }
        }
        return result;
    }
}

/// <summary>Draft rich nodes stay with their conversation; history/send continue to use plain command text.</summary>
public sealed class ComposerDraftStore(string file)
{
    private readonly Dictionary<string, ComposerDocument> _drafts = Load(file);
    public ComposerDocument Get(string session) => _drafts.GetValueOrDefault(session) ?? ComposerDocument.Empty;
    public void Save(string session, ComposerDocument document)
    {
        if (document.Text.Length == 0) _drafts.Remove(session);
        else _drafts[session] = document;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_drafts));
        File.Move(temporary, file, overwrite: true);
    }
    private static Dictionary<string, ComposerDocument> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, ComposerDocument>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
}
