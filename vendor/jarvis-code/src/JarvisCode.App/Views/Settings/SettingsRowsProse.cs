using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// The reference's row descriptions are ICU messages carrying markup tags whose
/// values are links (<c>&lt;link&gt;…&lt;/link&gt;</c>, <c>&lt;aupLink&gt;…&lt;/aupLink&gt;</c>).
/// This renders one as a wrapped description with the tagged runs as real links,
/// so the sentence stays the reference's byte for byte and the link still works.
/// </summary>
internal static partial class SettingsProse
{
    [GeneratedRegex(@"<([A-Za-z]+)>(.*?)</\1>", RegexOptions.Singleline)]
    private static partial Regex Tagged();

    /// <summary>Strips the markup tags, leaving the sentence the user reads.</summary>
    public static string PlainText(string message) => Tagged().Replace(message, "$2");

    /// <summary>
    /// The description block: every tagged run becomes a link to the url the tag
    /// names, everything else is plain text. A tag with no url stays plain.
    /// </summary>
    public static TextBlock Description(string message, params (string Tag, string Url)[] links)
    {
        var block = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        var at = 0;
        foreach (Match match in Tagged().Matches(message))
        {
            if (match.Index > at)
            {
                block.Inlines.Add(new Run(message[at..match.Index]));
            }

            var url = links.FirstOrDefault(l => l.Tag == match.Groups[1].Value).Url;
            if (url is null)
            {
                block.Inlines.Add(new Run(match.Groups[2].Value));
            }
            else
            {
                var link = new Hyperlink(new Run(match.Groups[2].Value)) { NavigateUri = new Uri(url) };
                link.RequestNavigate += (_, e) =>
                {
                    e.Handled = true;
                    Open(e.Uri.AbsoluteUri);
                };
                block.Inlines.Add(link);
            }

            at = match.Index + match.Length;
        }

        if (at < message.Length)
        {
            block.Inlines.Add(new Run(message[at..]));
        }

        return block;
    }

    /// <summary>A description followed by a separate trailing link, as the reference's " {Learn more}" tails are.</summary>
    public static TextBlock DescriptionWithTrailingLink(string message, string linkText, string url)
        => Description(message + " <trailing>" + linkText + "</trailing>", ("trailing", url));

    public static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // A machine with no browser association is not a reason to fault the settings page.
        }
    }
}
