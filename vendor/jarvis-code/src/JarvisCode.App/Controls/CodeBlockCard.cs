using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisCode.Core.Markdown;

namespace JarvisCode.App.Controls;

/// <summary>
/// A fenced code block, in whichever of the two reference shapes the profile asks
/// for. Neither reference renderer draws the header bar this port used to: both
/// set <c>disableFileHeader</c> and float an icon-only action cluster over the
/// top-right corner instead. The chat renderer fades that cluster in on hover and
/// prints the fence's language above the code; the transcript renderer shows the
/// cluster always, prints no language, and adds the terminal actions.
/// </summary>
internal sealed class CodeBlockCard : ContentControl
{
    /// <summary>The reference resets its copied state after 1200ms.</summary>
    private static readonly TimeSpan CopiedDwell = TimeSpan.FromMilliseconds(1200);

    /// <summary>The reference's --h4 button box and --p2 gap between two of them.</summary>
    private const double ActionSize = 24;
    private const double ActionGap = 3;

    /// <summary>
    /// Past this the reference stops highlighting a block and renders it plain.
    /// Its check is on characters first and then on UTF-8 bytes, because a block
    /// under the limit in characters can still be over it in bytes.
    /// </summary>
    private const int SizeLimit = 204800;

    /// <summary>Lines measured when sizing a block to its content; enough for any real fence.</summary>
    private const int MeasuredLines = 500;

    private static readonly Geometry CopyGlyph = Geometry.Parse(
        "M10,8 H20 A2,2 0 0 1 22,10 V20 A2,2 0 0 1 20,22 H10 A2,2 0 0 1 8,20 V10 A2,2 0 0 1 10,8 Z " +
        "M4,16 C2.9,16 2,15.1 2,14 V4 C2,2.9 2.9,2 4,2 H14 C15.1,2 16,2.9 16,4");

    private static readonly Geometry CheckGlyph = Geometry.Parse("M20,6 L9,17 L4,12");

    private static readonly Geometry PlayGlyph = Geometry.Parse("M6,3 L20,12 L6,21 Z");

    private static readonly Geometry TerminalGlyph = Geometry.Parse("M4,17 L10,11 L4,5 M12,19 H20");

    private static readonly Geometry RevealGlyph = Geometry.Parse(
        "M2,12 C4,7 7.6,4.5 12,4.5 C16.4,4.5 20,7 22,12 C20,17 16.4,19.5 12,19.5 C7.6,19.5 4,17 2,12 Z " +
        "M12,9 A3,3 0 1 1 12,15 A3,3 0 1 1 12,9 Z");

    private readonly StackPanel _actions;
    private readonly CodeText _body;
    private readonly string _code;
    private readonly IReadOnlyList<CodeSecrets.Span> _secrets;
    private DispatcherTimer? _copiedTimer;
    private bool _revealed;

