namespace JarvisCode.App.Services;

/// <summary>
/// The question a widget's <c>ui/open-link</c> raises, ported from the
/// reference's own external-link dialog (its <c>iy</c> behind <c>oy</c> in
/// ion-dist <c>shared-11-BFWs2XIz.js</c>, which the MCP-app row hands its
/// <c>onopenlink</c> to).
///
/// A widget is model-authored HTML, so a link it asks the host to follow is a
/// destination nobody has read. The reference refuses anything that is not
/// https before it asks at all, shows the resolved address, focuses Cancel, and
/// warns about the two ways an address can lie about where it goes: a
/// punycode label and embedded credentials.
/// </summary>
public static class WidgetLinkPrompt
{
    public const string Title = "Open external link";

    public const string Description = "You’re leaving Jarvis to visit an external link:";

    public const string OpenLabel = "Open link";

    public const string CancelLabel = "Cancel";

    public const string PunycodeWarning =
        "This link contains an internationalized domain name that may be deceptive. The domain appears as:";

    // One literal rather than two concatenated pieces: the string manifest checks
    // that the source it credits still contains the sentence, and a wrapped
    // literal contains only halves of it.
    public const string CredentialsWarning =
        "This link contains embedded credentials, which may be an attempt to disguise its destination. You will actually be sent to:";

    /// <summary>
    /// The reference follows only https. A widget asking for anything else is
    /// answered with its error rather than a question.
    /// </summary>
    public static bool IsFollowable(string? url, out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

    /// <summary>True when any label of the host is punycode, which reads as one thing and resolves to another.</summary>
    public static bool HasPunycode(Uri uri) =>
        uri.Host.Split('.').Any(static label => label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the address carries a <c>user:password@</c> part, which hides the real host.</summary>
    public static bool HasCredentials(Uri uri) => uri.UserInfo.Length > 0;

    /// <summary>
    /// What the dialog says under its title: the sentence, the address, and
    /// whichever of the two warnings applies.
    /// </summary>
    public static string Detail(Uri uri)
    {
        var detail = Description + "\n\n" + uri.AbsoluteUri;
        if (HasPunycode(uri))
        {
            detail += "\n\n" + PunycodeWarning + "\n" + uri.IdnHost;
        }

        if (HasCredentials(uri))
        {
            detail += "\n\n" + CredentialsWarning + "\n" + uri.GetLeftPart(UriPartial.Authority);
        }

        return detail;
    }
}
