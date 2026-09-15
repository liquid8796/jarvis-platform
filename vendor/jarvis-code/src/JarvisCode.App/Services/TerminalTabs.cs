namespace JarvisCode.App.Services;

/// <summary>
/// The terminal tab strip's labels, ported from the reference's BM (ion-dist chunk
/// c360a9e1c-DUoNQd2W.js at the "Terminal {n}" literal). Its rule is the part worth
/// pinning: a tab keeps whatever name the user gave it, an unnamed one reads plain
/// "Terminal" while there is only one, and every unnamed tab is numbered as soon as
/// there are two.
/// </summary>
public static class TerminalTabs
{
    public const string NewTab = "New terminal";
    public const string MoreTabs = "More terminals";
    public const string CloseTab = "Close terminal";
    public const string RenameTab = "Rename terminal";
    public const string CloseOtherTabs = "Close other terminals";
    public const string CloseTabHint = "press right arrow to close";
    public const string CloseTabDeleteHint = "Press Delete or Backspace to close the terminal";
    public const string TabStripLabel = "Terminal tabs";
    public const string OutputAttached = "Terminal output attached to chat";

    /// <summary>The label one tab wears.</summary>
    public static string Label(string? name, int ordinal, int tabCount) =>
        name is { Length: > 0 } given
            ? given
            : tabCount > 1 ? $"Terminal {ordinal}" : "Terminal";
}
