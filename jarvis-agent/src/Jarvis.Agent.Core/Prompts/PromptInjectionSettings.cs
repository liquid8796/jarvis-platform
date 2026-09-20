using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Prompts;

public sealed record PromptInjectionEntry(string Id, string Title, string Text, bool Enabled = false);

/// <summary>Local user's optional context presets. Entirely separate from tool permissions.</summary>
public sealed record PromptInjectionSettings
{
    public long Revision { get; init; } = 1;
    public bool Enabled { get; init; }
    public IReadOnlyList<PromptInjectionEntry> Entries { get; init; } = [];
    public const int MaxStoredCharacters = 64000;

    public void Validate()
    {
        if (Revision < 1 || Entries is null || Entries.Count > UserPromptContext.MaxPrompts)
            throw new ArgumentException("Invalid prompt settings revision or count (maximum 64 prompts).");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var characters = 0;
        foreach (var entry in Entries)
        {
            if (entry is null) throw new ArgumentException("Prompt entries cannot be null.");
            UserPromptContext.ValidateSnippet(new(entry.Id, entry.Title, entry.Text));
            if (!ids.Add(entry.Id)) throw new ArgumentException("Prompt IDs must be unique.");
            characters += entry.Title.Length + entry.Text.Length;
        }
        if (characters > MaxStoredCharacters)
            throw new ArgumentException("Stored prompt titles and text exceed 64,000 characters.");
        var active = Entries.Where(p => p.Enabled).Select(p => new UserPromptSnippet(p.Id, p.Title, p.Text)).ToArray();
        if (active.Length > 0) new UserPromptContext(Revision, active).Validate();
    }

    public UserPromptContext? CreateContext()
    {
        Validate();
        if (!Enabled) return null;
        var active = Entries.Where(p => p.Enabled).Select(p => new UserPromptSnippet(p.Id, p.Title, p.Text)).ToArray();
        return active.Length == 0 ? null : new(Revision, Array.AsReadOnly(active));
    }
}
