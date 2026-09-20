namespace Jarvis.Protocol;

public sealed record UserPromptSnippet(string Id, string Title, string Text);

/// <summary>User-editable optional tool-result context, never a system message or permission grant.</summary>
public sealed record UserPromptContext(long Revision, IReadOnlyList<UserPromptSnippet> Prompts)
{
    public const string Capability = "user-prompt-context-v1";
    public const int MaxPrompts = 64;
    public const int MaxTitleLength = 120;
    public const int MaxTextLength = 4000;
    public const int MaxContextLength = 16000;
    private const string Separator = "\n\n";

    public void Validate()
    {
        if (Revision < 1 || Prompts is null || Prompts.Count is < 1 or > MaxPrompts)
            throw new ArgumentException("Invalid prompt context revision or count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var length = 0;
        var renderedLength = Separator.Length * (Prompts.Count - 1);
        foreach (var prompt in Prompts)
        {
            if (prompt is null) throw new ArgumentException("Prompt entries cannot be null.");
            ValidateSnippet(prompt);
            if (!ids.Add(prompt.Id)) throw new ArgumentException("Prompt IDs must be unique.");
            length += prompt.Title.Length + prompt.Text.Length;
            renderedLength += prompt.Text.Length;
        }
        if (length > MaxContextLength)
            throw new ArgumentException($"Enabled prompt titles and text exceed {MaxContextLength:N0} characters.");
        if (renderedLength > MaxContextLength)
            throw new ArgumentException($"Rendered prompt text exceeds {MaxContextLength:N0} characters including separators.");
    }

    public static void ValidateSnippet(UserPromptSnippet prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrEmpty(prompt.Id) || prompt.Id.Length > 64 ||
            prompt.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Prompt IDs must contain 1..64 letters, digits, hyphens or underscores.");
        if (string.IsNullOrWhiteSpace(prompt.Title) || prompt.Title.Length > MaxTitleLength)
            throw new ArgumentException($"Each prompt needs a title of 1..{MaxTitleLength} characters.");
        if (string.IsNullOrWhiteSpace(prompt.Text) || prompt.Text.Length > MaxTextLength)
            throw new ArgumentException($"Each prompt needs text of 1..{MaxTextLength} characters.");
    }

    public string ToContextText()
    {
        Validate();
        // Titles/IDs are editor metadata. Preserve each body exactly; add only blank lines
        // between bodies. No provenance banner, nested JSON, fences or invisible escaping.
        // The caller still returns this as tool-result content. Text formatting cannot
        // promote it to a host system/developer message or enforce model-level isolation.
        return string.Join(Separator, Prompts.Select(prompt => prompt.Text));
    }
}
