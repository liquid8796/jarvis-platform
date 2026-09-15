using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace JarvisCode.Core.Documents;

/// <summary>
/// Extracts the readable text of the zipped-XML Office formats (xlsx/xlsm,
/// docx, pptx) with nothing but the framework: the agent's Read only
/// handles text, and these files are zip containers, so without this they
/// reach the model as binary noise.
///
/// Elements are matched by local name, never by namespace URI, because strict
/// OOXML files use a different namespace family (purl.oclc.org) than the
/// transitional ones every generator emits — matching the URI would silently
/// return nothing for strict documents.
/// </summary>
public static class OfficeDocumentReader
{
    /// <summary>Rows per sheet kept before truncating.</summary>
    private const int MaxRowsPerSheet = 5_000;

    /// <summary>Spreadsheet column ceiling (the format's own limit); guards a corrupt cell reference.</summary>
    private const int MaxColumns = 16_384;

    public static readonly string[] SupportedExtensions = [".xlsx", ".xlsm", ".docx", ".pptx"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the document's text. Throws <see cref="NotSupportedException"/> for
    /// other extensions and <see cref="InvalidDataException"/> for a file that is
    /// not a readable OOXML container.
    /// </summary>
    public static string Extract(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!IsSupported(path))
        {
            throw new NotSupportedException(
                $"'{extension}' is not a supported document format. Supported: {string.Join(", ", SupportedExtensions)}.");
        }

        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return extension switch
        {
            ".xlsx" or ".xlsm" => ExtractWorkbook(archive),
            ".docx" => ExtractWordDocument(archive),
            _ => ExtractPresentation(archive),
        };
    }

    // ---- spreadsheets ----

    private static string ExtractWorkbook(ZipArchive archive)
    {
        var shared = ReadSharedStrings(archive);
        var text = new StringBuilder();
        foreach (var (name, entryPath) in WorkbookSheets(archive))
        {
            if (Load(archive, entryPath) is not { } sheet)
                continue;
            if (text.Length > 0)
                text.AppendLine();
            text.AppendLine($"# Sheet: {name}");

            int rowCount = 0;
            foreach (var row in Descendants(sheet.Root, "row"))
            {
                if (++rowCount > MaxRowsPerSheet)
                {
                    text.AppendLine($"… (sheet truncated at {MaxRowsPerSheet} rows)");
                    break;
                }

                text.AppendLine(RenderRow(row, shared));
            }

            if (rowCount == 0)
                text.AppendLine("(empty sheet)");
        }

        return text.Length == 0 ? "" : text.ToString().TrimEnd();
    }

    /// <summary>Sheet names in workbook order, paired with their part path via the rels file.</summary>
    private static List<(string Name, string Path)> WorkbookSheets(ZipArchive archive)
    {
        var sheets = new List<(string, string)>();
        var workbook = Load(archive, "xl/workbook.xml");
        var rels = Load(archive, "xl/_rels/workbook.xml.rels");
        if (workbook is not null && rels is not null)
        {
            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relationship in Descendants(rels.Root, "Relationship"))
            {
                var id = relationship.Attribute("Id")?.Value;
                var target = relationship.Attribute("Target")?.Value;
                if (id is not null && target is not null)
                    targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target.Replace("../", "");
            }

            foreach (var sheet in Descendants(workbook.Root, "sheet"))
            {
                var name = sheet.Attribute("name")?.Value ?? "Sheet";
                // r:id — the attribute's namespace varies, so match on local name.
                var id = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
                if (id is not null && targets.TryGetValue(id, out var target))
                    sheets.Add((name, target));
            }
        }

        if (sheets.Count > 0)
            return sheets;

        // A workbook without a readable rels part still has its sheet parts.
        return [.. archive.Entries
            .Where(static e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
                               e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static e => NumberIn(e.FullName))
            .Select(static e => (Path.GetFileNameWithoutExtension(e.FullName), e.FullName))];
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var strings = new List<string>();
        if (Load(archive, "xl/sharedStrings.xml") is not { } document)
            return strings;
        foreach (var item in Descendants(document.Root, "si"))
        {
            // Rich text splits one string across several runs; join every t.
            strings.Add(string.Concat(Descendants(item, "t").Select(static t => t.Value)));
        }

