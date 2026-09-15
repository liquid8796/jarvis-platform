using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JarvisCode.App.Controls;

/// <summary>Which of the reference's two in-place rename editors this is.</summary>
public enum InlineRenameLook
{
    /// <summary>
    /// The boxed one the sidebar row swaps in — the reference's
    /// <c>flex h-7 max-w-full items-center rounded-md bg-surface-3 px-1.5 ring-1 ring-inset
    /// ring-fill-accent</c> (desktop 1.44121.4.0, ion-dist chunk <c>c19ba0b81-DU76bj3t.js</c>).
    /// </summary>
    Boxed,

    /// <summary>
    /// The bare one the session titlebar swaps in — the reference's
    /// <c>min-w-0 [field-sizing:content] text-body-medium text-primary bg-transparent
    /// border-0 p-0 rounded-[3px] outline outline-[1.5px] outline-[var(--cds-fill-accent)]
    /// outline-offset-[1px]</c> (its <c>jh</c> in <c>c66fe388e-DOFZnzRG.js</c>). It carries
    /// no fill and no padding at all: only an accent outline standing off the text.
    /// </summary>
    TitleBar,
}

/// <summary>
/// The reference's in-place session rename editor, in both the shapes it ships. Enter
/// commits, Escape cancels, and losing focus commits too; a name that is blank after
/// trimming, or unchanged, leaves the old one standing.
///
/// One control rather than two because the behaviour is the reference's own single
/// commit/cancel pair — only the chrome differs between the row and the titlebar.
/// </summary>
public sealed class InlineRenameBox : Border
{
    private readonly TextBox _input;
    private readonly Action<string> _commit;
    private readonly Action _close;
    private readonly InlineRenameLook _look;
    private bool _closed;

    /// <summary>The boxed variant's height (the reference's <c>h-7</c>).</summary>
    public const double BoxHeight = 28;

    /// <summary>The titlebar variant's <c>text-body-medium</c> at the desktop's comfortable density.</summary>
    private const double TitleFontSize = 14;

    /// <summary>Its <c>outline-[1.5px]</c>.</summary>
    private const double TitleOutlineThickness = 1.5;

    /// <summary>Its <c>outline-offset-[1px]</c>, which is why the outline stands off the text.</summary>
    private const double TitleOutlineOffset = 1;

    /// <summary>The floor of the reference's <c>size={min(max(len,10),30)}</c>, in characters.</summary>
    private const int TitleMinChars = 10;

    /// <summary>The ceiling of that same clamp.</summary>
    private const int TitleMaxChars = 30;

    public InlineRenameBox(
        string currentName,
        Action<string> commit,
        Action close,
        InlineRenameLook look = InlineRenameLook.Boxed)
    {
        _commit = commit;
        _close = close;
        _look = look;

        _input = new TextBox
        {
            Text = currentName,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = look == InlineRenameLook.TitleBar ? TitleFontSize : 13,
        };

        if (look == InlineRenameLook.TitleBar)
        {
            _input.FontWeight = FontWeights.Medium;
            _input.SetResourceReference(Control.ForegroundProperty, "Text100Brush");
            _input.TextChanged += (_, _) => ResizeToContent();
        }
        else
        {
            _input.MinWidth = 48;
            _input.SetResourceReference(Control.ForegroundProperty, "Text100Brush");
        }

        _input.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Session name");
        _input.KeyDown += OnKeyDown;
        _input.LostKeyboardFocus += (_, _) => Commit();

        if (look == InlineRenameLook.TitleBar)
        {
            CornerRadius = new CornerRadius(4);
            BorderThickness = new Thickness(TitleOutlineThickness);
            Padding = new Thickness(TitleOutlineOffset);
            Background = Brushes.Transparent;
            VerticalAlignment = VerticalAlignment.Center;
            SetResourceReference(BorderBrushProperty, "Accent100Brush");
        }
        else
        {
            Height = BoxHeight;
            CornerRadius = new CornerRadius(6);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(8, 0, 8, 0);
            SetResourceReference(BackgroundProperty, "Bg300Brush");
            SetResourceReference(BorderBrushProperty, "AccentBrandBrush");
        }

        Child = _input;

        Loaded += (_, _) =>
        {
            if (_look == InlineRenameLook.TitleBar)
            {
                ResizeToContent();
            }

            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <summary>
    /// The titlebar editor's <c>[field-sizing:content]</c>: the box follows the text rather
    /// than filling its slot, between the two ends of the reference's own
    /// <c>size={min(max(len,10),30)}</c> clamp.
    /// </summary>
    private void ResizeToContent()
    {
        var typeface = new Typeface(_input.FontFamily, _input.FontStyle, _input.FontWeight, _input.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var em = Measure("0", typeface, dpi);
        var text = Measure(_input.Text.Length == 0 ? " " : _input.Text, typeface, dpi);

        _input.MinWidth = em * TitleMinChars;
        _input.MaxWidth = em * TitleMaxChars;
        // The caret needs a column of its own past the last glyph, or typing at the end
        // scrolls the text the editor is meant to be sizing itself around.
        _input.Width = Math.Clamp(text + 2, _input.MinWidth, _input.MaxWidth);
    }

    private double Measure(string text, Typeface typeface, double dpi) =>
        new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            _input.FontSize,
            Brushes.Black,
            dpi).WidthIncludingTrailingWhitespace;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Commit();
                break;
            case Key.Escape:
                e.Handled = true;
                Cancel();
                break;
        }
    }

    private void Commit()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        var name = _input.Text.Trim();
        if (name.Length > 0)
        {
            _commit(name);
        }

        _close();
    }

    private void Cancel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _close();
    }

    /// <summary>The accessible name the reference gives the clickable title (`Z444Es4IIr`).</summary>
    public static string RenameLabel(string name) =>
        $"{(string.IsNullOrWhiteSpace(name) ? Untitled : name)}, rename session";

    /// <summary>The reference's placeholder for a session with no name.</summary>
    public const string Untitled = "Untitled";
}