    internal CodeBlockCard(
        CodeFenceBlock fence,
        MarkdownMetrics metrics,
        Action<string>? shellRunner,
        Action<string>? terminalOpener)
    {
        _code = fence.Code;
        Focusable = false;
        IsTabStop = false;

        // The transcript covers credential-shaped spans until the reader asks;
        // the chat renderer has no such control, so it never masks.
        _secrets = metrics.ShowsLanguageLabel ? [] : CodeSecrets.Find(_code);

        // Over the size limit the reference renders the block without
        // highlighting; dropping the language is what turns the scanner off.
        bool overSizeLimit = ExceedsSizeLimit(_code);

        _body = new CodeText
        {
            Code = VisibleCode(),
            Language = overSizeLimit ? null : fence.Language,
            Wrap = metrics.Wrap switch
            {
                CodeWrapMode.Always => true,
                CodeWrapMode.WhenUntagged => string.IsNullOrWhiteSpace(fence.Language),
                _ => false,
            },
            FontSize = metrics.CodeSize,
            CodeLineHeight = metrics.CodeLineHeight,
            Padding = metrics.CodePadding,
        };
        _body.SetResourceReference(ForegroundProperty, "Text100Brush");

        var stack = new StackPanel();
        if (metrics.ShowsLanguageLabel && !string.IsNullOrWhiteSpace(fence.Language))
        {
            // "text-text-500 font-small p-3.5 pb-0" — a caption above the code,
            // and nothing at all when the fence declared no language.
            var label = new TextBlock
            {
                Text = fence.Language.Trim().ToLowerInvariant(),
                FontSize = 12,
                Margin = new Thickness(
                    metrics.CodePadding.Left, metrics.CodePadding.Top, metrics.CodePadding.Right, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "ChatFontFamily");
            stack.Children.Add(label);
            _body.Padding = new Thickness(
                metrics.CodePadding.Left, 0, metrics.CodePadding.Right, metrics.CodePadding.Bottom);
        }

        stack.Children.Add(_body);

        var card = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(metrics.CodeRadius),
            BorderThickness = new Thickness(metrics.CodeCardBorderThickness),
        };
        card.SetResourceReference(Border.BackgroundProperty, metrics.CodeCardFillKey);
        if (metrics.CodeCardBorderKey is { } borderKey)
        {
            card.SetResourceReference(Border.BorderBrushProperty, borderKey);
        }

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            // The chat cluster sits 8px in; the transcript's --p4 + --p1 is 7px.
            Margin = metrics.ShowsLanguageLabel ? new Thickness(0, 8, 8, 0) : new Thickness(0, 5, 7, 0),
            Opacity = metrics.HoverRevealsActions ? 0 : 1,
        };

        AddTerminalActions(fence, shellRunner, terminalOpener);
        if (_secrets.Count > 0)
        {
            AddRevealAction();
        }

        AddCopyAction(metrics);

        var root = new Grid();
        root.Children.Add(card);
        root.Children.Add(_actions);

        Content = root;
        TextSelectionScope.SetCopyText(this, _code);
        TextSelectionScope.SetCopyKind(this, TextSelectionScope.CopyKind.CodeBlock);
        HorizontalAlignment = metrics.ShrinksToContent ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        if (metrics.ShrinksToContent)
        {
            // w-fit: the block is only as wide as its longest line, and wraps
            // when the column is narrower than that. Measured once the control
            // is loaded, because the mono typeface is a theme resource.
            Loaded += (_, _) => card.MaxWidth = ContentWidth(metrics);
        }

        // "role=group" with the language in its name, which is what a screen
        // reader announces before reading the code.
        AutomationProperties.SetName(
            this,
            string.IsNullOrWhiteSpace(fence.Language) ? "Code" : $"{fence.Language.Trim()} code");

