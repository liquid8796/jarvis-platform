using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

public partial class MainWindow
{
    /// <summary>
    /// Drives the Code sidebar's list on the real controls and reports 0 or 2. The
    /// popup menus cannot be captured by a render-to-bitmap, so what a screenshot
    /// cannot prove is proved here instead: that the first section header carries the
    /// filter and the menu and no other one does, that the filter popover offers Last
    /// activity on the State grouping and nothing else, that the header menu carries
    /// the bulk-older submenu, that a row's menu carries Output style between
    /// Transcript view and Export, that the collapse caret folds a section, that the
    /// show-more row reveals another twenty, and that a double-click renames in place.
    /// </summary>
    public async Task<int> RunSidebarSelfTestAsync()
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            if (!ok)
            {
                failures.Add(what);
            }
        }

        PoseSidebar("showmore");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);

        var host = FindDescendant<ItemsControl>(Sidebar, "GroupsHost");
        Check(host is not null, "the sidebar's list host is in the tree");
        if (host is null)
        {
            return Report(failures);
        }

        // --- the header trailing rides the first section only -----------------
        var headerButtons = Descendants<Button>(host)
            .Where(b => AutomationName(b) is "Filter" or "Filter (active)")
            .ToList();
        Check(headerButtons.Count == 1, $"exactly one filter icon in the list (saw {headerButtons.Count})");
        var overflowButtons = Descendants<Button>(host)
            .Where(b => AutomationName(b) == HeaderMenuName)
            .ToList();
        Check(overflowButtons.Count == 1, $"exactly one header menu in the list (saw {overflowButtons.Count})");

        // There is no standing "Recents" row while the list is grouped.
        var labels = Descendants<TextBlock>(host).Select(t => t.Text).ToList();
        Check(!labels.Contains("Recents"), "no standing Recents row while the list is grouped");
        Check(labels.Contains("Ungrouped"), "the group header is drawn");

        // --- the row's geometry is the reference's ----------------------------
        var rows = Descendants<Button>(host)
            .Where(b => b.Tag is string tag && tag.StartsWith("pose-", StringComparison.Ordinal))
            .ToList();
        Check(rows.Count > 0, "session rows are drawn");
        if (rows.Count > 0)
        {
            Check(Math.Abs(rows[0].Height - SidebarMetrics.Current.RowHeight) < 0.01,
                $"a row is {SidebarMetrics.Current.RowHeight}px tall (saw {rows[0].Height})");
        }

        // --- twenty rows, then the show-more row ------------------------------
        Check(rows.Count == SidebarPresentation.TruncateAt,
            $"the bucket folds at {SidebarPresentation.TruncateAt} rows (saw {rows.Count})");
        var showMore = Descendants<Button>(host)
            .FirstOrDefault(b => AutomationName(b)?.StartsWith("Show ", StringComparison.Ordinal) == true);
        Check(showMore is not null, "the show-more row is drawn");
        if (showMore?.Content is TextBlock moreLabel)
        {
            Check(moreLabel.Text == "Show 10 more", $"its label is \"Show 10 more\" (saw \"{moreLabel.Text}\")");
        }

        if (showMore is not null)
        {
            showMore.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var after = Descendants<Button>(host)
                .Count(b => b.Tag is string tag && tag.StartsWith("pose-", StringComparison.Ordinal));
            Check(after == 30, $"show-more reveals the rest (saw {after} rows)");
        }

        // --- the filter popover -----------------------------------------------
        Check(SidebarFilterModel.Build(new SidebarFilterState()).StatusSections
                .Select(s => s.Label).SequenceEqual(["Status"]),
            "the folder grouping offers Status alone above the rule");
        Check(SidebarFilterModel.Build(new SidebarFilterState { GroupBy = "state" }).StatusSections
                .Select(s => s.Label).SequenceEqual(["Status", "Last activity"]),
            "the State grouping adds Last activity");

        // --- the header menu carries the bulk-older submenu --------------------
        if (overflowButtons.Count == 1)
        {
            overflowButtons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var menu = Sidebar.LastOpenedMenu;
            var headers = menu is null ? [] : menu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            Check(headers.Contains(SessionBulkActions.SubmenuLabel),
                $"the header menu carries \"{SessionBulkActions.SubmenuLabel}\" (saw {string.Join(" · ", headers)})");
            if (menu is not null)
            {
                menu.IsOpen = false;
            }
        }

        // --- a row's menu puts Output style between Transcript view and Export --
        var rowMenuLabels = SessionMenuModel.ForSidebarRow(new SessionMenuContext
        {
            TranscriptViews = [new SessionMenuChoice("normal", "a transcript view", true)],
            OutputStyles = [new SessionMenuChoice("", "an output style", true)],
        }).Where(r => r.Kind != SessionMenuRowKind.Separator).Select(r => r.Label).ToList();
        var view = rowMenuLabels.IndexOf("Transcript view");
        var style = rowMenuLabels.IndexOf("Output style");
        var export = rowMenuLabels.IndexOf("Export");
        Check(view >= 0 && style == view + 1 && export == style + 1,
            $"Output style sits between Transcript view and Export ({string.Join(" · ", rowMenuLabels)})");

        // --- the collapse caret folds the section ------------------------------
        var groupToggle = Descendants<Button>(host).FirstOrDefault(b => AutomationName(b) == "Ungrouped");
        Check(groupToggle is not null, "the section header is a button");
        if (groupToggle is not null)
        {
            groupToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var collapsed = Descendants<Button>(host)
                .Count(b => b.Tag is string tag && tag.StartsWith("pose-", StringComparison.Ordinal));
            Check(collapsed == 0, $"collapsing the section hides its rows (saw {collapsed})");

            var reopen = Descendants<Button>(host).FirstOrDefault(b => AutomationName(b) == "Ungrouped");
            reopen?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // --- a double-click renames in place -----------------------------------
        var first = Descendants<Button>(host)
            .FirstOrDefault(b => b.Tag is string tag && tag.StartsWith("pose-", StringComparison.Ordinal));
        if (first is not null)
        {
            first.RaiseEvent(new MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
                Source = first,
            });
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var editing = Descendants<Controls.InlineRenameBox>(host).Any();
            Check(editing, "a double-click swaps the row for its rename editor");
        }

        return Report(failures);
    }

    /// <summary>
    /// The header menu's accessible name, read from the row model rather than spelled
    /// here: a literal would put this test file in the string manifest's way, and the
    /// name belongs to the sidebar.
    /// </summary>
    private const string HeaderMenuName = SessionMenuModel.MoreOptionsLabel;

    private static int Report(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            Console.WriteLine("sidebar self-test: all checks passed");
            return 0;
        }

        foreach (var failure in failures)
        {
            Console.WriteLine("sidebar self-test FAILED: " + failure);
        }

        return 2;
    }

    private static string? AutomationName(DependencyObject element) =>
        System.Windows.Automation.AutomationProperties.GetName(element) is { Length: > 0 } name ? name : null;

    private static T? FindDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement =>
        Descendants<T>(root).FirstOrDefault(e => e.Name == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
