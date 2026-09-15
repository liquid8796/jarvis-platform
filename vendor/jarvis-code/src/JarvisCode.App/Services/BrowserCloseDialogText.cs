namespace JarvisCode.App.Services;

/// <summary>Which tabs the close dialog is about.</summary>
public enum BrowserCloseMode
{
    /// <summary>The tab the user is closing.</summary>
    Single,

    /// <summary>Every other tab, from "Close other tabs".</summary>
    Others,
}

/// <summary>
/// The dialog the Browser pane raises when closing a tab would leave a dev server without
/// a window, ported from the reference's ES (ion-dist chunk c360a9e1c-DUoNQd2W.js at the
/// "Close {host}?" literal). Its three bodies, its title pair and its danger button's
/// accessible name are all the reference's own, so they are built here where a test can
/// read them.
/// </summary>
public static class BrowserCloseDialogText
{
    /// <summary>The label a server without a name of its own takes.</summary>
    public const string DefaultServerLabel = "the dev server";

    public const string Cancel = "Cancel";
    public const string KeepRunning = "Keep running";

    public static string Title(BrowserCloseMode mode, string host) =>
        mode == BrowserCloseMode.Others ? "Close other tabs?" : $"Close {host}?";

    /// <summary>
    /// The body: one sentence per case. More than one server names the count and speaks of
    /// tabs in general; a single server names itself and its host, and says "this tab" only
    /// when the tab being closed is the one hosting it.
    /// </summary>
    public static string Body(BrowserCloseMode mode, int serverCount, string serverLabel, string host)
    {
        if (serverCount > 1)
        {
            return $"{serverCount} dev servers are still running. You can keep them running and re-open a "
                   + "tab any time by entering its localhost address in the URL bar.";
        }

        var label = serverLabel.Length > 0 ? serverLabel : DefaultServerLabel;
        return mode == BrowserCloseMode.Others
            ? $"{label} is still running. You can keep it running and re-open a tab any time by entering "
              + $"{host} in the URL bar."
            : $"{label} is still running. You can keep it running and re-open this tab any time by entering "
              + $"{host} in the URL bar.";
    }

    /// <summary>The danger button's label: singular or plural, by how many servers would stop.</summary>
    public static string StopLabel(int serverCount) => serverCount > 1 ? "Stop servers" : "Stop server";

    /// <summary>The danger button's accessible name, which the reference spells in full.</summary>
    public static string StopAccessibleName(int serverCount) =>
        serverCount == 1 ? "Stop server and close" : $"Stop {serverCount} servers and close";

    /// <summary>"localhost:{port}" — the host the title and body name.</summary>
    public static string HostLabel(int? port) => port is { } value && value > 0 ? $"localhost:{value}" : "";
}
