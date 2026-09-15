using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace JarvisCode.App.Services;

/// <summary>One captured screencast frame: a JPEG and the time it was taken.</summary>
public readonly record struct GifFrame(byte[] Jpeg, double Timestamp);

/// <summary>An input event to draw over the frames around its timestamp.</summary>
public readonly record struct GifEvent(string Action, double Timestamp, double? X, double? Y, string? Label);

/// <summary>What the reference gif_creator's `options` switch on.</summary>
public sealed record GifOptions
{
    public bool ShowClickIndicators { get; init; } = true;
    public bool ShowActionLabels { get; init; } = true;
    public bool ShowProgressBar { get; init; } = true;
    public bool ShowWatermark { get; init; } = true;
    public bool ShowDragPaths { get; init; } = true;

    /// <summary>1 (coarse) to 10 (fine); scales the frames down as it drops.</summary>
    public int Quality { get; init; } = 7;
}

/// <summary>
/// Turns captured screencast frames plus the input events logged beside them
/// into one animated GIF, with the reference's overlays (click rings, action
/// labels, progress bar, watermark).
///
/// System.Drawing writes single-frame GIFs — palette and LZW included — so each
/// composed frame is encoded that way and the GIF89a stream is then assembled
/// from those parts: screen descriptor, NETSCAPE looping block, and per frame a
/// graphic control extension carrying its own delay.
/// </summary>
public static class GifComposer
{
    private const int MaxWidth = 800;
    private const double DefaultDelaySeconds = 0.5;
    internal const int MaxFrames = 120;

    private static readonly Color Accent = Color.FromArgb(204, 120, 92);

