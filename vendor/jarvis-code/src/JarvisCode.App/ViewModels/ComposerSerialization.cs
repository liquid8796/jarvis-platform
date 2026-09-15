namespace JarvisCode.App.ViewModels;

/// <summary>
/// Turns composer chips into the sent text the way the reference composer does:
/// file/folder chips as a leading line of quoted @-mentions (folders keep a
/// trailing slash), attach-as-context snippets next, the typed text last.
/// </summary>
public static class ComposerSerialization
{
    public static string ComposeOutgoingText(
        string typed, IReadOnlyList<ComposerAttachment> chips, string? sessionId = null)
    {
        // A window the user pointed at rides the message it was pointed at from,
        // once — the reference's appendCuWindowHint, which appends its block and
        // clears the pick.
        var hint = Services.ComputerUseWindowHints.Drain(sessionId);
        if (hint is not null)
        {
            typed += hint;
        }

        var mentions = chips
            .Where(static c => c.Kind is ComposerAttachmentKind.File or ComposerAttachmentKind.Folder)
            .Select(static c => $"@\"{c.MentionPath}\"")
            .ToList();
        var snippets = chips
            .Where(static c => c.Kind == ComposerAttachmentKind.Context)
            .Select(static c => SerializeSnippet(c.Text))
            .ToList();
        if (mentions.Count == 0 && snippets.Count == 0)
        {
            return typed;
        }

        var builder = new System.Text.StringBuilder();
        if (mentions.Count > 0)
        {
            builder.Append(string.Join(" ", mentions)).Append('\n');
        }

        if (snippets.Count > 0)
        {
            builder.Append(string.Join("\n\n", snippets)).Append("\n\n");
        }

        builder.Append(typed);
        return builder.ToString().Trim();
    }

    /// <summary>
    /// The reference attach-as-context wire format: an HTML-comment header
    /// followed by the attached text as a blockquote.
    /// </summary>
    public static string SerializeSnippet(string text)
    {
        var lines = text.TrimEnd().ReplaceLineEndings("\n").Split('\n');
        return "<!-- attach -->\n" + string.Join("\n", lines.Select(static l => "> " + l));
    }
}
