namespace JarvisCode.App.Services;

/// <summary>
/// The reference's "Transcript width" choices as pixels, measured out of the
/// desktop's epitaxy stylesheet (<c>c6a992d55-iGxCOsRk.css</c>): the transcript
/// and composer columns cap at <c>--max-content-width</c> — 768px by default (the
/// "s" choice), 960px under <c>[data-transcript-width=m]</c> and 1280px under
/// <c>[data-transcript-width=l]</c> — plus a 32px gutter on each side
/// (<c>--chat-gutter</c>), which is what the column's own max-width includes.
/// </summary>
public static class TranscriptWidths
{
    public const double Gutter = 32;

    public const string Narrow = "s";
    public const string Medium = "m";
    public const string Wide = "l";

    /// <summary>The content width the choice names; an unknown choice reads as Narrow, like the reference's null.</summary>
    public static double ContentWidth(string? choice) => choice switch
    {
        Medium => 960,
        Wide => 1280,
        _ => 768,
    };

    /// <summary>The column's max-width: the content plus both gutters.</summary>
    public static double MaxWidth(string? choice) => ContentWidth(choice) + Gutter * 2;
}
