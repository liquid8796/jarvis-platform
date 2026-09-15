using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Dialogs;

/// <summary>One row of a select dialog.</summary>
internal sealed record SelectOption(
    string Value,
    string Label,
    string? Description = null,
    bool IsInput = false,
    string? Placeholder = null,
    bool Disabled = false);

/// <summary>What a dialog did with a key.</summary>
internal enum DialogOutcome
{
    /// <summary>The key was consumed and the dialog is still up.</summary>
    Handled,

    /// <summary>The key means nothing here.</summary>
    Ignored,

    /// <summary>The dialog finished with a value.</summary>
    Accepted,

    /// <summary>The dialog was cancelled.</summary>
    Cancelled,
}

internal readonly record struct DialogResult(DialogOutcome Outcome, string? Value = null, string? Text = null)
{
    public static readonly DialogResult Handled = new(DialogOutcome.Handled);
    public static readonly DialogResult Ignored = new(DialogOutcome.Ignored);
    public static readonly DialogResult Cancelled = new(DialogOutcome.Cancelled);
    public static DialogResult Accept(string value, string? text = null) => new(DialogOutcome.Accepted, value, text);
}

/// <summary>
/// The reference's select component, as pure state: a pointer over the rows,
/// digits 1-9 jumping to a row, an optional free-text row that takes typing
/// instead of moving the pointer, and the reference's own rendering — the
/// pointer glyph, the row's number, and the description under the highlighted
/// row.
/// </summary>
internal sealed class SelectList(IReadOnlyList<SelectOption> options, int index = 0)
{
    private readonly List<SelectOption> _options = [.. options];

    public IReadOnlyList<SelectOption> Options => _options;
    public int Index { get; private set; } = index;

    /// <summary>The text typed into the highlighted input row, if it is one.</summary>
    public string InputText { get; private set; } = "";

    /// <summary>True while the pointer sits on a row that takes typed text.</summary>
    public bool IsEditing => Current?.IsInput == true;

    public SelectOption? Current => _options.Count == 0 ? null : _options[Math.Clamp(Index, 0, _options.Count - 1)];

    /// <summary>Whether digits and letters should be read as a jump or as text.</summary>
    public bool HideIndexes { get; init; }

    public void Move(int delta)
    {
        if (_options.Count == 0)
        {
            return;
        }

        int next = Index;
        for (int i = 0; i < _options.Count; i++)
        {
            next = ((next + delta) % _options.Count + _options.Count) % _options.Count;
            if (!_options[next].Disabled)
            {
                break;
            }
        }

        Index = next;
    }

    public void MoveTo(int index)
    {
        if (index >= 0 && index < _options.Count && !_options[index].Disabled)
        {
            Index = index;
        }
    }

    /// <summary>Applies a routed action, then falls back to the reference's digit selection.</summary>
    public DialogResult Handle(string? action, KeyPress press)
    {
        switch (action)
        {
            case "select:next" or "confirm:next":
                Move(1);
                return DialogResult.Handled;
            case "select:previous" or "confirm:previous":
                Move(-1);
                return DialogResult.Handled;
            case "select:first":
                MoveTo(0);
                return DialogResult.Handled;
            case "select:last":
                MoveTo(_options.Count - 1);
                return DialogResult.Handled;
            case "select:accept" or "confirm:yes":
                return Current is { } current
                    ? DialogResult.Accept(current.Value, IsEditing ? InputText : null)
                    : DialogResult.Handled;
            case "select:cancel" or "confirm:no":
                return DialogResult.Cancelled;
        }

        if (IsEditing)
        {
            if (press.Key == "backspace")
            {
                InputText = InputText.Length > 0 ? InputText[..^1] : "";
                return DialogResult.Handled;
            }

            if (press.Text is { Length: > 0 } typed && press.IsPrintable)
            {
                InputText += typed;
                return DialogResult.Handled;
            }

            return DialogResult.Ignored;
        }

        if (!HideIndexes && press.Text is [>= '1' and <= '9'] digit && press.IsPrintable)
        {
            int wanted = digit[0] - '1';
            if (wanted < _options.Count && !_options[wanted].Disabled)
            {
                MoveTo(wanted);
                return DialogResult.Accept(_options[wanted].Value);
            }
        }

        return DialogResult.Ignored;
    }

    /// <summary>The rows as the reference draws them.</summary>
    public IReadOnlyList<string> Render(Ansi ansi)
    {
        var lines = new List<string>(_options.Count);
        for (int i = 0; i < _options.Count; i++)
        {
            var option = _options[i];
            bool selected = i == Index;
            var pointer = selected ? ansi.Color("suggestion", Glyphs.Pointer) : " ";
            var number = HideIndexes ? "" : ansi.Dim($" {i + 1}.");
            var label = option.Label;
            if (option.IsInput)
            {
                var text = selected ? InputText : "";
                label = text.Length > 0
                    ? label + " " + text
                    : label + " " + ansi.Dim(option.Placeholder ?? "");
            }

            if (option.Disabled)
            {
                label = ansi.Dim(label);
            }
            else if (selected)
            {
                label = ansi.Color("suggestion", label);
            }

            lines.Add($"{pointer}{number} {label}");
            if (selected && option.Description is { Length: > 0 } description)
            {
                lines.Add("    " + ansi.Dim(description));
            }
        }

        return lines;
    }
}