    /// <summary>Composes the GIF. Throws ArgumentException when there is nothing to compose.</summary>
    public static byte[] Compose(IReadOnlyList<GifFrame> frames, IReadOnlyList<GifEvent> events, GifOptions options)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("No frames were recorded.", nameof(frames));
        }

        var selected = Subsample(frames, MaxFrames);
        var scale = Math.Clamp(options.Quality, 1, 10) / 10d;
        var encoded = new List<(byte[] Gif, int Delay)>(selected.Count);

        using var first = Decode(selected[0].Jpeg);
        var width = Math.Max(1, (int)(Math.Min(first.Width, MaxWidth) * scale));
        var height = Math.Max(1, (int)(first.Height * ((double)width / first.Width)));
        var span = selected[^1].Timestamp - selected[0].Timestamp;

        for (var i = 0; i < selected.Count; i++)
        {
            var frame = selected[i];
            var next = i + 1 < selected.Count ? selected[i + 1].Timestamp : frame.Timestamp + DefaultDelaySeconds;
            var progress = span > 0 ? (frame.Timestamp - selected[0].Timestamp) / span : 1;

            using var source = i == 0 ? CloneOf(first) : Decode(frame.Jpeg);
            using var composed = Compose(source, width, height, frame.Timestamp, events, options, progress);
            encoded.Add((EncodeSingleFrameGif(composed), DelayHundredths(next - frame.Timestamp)));
        }

        return Assemble(encoded, width, height);
    }

    /// <summary>Keeps the ends and thins the middle evenly when there are too many frames.</summary>
    internal static List<GifFrame> Subsample(IReadOnlyList<GifFrame> frames, int max)
    {
        if (frames.Count <= max)
        {
            return [.. frames];
        }

        var kept = new List<GifFrame>(max);
        for (var i = 0; i < max; i++)
        {
            kept.Add(frames[(int)((long)i * (frames.Count - 1) / (max - 1))]);
        }

        return kept;
    }

    /// <summary>GIF delays are hundredths of a second; browsers floor anything under 2.</summary>
    internal static int DelayHundredths(double seconds)
        => Math.Clamp((int)Math.Round(seconds * 100), 2, 1000);

    private static Bitmap Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static Bitmap CloneOf(Bitmap source) => new(source);

    private static Bitmap Compose(
        Bitmap source, int width, int height, double timestamp,
        IReadOnlyList<GifEvent> events, GifOptions options, double progress)
    {
        var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));

        var scaleX = (double)width / source.Width;
        var scaleY = (double)height / source.Height;

        // Events stay on screen for a moment after they happen, like the reference.
        foreach (var change in events)
        {
            var age = timestamp - change.Timestamp;
            if (age < 0 || age > 0.6)
            {
                continue;
            }

            if (options.ShowClickIndicators && change.X is { } x && change.Y is { } y &&
                (options.ShowDragPaths || change.Action != "left_click_drag"))
            {
                DrawClickIndicator(graphics, (float)(x * scaleX), (float)(y * scaleY), age);
            }

            if (options.ShowActionLabels)
            {
                DrawActionLabel(graphics, width, change.Label ?? change.Action);
            }
        }

        if (options.ShowProgressBar)
        {
            using var track = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
            using var fill = new SolidBrush(Accent);
            graphics.FillRectangle(track, 0, height - 4, width, 4);
            graphics.FillRectangle(fill, 0, height - 4, (float)(width * Math.Clamp(progress, 0, 1)), 4);
        }

        if (options.ShowWatermark)
        {
            using var font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
            const string mark = "Jarvis Code";
            var size = graphics.MeasureString(mark, font);
            using var plate = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
            using var text = new SolidBrush(Color.White);
            graphics.FillRectangle(plate, width - size.Width - 12, height - size.Height - 12, size.Width + 8, size.Height + 2);
            graphics.DrawString(mark, font, text, width - size.Width - 8, height - size.Height - 11);
        }

        return canvas;
    }

    private static void DrawClickIndicator(Graphics graphics, float x, float y, double age)
    {
        var radius = (float)(10 + age * 30);
        var alpha = (int)Math.Clamp(220 * (1 - age / 0.6), 0, 255);
        using var ring = new Pen(Color.FromArgb(alpha, Accent), 3f);
        graphics.DrawEllipse(ring, x - radius, y - radius, radius * 2, radius * 2);
        using var dot = new SolidBrush(Color.FromArgb(alpha, Accent));
        graphics.FillEllipse(dot, x - 4, y - 4, 8, 8);
    }

    private static void DrawActionLabel(Graphics graphics, int width, string label)
    {
        using var font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        var size = graphics.MeasureString(label, font);
        var boxWidth = Math.Min(size.Width + 14, width - 16);
        using var plate = new SolidBrush(Color.FromArgb(190, Accent));
        using var text = new SolidBrush(Color.White);
        graphics.FillRectangle(plate, 8, 8, boxWidth, size.Height + 4);
        graphics.DrawString(label, font, text, 15, 10);
    }

    private static byte[] EncodeSingleFrameGif(Bitmap frame)
    {
        using var stream = new MemoryStream();
        frame.Save(stream, ImageFormat.Gif);
        return stream.ToArray();
    }

    // ---- GIF89a assembly -------------------------------------------------

    /// <summary>
    /// Splices the single-frame GIFs into one animation: each frame keeps its
    /// own palette as a local color table, and gains a graphic control
    /// extension carrying its delay.
    /// </summary>
    internal static byte[] Assemble(IReadOnlyList<(byte[] Gif, int Delay)> frames, int width, int height)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        writer.Write("GIF89a"u8);
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((byte)0x70); // no global color table, 8-bit color resolution
        writer.Write((byte)0);    // background color index
        writer.Write((byte)0);    // default pixel aspect ratio

        // NETSCAPE2.0 application extension: loop forever.
        writer.Write((byte)0x21);
        writer.Write((byte)0xFF);
        writer.Write((byte)11);
        writer.Write("NETSCAPE2.0"u8);
        writer.Write((byte)3);
        writer.Write((byte)1);
        writer.Write((ushort)0);
        writer.Write((byte)0);

        foreach (var (gif, delay) in frames)
        {
            var parsed = ParseSingleFrame(gif);

            // Graphic control extension — delay, and leave the frame in place.
            writer.Write((byte)0x21);
            writer.Write((byte)0xF9);
            writer.Write((byte)4);
            writer.Write((byte)0x04); // disposal method 1 (do not dispose), no transparency
            writer.Write((ushort)delay);
            writer.Write((byte)0);    // transparent color index (unused)
            writer.Write((byte)0);

            // Image descriptor, rewritten to carry this frame's palette locally.
            writer.Write((byte)0x2C);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)parsed.Width);
            writer.Write((ushort)parsed.Height);
            writer.Write((byte)(0x80 | (parsed.PaletteBits - 1))); // local color table, its size
            writer.Write(parsed.Palette);
            writer.Write(parsed.ImageData);
        }

        writer.Write((byte)0x3B); // trailer
        writer.Flush();
        return output.ToArray();
    }

    private readonly record struct SingleFrame(
        int Width, int Height, byte[] Palette, int PaletteBits, byte[] ImageData);

    /// <summary>
    /// Reads the parts we need out of a single-frame GIF: its global palette
    /// and the LZW image data (from the image descriptor's minimum code size
    /// through the block terminator).
    /// </summary>
    private static SingleFrame ParseSingleFrame(byte[] gif)
    {
        if (gif.Length < 14 || gif[0] != 'G' || gif[1] != 'I' || gif[2] != 'F')
        {
            throw new InvalidDataException("The encoder did not produce a GIF.");
        }

        var width = gif[6] | (gif[7] << 8);
        var height = gif[8] | (gif[9] << 8);
        var packed = gif[10];
        var paletteBits = (packed & 0x07) + 1;
        var offset = 13;
        byte[] palette = [];
        if ((packed & 0x80) != 0)
        {
            var entries = 1 << paletteBits;
            palette = gif[offset..(offset + entries * 3)];
            offset += entries * 3;
        }

        // Walk the blocks to the first image descriptor.
        while (offset < gif.Length)
        {
            switch (gif[offset])
            {
                case 0x21: // extension: skip its label and sub-blocks
                    offset += 2;
                    offset = SkipSubBlocks(gif, offset);
                    break;

                case 0x2C:
                {
                    var descriptorPacked = gif[offset + 9];
                    var imageWidth = gif[offset + 5] | (gif[offset + 6] << 8);
                    var imageHeight = gif[offset + 7] | (gif[offset + 8] << 8);
                    offset += 10;
                    if ((descriptorPacked & 0x80) != 0)
                    {
                        // A local table here wins over the global one.
                        paletteBits = (descriptorPacked & 0x07) + 1;
                        var entries = 1 << paletteBits;
                        palette = gif[offset..(offset + entries * 3)];
                        offset += entries * 3;
                    }

                    var dataStart = offset;
                    offset = SkipSubBlocks(gif, offset + 1); // past the LZW minimum code size
                    if (palette.Length == 0)
                    {
                        throw new InvalidDataException("The encoded frame carries no palette.");
                    }

                    return new SingleFrame(
                        imageWidth == 0 ? width : imageWidth,
                        imageHeight == 0 ? height : imageHeight,
                        palette, paletteBits, gif[dataStart..offset]);
                }

                default:
                    throw new InvalidDataException($"Unexpected GIF block 0x{gif[offset]:X2}.");
            }
        }

        throw new InvalidDataException("The encoded frame has no image data.");
    }

    /// <summary>Advances past a chain of length-prefixed sub-blocks, terminator included.</summary>
    private static int SkipSubBlocks(byte[] data, int offset)
    {
        while (offset < data.Length && data[offset] != 0)
        {
            offset += data[offset] + 1;
        }

        return Math.Min(offset + 1, data.Length);
    }
}
