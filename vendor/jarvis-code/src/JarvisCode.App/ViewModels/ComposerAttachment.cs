namespace JarvisCode.App.ViewModels;

public enum ComposerAttachmentKind
{
    /// <summary>Transcript text attached via right-click / selection → "Attach as context".</summary>
    Context,

    /// <summary>A long paste that became a chip instead of flooding the input box (Chat surface only).</summary>
    PastedText,

    /// <summary>An image (pasted, dropped, or picked) sent to the model as an image block.</summary>
    Image,

    /// <summary>A non-image file attached as a chip, serialized as a leading @"path" mention on send.</summary>
    File,

    /// <summary>A folder attached as a chip, serialized as @"path/" on send (Code surface only).</summary>
    Folder,
}

/// <summary>
/// One chip above the composer input: attached context, pasted text, an image,
/// a file, or a folder. Rides the view model so it survives a session switch,
/// and is folded into the outgoing message on send.
/// </summary>
public sealed class ComposerAttachment
{
    public required ComposerAttachmentKind Kind { get; init; }

    /// <summary>The attached text for Context and PastedText.</summary>
    public string Text { get; init; } = "";

    /// <summary>The backing file for Image, File and Folder.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Display and mention path for File/Folder chips: relative to the working
    /// directory when the path lives under it, absolute otherwise.
    /// </summary>
    public string? RelativePath { get; init; }

    /// <summary>Images render as thumbnails in the composer, like the reference.</summary>
    public bool IsImage => Kind == ComposerAttachmentKind.Image;

    /// <summary>
    /// The path inside the serialized @"…" mention, reference-style: quotes
    /// stripped, trailing separators stripped, folders keep a trailing slash.
    /// </summary>
    public string MentionPath =>
        (RelativePath ?? FilePath ?? "").Replace("\"", "").TrimEnd('/', '\\')
        + (Kind == ComposerAttachmentKind.Folder ? "/" : "");

    public string Label => Kind switch
    {
        ComposerAttachmentKind.PastedText => "Pasted text",
        ComposerAttachmentKind.Image => System.IO.Path.GetFileName(FilePath) is { Length: > 0 } name ? name : "Image",
        ComposerAttachmentKind.File or ComposerAttachmentKind.Folder =>
            RelativePath is { Length: > 0 } rel ? rel : FileName,
        _ => Preview,
    };

    /// <summary>Segoe MDL2 glyph for the chip icon.</summary>
    public string Glyph => Kind switch
    {
        ComposerAttachmentKind.Image => "",
        ComposerAttachmentKind.PastedText => "",
        ComposerAttachmentKind.File => "",
        ComposerAttachmentKind.Folder => "",
        _ => "",
    };

    public string Tooltip => Kind switch
    {
        ComposerAttachmentKind.Image or ComposerAttachmentKind.File or ComposerAttachmentKind.Folder =>
            FilePath ?? Label,
        _ => Text.Length > 1200 ? Text[..1200] + "…" : Text,
    };

    public string FileName =>
        System.IO.Path.GetFileName((FilePath ?? "").TrimEnd('\\', '/')) is { Length: > 0 } name
            ? name
            : FilePath ?? "";

    private string Preview
    {
        get
        {
            var line = Text.ReplaceLineEndings(" ").Trim();
            return line.Length > 34 ? line[..34] + "…" : line.Length > 0 ? line : "Attached context";
        }
    }
}
