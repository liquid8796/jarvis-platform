using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The "Tab Context" trailer every page-facing Browser pane result carries, ported from
/// the reference desktop's <c>hQ</c> (app.asar 1.40609.0.0,
/// <c>.vite/build/index.chunk-C5__TEgr.js</c>). It names **only the tab the call ran on** —
/// the all-tabs listing in the same bundle belongs to the browser *extension* bridge, a
/// different surface — and carries the notices that tell the model what the page was not
/// allowed to do while it was being read.
/// </summary>
public static partial class BrowserPaneTrailer
{
    /// <summary>
    /// How the trailer names the page: its origin for http(s) (a leading "www." dropped
    /// once the host has three or more labels and is neither an address nor a .local
    /// name), the bare scheme for anything else, and the reference's two fallbacks — for a
    /// tab with no page, and for an address that will not parse.
    /// </summary>
    public static string OriginLabel(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "(no page)";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "page content";
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return uri.Scheme + ":";
        }

        var host = uri.Host.TrimEnd('.');
        if (host.Length == 0)
        {
            return "page content";
        }

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            && host.Split('.').Length >= 3
            && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            host = host[4..];
        }

        return $"{uri.Scheme}://{host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
    }

    /// <summary>
    /// The trailer for one result. <paramref name="deniedMediaKinds"/> is the tab's
    /// accumulated set of camera/microphone requests the pane refused; the reference sorts
    /// it and joins with " and ".
    /// </summary>
    public static string Build(
        string tabId, string? title, string? url, IReadOnlyCollection<string> deniedMediaKinds,
        bool clipboardChanged = false)
    {
        var trailer = $"\n\nTab Context:\n- Executed on tabId: {tabId}\n- Available tabs:\n"
                      + $"  \u2022 tabId {tabId}: \"{Clean(title)}\" ({OriginLabel(url)})";

        if (deniedMediaKinds.Count > 0)
        {
            var kinds = string.Join(" and ", deniedMediaKinds.Order(StringComparer.Ordinal));
            trailer += $"\n- Note: the page (or a frame it embeds) requested {kinds} access, "
                       + "which is blocked in the Browser pane; the user was shown a notice. "
                       + "Don't treat device capture as working.";
        }

        if (clipboardChanged)
        {
            // The reference has three of these, separated by whether it could attribute the
            // write to the page. It cannot always, and this is the sentence it uses then.
            trailer += "\n- Note: the user's OS clipboard changed during your synthetic input "
                       + "(plausibly the user's own copy, or their own app); the user was notified "
                       + "to check it before pasting.";
        }

        return trailer;
    }

    /// <summary>
    /// The reference's text sanitiser (MOn): quotes and control characters become spaces,
    /// trimmed, 200 characters. Shared with get_page_text's header, which uses the same one.
    /// </summary>
    internal static string Clean(string? text)
    {
        if (text is null)
        {
            return "";
        }

        var cleaned = ControlOrQuote().Replace(text, " ").Trim();
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    [GeneratedRegex("""[\r\n\t"\\]""")]
    private static partial Regex ControlOrQuote();
}
