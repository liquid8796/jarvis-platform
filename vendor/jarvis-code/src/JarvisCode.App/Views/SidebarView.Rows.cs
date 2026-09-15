using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The Code sidebar's list, drawn the way the reference desktop draws it
/// (1.44121.2.0): sections whose <em>first</em> header carries the filter icon and the
/// header menu, rows with no leading icon whose status mark rides the trailing edge
/// and steps aside for the row's controls, a title that fades out rather than
/// ellipsing, side sessions nested under an accordion guide, twenty rows per bucket
/// over a "Show {n} more" row, and the jump-hint keycaps Ctrl+Shift raises.
/// </summary>
public partial class SidebarView
{
    private static readonly SidebarMetrics M = SidebarMetrics.Current;

    /// <summary>How many extra rows "Show more" has revealed, per bucket key.</summary>
    private readonly Dictionary<string, int> _revealed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _visibleKeys = new(StringComparer.Ordinal);
    private string? _windowFilterKey;

    /// <summary>The row ids as drawn, so the jump hints and Ctrl+1…9 cannot disagree.</summary>
    private readonly List<string> _renderedOrder = [];

    /// <summary>Every row's hint keycap, in draw order, so Ctrl+Shift can raise them together.</summary>
    private readonly List<Border> _jumpHints = [];

    private bool _hintsVisible;
    private DispatcherTimerLite? _hintTimer;

    /// <summary>The pin target, which is only in the tree while nothing is pinned.</summary>
    private FrameworkElement? _pinDropZone;

    /// <summary>The reference's jump-hint digits, in its own order.</summary>
    private const string JumpHintDigits = "123456789";

    /// <summary>The reference's <c>CB</c>: how long the chord is held before the caps appear.</summary>
    private const int JumpHintDelayMs = 200;

    /// <summary>The order the list is actually drawn in — what Ctrl+1…9 should follow.</summary>
    public IReadOnlyList<string> RenderedOrder => _renderedOrder;

    /// <summary>The Code surface's list: the reference's sections, rows and truncation.</summary>
    private void RenderCodeList(string filter)
    {
        var groups = _groups!;
        groups.Prune(_sessions.Select(static s => s.Id));

        var state = FilterState;
        var filterKey = filter + "|" + System.Text.Json.JsonSerializer.Serialize(state);
        if (_windowFilterKey != filterKey) { _visibleKeys.Clear(); _windowFilterKey = filterKey; }
        var inputs = _sessions.Select(ToInput).ToList();
        var sections = SidebarPresentation.Build(
            inputs,
            state,
            [.. groups.Data.Groups.Select(g => (g.Id, g.Name, g.Collapsed))],
            filter,
            IsSectionCollapsed,
            DateTimeOffset.Now);

        _renderedOrder.Clear();
        _jumpHints.Clear();

        // With nothing pinned, the reference keeps a pin target available for a drag
        // and teaches it with its own three labels; it is out of the way until a row
        // is actually being dragged.
        _pinDropZone = sections.Any(static s => s.Pinned && s.Rows.Count > 0) ? null : BuildPinDropZone();
        if (_pinDropZone is not null)
        {
            GroupsHost.Items.Add(_pinDropZone);
        }

        if (sections.Count == 0)
        {
            // With nothing to list, the reference draws the standing "Recents" header
            // so the filter and the menu still have a home (its `Tn.length === 0` arm).
            GroupsHost.Items.Add(BuildSectionHeader(
                new SidebarSection(SidebarPresentation.RecentsKey, SidebarPresentation.RecentsHeader, [],
                    new Dictionary<string, string>(), CarriesHeaderTrailing: true) { Collapsible = false }));
            GroupsHost.Items.Add(EmptyLabel(filter.Length > 0
                ? SidebarPresentation.NoMatches
                : SidebarPresentation.NoActiveSessions));
        }
        else
        {
            foreach (var section in sections)
            {
                GroupsHost.Items.Add(BuildSection(section));
            }
        }

        // The reference's PR badge needs one lookup per folder; the cache does them
        // off the UI thread and repaints when it has an answer.
        if (state.ShowPrStatus && _services is { } services)
        {
            _ = services.PullRequests.RefreshAsync(inputs.Select(static s => s.WorkingDirectory));
        }

        // Right-click on the empty list area creates a group.
        if (GroupsHost.ContextMenu is null)
        {
            var menu = new ContextMenu();
            var newGroup = new MenuItem { Header = SidebarPresentation.NewGroup };
            newGroup.Click += (_, _) => CreateGroupPrompt();
            menu.Items.Add(newGroup);
            GroupsHost.ContextMenu = menu;
        }
    }

