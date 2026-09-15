namespace JarvisCode.App.Services;

/// <summary>
/// What a page-opened popup stops the agent from doing.
///
/// A popup is the window a page opens for itself — almost always a sign-in
/// window — and the reference will not let the agent drive, navigate or close
/// one, nor run script or navigate anywhere else while one is open: a
/// newly-loaded page's own script can reach a popup its opener still holds.
///
/// Ported from the reference desktop's own guard (app.asar
/// `index.chunk-DnlgCaT3.js`, its <c>mEr</c> classification map, its <c>hEr</c>
/// set of harmless computer actions, and the <c>yEr</c>/<c>bEr</c> pair that
/// reads them). Pure: it names a tool, its arguments and whether a popup is
/// open, and answers with the sentence to refuse with, or null.
/// </summary>
public static class BrowserPanePopupGuard
{
    /// <summary>The id prefix the engine gives a tab that a page opened.</summary>
    public const string PopupTabPrefix = "popup-";

    public const string PopupTabRefusal =
        "That tab is a page-opened popup (often a sign-in window), which the agent may not drive, " +
        "navigate, or close. Ask the user to finish or close it, or act on a regular tab instead.";

    public const string NavigationPausedRefusal =
        "Navigation is paused while a page-opened popup (often a sign-in window) is open — a newly " +
        "loaded page's own script could reach it. Ask the user to finish or close the popup first.";

    public const string ScriptPausedRefusal =
        "JavaScript execution is paused while a page-opened popup (often a sign-in window) is open — " +
        "script in any tab could reach it. Ask the user to finish or close the popup first.";

    /// <summary>
    /// What each tool does to the tab it names. A tool that only reads, or that
    /// names no tab at all, is left alone; anything else is guarded.
    /// </summary>
    private static readonly Dictionary<string, string> Effects = new(StringComparer.Ordinal)
    {
        ["read_page"] = "reads-tab",
        ["computer"] = "mutates-tab",
        ["form_input"] = "mutates-tab",
        ["navigate"] = "mutates-tab",
        ["find"] = "reads-tab",
        ["get_page_text"] = "reads-tab",
        ["javascript_tool"] = "mutates-tab",
        ["read_console_messages"] = "reads-tab",
        ["read_network_requests"] = "reads-tab",
        ["resize_window"] = "mutates-tab",
        ["tabs_context"] = "reads-tab",
        ["tabs_create"] = "no-tab-target",
        ["tabs_select"] = "mutates-tab",
        ["tabs_close"] = "mutates-tab",
        ["browser_batch"] = "no-tab-target",
        ["preview_start"] = "no-tab-target",
        ["preview_stop"] = "no-tab-target",
        ["preview_list"] = "no-tab-target",
        ["preview_logs"] = "no-tab-target",
    };

    /// <summary>
    /// The computer actions that change nothing about the page, so a popup does
    /// not stand in their way.
    /// </summary>
    private static readonly HashSet<string> HarmlessActions = new(StringComparer.Ordinal)
    {
        "screenshot", "wait", "scroll", "zoom", "scroll_to", "hover",
    };

    /// <summary>
    /// Whether this call is one the guard has an opinion about. javascript_tool
    /// always is; a tool the map does not know is treated as one too, so a tool
    /// added later is guarded until it is classified.
    /// </summary>
    public static bool IsGuarded(string tool, string? computerAction)
    {
        if (tool == "javascript_tool")
        {
            return true;
        }

        if (!Effects.TryGetValue(tool, out var effect))
        {
            return true;
        }

        return effect == "mutates-tab"
            && (tool != "computer" || !HarmlessActions.Contains(computerAction ?? ""));
    }

    /// <summary>
    /// The sentence to refuse this call with, or null to let it through.
    /// </summary>
    /// <param name="tool">The pane tool's short name.</param>
    /// <param name="computerAction">computer's action, when this is that tool.</param>
    /// <param name="targetIsPopup">Whether the tab the call resolved to is one.</param>
    /// <param name="hasLivePopup">Whether any tab in the pane is a page-opened popup.</param>
    public static string? Refusal(
        string tool, string? computerAction, bool targetIsPopup, bool hasLivePopup)
    {
        if (!IsGuarded(tool, computerAction))
        {
            return null;
        }

        if (targetIsPopup)
        {
            return PopupTabRefusal;
        }

        if (!hasLivePopup)
        {
            return null;
        }

        return tool switch
        {
            "javascript_tool" => ScriptPausedRefusal,
            "navigate" => NavigationPausedRefusal,
            _ => null,
        };
    }
}
