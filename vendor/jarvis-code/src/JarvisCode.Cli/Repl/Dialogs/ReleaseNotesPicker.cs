using JarvisCode.App.Services;
using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Dialogs;

/// <summary>
/// The reference's release-notes picker (CLI 2.1.257, the module exporting
/// <c>ReleaseNotesPicker</c> at 210560000): a "Show all" row carrying the
/// version count over one row per version, newest first, with its hint above the
/// list and ten rows visible. Choosing a row prints the notes as a notice and
/// closes.
/// </summary>
internal sealed class ReleaseNotesPicker
{
    /// <summary>The value the "Show all" row carries.</summary>
    public const string ShowAllValue = "__show_all__";

    private readonly IReadOnlyList<ReleaseNote> _notes;
    private readonly SelectList _list;
    private readonly Ansi _ansi;

    public ReleaseNotesPicker(IReadOnlyList<ReleaseNote> notes, Ansi ansi)
    {
        _notes = ReleaseNotes.Newest(notes);
        _ansi = ansi;
        List<SelectOption> options =
        [
            new(ShowAllValue, ReleaseNotes.ShowAll, ReleaseNotes.VersionCount(_notes.Count)),
            .. _notes.Select(static n => new SelectOption(n.Version, n.Version)),
        ];
        _list = new SelectList(options);
    }

    /// <summary>The key context this dialog routes under, which is the select list's.</summary>
    public string Context => "Select";

    /// <summary>The notes the chosen row prints, or null when it chose nothing.</summary>
    public string? Chosen(string value) =>
        value == ShowAllValue
            ? ReleaseNotes.FormatAll(_notes)
            : _notes.FirstOrDefault(n => n.Version == value) is { } note
                ? ReleaseNotes.FormatVersion(note)
                : null;

    public DialogResult Handle(string? action, KeyPress press) => _list.Handle(action, press);

    /// <summary>
    /// The dialog as the reference draws it: its title, the hint, then the rows
    /// windowed to <see cref="ReleaseNotes.VisibleOptionCount"/> around the
    /// pointer.
    /// </summary>
    public IReadOnlyList<string> Render(int width)
    {
        _ = width;
        var lines = new List<string>
        {
            _ansi.Bold(ReleaseNotes.Title),
            "",
            _ansi.Dim(ReleaseNotes.SelectHint),
            "",
        };

        var options = _list.Options;
        var first = Math.Clamp(
            _list.Index - (ReleaseNotes.VisibleOptionCount / 2),
            0,
            Math.Max(0, options.Count - ReleaseNotes.VisibleOptionCount));
        for (int i = first; i < options.Count && i < first + ReleaseNotes.VisibleOptionCount; i++)
        {
            var selected = i == _list.Index;
            var pointer = selected ? _ansi.Color("suggestion", Glyphs.Pointer) : " ";
            var label = selected ? _ansi.Color("suggestion", options[i].Label) : options[i].Label;
            lines.Add($"{pointer} {label}");
            if (selected && options[i].Description is { Length: > 0 } description)
            {
                lines.Add("    " + _ansi.Dim(description));
            }
        }

        return lines;
    }
}
