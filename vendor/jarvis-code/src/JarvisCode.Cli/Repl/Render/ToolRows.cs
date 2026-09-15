namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The transcript's tool rows: the reference draws the call as
/// <c>● {label}</c> and its result under it behind the <c>⎿  </c> connector,
/// folded to three lines. A row that is still running says so instead of
/// showing a result.
/// </summary>
internal static class ToolRows
{
    /// <summary>The reference's connector plus the two spaces it writes after it.</summary>
    public const string Connector = Glyphs.ResultConnector + "  ";

    /// <summary>The indent a result sits at under its row.</summary>
    public const string ResultIndent = "  ";

    /// <summary>The continuation indent, so a wrapped result lines up under the first line's text.</summary>
    public static readonly string ContinuationIndent = new(' ', ResultIndent.Length + Connector.Length);

    /// <summary>A shell command is shown two lines deep and 160 characters wide.</summary>
    public const int CommandMaxLines = 2;
    public const int CommandMaxChars = 160;

    /// <summary>The call's own row.</summary>
    public static string Header(string label) => $"{Glyphs.Bullet} {label}";

    /// <summary>
    /// A shell command as the row shows it: at most two lines, at most 160
    /// characters, with an ellipsis where it was cut.
    /// </summary>
    public static string Command(string command)
    {
        var lines = command.ReplaceLineEndings("\n").Split('\n');
        var kept = lines.Take(CommandMaxLines).ToList();
        var text = string.Join('\n', kept);
        if (lines.Length > CommandMaxLines || text.Length > CommandMaxChars)
        {
            text = text.Length > CommandMaxChars ? text[..CommandMaxChars] : text;
            text += "…";
        }

        return text;
    }

    /// <summary>The result under a row, folded and indented behind the connector.</summary>
    public static IReadOnlyList<string> ResultLines(
        string result, int columns, bool hideExpandHint = false, string chord = "ctrl+o")
    {
        var folded = ResultFold.Render(result, columns, hideExpandHint, chord);
        if (folded.Length == 0)
        {
            return [];
        }

        var lines = folded.Split('\n');
        var rows = new List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            rows.Add(i == 0 ? ResultIndent + Connector + lines[i] : ContinuationIndent + lines[i]);
        }

        return rows;
    }

    /// <summary>The reference's memory row verbs.</summary>
    public const string Recalled = "Recalled";
    public const string Remembered = "Remembered";
}
