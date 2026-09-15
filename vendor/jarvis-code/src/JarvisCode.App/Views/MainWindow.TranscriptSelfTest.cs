using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

public partial class MainWindow
{
    /// <summary>How many turns the posed transcript carries; four rows each.</summary>
    private const int SelfTestTurns = 500;

    /// <summary>
    /// Drives the transcript's virtualizer on the real controls and reports 0 or 2.
    /// A screenshot cannot prove any of this: what is on screen looks the same
    /// whether two thousand rows exist or forty do, and the whole point of the
    /// change is that only forty do. So this poses more rows than any real session,
    /// then asserts what the reference's own virtualizer guarantees — that the
    /// built rows are bounded by the viewport and its overscan, that they follow
    /// the reader, that the content height covers every row and not only the built
    /// ones, that a row nobody has built can still be scrolled to, and that Select
    /// all reaches rows that were never on screen.
    /// </summary>
    public async Task<int> RunTranscriptSelfTestAsync()
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            if (!ok)
            {
                failures.Add(what);
            }
        }

        StartCodeSessionIn(Environment.CurrentDirectory);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);

        var surface = FindDescendant<ChatSurface>(this, "Chat") ??
            Descendants<ChatSurface>(this).FirstOrDefault();
        Check(surface is not null, "the chat surface is in the tree");
        if (surface?.DataContext is not ViewModels.ChatViewModel viewModel)
        {
            return ReportTranscript(failures);
        }

        var built = Stopwatch.StartNew();
        viewModel.ShowLargeTranscript(SelfTestTurns);
        built.Stop();
        var laid = Stopwatch.StartNew();
        surface.UpdateLayout();
        laid.Stop();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(700);

        var scroller = FindDescendant<ScrollViewer>(surface, "TranscriptScroll");
        var list = FindDescendant<ItemsControl>(surface, "TranscriptItems");
        Check(scroller is not null && list is not null, "the transcript scroller and list are in the tree");
        if (scroller is null || list is null)
        {
            return ReportTranscript(failures);
        }

        var panel = VirtualizingTranscriptPanel.GetPanel(list);
        Check(panel is not null, "the transcript is laid out by the virtualizer");
        if (panel is null)
        {
            return ReportTranscript(failures);
        }

        Check(viewModel.Transcript.Count == SelfTestTurns * 4,
            $"the pose built {SelfTestTurns * 4} rows (saw {viewModel.Transcript.Count})");

        // The list the panel lays out is the transcript *view*, and the Normal view
        // hides thinking — so the denominator for everything below is what the
        // items control actually holds, not what the session does.
        int rows = list.Items.Count;
        Check(rows > SelfTestTurns * 2, $"the transcript view carries its rows (saw {rows})");

        // The window may not exceed what the viewport plus both overscans can hold.
        // A row is never shorter than the reference's smallest constant, so the
        // ceiling is that height divided into the band — with the two end-snaps,
        // which reach a further two viewports each, allowed for.
        double band = scroller.ViewportHeight + (2 * TranscriptWindow.Overscan) +
            (4 * scroller.ViewportHeight);
        int ceiling = (int)Math.Ceiling(band / TranscriptRowEstimates.ThinkingRow) + 4;

        Console.WriteLine($"transcript self-test: rows={rows} build={built.ElapsedMilliseconds}ms " +
            $"layout={laid.ElapsedMilliseconds}ms " +
            $"viewport={scroller.ViewportHeight:F0} ceiling={ceiling} {panel.Counters}");

        Check(panel.Counters.RowsWindowed < rows,
            $"the transcript does not build every row (built {panel.Counters.RowsWindowed} of {rows})");
        Check(panel.Counters.RowsWindowed <= ceiling,
            $"the built rows stay inside the viewport and its overscan " +
            $"(built {panel.Counters.RowsWindowed}, ceiling {ceiling})");

        // The extent describes every row, not the built ones: a scrollbar over a
        // fraction of the content is the tell that virtualization is lying.
        Check(scroller.ExtentHeight > scroller.ViewportHeight * 10,
            $"the content height covers the whole transcript (extent {scroller.ExtentHeight:F0})");
        Check(Math.Abs(panel.Counters.TotalSizePx - scroller.ExtentHeight) < scroller.ViewportHeight,
            $"the scroller's extent is the virtualizer's own total " +
            $"({panel.Counters.TotalSizePx:F0} vs {scroller.ExtentHeight:F0})");

        // Opening a session lands on the tail, and the window that was built is the
        // one under the reader rather than the top of the conversation.
        Check(panel.Window.Last >= rows - 2,
            $"opening lands on the tail (window ends at {panel.Window.Last} of {rows - 1})");

        // --- the window follows the reader -----------------------------------
        double middle = scroller.ScrollableHeight / 2;
        scroller.ScrollToVerticalOffset(middle);
        surface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);

        int windowedMid = panel.Counters.RowsWindowed;
        Check(windowedMid <= ceiling,
            $"the window stays bounded mid-transcript (built {windowedMid}, ceiling {ceiling})");
        Check(panel.Window.First > 0 && panel.Window.Last < rows - 1,
            $"the middle of the transcript builds neither end " +
            $"(window {panel.Window.First}..{panel.Window.Last})");

        // --- scrolling costs what a screenful costs, not what the session does --
        var swept = Stopwatch.StartNew();
        for (int step = 0; step < 20; step++)
        {
            scroller.ScrollToVerticalOffset(middle + (step * scroller.ViewportHeight));
            surface.UpdateLayout();
        }

        for (int step = 20; step > 0; step--)
        {
            scroller.ScrollToVerticalOffset(middle + (step * scroller.ViewportHeight));
            surface.UpdateLayout();
        }

        swept.Stop();
        Console.WriteLine($"transcript self-test: sweep40={swept.ElapsedMilliseconds}ms " +
            $"({swept.ElapsedMilliseconds / 40.0:F1}ms per screen)");
        Check(swept.ElapsedMilliseconds / 40.0 < 100,
            $"a screen of scrolling costs under 100ms (saw {swept.ElapsedMilliseconds / 40.0:F1}ms)");

        // --- a row nobody built can still be reached --------------------------
        int target = rows / 4;
        Check(panel.ScrollToIndex(target), "a row that was never built can be scrolled to");
        surface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);
        Check(panel.Window.Contains(target),
            $"scrolling to row {target} builds it (window {panel.Window.First}..{panel.Window.Last})");

        // --- the anchor survives a round trip ---------------------------------
        var anchor = panel.AnchorAt(scroller.VerticalOffset);
        Check(anchor is not null, "the viewport names the row it is anchored on");
        if (anchor is { } saved)
        {
            scroller.ScrollToVerticalOffset(0);
            surface.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(panel.ScrollToKey(saved.Key, saved.Offset), "the stored anchor resolves to its row");
            surface.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            Check(panel.Window.Contains(target),
                $"restoring the anchor lands on the row it named " +
                $"(window {panel.Window.First}..{panel.Window.Last})");
        }

        // --- Select all reaches rows that were never on screen -----------------
        var selection = FindDescendant<TextSelectionScope>(surface, "TranscriptSelection");
        Check(selection is not null, "the transcript's selection scope is in the tree");
        if (selection is not null)
        {
            var realized = Stopwatch.StartNew();
            panel.RealizeAll();
            surface.UpdateLayout();
            realized.Stop();
            Check(panel.Counters.RowsWindowed >= rows,
                $"Select all builds every row (built {panel.Counters.RowsWindowed} of {rows})");
            Console.WriteLine($"transcript self-test: realize-all={realized.ElapsedMilliseconds}ms");

            panel.ReleaseHold();
            surface.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            Check(panel.Counters.RowsWindowed < rows,
                $"releasing the hold gives the rows back (built {panel.Counters.RowsWindowed})");
        }

        // --- an expanded run does not build every call it holds ----------------
        var wide = viewModel.ShowWideToolRun(300);
        surface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        scroller.ScrollToEnd();
        surface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);

        Check(wide.IsExpanded && wide.RendersCard, "the posed run is an expanded card");
        var inner = Descendants<ItemsControl>(surface)
            .Select(control => VirtualizingTranscriptPanel.GetPanel(control))
            .FirstOrDefault(candidate =>
                candidate is not null && candidate != panel && candidate.Counters.ItemCount == 300);
        Check(inner is not null, "the run's call list is laid out by the virtualizer");
        if (inner is not null)
        {
            Console.WriteLine($"transcript self-test: expanded run {inner.Counters}");
            Check(inner.Counters.RowsWindowed < 300,
                $"expanding a 300-call run does not build all of it " +
                $"(built {inner.Counters.RowsWindowed})");
        }

        // --- history arrives a page at a time ---------------------------------
        const int stored = (ViewModels.ChatViewModel.TranscriptPageSize * 3) + 40;
        viewModel.ShowPagedHistory(stored);
        surface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);

        Check(viewModel.TranscriptHasOlder,
            $"a session of {stored} messages opens on a page rather than all of it");
        int paged = list.Items.Count;
        Check(paged > 0 && paged < stored,
            $"the first page carries some of the conversation (rows {paged} of {stored} messages)");

        var pagedKeys = viewModel.Transcript.Select(row => row.RowKey).ToHashSet(StringComparer.Ordinal);
        Check(viewModel.LoadOlderTranscript(), "the page above this one loads");
        surface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(viewModel.Transcript.Count > paged,
            $"loading older extends the transcript ({viewModel.Transcript.Count} vs {paged})");
        Check(pagedKeys.IsSubsetOf(viewModel.Transcript.Select(row => row.RowKey)),
            "every row that was already loaded keeps its key across the page above it");

        viewModel.LoadEntireTranscript();
        surface.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        Check(!viewModel.TranscriptHasOlder, "loading the rest leaves nothing older");
        Check(viewModel.Transcript.OfType<ViewModels.UserMessageItem>().First().TurnNumber == 1,
            "the first prompt of the conversation is numbered 1");
        Check(panel.Counters.RowsWindowed < viewModel.Transcript.Count,
            $"the whole conversation is still windowed " +
            $"(built {panel.Counters.RowsWindowed} of {viewModel.Transcript.Count})");

        // --- the measurements are real, and they cost nothing to keep ----------
        Check(panel.Counters.Measured > 0, "rows that were built are measured rather than estimated");
        Check(panel.Counters.BoxlessSkips == 0,
            $"no row measured as an empty box (saw {panel.Counters.BoxlessSkips})");

        return ReportTranscript(failures);
    }

    private static int ReportTranscript(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            Console.WriteLine("transcript self-test: OK");
            return 0;
        }

        foreach (var failure in failures)
        {
            Console.WriteLine("transcript self-test FAILED: " + failure);
        }

        return 2;
    }
}
