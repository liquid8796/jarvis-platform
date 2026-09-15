using System.Windows;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// Find inside the page the pane is showing. The reference's Edit ▸ Find targets
/// the browser pane whenever the pane holds focus (its <c>TIn</c> checks
/// <c>wIn()</c> first), so this app's does too.
///
/// The search is Chromium's own <c>findInPage</c>, which the engine exposes
/// directly, so the highlight and the scroll are the browser's rather than
/// something re-implemented on top of a selection.
/// </summary>
public partial class BrowserPanel
{
    private string _findQuery = "";

    /// <summary>Asks for a search term and runs the first match.</summary>
    public void OpenFind()
    {
        var query = InputDialog.Prompt(
            Window.GetWindow(this), "Find in page", _findQuery, "Find");
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        _findQuery = query;
        _ = FindAsync(forward: true, fromStart: true);
    }

    /// <summary>Moves to the next or previous match of the term already searched for.</summary>
    public void StepFind(int direction)
    {
        if (_findQuery.Length == 0)
        {
            OpenFind();
            return;
        }

        _ = FindAsync(forward: direction >= 0, fromStart: false);
    }

    private async Task FindAsync(bool forward, bool fromStart)
    {
        try
        {
            await FindInActiveTabAsync(_findQuery, forward, findNext: !fromStart);
        }
        catch (InvalidOperationException)
        {
            // The tab went away mid-search; there is nothing left to highlight.
        }
    }
}
