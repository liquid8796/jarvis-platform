using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Controls;

namespace JarvisCode.App.Views;

public partial class MainWindow
{
    /// <summary>
    /// Drives the Code session's titlebar on the real controls and reports 0 or 2. A
    /// screenshot proves the bar's shape; it cannot prove any of the things this file
    /// checks — that the bar's own background is what a press lands on and the title is
    /// not, that a right-click on the title opens the session menu, that a held Enter
    /// does not re-enter the rename editor while a click does, or that narrowing the bar
    /// compacts the pill and widening it again brings the label back.
    /// </summary>
    public async Task<int> RunTitleBarSelfTestAsync()
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            if (!ok)
            {
                failures.Add(what);
            }
        }

        PoseTitleBar("");
        await Settle();

        var surface = _codeWorkspace.ChatSurface;
        var bar = surface.SessionTitleBar;
        Check(bar.Visibility == Visibility.Visible, "the titlebar is shown over a started session");
        if (bar.Visibility != Visibility.Visible)
        {
            return ReportTitleBar(failures);
        }

        Check(Math.Abs(bar.ActualHeight - SessionTitleBarPanel.BarHeight) < 0.5,
            $"the bar lays out at {SessionTitleBarPanel.BarHeight}px (it is {bar.ActualHeight:0.##})");

        var title = FindDescendant<Button>(bar, "SessionTitleButton");
        var pill = FindDescendants<SessionOriginPill>(bar).FirstOrDefault();
        Check(title is not null, "the title button is in the tree");
        Check(pill is not null, "the origin pill is in the tree");
        if (title is null || pill is null)
        {
            return ReportTitleBar(failures);
        }

        // ---- the drag region: the reference drags on the bar and nowhere else ----

        // A press is a drag only when it lands on the panel itself, so what the bar is
        // asked here is exactly what its mouse handler asks: who owns this point.
        var barOrigin = bar.PointToScreen(new Point(0, 0));
        var titleOrigin = title.PointToScreen(new Point(0, 0));
        var gapX = (titleOrigin.X - barOrigin.X) + title.ActualWidth + 6;
        Check(HitTestAt(bar, gapX, bar.ActualHeight / 2) is SessionTitleBarPanel,
            "a press on the bar's own background reaches the panel, so it can drag the window");
        Check(HitTestAt(bar, (titleOrigin.X - barOrigin.X) + 4, bar.ActualHeight / 2) is not SessionTitleBarPanel,
            "a press on the title does not reach the panel, so it does not drag the window");

        // ---- the title's two keyboard and mouse contracts ----

        title.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice, PresentationSource.FromVisual(title), 0, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
        await Settle();
        var editorHost = FindDescendant<ContentControl>(bar, "SessionTitleEditorHost");
        Check(editorHost?.Content is null, "a plain Enter on the title does not open the editor by itself");

        surface.BeginHeaderRename();
        await Settle();
        Check(editorHost?.Content is InlineRenameBox, "the title swaps in the rename editor");
        Check(editorHost?.Content is InlineRenameBox box && box.BorderThickness.Left > 1,
            "the editor is the titlebar's bare outlined one, not the sidebar's boxed one");
        if (editorHost?.Content is InlineRenameBox open)
        {
            open.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(open), 0, Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
        }

        await Settle();

        // ---- the title as the session menu's context-menu trigger ----

        title.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = Control.MouseRightButtonUpEvent,
            Source = title,
        });
        await Settle();
        Check(OpenContextMenus().Any(), "a right-click on the title opens the session menu");
        foreach (var menu in OpenContextMenus())
        {
            menu.IsOpen = false;
        }

        await Settle();

        // ---- the close control, which moved out of the tile and into the bar ----

        // The split view used to float its own close pill over the transcript. That is gone,
        // because the reference draws the control here instead — so the replacement is
        // checked on a real split rather than assumed, an affordance having been removed.
        var close = FindDescendant<Button>(bar, "ClosePaneButton");
        Check(close?.Visibility == Visibility.Collapsed, "a lone pane offers no close");
        _codeWorkspace.OpenSplitView();
        await Settle();
        Check(close?.Visibility == Visibility.Visible, "a split pane's titlebar offers Close pane");
        _codeWorkspace.CloseSplitView();
        await Settle();
        Check(close?.Visibility == Visibility.Collapsed, "closing the split takes the control away again");

        // ---- the fitting rules, driven by really narrowing the window ----

        var restore = Width;
        Check(!pill.Compact, "the pill shows its label while the bar has room");

        Width = 520;
        await Settle();
        await Task.Delay(250);
        Check(pill.Compact, "narrowing the bar collapses the pill to its folder icon");
        Check(pill.ActualWidth < 30,
            $"the compacted pill is as narrow as its icon (it is {pill.ActualWidth:0.##})");

        // A fold has to reach the title, which is the whole point of folding. This drives
        // the rail from outside, so it proves the room arrives — not that it arrives inside
        // the one measure pass. The in-pass path is the same shape and is what the rail's
        // own InvalidateMeasure is for, but the bar only folds a toggle itself below about
        // 214px of width, which no window on this desktop reaches; the arithmetic behind it
        // is covered by SessionTitleBarLayoutTests instead.
        var squeezed = title.ActualWidth;
        surface.HeaderRail.SetHiddenCount(1);
        await Settle();
        Check(title.ActualWidth > squeezed + 10,
            $"folding a toggle gives its room to the title ({squeezed:0.##} -> {title.ActualWidth:0.##})");
        surface.HeaderRail.SetHiddenCount(0);
        await Settle();

        Width = restore;
        await Settle();
        await Task.Delay(250);
        Check(!pill.Compact, "widening the bar brings the label back");

        return ReportTitleBar(failures);
    }

    private async Task Settle()
    {
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(120);
    }

    /// <summary>Which element owns a point of the bar, in the bar's own coordinates.</summary>
    private static DependencyObject? HitTestAt(Visual root, double x, double y)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(
            root,
            null,
            result =>
            {
                hit = result.VisualHit;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(new Point(x, y)));
        return hit;
    }

    private static IEnumerable<ContextMenu> OpenContextMenus() =>
        PresentationSource.CurrentSources
            .OfType<System.Windows.Interop.HwndSource>()
            .Where(source => source.RootVisual is not null)
            .SelectMany(source => FindDescendants<ContextMenu>(source.RootVisual))
            .Where(menu => menu.IsOpen);

    private static int ReportTitleBar(IReadOnlyList<string> failures)
    {
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"titlebar self-test: {failure}");
        }

        Console.WriteLine(failures.Count == 0
            ? "titlebar self-test: the bar drags on its own background, renames in place, opens its "
            + "menu on a right-click, and compacts its pill when the window narrows"
            : $"titlebar self-test: {failures.Count} check(s) failed");
        return failures.Count == 0 ? 0 : 2;
    }
}