    /// <summary>Whether a section is folded shut, by its own bucket key.</summary>
    private bool IsSectionCollapsed(string bucketKey)
    {
        var groups = _groups;
        if (groups is null)
        {
            return false;
        }

        return bucketKey switch
        {
            SidebarPresentation.UngroupedKey => groups.Data.UngroupedCollapsed,
            SidebarPresentation.ArchivedKey => groups.Data.ArchivedCollapsed,
            _ => groups.IsBucketCollapsed(bucketKey),
        };
    }

    private void ToggleSectionCollapsed(SidebarSection section)
    {
        var groups = _groups;
        if (groups is null)
        {
            return;
        }

        var collapsed = !section.Collapsed;
        if (section.GroupId is { } groupId)
        {
            groups.SetCollapsed(groupId, collapsed);
        }
        else if (section.Key == SidebarPresentation.UngroupedKey)
        {
            groups.Data.UngroupedCollapsed = collapsed;
            groups.Save();
        }
        else if (section.Key == SidebarPresentation.ArchivedKey)
        {
            groups.Data.ArchivedCollapsed = collapsed;
            groups.Save();
        }
        else
        {
            groups.SetBucketCollapsed(section.Key, collapsed);
        }

        Render();
    }

    private FrameworkElement BuildSection(SidebarSection section)
    {
        var container = new StackPanel { AllowDrop = section.AcceptsDrop, Tag = section.GroupId };
        if (section.AcceptsDrop)
        {
            HookDropTarget(container, section.GroupId);
        }

        container.Children.Add(BuildSectionHeader(section));
        if (section.Collapsed)
        {
            return container;
        }

        var extra = _revealed.TryGetValue(section.Key, out var revealed) ? revealed : 0;
        var (initial, hidden) = SidebarPresentation.Truncate(section.Rows, extra);
        var visible = StableSidebarWindow.Select(section.Rows, _visibleKeys.GetValueOrDefault(section.Key), initial.Count,
            GroupsHost.IsMouseOver || GroupsHost.IsKeyboardFocusWithin);
        _visibleKeys[section.Key] = visible.Select(row => row.Id).ToArray();

        if (visible.Count == 0)
        {
            if (section.EmptyBody is { } empty)
            {
                container.Children.Add(section.AcceptsDrop
                    ? BuildDropPlaceholder(section.GroupId)
                    : SectionNotice(empty));
            }

            return container;
        }

        foreach (var run in SidebarPresentation.Runs(visible, section.ParentOf))
        {
            if (run.ParentId is null)
            {
                foreach (var row in run.Rows)
                {
                    container.Children.Add(BuildSessionRow(row, section));
                }

                continue;
            }

            if (IsChildBlockCollapsed(run.ParentId))
            {
                // The parent's caret is folded, so its children are not drawn - but
                // they still hold their place in the jump order's numbering nowhere,
                // since the reference numbers only what is on screen.
                continue;
            }

            container.Children.Add(BuildChildBlock(run, section));
        }

        if (hidden > 0)
        {
            container.Children.Add(BuildShowMoreRow(section, hidden));
        }

        return container;
    }

