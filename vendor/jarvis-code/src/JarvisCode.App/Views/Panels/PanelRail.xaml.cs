using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The session header's pane rail. Ported from the reference's Xb/Jb/ey (ion-dist chunk
/// ca80fca8d-DkeN2GSR.js at the "Diff (uncommitted changes)" literal): a toggle per pane
/// whose tooltip carries the label and the chord as keycaps, whose accessible name is that
/// same label, and which grows a 4px accent dot at its top-right corner while the pane has
/// something waiting — the diff toggle then reads "Diff (uncommitted changes)" instead.
/// </summary>
public partial class PanelRail : UserControl
{
    private readonly List<(ToggleButton Toggle, PaneRailSpec Spec)> _rows = [];
    private bool _syncing;
    private bool _showToggles;
    private bool _diffActivity;
    private IReadOnlyCollection<string> _active = [];

    public PanelRail()
    {
        InitializeComponent();
        Rebuild();
    }

    /// <summary>Raised with the requested pane key, or null when the pane should close.</summary>
    public event EventHandler<string?>? PanelRequested;

    /// <summary>Raised when the overflow (⋮) button is pressed.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>The pane the rail last reported, kept for callers that ask.</summary>
    public string? ActivePanel { get; private set; }

    /// <summary>The Code surface shows the pane toggles; Chat shows only the menu.</summary>
    public bool ShowPanelToggles
    {
        get => _showToggles;
        set
        {
            _showToggles = value;
            Toggles.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The working tree has uncommitted changes: the diff toggle says so.</summary>
    public bool DiffActivity
    {
        get => _diffActivity;
        set
        {
            if (_diffActivity == value)
            {
                return;
            }

            _diffActivity = value;
            Rebuild();
        }
    }

    /// <summary>The overflow button, so a menu can be placed against it.</summary>
    public UIElement MenuPlacementTarget => MenuButton;

    /// <summary>The specs the rail is showing, in order — the ⋮ menu lists the overflow.</summary>
    public IReadOnlyList<PaneRailSpec> Specs => [.. _rows.Select(r => r.Spec)];

    /// <summary>
    /// How many toggles the rail could not fit. The reference slices its spec list by this
    /// count (its ey takes hiddenCount) and hands the first N to the ⋮ menu instead, where
    /// they render as checkbox rows carrying the same label, chord and activity dot.
    /// </summary>
    public int HiddenCount { get; private set; }

    /// <summary>The overflowed specs, in rail order, for the ⋮ menu to list.</summary>
    public IReadOnlyList<PaneRailSpec> OverflowSpecs => [.. Specs.Take(HiddenCount)];

    /// <summary>
    /// How wide one toggle costs the header, which is what the fitting arithmetic steps by:
    /// the toggle itself plus the gap after it, read off a real one rather than assumed, and
    /// falling back to the reference's own default until one has been laid out.
    /// </summary>
    public double ToggleWidth
    {
        get
        {
            if (_rows.Count == 0)
            {
                return SessionTitleBarLayout.DefaultToggleWidth;
            }

            var toggle = _rows[0].Toggle;
            var width = toggle.ActualWidth > 0 ? toggle.ActualWidth : toggle.DesiredSize.Width;
            var gap = toggle.Margin.Left + toggle.Margin.Right;
            return width > 0 ? width + gap : SessionTitleBarLayout.DefaultToggleWidth;
        }
    }

    /// <summary>
    /// Folds the first <paramref name="hidden"/> toggles into the ⋮ menu. The front is the
    /// end the reference slices from (its <c>oh</c> does
    /// <c>specs.slice(min(hiddenCount, specs.length))</c>), and how many that is belongs to
    /// the titlebar's fitting pass rather than to the rail — the rail cannot see the title
    /// it is competing with for room.
    /// </summary>
    public void SetHiddenCount(int hidden)
    {
        hidden = Math.Clamp(hidden, 0, _rows.Count);
        if (hidden == HiddenCount)
        {
            return;
        }

        HiddenCount = hidden;
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].Toggle.Visibility = i < hidden ? Visibility.Collapsed : Visibility.Visible;
        }

        // A toggle's Visibility invalidates that toggle, not the rail, and the titlebar
        // measures the rail: without this the room a fold gives back is not seen until some
        // later layout pass, which is a fold that appears to do nothing.
        InvalidateMeasure();
    }

    /// <summary>Reflects the panes the host actually shows, without raising the request.</summary>
    public void SetActivePanes(IReadOnlyCollection<string> panes)
    {
        _active = panes;
        ActivePanel = panes.FirstOrDefault();
        _syncing = true;
        foreach (var (toggle, spec) in _rows)
        {
            toggle.IsChecked = panes.Contains(spec.Pane);
        }

        _syncing = false;
    }

    /// <summary>The single-pane spelling the older call sites use.</summary>
    public void SetActive(string? panel) => SetActivePanes(panel is null ? [] : [panel]);

    /// <summary>
    /// The reference's spec list, in its order: terminal, diff, then the preview toggle.
    /// The chords come from the ported default keymap, so a rail tooltip and the window's
    /// chord can never drift apart.
    /// </summary>
    private IReadOnlyList<PaneRailSpec> BuildSpecs() =>
    [
        new PaneRailSpec(
            SidePanes.Terminal, "TerminalGlyph", SidePanes.Title(SidePanes.Terminal),
            PaneShortcuts.Display(PaneCommand.ToggleTerminal), _active.Contains(SidePanes.Terminal)),
        new PaneRailSpec(
            SidePanes.Diff, "ChangesGlyph", SidePanes.Title(SidePanes.Diff),
            PaneShortcuts.Display(PaneCommand.ToggleDiff), _active.Contains(SidePanes.Diff),
            _diffActivity, SidePanes.DiffActivityLabel),
        new PaneRailSpec(
            SidePanes.Preview, "GlobeGlyph", SidePanes.Title(SidePanes.Preview),
            PaneShortcuts.Display(PaneCommand.TogglePreview), _active.Contains(SidePanes.Preview)),
    ];

    private void Rebuild()
    {
        var specs = BuildSpecs();
        _rows.Clear();
        Toggles.Items.Clear();
        foreach (var spec in specs)
        {
            var toggle = new ToggleButton
            {
                Tag = spec.Pane,
                IsChecked = _active.Contains(spec.Pane),
                ToolTip = BuildTooltip(spec),
                Content = BuildContent(spec),
            };
            toggle.SetResourceReference(StyleProperty, "PanelToggle");
            toggle.SetValue(AutomationProperties.NameProperty, spec.Tooltip);
            toggle.Checked += OnToggleChanged;
            toggle.Unchecked += OnToggleChanged;
            Toggles.Items.Add(toggle);
            _rows.Add((toggle, spec));
        }

        Toggles.Visibility = _showToggles ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The reference's tooltip: the label, then the chord drawn as one cap per key.</summary>
    private static object BuildTooltip(PaneRailSpec spec)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = spec.Tooltip, VerticalAlignment = VerticalAlignment.Center });
        if (spec.Shortcut is { Length: > 0 } shortcut)
        {
            foreach (var key in shortcut.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries))
            {
                var cap = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(4, 0, 0, 0),
                    MinWidth = 18,
                    Height = 18,
                    Padding = new Thickness(3, 0, 3, 0),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cap.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
                var text = new TextBlock
                {
                    Text = key,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
                cap.Child = text;
                row.Children.Add(cap);
            }
        }

        return row;
    }

    /// <summary>The glyph, with the reference's 4px accent dot pinned to its top-right corner.</summary>
    private static object BuildContent(PaneRailSpec spec)
    {
        var glyph = new Path();
        glyph.SetResourceReference(StyleProperty, "PanelGlyph");
        glyph.SetResourceReference(Path.DataProperty, spec.Icon);
        if (!spec.Activity)
        {
            return glyph;
        }

        var grid = new Grid();
        grid.Children.Add(glyph);
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -1, -1, 0),
        };
        dot.SetResourceReference(Shape.FillProperty, "Accent100Brush");
        grid.Children.Add(dot);
        return grid;
    }

    /// <summary>
    /// Driven by the toggle's own checked state rather than Click, so a keyboard, a screen
    /// reader or a UI-automation client moves the rail exactly the way a mouse does.
    /// </summary>
    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var toggle = (ToggleButton)sender;
        var key = (string)toggle.Tag;
        var next = toggle.IsChecked == true ? key : null;
        ActivePanel = next;
        PanelRequested?.Invoke(this, next);
    }

    private void OnMenuClick(object sender, RoutedEventArgs e) => MenuRequested?.Invoke(this, EventArgs.Empty);
}
