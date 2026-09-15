using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// Draws whatever <see cref="ToastQueue"/> holds, in the reference desktop's own
/// geometry (its toast viewport in <c>shared-16-DFDNRrwQ.js</c>): pinned to the
/// bottom-right corner 16px in, 360px wide, the newest card in front and the
/// ones behind it lifted 14px and scaled down 4% each.
/// </summary>
public sealed class ToastHost : UserControl
{
    private const double CardWidth = 360;
    private const double ViewportInset = 16;

    private readonly Grid _stack = new();
    private readonly Dictionary<int, DispatcherTimer> _timers = [];
    private ToastQueue? _queue;

    public ToastHost()
    {
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(ViewportInset);
        Width = CardWidth;
        IsHitTestVisible = true;
        Content = _stack;
    }

    public void Attach(ToastQueue queue)
    {
        if (_queue is not null)
        {
            _queue.Changed -= Render;
        }

        _queue = queue;
        _queue.Changed += Render;
        Render();
    }

    private void Render()
    {
        _stack.Children.Clear();
        if (_queue is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var toasts = _queue.Toasts;
        Visibility = toasts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Newest last in the queue, and the newest is the card in front, so the
        // stack index counts backwards from the end.
        for (var i = toasts.Count - 1; i >= 0; i--)
        {
            var stackIndex = toasts.Count - 1 - i;
            if (!ToastQueue.IsVisible(stackIndex))
            {
                break;
            }

            var card = BuildCard(toasts[i], stackIndex);
            _stack.Children.Add(card);
        }

        SyncTimers(toasts);
    }

    private void SyncTimers(IReadOnlyList<Toast> toasts)
    {
        foreach (var id in _timers.Keys.ToList())
        {
            if (!toasts.Any(t => t.Id == id))
            {
                _timers[id].Stop();
                _timers.Remove(id);
            }
        }

        foreach (var toast in toasts)
        {
            if (toast.TimeoutMs <= 0 || _timers.ContainsKey(toast.Id))
            {
                continue;
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(toast.TimeoutMs) };
            var id = toast.Id;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _timers.Remove(id);
                _queue?.Close(id);
            };
            _timers[id] = timer;
            timer.Start();
        }
    }

    private FrameworkElement BuildCard(Toast toast, int stackIndex)
    {
        var (surface, foreground, ring, glyph) = toast.Variant switch
        {
            ToastVariant.Warning => ("Warning900Brush", "Warning000Brush", "Warning100Brush", "\uE7BA"),
            ToastVariant.Danger => ("Danger900Brush", "Danger000Brush", "Danger100Brush", "\uEA39"),
            _ => ("Bg000Brush", "Text100Brush", "BorderMidBrush", "\uE946"),
        };

        var row = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0),
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
        // The reference tints only the neutral icon down; warning and danger take
        // the card's own colour.
        icon.SetResourceReference(TextBlock.ForegroundProperty,
            toast.Variant == ToastVariant.Neutral ? "Text400Brush" : foreground);
        row.Children.Add(icon);

        var body = new StackPanel();
        var title = new TextBlock
        {
            Text = toast.Title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        body.Children.Add(title);

        if (toast.Description is { Length: > 0 } description)
        {
            var caption = new TextBlock
            {
                Text = description,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
            body.Children.Add(caption);
        }

        if (toast.ActionLabel is { Length: > 0 } actionLabel)
        {
            var action = new Button
            {
                Content = actionLabel,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            action.SetResourceReference(StyleProperty, "SecondaryButton");
            var id = toast.Id;
            var invoke = toast.ActionInvoke;
            action.Click += (_, _) =>
            {
                _queue?.Close(id);
                invoke?.Invoke();
            };
            body.Children.Add(action);
        }

        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        var dismiss = new Button
        {
            Content = new TextBlock { Text = "\uE8BB", FontSize = 9 },
            Padding = new Thickness(5),
            Margin = new Thickness(6, -3, -4, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        dismiss.SetResourceReference(StyleProperty, "IconButton");
        dismiss.SetResourceReference(FontFamilyProperty, "IconFontFamily");
        dismiss.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Dismiss");
        dismiss.ToolTip = "Dismiss";
        var closeId = toast.Id;
        dismiss.Click += (_, _) => _queue?.Close(closeId);
        Grid.SetColumn(dismiss, 2);
        row.Children.Add(dismiss);

        var card = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 3,
                Opacity = 0.3,
                Color = Colors.Black,
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, surface);
        card.SetResourceReference(Border.BorderBrushProperty, ring);

        if (stackIndex > 0)
        {
            // The reference keeps the cards behind the front one in the tree but
            // invisible, lifted and shrunk, so the stack reads as depth.
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(
                ToastQueue.ScaleFor(stackIndex), ToastQueue.ScaleFor(stackIndex), CardWidth / 2, 0));
            transform.Children.Add(new TranslateTransform(0, ToastQueue.OffsetFor(stackIndex)));
            card.RenderTransform = transform;
            card.Opacity = 0;
            card.IsHitTestVisible = false;
        }

        Panel.SetZIndex(card, ToastQueue.VisibleLimit - stackIndex);
        return card;
    }
}