    /// <summary>
    /// The reference's section header (<c>WE</c>): the label, a caret that points right
    /// while the section is shut and turns down when it is open — revealed on hover —
    /// and a trailing slot the <em>first</em> section fills with the filter icon and
    /// the header menu.
    /// </summary>
    private FrameworkElement BuildSectionHeader(SidebarSection section)
    {
        var label = new TextBlock
        {
            Text = section.Title,
            FontSize = M.GroupFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        var caret = new TextBlock
        {
            Text = "",
            FontSize = 8,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = section.Collapsed ? 1 : 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(section.Collapsed ? 0 : 90),
        };
        caret.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
        caret.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(label);
        if (section.Collapsible)
        {
            content.Children.Add(caret);
        }

        var grid = new Grid
        {
            MinHeight = M.GroupHeaderMinHeight,
            Margin = new Thickness(M.LabelInset, M.GroupPaddingTop, M.GroupHeaderPaddingRight, 4),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement first = content;
        if (section.Collapsible)
        {
            var toggle = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(2, 2, 2, 2),
                Margin = new Thickness(-2, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = FlatButtonTemplate(),
            };
            toggle.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, section.Title);
            toggle.Click += (_, _) => ToggleSectionCollapsed(section);
            toggle.MouseEnter += (_, _) => caret.Opacity = 1;
            toggle.MouseLeave += (_, _) => caret.Opacity = section.Collapsed ? 1 : 0;
            first = toggle;
        }

        grid.Children.Add(first);

        if (section.CarriesHeaderTrailing)
        {
            var trailing = BuildHeaderTrailing();
            Grid.SetColumn(trailing, 1);
            grid.Children.Add(trailing);
        }

        if (section.GroupId is not null)
        {
            grid.ContextMenu = BuildGroupMenu(section);
            HookGroupReorder(grid, section.GroupId);
        }

        // Hovering anywhere in the section reveals its caret, which is what the
        // reference's `group-hover/section` selector does.
        grid.MouseEnter += (_, _) => caret.Opacity = 1;
        grid.MouseLeave += (_, _) => caret.Opacity = section.Collapsed ? 1 : 0;
        return grid;
    }

    /// <summary>
    /// The format a dragged group header carries. The reference reorders its custom
    /// group sections two ways — a drag on the header and the header menu's Move
    /// up / Move down, its `onCommitDrag` and `onMoveSection` with the sources
    /// "drag" and "header_menu" — and this is the first of them.
    /// </summary>
    private const string GroupDragFormat = "JarvisCode.SessionGroupOrder";

    private void HookGroupReorder(FrameworkElement header, string groupId)
    {
        var start = new Point();
        header.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(this);
        header.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) <= SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            DragDrop.DoDragDrop(header, new DataObject(GroupDragFormat, groupId), DragDropEffects.Move);
        };

        header.AllowDrop = true;
        header.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(GroupDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        header.Drop += (_, e) =>
        {
            if (_groups is { } groups && e.Data.GetData(GroupDragFormat) is string dragged && dragged != groupId)
            {
                var from = groups.Data.Groups.FindIndex(g => g.Id == dragged);
                var to = groups.Data.Groups.FindIndex(g => g.Id == groupId);
                if (from >= 0 && to >= 0 && groups.MoveGroup(dragged, to - from))
                {
                    Render();
                }
            }

            e.Handled = true;
        };
    }

