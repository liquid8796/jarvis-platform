using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JarvisCode.App.Controls;

/// <summary>
/// The session titlebar's origin label — the working folder, and the environment it
/// runs in where there is one. Ported from the reference desktop 1.44121.4.0 (ion-dist
/// chunk <c>c66fe388e-DOFZnzRG.js</c>: <c>bp</c> over the class <c>hp</c> = <c>up</c> +
/// <c>before:bg-alpha-2 text-secondary min-w-0</c>, with the content <c>yp</c>).
///
/// Its three parts swap on one flag, which is the whole point of the control: while the
/// header has room the pill reads <c>{environment} · {label}</c>, and once
/// <see cref="Compact"/> is set the words go and a folder icon takes their place, so a
/// narrow header keeps the origin without keeping its width.
/// </summary>
public sealed class SessionOriginPill : Border
{
    /// <summary>The reference's <c>h-[20px]</c>.</summary>
    private const double PillHeight = 20;

    /// <summary>The reference's <c>px-1.25</c>.</summary>
    private const double PillPaddingX = 5;

    /// <summary>The reference's <c>gap-0.75</c>.</summary>
    private const double PartGap = 3;

    /// <summary>The reference's <c>rounded</c> at the desktop's comfortable density.</summary>
    private const double PillRadius = 8;

    /// <summary>The reference's <c>text-footnote</c> at the desktop's comfortable density.</summary>
    private const double FootnoteSize = 13;

    /// <summary>Its <c>max-w-[160px]</c> on the environment segment.</summary>
    private const double EnvironmentMaxWidth = 160;

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _folder;
    private readonly TextBlock _environment;
    private readonly Border _separator;
    private readonly TextBlock _label;

    private string _labelText = "";
    private string? _environmentName;
    private bool _compact;

    public SessionOriginPill()
    {
        Height = PillHeight;
        CornerRadius = new CornerRadius(PillRadius);
        Padding = new Thickness(PillPaddingX, 0, PillPaddingX, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Arrow;
        Focusable = false;
        SetResourceReference(BackgroundProperty, "SelectedOverlayBrush");
        SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");

        _folder = new Path
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _folder.SetResourceReference(Shape.StrokeProperty, "Text300Brush");
        _folder.SetResourceReference(Path.DataProperty, "FolderGlyph");

        _environment = Part();
        _environment.MaxWidth = EnvironmentMaxWidth;

        // Its `vp`: a 2px dot at half opacity, carrying the text colour, which rides
        // between the environment and the label and hides with them.
        _separator = new Border
        {
            Width = 2,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Opacity = 0.5,
            Margin = new Thickness(PartGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _separator.SetResourceReference(BackgroundProperty, "Text300Brush");

        _label = Part();

        _row.Children.Add(_folder);
        _row.Children.Add(_environment);
        _row.Children.Add(_separator);
        _row.Children.Add(_label);
        Child = _row;
        Apply();
    }

    private static TextBlock Part()
    {
        var block = new TextBlock
        {
            FontSize = FootnoteSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        return block;
    }

    /// <summary>The folder the session works in — the reference's <c>label</c>.</summary>
    public string Label
    {
        get => _labelText;
        set
        {
            if (_labelText == value)
            {
                return;
            }

            _labelText = value;
            Apply();
        }
    }

    /// <summary>
    /// The environment the session runs in, which the reference shows ahead of the label
    /// and which is also the only thing that gives the pill a tooltip. Null here: this
    /// build has no environments.
    /// </summary>
    public string? EnvironmentName
    {
        get => _environmentName;
        set
        {
            if (_environmentName == value)
            {
                return;
            }

            _environmentName = value;
            Apply();
        }
    }

    /// <summary>
    /// The header has run out of room: show the folder icon alone. The reference does this
    /// with <c>group-data-[pills-compact]/lead</c> on the three parts rather than by
    /// rebuilding the pill, so the swap costs no layout beyond the widths themselves.
    /// </summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value)
            {
                return;
            }

            _compact = value;
            Apply();
        }
    }

    private void Apply()
    {
        var hasLabel = _labelText.Length > 0;
        var hasEnvironment = _environmentName is { Length: > 0 };

        Visibility = hasLabel || hasEnvironment ? Visibility.Visible : Visibility.Collapsed;

        _folder.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;

        _environment.Text = _environmentName ?? "";
        _environment.Visibility = hasEnvironment && !_compact ? Visibility.Visible : Visibility.Collapsed;
        _separator.Visibility = _environment.Visibility;

        _label.Text = _labelText;
        _label.Margin = new Thickness(
            _separator.Visibility == Visibility.Visible || _folder.Visibility == Visibility.Visible ? PartGap : 0,
            0, 0, 0);
        _label.Visibility = hasLabel && !_compact ? Visibility.Visible : Visibility.Collapsed;

        // The reference gives the pill a tooltip only where there is an environment to
        // name beside the folder; a plain local pill carries none.
        ToolTip = hasEnvironment && hasLabel ? $"{_environmentName} · {_labelText}" : null;

        // Changing a child's Visibility invalidates that child, not this control, so the
        // titlebar's own measure pass would re-measure the pill and be handed the width it
        // had before the swap — which is how a compacted pill kept its label's width.
        InvalidateMeasure();
    }
}