        return strings;
    }

    private static string RenderRow(XElement row, List<string> shared)
    {
        var cells = new SortedDictionary<int, string>();
        foreach (var cell in Descendants(row, "c"))
        {
            var reference = cell.Attribute("r")?.Value;
            int column = reference is null ? cells.Count + 1 : ColumnIndex(reference);
            if (column <= MaxColumns)
                cells[column] = CellText(cell, shared);
        }

        if (cells.Count == 0)
            return "";

        // Place each cell at its own column so gaps stay gaps: A and C with B
        // empty must read "a\t\tc", not "a\tc".
        var values = new string[cells.Keys.Max()];
        foreach (var (column, value) in cells)
            values[column - 1] = value;
        return string.Join('\t', values.Select(static v => v ?? "")).TrimEnd('\t');
    }

    private static string CellText(XElement cell, List<string> shared)
    {
        var type = cell.Attribute("t")?.Value;
        switch (type)
        {
            case "s":
                var raw = Descendants(cell, "v").FirstOrDefault()?.Value;
                return int.TryParse(raw, out int index) && index >= 0 && index < shared.Count
                    ? shared[index]
                    : "";
            case "inlineStr":
                return string.Concat(Descendants(cell, "t").Select(static t => t.Value));
            case "b":
                return Descendants(cell, "v").FirstOrDefault()?.Value == "1" ? "TRUE" : "FALSE";
            default:
                // Numbers, dates (serial numbers) and cached formula results.
                return Descendants(cell, "v").FirstOrDefault()?.Value ?? "";
        }
    }

    /// <summary>"BC12" → 55. Returns 1 when the reference has no letters.</summary>
    internal static int ColumnIndex(string cellReference)
    {
        int index = 0;
        foreach (char c in cellReference)
        {
            if (!char.IsAsciiLetter(c))
                break;
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return index == 0 ? 1 : index;
    }

    // ---- word documents ----

    private static string ExtractWordDocument(ZipArchive archive)
    {
        if (Load(archive, "word/document.xml") is not { Root: { } root })
            throw new InvalidDataException("The .docx file has no word/document.xml part.");

        var body = Descendants(root, "body").FirstOrDefault() ?? root;
        var text = new StringBuilder();
        foreach (var element in body.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "p":
                    text.AppendLine(ParagraphText(element));
                    break;
                case "tbl":
                    foreach (var row in Descendants(element, "tr"))
                    {
                        var cells = Descendants(row, "tc")
                            .Select(cell => string.Join(' ', Descendants(cell, "p").Select(ParagraphText)).Trim());
                        text.AppendLine(string.Join('\t', cells));
                    }

                    text.AppendLine();
                    break;
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>A w:p's text, with tabs and line breaks preserved.</summary>
    private static string ParagraphText(XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            switch (node.Name.LocalName)
            {
                case "t":
                    text.Append(node.Value);
                    break;
                case "tab":
                    text.Append('\t');
                    break;
                case "br":
                case "cr":
                    text.Append('\n');
                    break;
            }
        }

        return text.ToString();
    }

    // ---- presentations ----

    private static string ExtractPresentation(ZipArchive archive)
    {
        var slides = archive.Entries
            .Where(static e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                               e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            // slide10 must not sort before slide2, so order by the number in the name.
            .OrderBy(static e => NumberIn(e.FullName))
            .ToList();

        var text = new StringBuilder();
        foreach (var slide in slides)
        {
            int number = NumberIn(slide.FullName);
            if (text.Length > 0)
                text.AppendLine();
            text.AppendLine($"# Slide {number}");
            if (Load(archive, slide.FullName) is { Root: { } root })
            {
                foreach (var paragraph in Descendants(root, "p"))
                {
                    var line = string.Concat(Descendants(paragraph, "t").Select(static t => t.Value));
                    if (line.Trim().Length > 0)
                        text.AppendLine(line);
                }
            }

            var notesPath = $"ppt/notesSlides/notesSlide{number}.xml";
            if (Load(archive, notesPath) is { Root: { } notesRoot })
            {
                var notes = Descendants(notesRoot, "p")
                    .Select(p => string.Concat(Descendants(p, "t").Select(static t => t.Value)))
                    .Where(static line => line.Trim().Length > 0)
                    .ToList();
                // The notes part repeats the slide number as its own text run; skip a
                // notes block that carries nothing else.
                if (notes.Any(line => line.Trim() != number.ToString()))
                {
                    text.AppendLine("Notes:");
                    foreach (var line in notes.Where(line => line.Trim() != number.ToString()))
                        text.AppendLine("  " + line);
                }
            }
        }

        return text.ToString().TrimEnd();
    }

    // ---- shared helpers ----

    private static XDocument? Load(ZipArchive archive, string entryPath)
    {
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Equals(entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        try
        {
            using var stream = entry.Open();
            // The file comes from the user's disk, not from us: no DTDs, no
            // external entity resolution.
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return null; // a malformed part must not sink the whole document
        }
    }

    private static IEnumerable<XElement> Descendants(XElement? root, string localName) =>
        root is null ? [] : root.Descendants().Where(e => e.Name.LocalName == localName);

    /// <summary>The first run of digits in a part name ("slide10.xml" → 10).</summary>
    private static int NumberIn(string name)
    {
        var digits = new string([.. Path.GetFileNameWithoutExtension(name).Where(char.IsAsciiDigit)]);
        return int.TryParse(digits, out int number) ? number : 0;
    }
}
