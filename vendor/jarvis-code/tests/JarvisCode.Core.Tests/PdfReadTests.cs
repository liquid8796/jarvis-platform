using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Documents;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Tests;

public sealed class PdfReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-pdf-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Read_returns_only_selected_page_text_and_real_page_pixels()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "document.pdf");
        await File.WriteAllBytesAsync(file, Fixture());
        var tool = new ReadFileTool();
        var context = new ToolExecutionContext { WorkingDirectory = _root };
        var first = await tool.ExecuteAsync(new JsonObject { ["file_path"] = file, ["pages"] = "1" }, context, default);
        var second = await tool.ExecuteAsync(new JsonObject { ["file_path"] = file, ["pages"] = "2" }, context, default);
        Assert.False(first.IsError, first.Content);
        Assert.False(second.IsError, second.Content);
        Assert.Contains("First page", first.Content);
        Assert.DoesNotContain("Second page", first.Content);
        Assert.Contains("Second page", second.Content);
        Assert.DoesNotContain("First page", second.Content);
        Assert.Contains("Page 2", second.Content);
        var image1 = Convert.FromBase64String(Assert.Single(first.Images!).Base64Data);
        var image2 = Convert.FromBase64String(Assert.Single(second.Images!).Base64Data);
        var (width, height, pixels) = DecodeRgb(image1);
        Assert.Equal(PdfDocumentReader.MaximumImageDimension, Math.Max(width, height));
        Assert.True(pixels.Chunk(3).Any(rgb => rgb[0] > 200 && rgb[1] < 30 && rgb[2] < 30), "Page 1 must contain its red rectangle.");
        var blue = DecodeRgb(image2).Pixels;
        Assert.True(blue.Chunk(3).Any(rgb => rgb[2] > 200 && rgb[0] < 30 && rgb[1] < 30), "Page 2 must contain its blue rectangle.");
        Assert.NotEqual(image1, image2);
        if (Environment.GetEnvironmentVariable("JARVIS_PDF_TEST_IMAGE_DIR") is { Length: > 0 } preview)
        {
            Directory.CreateDirectory(preview);
            await File.WriteAllBytesAsync(Path.Combine(preview, "page-1.png"), image1);
            await File.WriteAllBytesAsync(Path.Combine(preview, "page-2.png"), image2);
        }
    }

    [Fact]
    public async Task Invalid_pdf_and_page_ranges_are_errors_not_successful_empty_reads()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "invalid.pdf");
        await File.WriteAllTextAsync(file, "not a PDF");
        var result = await PdfDocumentReader.ReadAsync(file, "1", 60000, default);
        Assert.True(result.IsError);
        Assert.Contains("not a valid PDF", result.Content);
        Assert.Throws<ArgumentException>(() => PdfDocumentReader.SelectPages(null, 11));
        Assert.Throws<ArgumentException>(() => PdfDocumentReader.SelectPages("1-21", 30));
        foreach (var range in new[] { "0", "2-1", "1-4", "1,2", "-1", "x", "1-2-3" })
            Assert.Throws<ArgumentException>(() => PdfDocumentReader.SelectPages(range, 3));
        Assert.Equal((2, 3), PdfDocumentReader.SelectPages("2-3", 3));
    }

    [Fact]
    public async Task Cancelled_read_never_returns_partial_success()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "document.pdf");
        await File.WriteAllBytesAsync(file, Fixture());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PdfDocumentReader.ReadAsync(file, "1", 60000, cancellation.Token));
    }

    // A complete two-page PDF with independently observable text and coloured
    // drawings; native PDFium, rather than a stub reader, interprets these bytes.
    private static byte[] Fixture()
    {
        const string first = "1 0 0 rg 20 20 100 100 re f\n0 0 0 rg BT /F1 20 Tf 20 170 Td (First page) Tj ET\n";
        const string second = "0 0 1 rg 20 20 100 100 re f\n0 0 0 rg BT /F1 20 Tf 20 170 Td (Second page) Tj ET\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 5 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {first.Length} >>\nstream\n{first}endstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
            $"<< /Length {second.Length} >>\nstream\n{second}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var result = new StringBuilder("%PDF-1.4\n");
        List<int> offsets = [];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(result.Length);
            result.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = result.Length;
        result.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) result.Append($"{offset:0000000000} 00000 n \n");
        result.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(result.ToString());
    }

    private static (int Width, int Height, byte[] Pixels) DecodeRgb(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        using var compressed = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT")
                compressed.Write(png, offset + 8, length);
            offset += length + 12;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        var rows = decoded.ToArray();
        Assert.Equal((width * 3 + 1) * height, rows.Length);
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, rows[y * (width * 3 + 1)]);
            rows.AsSpan(y * (width * 3 + 1) + 1, width * 3).CopyTo(rgb.AsSpan(y * width * 3));
        }
        return (width, height, rgb);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
