using JarvisCode.Cli.Repl.Render;

namespace JarvisCode.Cli.Repl.Dialogs;

internal sealed class ScrollableDocument(string title, IReadOnlyList<string> lines)
{
    public string Title { get; } = title;
    public IReadOnlyList<string> Lines { get; } = lines;
    public int Offset { get; private set; }

    public bool Scroll(string? action, int height)
    {
        var page = Math.Max(1, height - 3);
        var next = action switch
        {
            "scroll:top" => 0, "scroll:bottom" => Math.Max(0, Lines.Count - page),
            "scroll:lineUp" => Offset - 1, "scroll:lineDown" => Offset + 1,
            "scroll:pageUp" or "scroll:fullPageUp" => Offset - page,
            "scroll:pageDown" or "scroll:fullPageDown" => Offset + page,
            "scroll:halfPageUp" => Offset - Math.Max(1, page / 2),
            "scroll:halfPageDown" => Offset + Math.Max(1, page / 2), _ => (int?)null,
        };
        if (next is null) return false;
        Offset = Math.Clamp(next.Value, 0, Math.Max(0, Lines.Count - page));
        return true;
    }

    public IReadOnlyList<string> Render(Ansi ansi, int columns, int height)
    {
        var page = Math.Max(1, height - 3);
        return [ansi.Bold(TextWidth.Truncate(Title, columns, "…")),
            .. Lines.Skip(Offset).Take(page).Select(line => TextWidth.Truncate(line, columns, "…")),
            ansi.Dim($"{Math.Min(Offset + 1, Lines.Count)}–{Math.Min(Lines.Count, Offset + page)} / {Lines.Count}  · ↑/↓ scroll · PgUp/PgDn page · Esc close")];
    }
}

internal sealed record DiffFile(string Name, IReadOnlyList<string> Lines);
internal sealed record DiffSource(string Name, IReadOnlyList<DiffFile> Files);

internal sealed class DiffViewer(IReadOnlyList<DiffSource> sources)
{
    public int SourceIndex { get; private set; }
    public int FileIndex { get; private set; }
    public bool Details { get; private set; }
    private ScrollableDocument? _document;
    private DiffSource Source => sources[SourceIndex];
    private void Reset() => _document = null;
    private ScrollableDocument Document => _document ??= new ScrollableDocument(
        Source.Files.Count > 0 ? Source.Files[FileIndex].Name : "No changes",
        Source.Files.Count > 0 ? Source.Files[FileIndex].Lines : []);

    public bool Handle(string? action, int height)
    {
        if (action == "diff:dismiss")
        { if (!Details) return false; Details = false; return true; }
        if (action is "diff:previousSource" or "diff:nextSource" or "app:cycleDiffBase")
        {
            SourceIndex = (SourceIndex + (action == "diff:previousSource" ? -1 : 1) + sources.Count) % sources.Count;
            FileIndex = 0; Reset(); return true;
        }
        if (action is "diff:previousFile" or "diff:nextFile")
        {
            if (Details) Document.Scroll(action == "diff:previousFile" ? "scroll:lineUp" : "scroll:lineDown", height - 2);
            else
            { FileIndex = Math.Clamp(FileIndex + (action == "diff:previousFile" ? -1 : 1), 0, Math.Max(0, Source.Files.Count - 1)); Reset(); }
            return true;
        }
        if (action == "diff:viewDetails") { Details = !Details; return true; }
        Document.Scroll(action, height - 2); return true;
    }

    public IReadOnlyList<string> Render(Ansi ansi, int columns, int height)
    {
        var lines = new List<string> { string.Join("  ", sources.Select((source, index) =>
            index == SourceIndex ? ansi.Bold("[" + source.Name + "]") : source.Name)) };
        var width = Details || columns < 80 ? 0 : Math.Min(30, columns / 3);
        var patch = Document.Render(ansi, Math.Max(1, columns - width - (width > 0 ? 3 : 0)), Math.Max(4, height - 2));
        for (var index = 0; index < patch.Count; index++)
        {
            var fileIndex = Math.Max(0, FileIndex - 3) + index;
            var left = width == 0 ? "" : fileIndex < Source.Files.Count
                ? TextWidth.Truncate((fileIndex == FileIndex ? "> " : "  ") + Source.Files[fileIndex].Name, width, "…") : "";
            var line = patch[index];
            line = line.StartsWith('+') ? ansi.Color("diffAdded", line) : line.StartsWith('-') ? ansi.Color("diffRemoved", line) : line;
            lines.Add(width > 0 ? left.PadRight(width) + " │ " + line : line);
        }
        lines.Add(ansi.Dim("←/→ source · ↑/↓ " + (Details ? "scroll" : "file") + " · Enter " + (Details ? "file list" : "details") + " · Esc back"));
        return lines;
    }
}