    /// <summary>The filter icon and the header's own menu, which ride the first section's header.</summary>
    private FrameworkElement BuildHeaderTrailing()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var filter = new Button
        {
            Width = M.RowControl,
            Height = M.RowControl,
            Padding = new Thickness(0),
            Focusable = false,
            Content = new TextBlock
            {
                Text = "",
                FontSize = 12,
                FontFamily = (FontFamily)FindResource("IconFontFamily"),
            },
        };
        filter.SetResourceReference(StyleProperty, "IconButton");
        var state = FilterState;
        filter.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            SidebarFilterModel.TriggerLabel(state));
        filter.ToolTip = SidebarFilterModel.TriggerLabel(state);
        filter.Click += (_, _) => OpenFilterMenu(filter);
        row.Children.Add(filter);

        var overflow = new Button
        {
            Width = M.RowControl,
            Height = M.RowControl,
            Padding = new Thickness(0),
            Focusable = false,
            Content = new TextBlock { Text = "⋮", FontSize = 13, FontWeight = FontWeights.Bold },
        };
        overflow.SetResourceReference(StyleProperty, "IconButton");
        overflow.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            SessionMenuModel.MoreOptionsLabel);
        overflow.Click += (_, _) => OpenRecentsMenu(overflow);
        row.Children.Add(overflow);
        return row;
    }

    /// <summary>
    /// The pin target the reference offers while nothing is pinned, under its own
    /// "Pinned" header. Its label is the reference's three-step ladder — the resting
    /// tip, then the invitation while a row is in flight, then the release prompt once
    /// the pointer is over it.
    /// </summary>
    private FrameworkElement BuildPinDropZone()
    {
        var label = new TextBlock
        {
            Text = PinHints.DragToPin,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        var zone = new Border
        {
            Child = label,
            AllowDrop = true,
            Padding = new Thickness(8, 9, 8, 9),
            Margin = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(M.RadiusPill),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
        };
        zone.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");

        zone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        zone.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DragFormat))
            {
                label.Text = PinHints.LetGo;
                zone.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
            }
        };
        zone.DragLeave += (_, _) =>
        {
            label.Text = PinHints.DropHere;
            zone.Background = Brushes.Transparent;
        };
        zone.Drop += (_, e) =>
        {
            if (_groups is not null && e.Data.GetData(DragFormat) is string sessionId)
            {
                _groups.SetPinned(sessionId, true);
                Render();
            }

            e.Handled = true;
        };

        var header = new TextBlock
        {
            Text = SidebarPresentation.PinnedHeader,
            FontSize = M.GroupFontSize,
            Margin = new Thickness(M.LabelInset, M.GroupPaddingTop, 0, 4),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        var host = new StackPanel { Visibility = Visibility.Collapsed };
        host.Children.Add(header);
        host.Children.Add(zone);
        host.Tag = label;
        return host;
    }

    /// <summary>Reveals or hides the pin target for the length of one drag.</summary>
    private void SetPinDropZoneVisible(bool visible)
    {
        if (_pinDropZone is not StackPanel host)
        {
            return;
        }

        host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (host.Tag is TextBlock label)
        {
            label.Text = visible ? PinHints.DropHere : PinHints.DragToPin;
        }
    }

    /// <summary>The muted notice a bucket with nothing in it shows.</summary>
    private static TextBlock SectionNotice(string text)
    {
        var notice = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(M.LabelInset, 0, 0, 4),
        };
        notice.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return notice;
    }

    /// <summary>
    /// The reference's "Show {count} more" row: a muted row of the list's own height
    /// whose label sits in the icon column, revealing another <c>truncateAt</c> rows.
    /// </summary>
    private FrameworkElement BuildShowMoreRow(SidebarSection section, int hidden)
    {
        var label = new TextBlock
        {
            Text = $"Show {hidden} more",
            FontSize = M.RowFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(M.LeadingSlot + M.RowGap, 0, 0, 0),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        var row = new Button
        {
            Content = label,
            Height = M.RowHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(M.RowPaddingX, 0, M.RowPaddingX, 0),
        };
        row.SetResourceReference(StyleProperty, "RowButton");
        row.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            $"Show {hidden} more in {section.Title}");
        row.Click += (_, _) =>
        {
            _revealed[section.Key] = (_revealed.TryGetValue(section.Key, out var n) ? n : 0)
                + SidebarPresentation.TruncateAt;
            Render();
        };
        return row;
    }

    /// <summary>
    /// A parent's children, drawn under the reference's accordion guide: a one-pixel
    /// hairline down the leading slot's centre, with the rows indented past it.
    /// </summary>
    private FrameworkElement BuildChildBlock(SidebarRowRun run, SidebarSection section)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        foreach (var row in run.Rows)
        {
            rows.Children.Add(BuildSessionRow(row, section, nested: true));
        }

        var guide = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(M.AccordionGuideOffset, 0, 0, 4),
            IsHitTestVisible = false,
        };
        guide.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");

        var block = new Grid();
        block.Children.Add(rows);
        block.Children.Add(guide);
        return block;
    }

    private bool IsChildBlockCollapsed(string parentId) =>
        _groups?.IsBucketCollapsed(ChildBlockKey(parentId)) ?? false;

    private static string ChildBlockKey(string parentId) => $"children:{parentId}";

    /// <summary>
    /// One session row, at the reference's geometry: no leading icon, the title
    /// masked rather than ellipsed, the status mark on the trailing edge stepping
    /// aside for the ⋮ on hover, and the jump-hint keycap over the icon column.
    /// </summary>
    private FrameworkElement BuildSessionRow(SidebarSessionInput session, SidebarSection section, bool nested = false)
    {
        if (_renamingSessionId == session.Id)
        {
            return BuildRenameRow(session);
        }

        var isActive = session.Id == _activeSessionId;
        var isSelected = _selected.Contains(session.Id);
        var hasChildren = section.ParentOf.Values.Contains(session.Id, StringComparer.Ordinal);
        var state = session.State;
        var showsMark = SessionRowStates.ShowsMark(state, session.IsArchived) &&
                        !(state == SessionRowState.Ready && !_groupByProject);
        var decorated = showsMark;

        var grid = new Grid();

        // The session colour rides the row's leading edge without taking a column,
        // so a coloured row's title starts exactly where an uncoloured one's does.
        if (SessionColors.Find(session.ColorKey) is { } color)
        {
            var stripe = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 3, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Hex)),
                ToolTip = color.Label,
                IsHitTestVisible = false,
            };
            grid.Children.Add(stripe);
        }

        var caretWidth = 0.0;
        if (hasChildren)
        {
            var folded = IsChildBlockCollapsed(session.Id);
            var caret = new Button
            {
                Width = M.IconSize,
                Height = M.IconSize,
                Padding = new Thickness(0),
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(M.RowPaddingX, 0, 0, 0),
                Content = new TextBlock
                {
                    Text = "",
                    FontSize = 8,
                    FontFamily = (FontFamily)FindResource("IconFontFamily"),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(folded ? 0 : 90),
                },
            };
            caret.SetResourceReference(StyleProperty, "IconButton");
            caret.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                folded ? "Expand" : "Collapse");
            caret.Click += (_, _) =>
            {
                _groups?.SetBucketCollapsed(ChildBlockKey(session.Id), !folded);
                Render();
            };
            grid.Children.Add(caret);
            caretWidth = M.IconSize + M.RowGap;
        }

        var title = new TextBlock
        {
            Text = SidebarPresentation.TitleOf(session),
            FontSize = M.RowFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.NoWrap,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "Text100Brush" : "Text300Brush");

        var titleClip = new Grid
        {
            ClipToBounds = true,
            Margin = new Thickness(M.IconlessPad + caretWidth, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // A relative opacity mask is mapped to the element's *rendered* bounds, so
            // a panel with nothing painted in it would size the gradient to the text
            // and fade every title at its own last character. A transparent fill makes
            // the bounding box the layout box, which is what the fade is measured in.
            Background = Brushes.Transparent,
        };
        titleClip.Children.Add(title);
        grid.Children.Add(titleClip);

        FrameworkElement? mark = null;
        if (showsMark)
        {
            mark = StatusMark(session, state);
            mark.HorizontalAlignment = HorizontalAlignment.Right;
            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.Margin = new Thickness(0, 0, M.RowControlInset, 0);
            mark.IsHitTestVisible = false;
            // The decoration and the row's controls share one slot: whichever is
            // showing, the other is not.
            mark.Opacity = isActive ? 0 : 1;
            grid.Children.Add(mark);
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, M.RowControlInset, 0),
            Opacity = isActive ? 1 : 0,
        };

        if (FilterState.ShowPrStatus && PrBadge(session) is { } badge)
        {
            actions.Children.Add(badge);
        }

        var overflow = new Button
        {
            Width = M.RowControl,
            Height = M.RowControl,
            Padding = new Thickness(0),
            Focusable = false,
            Content = new TextBlock { Text = "⋮", FontSize = 13, FontWeight = FontWeights.Bold },
        };
        overflow.SetResourceReference(StyleProperty, "IconButton");
        overflow.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            SidebarPresentation.SessionActions);
        actions.Children.Add(overflow);
        grid.Children.Add(actions);

        var hint = JumpHintCap();
        grid.Children.Add(hint);

        var row = new Button
        {
            Content = grid,
            Height = M.RowHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(M.RowPaddingX + (nested ? M.RowIndent : 0), 0, M.RowPaddingX, 0),
            Tag = session.Id,
            AllowDrop = false,
        };
        row.SetResourceReference(StyleProperty, "RowButton");
        if (isActive || isSelected)
        {
            row.SetResourceReference(BackgroundProperty, isSelected ? "ActiveOverlayBrush" : "SelectedOverlayBrush");
        }

        row.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, title.Text);
        row.Click += (_, _) => OnRowClick(session);
        row.MouseEnter += (_, _) => SetRowActive(actions, mark, true);
        row.MouseLeave += (_, _) => SetRowActive(actions, mark, isActive);

        // The label fades out at the trailing edge instead of ellipsing, and deepens
        // once the row's controls are showing, which is the reference's two masks.
        // The deep mask is the reference's `!suffixed` hover / focus-within /
        // menu-open arm; a merely selected row is not one of them.
        var menuOpen = false;
        void Refresh() => ApplyFadeMask(titleClip, title, decorated, row.IsMouseOver || menuOpen);
        titleClip.SizeChanged += (_, _) => Refresh();
        title.SizeChanged += (_, _) => Refresh();
        row.MouseEnter += (_, _) => Refresh();
        row.MouseLeave += (_, _) => Refresh();
        row.Loaded += (_, _) => Refresh();

        // A double-click renames in place, which is what the reference's row does when
        // it is given an `onRename`.
        row.MouseDoubleClick += (_, e) =>
        {
            _renamingSessionId = session.Id;
            e.Handled = true;
            Render();
        };

        HookRowDrag(row, session);

        var menu = BuildRowMenu(session, section.Archived || session.IsArchived);
        overflow.Click += (_, _) =>
        {
            menu.PlacementTarget = overflow;
            menu.IsOpen = true;
        };
        row.ContextMenu = menu;

        // A row whose menu is open keeps the fill and the controls it had on hover,
        // which is the reference's `data-menu-open=true` arm of both.
        menu.Opened += (_, _) =>
        {
            menuOpen = true;
            row.SetResourceReference(BackgroundProperty, "HoverOverlayBrush");
            SetRowActive(actions, mark, true);
            Refresh();
        };
        menu.Closed += (_, _) =>
        {
            menuOpen = false;
            if (isActive || isSelected)
            {
                row.SetResourceReference(BackgroundProperty,
                    isSelected ? "ActiveOverlayBrush" : "SelectedOverlayBrush");
            }
            else
            {
                row.ClearValue(BackgroundProperty);
            }

            SetRowActive(actions, mark, isActive);
            Refresh();
        };

        if (section.Archived || session.IsArchived)
        {
            row.Opacity = 0.55;
        }

        _renderedOrder.Add(session.Id);
        _jumpHints.Add(hint);
        hint.Visibility = Visibility.Collapsed;
        return row;
    }

    private static void SetRowActive(UIElement actions, UIElement? mark, bool active)
    {
        actions.Opacity = active ? 1 : 0;
        if (mark is not null)
        {
            mark.Opacity = active ? 0 : 1;
        }
    }

    /// <summary>
    /// The reference's two masks: a 24px fade at rest, and a deeper one that clears
    /// 20px from the edge once the row's controls are showing or the row is decorated.
    /// </summary>
    private static void ApplyFadeMask(FrameworkElement clip, FrameworkElement label, bool decorated, bool active)
    {
        var width = clip.ActualWidth;
        if (width <= 0)
        {
            clip.OpacityMask = null;
            return;
        }

        var deep = decorated || active;
        if (!deep && !SidebarMetrics.LabelOverflows(label.ActualWidth, width, decorated: false, suffixed: false))
        {
            clip.OpacityMask = null;
            return;
        }

        var start = deep
            ? (width - SidebarMetrics.LabelFadeWidthActive) / width
            : (width - SidebarMetrics.LabelFadeWidth) / width;
        var end = deep ? (width - SidebarMetrics.LabelFadeClearActive) / width : 1.0;
        if (start <= 0)
        {
            start = 0;
        }

        if (end <= start)
        {
            end = Math.Min(1.0, start + 0.01);
        }

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, start));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, end));
        if (end < 1)
        {
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        }

        brush.Freeze();
        clip.OpacityMask = brush;
    }

    /// <summary>
    /// The row's status mark, measured on desktop 1.46388.2.0 where the import
    /// chain behind it finally resolves: a <c>status-dot</c> span whose whole
    /// shape is CSS — a 6px circle, accent while ready, warning while awaiting,
    /// and muted while running, where it blinks between .3 and 1 opacity over
    /// 1.2s. An error is not a dot at all there but a warning-shaped icon at the
    /// small icon size, which at this build's density is 16px.
    ///
    /// <para>
    /// Only the error icon's artwork is this build's: the reference draws it from
    /// its own icon set, so this uses lucide's triangle at the same size and
    /// colour — the substitution the thinking cell's gutter already makes.
    /// </para>
    /// </summary>
    private FrameworkElement StatusMark(SidebarSessionInput session, SessionRowState state)
    {
        FrameworkElement glyph;
        switch (state)
        {
            case SessionRowState.Running:
                var running = Dot("Text400Brush");
                // dframe-dot-blink: .3 at both ends, 1 at the midpoint.
                var blink = new DoubleAnimation(0.3, 1.0, new Duration(TimeSpan.FromSeconds(0.6)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                running.BeginAnimation(UIElement.OpacityProperty, blink);
                glyph = running;
                break;
            case SessionRowState.Awaiting:
                glyph = Dot("Warning100Brush");
                break;
            case SessionRowState.Error:
                var error = new System.Windows.Shapes.Path
                {
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = (Geometry)FindResource("WarningTriangleGlyph"),
                };
                error.SetResourceReference(Shape.StrokeProperty, "Warning100Brush");
                glyph = error;
                break;
            default:
                glyph = Dot("AccentBrandBrush");
                break;
        }

        var label = SessionRowStates.Label(state, session.Awaiting);
        var host = new Grid { Width = M.RowControl, Height = M.RowControl };
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        host.Children.Add(glyph);
        host.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label);
        host.ToolTip = label;
        return host;
    }

    /// <summary>The reference's status-dot: a 6px circle in the named brush.</summary>
    private static Ellipse Dot(string brushKey)
    {
        var dot = new Ellipse { Width = 6, Height = 6 };
        dot.SetResourceReference(Shape.FillProperty, brushKey);
        return dot;
    }

    /// <summary>The row's PR badge: a tinted "#12" whose tooltip names the state.</summary>
    private static FrameworkElement? PrBadge(SidebarSessionInput session)
    {
        if (session.PrState == PrDisplayState.None || session.PrNumber is not { } number)
        {
            return null;
        }

        var badge = new TextBlock
        {
            Text = $"#{number}",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = $"#{number} · {GitBarPresentation.StateLabel(session.PrState)}",
        };
        badge.SetResourceReference(TextBlock.ForegroundProperty,
            GitBarPresentation.StateBrushKey(session.PrState));
        return badge;
    }

    /// <summary>
    /// The reference's jump-hint keycap: a 16px cap in the icon column, raised while
    /// Ctrl+Shift is held, whose digit opens that row.
    /// </summary>
    private static Border JumpHintCap()
    {
        var text = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");

        var cap = new Border
        {
            Height = 16,
            MinWidth = 16,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(M.RowPaddingX + ((M.LeadingSlot - 16) / 2), 0, 0, 0),
            IsHitTestVisible = false,
            Child = text,
        };
        cap.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
        cap.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        return cap;
    }

    /// <summary>
    /// Ctrl+Shift held for the reference's 200ms raises a keycap on the first nine
    /// rows; letting go, or anything else, takes them down again.
    /// </summary>
    internal void UpdateJumpHints(bool chordHeld)
    {
        if (!chordHeld)
        {
            _hintTimer?.Cancel();
            _hintTimer = null;
            if (_hintsVisible)
            {
                _hintsVisible = false;
                foreach (var cap in _jumpHints)
                {
                    cap.Visibility = Visibility.Collapsed;
                }
            }

            return;
        }

        if (_hintsVisible || _hintTimer is not null)
        {
            return;
        }

        _hintTimer = DispatcherTimerLite.After(JumpHintDelayMs, () =>
        {
            _hintTimer = null;
            _hintsVisible = true;
            for (var i = 0; i < _jumpHints.Count; i++)
            {
                var cap = _jumpHints[i];
                if (i < JumpHintDigits.Length)
                {
                    ((TextBlock)cap.Child).Text = JumpHintDigits[i].ToString();
                    cap.Visibility = Visibility.Visible;
                }
                else
                {
                    cap.Visibility = Visibility.Collapsed;
                }
            }
        });
    }

    private void HookRowDrag(Button row, SidebarSessionInput session)
    {
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragCandidate = session.Id;
        };
        row.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate != session.Id || !_groupByProject)
            {
                return;
            }

            var delta = e.GetPosition(this) - _dragStart;
            if (Math.Abs(delta.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) <= SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragCandidate = null;
            SetPinDropZoneVisible(true);
            var effects = DragDrop.DoDragDrop(row, new DataObject(DragFormat, session.Id), DragDropEffects.Move);
            SetPinDropZoneVisible(false);

            // A drag that landed nowhere inside the app is the reference's
            // "drag it out": the session opens in its own window.
            if (effects == DragDropEffects.None && DraggedOutside(e))
            {
                SessionWindowRequested?.Invoke(this, session.Id);
            }
        };
    }
}

/// <summary>
/// A one-shot delay that runs on the dispatcher and can be cancelled — the sidebar's
/// jump hints need the reference's 200ms hold before they appear, and nothing else in
/// this view needs a timer.
/// </summary>
internal sealed class DispatcherTimerLite
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    private DispatcherTimerLite(System.Windows.Threading.DispatcherTimer timer) => _timer = timer;

    public static DispatcherTimerLite After(int milliseconds, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        var lite = new DispatcherTimerLite(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return lite;
    }

    public void Cancel() => _timer.Stop();
}
