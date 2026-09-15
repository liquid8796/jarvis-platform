using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's display-text sanitizer (app.asar <c>index.chunk-DnlgCaT3.js</c>,
/// its <c>ve</c>), applied to session titles and folder names before they become an
/// OS label. It matters because those strings are not the app's: a repository whose
/// name carries a right-to-left override or a zero-width joiner would otherwise
/// reorder or pad the row it lands in.
///
/// NFKC, then the format, private-use, unassigned and surrogate characters go —
/// except the four the reference keeps, which are the ones that change how a real
/// character renders (ZWNJ, ZWJ and the two variation selectors) — then the control
/// characters except the three whitespace ones, then an explicit list of invisibles
/// and bidi controls. Repeated until it stops changing, because one pass's
/// normalization can uncover a character the pass before it hid.
///
/// The category filtering is a code-point scan rather than a regex because the
/// reference's runs under the <c>u</c> flag: .NET's <c>\p{Cs}</c> matches each half
/// of a surrogate pair on its own, so a regex port of the same class deletes every
/// astral character — every emoji in a session title — instead of the lone
/// surrogates the reference means. The one deliberate delta is its <c>\p{DI}</c>
/// (default-ignorable) class, which has no .NET equivalent; the characters it adds
/// beyond <c>\p{Cf}</c> are the ones the explicit list below names anyway.
/// </summary>
public static partial class DisplayText
{
    /// <summary>The reference's own iteration cap.</summary>
    private const int MaxIterations = 10;

    private const int ZeroWidthNonJoiner = 0x200C;
    private const int ZeroWidthJoiner = 0x200D;
    private const int TextVariationSelector = 0xFE0E;
    private const int EmojiVariationSelector = 0xFE0F;

    /// <summary>The tag block, the only part of the reference's explicit list outside the BMP.</summary>
    private const int TagBlockFirst = 0xE0000;
    private const int TagBlockLast = 0xE01EF;

    /// <summary>
    /// The reference's explicit tail: zero-width space and the two directional
    /// marks, the bidi embedding/override block, the bidi isolates, the byte-order
    /// mark, the BMP private-use area, the first fourteen variation selectors, the
    /// Mongolian free variation selectors, and the seven blank characters it names
    /// one by one. Every one of those is inside the BMP, so a regex reads them
    /// safely.
    /// </summary>
    [GeneratedRegex(
        @"[\u200B\u200E\u200F]|[\u202A-\u202E]|[\u2066-\u2069]|\uFEFF|[\uE000-\uF8FF]" +
        @"|[\uFE00-\uFE0D]|[\u180B-\u180F]|\u034F|\u115F|\u1160|\u17B4|\u17B5|\u3164|\uFFA0")]
    private static partial Regex Invisibles();

    /// <summary>
    /// A text the reference would put on screen. Sanitization that will not settle
    /// within ten passes returns the last pass rather than throwing: the reference
    /// throws there, but the caller here is a label, and a jump list that cannot be
    /// built is worse than one row of odd text.
    /// </summary>
    public static string Sanitize(string text)
    {
        var current = text;
        var previous = "";
        for (var i = 0; i < MaxIterations && current != previous; i++)
        {
            previous = current;
            // The category pass runs before the normalization, not after it as the
            // reference's does: .NET's Normalize throws on a lone surrogate where
            // JavaScript's tolerates one, and this pass is what removes those. The
            // loop runs to a fixed point either way, so the result is the same.
            current = DropInvisibleCategories(current);
            current = current.Normalize(NormalizationForm.FormKC);
            current = Invisibles().Replace(current, "");
        }

        return current;
    }

    /// <summary>
    /// One pass of the reference's two category filters, by code point: the format,
    /// private-use, unassigned and lone-surrogate characters go except the four that
    /// change how a real character renders, and the control characters go except the
    /// three whitespace ones. The tag block rides along, since it is the only part
    /// of the explicit list that lives outside the BMP.
    /// </summary>
    private static string DropInvisibleCategories(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            var isPair = char.IsHighSurrogate(ch) && index + 1 < text.Length &&
                char.IsLowSurrogate(text[index + 1]);
            if (!isPair && char.IsSurrogate(ch))
            {
                // A half with no partner is what the reference's \p{Cs} means.
                index++;
                continue;
            }

            var width = isPair ? 2 : 1;
            var codePoint = isPair ? char.ConvertToUtf32(ch, text[index + 1]) : ch;
            if (Keep(text, index, codePoint))
            {
                builder.Append(text, index, width);
            }

            index += width;
        }

        return builder.ToString();
    }

    private static bool Keep(string text, int index, int codePoint)
    {
        if (codePoint is >= TagBlockFirst and <= TagBlockLast)
        {
            return false;
        }

        if (codePoint is ZeroWidthNonJoiner or ZeroWidthJoiner
            or TextVariationSelector or EmojiVariationSelector)
        {
            return true;
        }

        return CharUnicodeInfo.GetUnicodeCategory(text, index) switch
        {
            UnicodeCategory.Format or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate => false,
            UnicodeCategory.Control => codePoint is '\n' or '\t' or '\r',
            _ => true,
        };
    }
}
