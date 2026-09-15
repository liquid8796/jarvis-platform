using System.IO;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.Services;

/// <summary>
/// UI actions have no caller awaiting navigation. Observe their expected browser failures here,
/// not in the process-wide crash handler. Tool/driver calls deliberately do not use this boundary.
/// </summary>
internal static class BrowserNavigationUi
{
    internal static async Task ObserveAsync(Func<Task> navigate, Action<string> reportFailure)
    {
        try
        {
            await navigate();
        }
        catch (BrowserNavigationSupersededException)
        {
            // A later request (or closing the tab) owns the UI now. This is not a failed page.
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
            or IOException or OperationCanceledException)
        {
            DiagnosticLog.Write($"browser: navigation failed — {ex.Message}");
            reportFailure(ex is OperationCanceledException
                || ex.Message.StartsWith("ERR_ABORTED (-3)", StringComparison.Ordinal)
                ? "Navigation was interrupted. Try opening the page again."
                : $"Could not open the page: {ex.Message}");
        }
    }
}

/// <summary>
/// A replaced request is quiet in the UI but remains an explicit failure for awaited tool calls.
/// Deriving from InvalidOperationException preserves the browser tool's existing error handling.
/// </summary>
internal sealed class BrowserNavigationSupersededException(Exception? inner = null)
    : InvalidOperationException("Navigation was replaced by a newer request or the tab was closed.", inner);