        if (metrics.HoverRevealsActions)
        {
            MouseEnter += (_, _) => FadeActions(1);
            MouseLeave += (_, _) => FadeActions(0);
            // Keyboard reach: the cluster also appears when focus lands inside it.
            _actions.GotKeyboardFocus += (_, _) => FadeActions(1);
            _actions.LostKeyboardFocus += (_, _) => FadeActions(IsMouseOver ? 1 : 0);
        }
    }

    /// <summary>The reference's own two-step check: characters, then UTF-8 bytes.</summary>
    private static bool ExceedsSizeLimit(string code)
        => code.Length > SizeLimit
           || (3 * code.Length > SizeLimit && System.Text.Encoding.UTF8.GetByteCount(code) > SizeLimit);

    /// <summary>
    /// The width of the longest line plus the block's own padding and the room
    /// the action cluster needs, so the buttons never sit over the code.
    /// </summary>
    private double ContentWidth(MarkdownMetrics metrics)
    {
        var typeface = new Typeface(
            (FontFamily)FindResource("MonoFontFamily"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double widest = 0;
        int measured = 0;
        foreach (var line in VisibleCode().Split('\n'))
        {
            if (measured++ >= MeasuredLines)
            {
                break;
            }

            var text = new FormattedText(
                line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, metrics.CodeSize, Brushes.Black, pixelsPerDip);
            widest = Math.Max(widest, text.WidthIncludingTrailingWhitespace);
        }

        double actions = _actions.Children.Count * (ActionSize + ActionGap) + _actions.Margin.Right;
        return Math.Ceiling(widest + metrics.CodePadding.Left + metrics.CodePadding.Right + actions);
    }

    private string VisibleCode()
        => _secrets.Count == 0 || _revealed ? _code : CodeSecrets.Mask(_code, _secrets);

    private void AddTerminalActions(
        CodeFenceBlock fence, Action<string>? shellRunner, Action<string>? terminalOpener)
    {
        if (shellRunner is null || !MarkdownView.IsShellLanguage(fence.Language))
        {
            return;
        }

        // A masked block must not be run: what would go to the shell is the
        // covered text, and revealing first is the reader's decision.
        _actions.Children.Add(NewAction(
            PlayGlyph, "Run in terminal", () =>
            {
                if (_secrets.Count == 0 || _revealed)
                {
                    shellRunner(_code);
                }
            }));

        if (terminalOpener is not null)
        {
            _actions.Children.Add(NewAction(TerminalGlyph, "Open in terminal", () => terminalOpener(_code)));
        }
    }

    private void AddRevealAction()
    {
        Button? button = null;
        button = NewAction(RevealGlyph, RevealName(), () =>
        {
            _revealed = !_revealed;
            _body.Code = VisibleCode();
            AutomationProperties.SetName(button!, RevealName());
            button!.ToolTip = RevealName();
        });
        _actions.Children.Add(button);
    }

    private string RevealName()
        => _revealed
            ? $"Hide {_secrets.Count} hidden value{(_secrets.Count == 1 ? "" : "s")}"
            : $"Reveal {_secrets.Count} hidden value{(_secrets.Count == 1 ? "" : "s")}";

    private void AddCopyAction(MarkdownMetrics metrics)
    {
        // The chat renderer's button is labelled "Copy to clipboard" and tooltipped
        // "Copy"; the transcript's is labelled "Copy code" and becomes "Copied".
        var idle = metrics.ShowsLanguageLabel ? "Copy to clipboard" : "Copy code";
        Button? button = null;
        Path? glyph = null;
        button = NewAction(CopyGlyph, idle, () =>
        {
            // Copying a masked block copies what is on screen, not the secret.
            MarkdownView.TrySetClipboard(VisibleCode());
            glyph!.Data = CheckGlyph;
            AutomationProperties.SetName(button!, "Copied");
            button!.ToolTip = "Copied";

            _copiedTimer?.Stop();
            _copiedTimer = new DispatcherTimer { Interval = CopiedDwell };
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer!.Stop();
                _copiedTimer = null;
                glyph.Data = CopyGlyph;
                AutomationProperties.SetName(button!, idle);
                button.ToolTip = metrics.ShowsLanguageLabel ? "Copy" : idle;
            };
            _copiedTimer.Start();
        });
        button.ToolTip = metrics.ShowsLanguageLabel ? "Copy" : idle;
        glyph = (Path)((Viewbox)button.Content).Child;
        _actions.Children.Add(button);
    }

    private static Button NewAction(Geometry glyph, string name, Action onClick)
    {
        var path = new Path
        {
            Data = glyph,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
        };
        path.SetResourceReference(Shape.StrokeProperty, "Text500Brush");

        var button = new Button
        {
            Width = ActionSize,
            Height = ActionSize,
            Margin = new Thickness(ActionGap, 0, 0, 0),
            Padding = new Thickness(5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Content = new Viewbox { Child = path, Width = 14, Height = 14 },
        };
        button.Click += (_, _) => onClick();
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
        return button;
    }

    private void FadeActions(double to)
        => _actions.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.HoldEnd });
}
