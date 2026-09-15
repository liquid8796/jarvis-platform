using System.Text.RegularExpressions;
using System.Windows;
using JarvisCode.App.Views;

namespace JarvisCode.App.Services;

/// <summary>
/// Opening a link outside the app, ported from the reference's <c>pS</c> and
/// <c>idt</c> (app.asar <c>index.chunk-DnlgCaT3.js</c>): a link that the shell
/// refuses raises a dialog offering to copy it rather than failing silently, and a
/// <c>mailto:</c> asks first, naming the address it would write to.
/// </summary>
public static partial class ExternalLinks
{
    public const string FailedTitle = "Link couldn't be opened";
    public const string CopyLinkLabel = "Copy link";
    public const string MailtoTitle = "Open email link?";
    public const string OpenLabel = "Open";
    public const string CancelLabel = "Cancel";

    /// <summary>The reference strips exactly these from the address it shows.</summary>
    [GeneratedRegex(@"[\r\n\t\v\f\u2028\u2029]")]
    private static partial Regex LineBreaks();

    public static string FailedDetail(string url) =>
        "We failed to open a link using your system's default application for this type of link. " +
        "This is often caused by the default app misbehaving or your operating system refusing " +
        $"the link itself. {url}";

    public static string MailtoDetail(string address) =>
        $"This will open your default email application to compose a message to {address}.";

    /// <summary>
    /// The address a mailto: link writes to, as the reference shows it: the decoded
    /// path with line breaks removed, falling back to the link without its scheme.
    /// </summary>
    public static string MailtoAddress(string url)
    {
        try
        {
            var uri = new Uri(url);
            var decoded = LineBreaks().Replace(Uri.UnescapeDataString(uri.AbsolutePath), "");
            return decoded.Length > 0 ? decoded : url;
        }
        catch (UriFormatException)
        {
            return LineBreaks().Replace(
                Regex.Replace(url, "^mailto:", "", RegexOptions.IgnoreCase), "");
        }
    }

    /// <summary>
    /// Opens a link. A <c>mailto:</c> is confirmed first; anything the shell refuses
    /// raises the reference's dialog, whose first button copies the link.
    /// </summary>
    public static void Open(Window? owner, string url)
    {
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && !ConfirmMailto(owner, url))
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or System.IO.FileNotFoundException or InvalidOperationException)
        {
            var response = MessageDialog.Show(
                owner,
                FailedTitle,
                FailedDetail(url),
                [CopyLinkLabel, MessageDialog.OkLabel],
                defaultId: 0,
                cancelId: 1,
                MessageDialogType.Warning);
            if (response == 0)
            {
                try
                {
                    Clipboard.SetText(url);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Another process owns the clipboard; nothing more to offer.
                }
            }
        }
    }

    private static bool ConfirmMailto(Window? owner, string url) =>
        MessageDialog.Show(
            owner,
            MailtoTitle,
            MailtoDetail(MailtoAddress(url)),
            [OpenLabel, CancelLabel],
            defaultId: 0,
            cancelId: 1,
            MessageDialogType.Question) == 0;
}
