namespace JarvisCode.Cli.Repl.Input;

/// <summary>
/// What the leading character makes of the prompt box: an ordinary prompt, the
/// reference's <c>!</c> shell mode (the box takes its <c>bashBorder</c> colour
/// and the line runs as a command instead of going to the model), or its
/// <c>#</c> memory mode.
/// </summary>
internal enum ComposerMode
{
    Prompt,
    Bash,
    Memory,
}

/// <summary>
/// The composer: the editable text with its cursor, the vim layer when
/// <c>editorMode</c> asks for it, and the collapsed pastes that stand behind
/// the placeholders in it. Pure state — every method here is drivable from a
/// test without a terminal.
/// </summary>
internal sealed class Composer
{
    public Composer(bool vimEnabled = false)
    {
        Editor = new LineEditor();
        Vim = new VimMode(Editor);
        VimEnabled = vimEnabled;
    }

    public LineEditor Editor { get; }
    public VimMode Vim { get; }
    public PasteCollapse Pastes { get; } = new();
    public bool VimEnabled { get; }

    public string Text => Editor.Text;
    public int Offset => Editor.Offset;
    public bool IsEmpty => Editor.Text.Length == 0;

    /// <summary>The mode the first character puts the box in (the reference reads it off the raw text).</summary>
    public ComposerMode Mode => Editor.Text.TrimStart() switch
    {
        ['!', ..] => ComposerMode.Bash,
        ['#', ..] => ComposerMode.Memory,
        _ => ComposerMode.Prompt,
    };

    /// <summary>True while the text is a command line — a leading slash with no space before it.</summary>
    public bool IsCommandLine => Editor.Text.StartsWith('/');

    public void Clear()
    {
        Editor.Clear();
        Editor.ClearUndo();
        Pastes.Clear();
    }

    public void Set(string text, int? offset = null) => Editor.Set(text, offset);

    /// <summary>Types printable input, coalescing the undo snapshots the way the reference debounces them.</summary>
    public void Type(string text, DateTime now)
    {
        Editor.PushUndo(now);
        Editor.Kills.Interrupt();
        Editor.Insert(text);
    }

    /// <summary>
    /// A newline from shift+enter, <c>\</c>+enter or ctrl+j. A backslash
    /// immediately before the cursor is consumed, which is what makes
    /// <c>\</c>+enter read as one gesture rather than leaving the slash behind.
    /// </summary>
    public void Newline(DateTime now)
    {
        Editor.PushUndo(now, immediate: true);
        if (Editor.Offset > 0 && Editor.Text[Editor.Offset - 1] == '\\')
        {
            Editor.Backspace();
        }

        Editor.Insert("\n");
    }

    /// <summary>
    /// A paste. Long or multi-line content is collapsed to the reference's
    /// placeholder and kept beside the text; pasting the same content again
    /// while its placeholder is the most recent one expands it in place, which
    /// is what "paste again to expand" offers.
    /// </summary>
    public void Paste(string text, int rows, DateTime now)
    {
        Editor.PushUndo(now, immediate: true);
        Editor.Kills.Interrupt();
        if (Pastes.TryExpandRepeat(Editor.Text, Editor.Offset, text, out int expandedOffset) is { } expanded)
        {
            Editor.Set(expanded, expandedOffset);
            return;
        }

        if (!PasteCollapse.ShouldCollapse(text, rows))
        {
            Editor.Insert(text);
            return;
        }

        Editor.Insert(Pastes.CollapseText(Editor.Text, text));
    }

    /// <summary>Registers an image paste and inserts its placeholder.</summary>
    public string PasteImage(DateTime now)
    {
        Editor.PushUndo(now, immediate: true);
        var placeholder = Pastes.CollapseImage(Editor.Text);
        Editor.Insert(placeholder);
        return placeholder;
    }

    /// <summary>True while a placeholder in the box could be expanded by pasting its content again.</summary>
    public bool CanExpandPaste => Pastes.HasExpandableRepeat(Editor.Text);

    /// <summary>The text as the model sees it: every placeholder replaced by what it stands for.</summary>
    public string Expanded => Pastes.Expand(Editor.Text);

    /// <summary>The submission: the expanded text plus the paste records history keeps beside it.</summary>
    public (string Display, string Expanded, IReadOnlyDictionary<string, string> Pasted) TakeSubmission()
    {
        var display = Editor.Text;
        var expanded = Pastes.Expand(display);
        var pasted = Pastes.RecordsIn(display);
        Clear();
        return (display, expanded, pasted);
    }

    /// <summary>Handles a vim-mode key; false means the press is ordinary editing.</summary>
    public bool HandleVim(Terminal.KeyPress press, DateTime now) =>
        VimEnabled && Vim.Handle(press, now);

    /// <summary>The indicator under the box while vim mode is on and not suppressed.</summary>
    public string? VimIndicator => VimEnabled ? Vim.Indicator : null;
}
