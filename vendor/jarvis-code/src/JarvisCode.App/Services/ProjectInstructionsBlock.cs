using System.Globalization;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>One reference link attached to a project.</summary>
/// <param name="Url">The link itself.</param>
/// <param name="Title">Its title, when it has one.</param>
public sealed record ProjectLink(string Url, string? Title = null);

/// <summary>
/// The block a project puts in front of the model, ported byte for byte from the
/// reference desktop's own builder (<c>shared-5--MfpzEVV.js</c>, its <c>uV</c>,
/// found by the literal <c>&lt;project_instructions&gt;</c>) together with the three
/// escapers it composes: <c>sV</c> for a url, <c>iV</c> for text inside a tag and
/// <c>cV</c> for a name or title, which normalizes before it escapes.
///
/// This is the desktop's own local-project wording ("their local project"), not
/// claude.ai's server-side project prompt — the desktop builds it in the client,
/// which is why it can be read at all.
/// </summary>
public static class ProjectInstructionsBlock
{
    /// <summary>The reference's link budget: 2000 characters of rendered links.</summary>
    internal const int LinkBudget = 2000;

    /// <summary>U+202F NARROW NO-BREAK SPACE, which the reference's normalizer steps over.</summary>
    private const char NarrowNoBreakSpace = ' ';

    /// <summary>U+2033 DOUBLE PRIME, the other character it steps over.</summary>
    private const char DoublePrime = '″';

    /// <summary>Builds the block, or an empty string when there is nothing to say.</summary>
    public static string Build(
        string name, string? description, string? instructions, IReadOnlyList<ProjectLink>? links = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(instructions))
        {
            parts.Add(
                "<project_instructions>\n" +
                $"The user has configured the following instructions for this project (\"{Normalized(name)}\"):\n\n" +
                $"{Escaped(instructions)}\n\n" +
                "Follow these instructions when working in this project.\n" +
                "</project_instructions>");
        }
        else
        {
            var tail = string.IsNullOrEmpty(description) ? "" : $" {Normalized(description)}";
            parts.Add(
                "<project_instructions>\n" +
                $"The user is working in their local project \"{Normalized(name)}\".{tail}\n" +
                "</project_instructions>");
        }

        if (links is { Count: > 0 })
        {
            parts.Add(LinksBlock(links));
        }

        return string.Join("\n\n", parts);
    }

    private static string LinksBlock(IReadOnlyList<ProjectLink> links)
    {
        var rendered = new List<string>();
        var used = 0;
        var omitted = 0;
        foreach (var link in links)
        {
            var url = EscapedUrl(link.Url);
            var tag = string.IsNullOrEmpty(link.Title)
                ? $"<link url=\"{url}\" />"
                : $"<link url=\"{url}\" title=\"{Normalized(link.Title)}\" />";
            if (used + tag.Length > LinkBudget)
            {
                omitted = links.Count - rendered.Count;
                break;
            }

            rendered.Add(tag);
            used += tag.Length + 1;
        }

        var note = omitted > 0
            ? $"\n({omitted} additional link{(omitted == 1 ? "" : "s")} omitted — " +
              "the user can reference them directly if needed.)"
            : "";
        return
            "<project_links>\n" +
            "The user has attached the following links to this project as reference context.\n" +
            "If any of these are relevant to the task, use the connector that matches each link's source to read them.\n\n" +
            string.Join("\n", rendered) + note + "\n" +
            "</project_links>";
    }

    /// <summary>The reference's <c>sV</c>: angle brackets only, which is all a url needs.</summary>
    internal static string EscapedUrl(string value) =>
        value.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>The reference's <c>iV</c>: the four XML entities, ampersand first.</summary>
    internal static string Escaped(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>
    /// The reference's <c>cV</c>: drop the invisible tag characters, NFKD-normalize
    /// everything except a narrow no-break space or a double prime, then escape.
    /// Those two are stepped over because NFKD would turn them into an ordinary
    /// space and a pair of apostrophes, and both carry meaning in a name.
    /// </summary>
    internal static string Normalized(string value)
    {
        var stripped = StripTagCharacters(value);
        var builder = new StringBuilder(stripped.Length);
        var start = 0;
        for (var i = 0; i < stripped.Length; i++)
        {
            if (stripped[i] != NarrowNoBreakSpace && stripped[i] != DoublePrime)
            {
                continue;
            }

            builder.Append(stripped[start..i].Normalize(NormalizationForm.FormKD));
            builder.Append(stripped[i]);
            start = i + 1;
        }

        builder.Append(stripped[start..].Normalize(NormalizationForm.FormKD));
        return Escaped(builder.ToString());
    }

    /// <summary>Removes U+E0000..U+E007F, the tag block the reference strips by code point.</summary>
    private static string StripTagCharacters(string value)
    {
        if (!value.Contains('\udb40'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var point = char.ConvertToUtf32(value[i], value[i + 1]);
                i++;
                if (point is >= 0xE0000 and <= 0xE007F)
                {
                    continue;
                }

                builder.Append(char.ConvertFromUtf32(point));
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
