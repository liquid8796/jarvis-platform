using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Documents;

/// <summary>
/// Reads embedded text and actual page rasters through the packaged PDFium runtime.
/// PDFium is process-global and not thread-safe, so a document owns the gate until
/// every native page/text/bitmap handle is closed. No script or form action runs.
/// </summary>
public static class PdfDocumentReader
{
    public const int MaximumPages = 20;
    public const int MaximumImageDimension = 1568;
    private const long MaximumFileBytes = 100 * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _initialized;

    public static async Task<ToolResult> ReadAsync(
        string path, string? pages, int maxTextChars, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return ToolResult.Error("The packaged PDF renderer requires Windows.");
        if (new FileInfo(path).Length > MaximumFileBytes)
            return ToolResult.Error("PDF exceeds the 100 MB reading limit. Split it into smaller documents first.");
        // Open before the native call; this also supports Unicode paths without
        // relying on PDFium's platform-dependent filename encoding.
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Read(bytes, path, pages, Math.Max(1000, maxTextChars), cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return ToolResult.Error("The packaged PDF renderer could not be loaded. Repair the app installation. " + ex.Message);
        }
        finally { Gate.Release(); }
    }

    private static ToolResult Read(byte[] bytes, string path, string? selection, int maxTextChars, CancellationToken token)
    {
        if (!_initialized)
        {
            Native.FPDF_InitLibrary();
            _initialized = true;
        }
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        nint document = 0;
        try
        {
            document = Native.FPDF_LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)bytes.Length, null);
            if (document == 0)
                throw new InvalidDataException(Native.FPDF_GetLastError() == 4
                    ? "PDF is password protected. Open an unencrypted copy to read its pages."
                    : "PDF could not be opened; the file is damaged or is not a valid PDF.");
            var count = Native.FPDF_GetPageCount(document);
            var (first, last) = SelectPages(selection, count);
            var text = new StringBuilder($"PDF: {path}\nPages {first}-{last} of {count}. " +
                "Page images follow in the same order as the page headings.\n");
            List<ImageBlock> images = [];
            var truncated = false;
            for (var number = first; number <= last; number++)
            {
                token.ThrowIfCancellationRequested();
                var page = Native.FPDF_LoadPage(document, number - 1);
                if (page == 0) throw new InvalidDataException($"PDF page {number} could not be loaded.");
                try
                {
                    var heading = $"\n--- Page {number} ---\n";
                    text.Append(heading);
                    var remaining = Math.Max(0, maxTextChars - text.Length - (last - number) * 40);
                    var pageText = ReadText(page, Math.Min(remaining, 1_000_000), out var pageTruncated);
                    if (pageText.Length > 0) text.Append(pageText);
                    else text.Append(remaining == 0 ? "[Text limit reached; see page image.]" : "[No embedded text; inspect the page image.]");
                    truncated |= pageTruncated;
                    images.Add(new ImageBlock("image/png", Convert.ToBase64String(Render(page))));
                }
                finally { Native.FPDF_ClosePage(page); }
            }
            if (truncated) text.Append("\n[Embedded text was truncated. Read a smaller page range for more text.]");
            return new ToolResult(text.ToString(), false, images);
        }
        finally
        {
            if (document != 0) Native.FPDF_CloseDocument(document);
            pinned.Free();
        }
    }

    internal static (int First, int Last) SelectPages(string? range, int pageCount)
    {
        if (pageCount <= 0) throw new InvalidDataException("PDF contains no readable pages.");
        if (string.IsNullOrWhiteSpace(range))
        {
            if (pageCount > 10)
                throw new ArgumentException($"PDF has {pageCount} pages. Specify 'pages' (for example '1-10'); read at most {MaximumPages} pages per request.");
            return (1, pageCount);
        }
        var parts = range.Trim().Split('-');
        if (parts.Length is < 1 or > 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
            (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new ArgumentException("Invalid PDF page range. Use a page number or a range, for example '3' or '1-5'.");
        var last = parts.Length == 1 ? first : int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (first < 1 || last < first || last > pageCount)
            throw new ArgumentException($"PDF page range '{range}' is outside the document's 1-{pageCount} pages.");
        if ((long)last - first + 1 > MaximumPages)
            throw new ArgumentException($"Read at most {MaximumPages} PDF pages per request.");
        return (first, last);
    }

    private static string ReadText(nint page, int maxChars, out bool truncated)
    {
        var textPage = Native.FPDFText_LoadPage(page);
        if (textPage == 0) throw new InvalidDataException("PDF page text could not be read.");
        try
        {
            var count = Math.Max(0, Native.FPDFText_CountChars(textPage));
            var take = Math.Min(count, maxChars);
            truncated = count > take;
            if (take == 0) return "";
            var buffer = new ushort[take + 1];
            var written = Native.FPDFText_GetText(textPage, 0, take, buffer);
            return new string(buffer.Take(Math.Clamp(written - 1, 0, take)).Select(c => (char)c).ToArray())
                .ReplaceLineEndings("\n");
        }
        finally { Native.FPDFText_ClosePage(textPage); }
    }

    private static byte[] Render(nint page)
    {
        var width = Native.FPDF_GetPageWidth(page);
        var height = Native.FPDF_GetPageHeight(page);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException("PDF page has invalid dimensions.");
        var scale = MaximumImageDimension / Math.Max(width, height);
        var pixelsWide = Math.Max(1, (int)Math.Round(width * scale));
        var pixelsHigh = Math.Max(1, (int)Math.Round(height * scale));
        var bitmap = Native.FPDFBitmap_Create(pixelsWide, pixelsHigh, 1);
        if (bitmap == 0) throw new InvalidDataException("PDF page bitmap could not be allocated.");
        try
        {
            Native.FPDFBitmap_FillRect(bitmap, 0, 0, pixelsWide, pixelsHigh, 0xffffffff);
            Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelsWide, pixelsHigh, 0, 1);
            var stride = Native.FPDFBitmap_GetStride(bitmap);
            if (stride < pixelsWide * 4 || stride > pixelsWide * 4 + 64)
                throw new InvalidDataException("PDF renderer returned an invalid bitmap stride.");
            var bgra = new byte[checked(stride * pixelsHigh)];
            Marshal.Copy(Native.FPDFBitmap_GetBuffer(bitmap), bgra, 0, bgra.Length);
            return PngBitmap.EncodeBgra(bgra, pixelsWide, pixelsHigh, stride);
        }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
    }

    private static class Native
    {
        private const string Library = "pdfium";
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern void FPDF_InitLibrary();
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern nint FPDF_LoadMemDocument64(nint data, nuint size, string? password);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern uint FPDF_GetLastError();
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern void FPDF_CloseDocument(nint document);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern int FPDF_GetPageCount(nint document);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern nint FPDF_LoadPage(nint document, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern void FPDF_ClosePage(nint page);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern double FPDF_GetPageWidth(nint page);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern double FPDF_GetPageHeight(nint page);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern nint FPDFText_LoadPage(nint page);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern void FPDFText_ClosePage(nint textPage);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern int FPDFText_CountChars(nint textPage);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern int FPDFText_GetText(nint textPage, int start, int count, [Out] ushort[] buffer);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern nint FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern void FPDFBitmap_Destroy(nint bitmap);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int left, int top, int width, int height, int rotate, int flags);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern nint FPDFBitmap_GetBuffer(nint bitmap);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)] internal static extern int FPDFBitmap_GetStride(nint bitmap);
    }
}
