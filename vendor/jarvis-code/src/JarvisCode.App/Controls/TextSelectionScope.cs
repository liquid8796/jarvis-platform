using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace JarvisCode.App.Controls;

/// <summary>
/// Browser-style text selection for the transcript — the piece WPF's TextBlock does
/// not give us. Press on any prose and drag, across paragraphs, messages, code and
/// tool rows alike, to highlight it; then Ctrl+C or right-click → Copy. Double-click
/// selects a word, triple-click a block, Shift+click extends. The scope indexes every
/// text element beneath it (TextBlock, read-only TextBox, RichTextBox, plus elements
/// carrying CopyText such as rendered LaTeX), tracks one cross-element range, paints
/// the highlight on an overlay canvas that scrolls with the content, and reassembles
/// the covered text on copy — inline code chips and math ride their paragraph as
/// embedded elements and are stitched back in. Presses on buttons, links, scrollbars
/// or inside a natively selectable box are left alone, so existing interactions keep
/// working, and a drag past the viewport edge auto-scrolls like a browser would.
/// </summary>
public sealed class TextSelectionScope : Grid
{
    /// <summary>
    /// Attached text for elements that draw their text as geometry (inline code chips,
    /// LaTeX): what a selection covering the element contributes on copy.
    /// </summary>
    public static readonly DependencyProperty CopyTextProperty = DependencyProperty.RegisterAttached(
        "CopyText", typeof(string), typeof(TextSelectionScope), new PropertyMetadata(null));

    public static void SetCopyText(DependencyObject element, string? value) => element.SetValue(CopyTextProperty, value);

    public static string? GetCopyText(DependencyObject element) => (string?)element.GetValue(CopyTextProperty);

    private readonly Canvas _overlay = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly List<Rectangle> _pool = [];
    private readonly DispatcherTimer _edgeScrollTimer;

    private List<Island>? _islands;
    private Dictionary<FrameworkElement, int>? _islandIndex;
    private bool _islandsDirty = true;
    private DateTime _lastBuild = DateTime.MinValue;
    private DateTime _lastRefresh = DateTime.MinValue;

    private FrameworkElement? _anchorElement;
    private FrameworkElement? _focusElement;
    private int _anchorOffset;
    private int _focusOffset;
    private Rect _renderedSpan = Rect.Empty;

    private bool _pendingDrag;
    private bool _dragging;
    private Point _downPoint;
    private ScrollViewer? _scroll;
    private Window? _window;

    public TextSelectionScope()
    {
        Background = Brushes.Transparent;
        SetZIndex(_overlay, 999);
        _edgeScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _edgeScrollTimer.Tick += OnEdgeScrollTick;
        BuildContextMenu();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                ClearSelection();
            }
        };
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Children.Add(_overlay);
    }

    /// <summary>
    /// Told when a selection is live, so a virtualizer above this scope can stop
    /// evicting rows. A drag reaches across rows that scroll out behind it, and a
    /// selection whose anchor was virtualized away is one that cannot be copied.
    /// </summary>
    public Action? HoldRows { get; set; }

    /// <summary>Told when the selection is gone and rows may be released again.</summary>
    public Action? ReleaseRows { get; set; }

    /// <summary>
    /// Told before Select all, which is the one action whose answer is the whole
    /// transcript rather than what is on screen.
    /// </summary>
    public Action? RealizeAllRows { get; set; }

    public bool HasSelection =>
        _anchorElement is not null && _focusElement is not null &&
        !(ReferenceEquals(_anchorElement, _focusElement) && _anchorOffset == _focusOffset);

    public void ClearSelection()
    {
        ReleaseRows?.Invoke();
        _anchorElement = null;
        _focusElement = null;
        _renderedSpan = Rect.Empty;
        foreach (var rect in _pool)
        {
            rect.Visibility = Visibility.Collapsed;
        }
    }

    // ---- lifecycle ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.PreviewKeyDown += OnWindowKeyDown;
        }

        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnWindowKeyDown;
            _window = null;
        }

        LayoutUpdated -= OnLayoutUpdated;
        _edgeScrollTimer.Stop();
        ClearSelection();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        _islandsDirty = true;
        if (!HasSelection || _dragging)
        {
            return;
        }

        // Streaming and re-renders move the selected content around; re-anchor the
        // highlight now and then, and drop it when its elements left the tree.
        var now = DateTime.UtcNow;
        if ((now - _lastRefresh).TotalMilliseconds < 200)
        {
            return;
        }

        _lastRefresh = now;
        try
        {
            if (_anchorElement is null || PresentationSource.FromVisual(_anchorElement) is null ||
                _focusElement is null || PresentationSource.FromVisual(_focusElement) is null)
            {
                ClearSelection();
                return;
            }

            var span = BoundsOf(_anchorElement);
            span.Union(BoundsOf(_focusElement));
            if (span != _renderedSpan)
            {
                RenderSelection();
            }
        }
        catch (InvalidOperationException)
        {
            ClearSelection();
        }
    }

    // ---- mouse ----

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        _pendingDrag = false;
        if (IsChromePress(e.OriginalSource))
        {
            ClearSelection();
            return;
        }

        var p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            SelectWordAt(p);
            return;
        }

        if (e.ClickCount >= 3)
        {
            SelectBlockAt(p);
            return;
        }

        if (HasSelection && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ExtendTo(p);
            e.Handled = true;
            return;
        }

        _downPoint = p;
        _pendingDrag = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            ExtendTo(p);
            EdgeScroll();
            return;
        }

        if (_pendingDrag && e.LeftButton == MouseButtonState.Pressed &&
            (Math.Abs(p.X - _downPoint.X) > 4 || Math.Abs(p.Y - _downPoint.Y) > 4))
        {
            _pendingDrag = false;
            if (HitPosition(_downPoint) is not { } start)
            {
                return;
            }

            HoldRows?.Invoke();
            _anchorElement = start.Island.Element;
            _anchorOffset = start.Offset;
            _focusElement = _anchorElement;
            _focusOffset = start.Offset;
            _dragging = true;
            Cursor = Cursors.IBeam;
            ForceCursor = true;
            CaptureMouse();
            _scroll ??= FindScrollViewer();
            ExtendTo(p);
            return;
        }

        if (!_pendingDrag && e.LeftButton == MouseButtonState.Released)
        {
            UpdateHoverCursor(p);
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_dragging)
        {
            EndDrag();
            if (!HasSelection)
            {
                ClearSelection();
            }

            e.Handled = true;
            return;
        }

        if (_pendingDrag)
        {
            // A plain click on text or empty space collapses the selection, as in a browser.
            _pendingDrag = false;
            ClearSelection();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            EndDrag();
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        ForceCursor = false;
        Cursor = null;
        _edgeScrollTimer.Stop();
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void ExtendTo(Point p)
    {
        if (_anchorElement is null || HitPosition(p) is not { } hit)
        {
            return;
        }

        _focusElement = hit.Island.Element;
        _focusOffset = hit.Offset;
        RenderSelection();
    }

    private void SelectWordAt(Point p)
    {
        if (HitPosition(p) is not { } hit)
        {
            return;
        }

        var (start, end) = hit.Island.WordAt(hit.Offset);
        SetSelection(hit.Island.Element, start, end);
    }

    private void SelectBlockAt(Point p)
    {
        if (HitPosition(p) is not { } hit)
        {
            return;
        }

        SetSelection(hit.Island.Element, 0, hit.Island.End);
    }

    private void SetSelection(FrameworkElement element, int start, int end)
    {
        _anchorElement = element;
        _anchorOffset = start;
        _focusElement = element;
        _focusOffset = end;
        RenderSelection();
    }

    private void SelectAll()
    {
        // Every row, not the screenful: the rows a virtualizer has not built carry
        // text the reader just asked for, and they have to exist to be copied.
        RealizeAllRows?.Invoke();
        // Dropping the list rather than only dirtying it: the rebuild is throttled
        // to ten a second, and the rows that just appeared are the point.
        _islands = null;
        var islands = Islands();
        if (islands.Count == 0)
        {
            return;
        }

        _anchorElement = islands[0].Element;
        _anchorOffset = 0;
        _focusElement = islands[^1].Element;
        _focusOffset = islands[^1].End;
        RenderSelection();
    }

    private void UpdateHoverCursor(Point p)
    {
        foreach (var island in Islands())
        {
            var bounds = island.Clip.IsEmpty ? island.Bounds : island.Clip;
            if (!bounds.Contains(p))
            {
                continue;
            }

            try
            {
                if (island.IsTextAt(TranslatePoint(p, island.Element)))
                {
                    Cursor = Cursors.IBeam;
                    return;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        Cursor = null;
    }

    // ---- edge auto-scroll while dragging ----

    private void EdgeScroll()
    {
        if (_scroll is null)
        {
            return;
        }

        var p = Mouse.GetPosition(_scroll);
        if (p.Y < 0 || p.Y > _scroll.ViewportHeight)
        {
            _edgeScrollTimer.Start();
        }
        else
        {
            _edgeScrollTimer.Stop();
        }
    }

    private void OnEdgeScrollTick(object? sender, EventArgs e)
    {
        if (!_dragging || _scroll is null)
        {
            _edgeScrollTimer.Stop();
            return;
        }

        var p = Mouse.GetPosition(_scroll);
        double overshoot = p.Y < 0 ? p.Y : p.Y > _scroll.ViewportHeight ? p.Y - _scroll.ViewportHeight : 0;
        if (overshoot == 0)
        {
            _edgeScrollTimer.Stop();
            return;
        }

        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + Math.Clamp(overshoot * 0.35, -80, 80));
        ExtendTo(Mouse.GetPosition(this));
    }

    private ScrollViewer? FindScrollViewer()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is ScrollViewer viewer)
            {
                return viewer;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    // ---- copy ----

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || !HasSelection || !IsVisible)
        {
            return;
        }

        // A focused box with its own selection wins (the composer, a code fence), and
        // the terminal keeps Ctrl+C for the shell no matter what.
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (IsWithin<TerminalView>(focused))
        {
            return;
        }

        switch (focused)
        {
            case TextBox { SelectionLength: > 0 }:
            case RichTextBox rich when !rich.Selection.IsEmpty:
            case PasswordBox:
                return;
        }

        CopySelection();
        e.Handled = true;
    }

    /// <summary>
    /// Resolves a whole-message fallback for "Attach as context" when nothing is
    /// selected: given the right-click's original source, the text of the message
    /// under the pointer (or null). The surface wires this.
    /// </summary>
    public Func<object, string?>? ResolveContextSource { get; set; }

    /// <summary>
    /// What a copy target is, which decides the wording the reference uses for
    /// it. The reference reads this off the element rather than guessing from
    /// the text: a block carries data-code-text, a chip data-epitaxy-inline-code
    /// and a file reference data-epitaxy-file-ref.
    /// </summary>
    public enum CopyKind
    {
        None,
        InlineCode,
        CodeBlock,
    }

    public static readonly DependencyProperty CopyKindProperty = DependencyProperty.RegisterAttached(
        "CopyKind", typeof(CopyKind), typeof(TextSelectionScope),
        new FrameworkPropertyMetadata(CopyKind.None));

    public static void SetCopyKind(DependencyObject element, CopyKind value)
        => element.SetValue(CopyKindProperty, value);

    public static CopyKind GetCopyKind(DependencyObject element)
        => (CopyKind)element.GetValue(CopyKindProperty);

    /// <summary>Raised with the covered text when "Attach as context" is chosen.</summary>
    public event EventHandler<string>? AttachAsContextRequested;

    /// <summary>Raised with the covered text when "Send to side chat" is chosen (Code surface).</summary>
    public event EventHandler<string>? SendToSideChatRequested;

    /// <summary>Raised with a web link the user asked to open in the Browser pane.</summary>
    public event EventHandler<Uri>? OpenInBrowserPaneRequested;

    /// <summary>
    /// The message under the pointer: what it says, what it said in markdown,
    /// and the two things the reference offers to do with the turn it belongs
    /// to. Null where nothing was clicked.
    /// </summary>
    /// <summary>
    /// The message the pointer is over, and what the menu may do with it.
    /// <paramref name="PinChapter"/> is the reference's own toggle - offered on
    /// an assistant turn alone, and reading "Unpin chapter" once that turn
    /// already carries one.
    /// </summary>
    public sealed record MessageTarget(
        string PlainText,
        string Markdown,
        Action? Rewind,
        Action? Fork,
        Action? PinChapter = null,
        bool IsPinned = false);

    /// <summary>Resolves the clicked element to the message it belongs to.</summary>
    public Func<object, MessageTarget?>? ResolveMessage { get; set; }

    /// <summary>The image under the pointer, when one was right-clicked.</summary>
    public Func<object, System.Windows.Media.Imaging.BitmapSource?>? ResolveImage { get; set; }

    private string? _contextFallback;

    private void BuildContextMenu()
    {
        // The reference's own transcript menu, in its order: the code entry, the
        // link group, the message pair, attach, then rewind and fork. This app's
        // own additions - Copy (selection), Send to side chat and Select all -
        // follow the runs they belong to.
        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => CopySelection();

        var copyCode = new MenuItem();
        string? codeText = null;
        copyCode.Click += (_, _) =>
        {
            if (codeText is { Length: > 0 })
            {
                MarkdownView.TrySetClipboard(codeText);
            }
        };

        var openInBrowser = new MenuItem { Header = "Open in default browser" };
        var openInPane = new MenuItem { Header = "Open in Browser pane" };
        var copyLink = new MenuItem { Header = "Copy link" };
        Uri? linkTarget = null;
        openInBrowser.Click += (_, _) =>
        {
            if (linkTarget is { IsAbsoluteUri: true } uri)
            {
                OpenInBrowser(uri);
            }
        };
        openInPane.Click += (_, _) =>
        {
            if (linkTarget is { IsAbsoluteUri: true } uri)
            {
                OpenInBrowserPaneRequested?.Invoke(this, uri);
            }
        };
        copyLink.Click += (_, _) =>
        {
            if (linkTarget is not null)
            {
                MarkdownView.TrySetClipboard(linkTarget.OriginalString);
                Services.ToastQueue.Current?.AddSuccess(Services.ToastText.LinkCopied);
            }
        };

        var copyImage = new MenuItem { Header = "Copy image" };
        var saveImage = new MenuItem { Header = "Save image" };
        System.Windows.Media.Imaging.BitmapSource? image = null;
        copyImage.Click += (_, _) => CopyImage(image);
        saveImage.Click += (_, _) => SaveImage(image);

        var messageSeparator = new Separator();
        var copyMessage = new MenuItem { Header = "Copy message" };
        var copyMessageMarkdown = new MenuItem { Header = "Copy message as Markdown" };
        MessageTarget? message = null;
        copyMessage.Click += (_, _) =>
        {
            if (message is { PlainText.Length: > 0 })
            {
                // Both flavours, as the reference copies them: the plain text,
                // and the HTML a rich editor keeps the structure from.
                MarkdownView.TrySetClipboard(message.PlainText, message.Markdown);
            }
        };
        copyMessageMarkdown.Click += (_, _) =>
        {
            if (message is { Markdown.Length: > 0 })
            {
                MarkdownView.TrySetClipboard(message.Markdown);
            }
        };

        var attachSeparator = new Separator();
        var attach = new MenuItem();
        attach.Click += (_, _) =>
        {
            var text = HasSelection ? BuildSelectionText() : _contextFallback;
            if (!string.IsNullOrWhiteSpace(text))
            {
                AttachAsContextRequested?.Invoke(this, text);
            }
        };
        var sideChat = new MenuItem { Header = "Send to side chat" };
        sideChat.Click += (_, _) =>
        {
            var text = HasSelection ? BuildSelectionText() : _contextFallback;
            if (!string.IsNullOrWhiteSpace(text))
            {
                SendToSideChatRequested?.Invoke(this, text);
            }
        };

        // The reference puts its chapter toggle just above the rewind/fork pair.
        var pinChapter = new MenuItem();
        pinChapter.Click += (_, _) => message?.PinChapter?.Invoke();

        var turnSeparator = new Separator();
        var rewind = new MenuItem { Header = "Rewind to here" };
        var fork = new MenuItem { Header = "Fork from here" };
        rewind.Click += (_, _) => message?.Rewind?.Invoke();
        fork.Click += (_, _) => message?.Fork?.Invoke();

        var selectAll = new MenuItem { Header = "Select all" };
        selectAll.Click += (_, _) => SelectAll();

        ContextMenu = new ContextMenu
        {
            Items =
            {
                copy, copyCode, openInBrowser, openInPane, copyLink, copyImage, saveImage,
                messageSeparator, copyMessage, copyMessageMarkdown,
                attachSeparator, attach, sideChat, selectAll, pinChapter,
                turnSeparator, rewind, fork,
            },
        };

        ContextMenuOpening += (_, e) =>
        {
            copy.IsEnabled = HasSelection;
            _contextFallback = HasSelection ? null : ResolveContextSource?.Invoke(e.OriginalSource);
            message = ResolveMessage?.Invoke(e.OriginalSource);
            image = ResolveImage?.Invoke(e.OriginalSource);

            // "Attach selection as context" once something is selected, and
            // "Attach message as context" otherwise - the reference's own pair.
            attach.Header = HasSelection ? "Attach selection as context" : "Attach message as context";
            attach.Visibility = AttachAsContextRequested is null ? Visibility.Collapsed : Visibility.Visible;
            attach.IsEnabled = HasSelection || !string.IsNullOrWhiteSpace(_contextFallback);
            sideChat.Visibility = SendToSideChatRequested is null ? Visibility.Collapsed : Visibility.Visible;
            sideChat.IsEnabled = HasSelection || !string.IsNullOrWhiteSpace(_contextFallback);

            linkTarget = (Mouse.DirectlyOver as Hyperlink)?.NavigateUri;
            bool isWebLink = linkTarget is { IsAbsoluteUri: true } uri && uri.Scheme is "http" or "https";
            openInBrowser.Visibility = Vis(isWebLink);
            openInPane.Visibility = Vis(isWebLink && OpenInBrowserPaneRequested is not null);
            copyLink.Visibility = Vis(isWebLink);

            var hasImage = image is not null;
            copyImage.Visibility = Vis(hasImage);
            saveImage.Visibility = Vis(hasImage);

            var hasMessage = message is not null;
            copyMessage.Visibility = Vis(hasMessage);
            copyMessageMarkdown.Visibility = Vis(hasMessage);
            pinChapter.Visibility = Vis(message?.PinChapter is not null);
            pinChapter.Header = message?.IsPinned == true ? "Unpin chapter" : "Pin as chapter";
            rewind.Visibility = Vis(message?.Rewind is not null);
            fork.Visibility = Vis(message?.Fork is not null);

            var (kind, text) = ResolveCopyTarget(e.OriginalSource as DependencyObject);
            codeText = text;
            if (kind == CopyKind.None && linkTarget is { IsAbsoluteUri: false })
            {
                // A relative link is a file reference, which the reference
                // offers as "Copy path" rather than as a link.
                codeText = linkTarget.OriginalString;
                copyCode.Header = "Copy path";
                copyCode.Visibility = Visibility.Visible;
            }
            else
            {
                copyCode.Header = kind == CopyKind.CodeBlock ? "Copy code block" : "Copy code";
                copyCode.Visibility = Vis(kind != CopyKind.None);
            }

            messageSeparator.Visibility = Vis(
                hasMessage && (copyCode.Visibility == Visibility.Visible || isWebLink || hasImage));
            attachSeparator.Visibility = Vis(hasMessage);
            turnSeparator.Visibility = Vis(message?.Rewind is not null || message?.Fork is not null);
        };

        static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void CopyImage(System.Windows.Media.Imaging.BitmapSource? image)
    {
        if (image is null)
        {
            return;
        }

        try
        {
            Clipboard.SetImage(image);
            Services.ToastQueue.Current?.AddSuccess(Services.ToastText.ImageCopied);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; nothing was copied.
        }
    }

    private static void SaveImage(System.Windows.Media.Imaging.BitmapSource? image)
    {
        if (image is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"image-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.png",
            Filter = "PNG image|*.png",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var stream = System.IO.File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            Services.ToastQueue.Current?.AddError(SaveImageFailed);
        }
    }

    /// <summary>The reference's own sentence when a save does not go through.</summary>
    private const string SaveImageFailed = "Couldn’t save image.";

    /// <summary>
    /// The nearest marked copy target above the clicked element. A block wins
    /// over a chip, which is the precedence the reference's own lookup uses.
    /// </summary>
    private static (CopyKind Kind, string? Text) ResolveCopyTarget(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (GetCopyKind(node) is var kind && kind != CopyKind.None)
            {
                return (kind, GetCopyText(node));
            }
        }

        return (CopyKind.None, null);
    }

    private static void OpenInBrowser(Uri uri)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser association; nothing sensible to do.
        }
    }

    private void CopySelection()
    {
        if (BuildSelectionText() is { Length: > 0 } text)
        {
            MarkdownView.TrySetClipboard(text);
        }
    }

    /// <summary>The selection reassembled in reading order, or null without one.</summary>
    public string? BuildSelectionText()
    {
        if (Normalized() is not { } range)
        {
            return null;
        }

        var (islands, first, firstOffset, last, lastOffset) = range;
        var buffer = new StringBuilder();
        Island? previous = null;
        for (int i = first; i <= last; i++)
        {
            var island = islands[i];
            int start = i == first ? firstOffset : 0;
            int end = i == last ? lastOffset : island.End;
            if (end <= start)
            {
                continue;
            }

            string text;
            try
            {
                text = island.TextFor(start, end);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            text = text.TrimEnd('\n');
            if (text.Length == 0)
            {
                continue;
            }

            if (previous is not null)
            {
                // Islands sharing a visual row read as one line: a list marker and
                // its item, table cells. Stacked islands break as blocks do.
                bool sameRow = island.Bounds.Top < previous.Bounds.Bottom - 4 &&
                               island.Bounds.Bottom > previous.Bounds.Top + 4;
                buffer.Append(sameRow ? ' ' : '\n');
            }

            buffer.Append(text);
            previous = island;
        }

        return buffer.Length > 0 ? buffer.Replace("\r\n", "\n").ToString() : null;
    }

    // ---- selection rendering ----

    private (List<Island> Islands, int First, int FirstOffset, int Last, int LastOffset)? Normalized()
    {
        var islands = Islands();
        if (_islandIndex is null || _anchorElement is null || _focusElement is null ||
            !_islandIndex.TryGetValue(_anchorElement, out int anchor) ||
            !_islandIndex.TryGetValue(_focusElement, out int focus))
        {
            return null;
        }

        int anchorOffset = _anchorOffset;
        int focusOffset = _focusOffset;
        if (anchor > focus || (anchor == focus && anchorOffset > focusOffset))
        {
            (anchor, focus) = (focus, anchor);
            (anchorOffset, focusOffset) = (focusOffset, anchorOffset);
        }

        return (islands, anchor, anchorOffset, focus, focusOffset);
    }

    private void RenderSelection()
    {
        if (Normalized() is not { } range)
        {
            ClearSelection();
            return;
        }

        var (islands, first, firstOffset, last, lastOffset) = range;
        int used = 0;
        try
        {
            for (int i = first; i <= last; i++)
            {
                var island = islands[i];
                int start = i == first ? firstOffset : 0;
                int end = i == last ? lastOffset : island.End;
                if (end <= start)
                {
                    continue;
                }

                // Whole-transcript selections would otherwise mean thousands of
                // per-line rectangles; past a sane budget, block rectangles do.
                List<Rect> rects = used > 1500
                    ? [new Rect(0, 0, island.Element.ActualWidth, island.Element.ActualHeight)]
                    : island.RectsFor(start, end);
                var transform = island.Element.TransformToAncestor(this);
                foreach (var r in rects)
                {
                    var mapped = transform.TransformBounds(r);
                    mapped.Intersect(island.Clip.IsEmpty ? island.Bounds : island.Clip);
                    if (mapped.IsEmpty || mapped.Width <= 0 || mapped.Height <= 0)
                    {
                        continue;
                    }

                    var rect = RectAt(used++);
                    Canvas.SetLeft(rect, mapped.X);
                    Canvas.SetTop(rect, mapped.Y);
                    rect.Width = mapped.Width;
                    rect.Height = mapped.Height;
                    rect.Visibility = Visibility.Visible;
                }
            }

            var span = BoundsOf(_anchorElement!);
            span.Union(BoundsOf(_focusElement!));
            _renderedSpan = span;
        }
        catch (InvalidOperationException)
        {
            ClearSelection();
            return;
        }

        for (int i = used; i < _pool.Count; i++)
        {
            _pool[i].Visibility = Visibility.Collapsed;
        }
    }

    private Rectangle RectAt(int index)
    {
        while (_pool.Count <= index)
        {
            var rect = new Rectangle { IsHitTestVisible = false };
            rect.SetResourceReference(Shape.FillProperty, "SelectionBrush");
            _overlay.Children.Add(rect);
            _pool.Add(rect);
        }

        return _pool[index];
    }

    // ---- hit testing ----

    private (Island Island, int Offset)? HitPosition(Point p)
    {
        var islands = Islands();
        if (islands.Count == 0)
        {
            return null;
        }

        int best = -1;
        double bestScore = double.MaxValue;
        for (int i = 0; i < islands.Count; i++)
        {
            var b = islands[i].Clip.IsEmpty ? islands[i].Bounds : islands[i].Clip;
            double dy = p.Y < b.Top ? b.Top - p.Y : p.Y > b.Bottom ? p.Y - b.Bottom : 0;
            double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;

            // Vertical distance dominates: the transcript reads top to bottom, so a
            // point in the gap between blocks belongs to the nearest line, not to
            // whichever block happens to sit closer sideways.
            double score = dy * 10000 + dx;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
                if (score == 0)
                {
                    break;
                }
            }
        }

        var island = islands[best];
        try
        {
            return (island, island.OffsetAtPoint(TranslatePoint(p, island.Element)));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private bool IsChromePress(object source)
    {
        var node = source as DependencyObject;
        while (node is not null && !ReferenceEquals(node, this))
        {
            if (node is ButtonBase or TextBoxBase or PasswordBox or ScrollBar or Thumb
                or Hyperlink or TurnStatusLine)
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private static bool IsWithin<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T)
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    // ---- island collection ----

    private List<Island> Islands()
    {
        if (_islands is not null && !_islandsDirty)
        {
            return _islands;
        }

        // Streaming dirties the tree constantly; rebuilding an in-use list a few
        // times a second is plenty, and stale bounds self-correct on the next pass.
        var now = DateTime.UtcNow;
        if (_islands is not null && (now - _lastBuild).TotalMilliseconds < 100)
        {
            return _islands;
        }

        _islandsDirty = false;
        _lastBuild = now;
        var list = new List<Island>();
        try
        {
            Collect(this, list);
        }
        catch (InvalidOperationException)
        {
            list.Clear();
        }

        _islands = list;
        _islandIndex = new Dictionary<FrameworkElement, int>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            _islandIndex[list[i].Element] = i;
        }

        return _islands;
    }

    private void Collect(DependencyObject node, List<Island> list)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (ReferenceEquals(child, _overlay))
            {
                continue;
            }

            switch (child)
            {
                // Chrome: labels on buttons, scrollbars and the live status line are
                // not transcript content.
                case ButtonBase or ScrollBar or Slider or TurnStatusLine:
                    continue;
                case UIElement { IsVisible: false }:
                    continue;

                // No descent into a TextBlock: inline code chips and math ride the
                // paragraph as embedded elements and are handled by its island — a
                // chip-aware one where there are chips, so their glyphs select like
                // the plain text they are in the reference.
                case TextBlock text:
                    Add(list, text, static el => CarriesChip((TextBlock)el)
                        ? new ChipTextIsland((TextBlock)el)
                        : new TextBlockIsland((TextBlock)el));
                    continue;
                case RichTextBox rich:
                    Add(list, rich, static el => new RichBoxIsland((RichTextBox)el));
                    continue;
                case TextBox { IsReadOnly: true } box:
                    Add(list, box, static el => new BoxIsland((TextBox)el));
                    continue;

                // Editable boxes keep their native selection end to end.
                case TextBoxBase or PasswordBox:
                    continue;
                case FrameworkElement opaque when GetCopyText(opaque) is { } copyText:
                    Add(list, opaque, el => new OpaqueIsland(el, copyText));
                    continue;
            }

            Collect(child, list);
        }
    }

    private void Add(List<Island> list, FrameworkElement element, Func<FrameworkElement, Island> make)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        var island = make(element);
        island.Bounds = BoundsOf(element);
        island.Clip = VisibleBoundsOf(element, island.Bounds);
        if (!island.Clip.IsEmpty)
        {
            list.Add(island);
        }
    }

    private Rect BoundsOf(FrameworkElement element) =>
        element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));

    /// <summary>Bounds cut down to every scrolled viewport between the element and the scope.</summary>
    private Rect VisibleBoundsOf(FrameworkElement element, Rect bounds)
    {
        var clip = bounds;
        var node = VisualTreeHelper.GetParent(element);
        while (node is not null && !ReferenceEquals(node, this))
        {
            if (node is ScrollContentPresenter presenter)
            {
                clip.Intersect(BoundsOf(presenter));
                if (clip.IsEmpty)
                {
                    return clip;
                }
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return clip;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void AddLineRect(List<Rect> rects, TextPointer from, TextPointer to, bool padEol)
    {
        var r1 = from.GetCharacterRect(LogicalDirection.Forward);
        var r2 = to.GetCharacterRect(LogicalDirection.Backward);
        if (r1.IsEmpty || r2.IsEmpty)
        {
            return;
        }

        double top = Math.Min(r1.Top, r2.Top);
        double bottom = Math.Max(r1.Bottom, r2.Bottom);

        // An empty line still shows a small stub, the way browsers render it.
        double width = Math.Max(r2.X - r1.X, padEol ? 4 : 0);
        if (width > 0 && bottom > top)
        {
            rects.Add(new Rect(r1.X, top, width, bottom - top));
        }
    }

    // ---- embedded elements ----

    /// <summary>Inline code chips and math draw text as geometry; stitch their source back in.</summary>
    private static string EmbeddedText(object? element)
    {
        var node = element as DependencyObject;
        if (node is InlineUIContainer container)
        {
            node = container.Child;
        }

        if (node is null)
        {
            return "";
        }

        if (GetCopyText(node) is { } copyText)
        {
            return copyText;
        }

        var text = new StringBuilder();
        AppendVisualText(node, text);
        return text.ToString();
    }

    private static void AppendVisualText(DependencyObject node, StringBuilder text)
    {
        if (node is TextBlock block)
        {
            text.Append(block.Text);
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            AppendVisualText(VisualTreeHelper.GetChild(node, i), text);
        }
    }

    /// <summary>
    /// The TextBlock inside an inline code chip, or null for anything else the
    /// paragraph embeds. A chip is text in the reference — an ordinary
    /// <c>&lt;code&gt;</c> — so its glyphs have to be reachable character by
    /// character, while math and a kbd key stay the atomic elements they already are.
    /// </summary>
    private static TextBlock? ChipTextOf(object? element)
    {
        var node = element as DependencyObject;
        if (node is InlineUIContainer container)
        {
            node = container.Child;
        }

        return node is not null && GetCopyKind(node) == CopyKind.InlineCode ? FirstTextBlock(node) : null;
    }

    /// <summary>
    /// The chip's own text element, found through the logical tree so it resolves
    /// before the chip has ever been rendered. A colour chip wraps its swatch and
    /// its text in a row, which is why this is a walk rather than a cast.
    /// </summary>
    private static TextBlock? FirstTextBlock(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock block:
                return block;
            case Border { Child: DependencyObject child }:
                return FirstTextBlock(child);
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    if (child is DependencyObject element && FirstTextBlock(element) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Whether a paragraph carries any chip, which decides which island it gets.</summary>
    private static bool CarriesChip(TextBlock text)
    {
        foreach (var inline in text.Inlines)
        {
            if (CarriesChip(inline))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CarriesChip(Inline inline) => inline switch
    {
        InlineUIContainer container => ChipTextOf(container.Child) is not null,
        Span span => span.Inlines.Any(CarriesChip),
        _ => false,
    };

    // ---- text islands ----

    private abstract class Island(FrameworkElement element)
    {
        public FrameworkElement Element { get; } = element;

        /// <summary>Bounds in scope coordinates.</summary>
        public Rect Bounds;

        /// <summary>Bounds cut to every ancestor viewport; hit tests and highlights honor it.</summary>
        public Rect Clip;

        /// <summary>The exclusive end offset; offsets are opaque but ordered, 0 is the start.</summary>
        public abstract int End { get; }

        /// <summary>Nearest offset for a point in island coordinates.</summary>
        public abstract int OffsetAtPoint(Point p);

        /// <summary>Whether an actual glyph sits under the point (for the I-beam cursor).</summary>
        public abstract bool IsTextAt(Point p);

        /// <summary>Highlight rectangles for a range, in island coordinates.</summary>
        public abstract List<Rect> RectsFor(int start, int end);

        public abstract string TextFor(int start, int end);

        public abstract (int Start, int End) WordAt(int offset);
    }

    /// <summary>TextPointer-based island: TextBlocks and RichTextBox documents.</summary>
    private abstract class PointerIsland(FrameworkElement element, TextPointer contentStart, TextPointer contentEnd)
        : Island(element)
    {
        protected abstract TextPointer? FromPoint(Point p, bool snap);

        public override int End => contentStart.GetOffsetToPosition(contentEnd);

        private TextPointer At(int offset) => contentStart.GetPositionAtOffset(offset) ?? contentEnd;

        public override int OffsetAtPoint(Point p)
        {
            var clamped = new Point(
                Math.Clamp(p.X, 1, Math.Max(1, Element.ActualWidth - 1)),
                Math.Clamp(p.Y, 1, Math.Max(1, Element.ActualHeight - 1)));
            var position = FromPoint(clamped, snap: true) ?? (p.Y <= 0 ? contentStart : contentEnd);
            return Math.Clamp(contentStart.GetOffsetToPosition(position), 0, End);
        }

        public override bool IsTextAt(Point p) => FromPoint(p, snap: false) is not null;

        public override List<Rect> RectsFor(int start, int end)
        {
            var rects = new List<Rect>();
            var position = At(start).GetInsertionPosition(LogicalDirection.Forward);

            // Normalised to an insertion position: a FlowDocument's ContentEnd sits
            // outside the last paragraph and yields a degenerate character rect.
            var endPosition = At(end).GetInsertionPosition(LogicalDirection.Backward) ?? At(end);
            if (position is null || position.CompareTo(endPosition) >= 0)
            {
                return rects;
            }

            try
            {
                for (int guard = 0; guard < 2048; guard++)
                {
                    var nextLine = position.GetLineStartPosition(1);
                    bool lastLine = nextLine is null || nextLine.CompareTo(endPosition) >= 0;
                    AddLineRect(rects, position, lastLine ? endPosition : nextLine!, padEol: !lastLine);
                    if (lastLine)
                    {
                        break;
                    }

                    position = nextLine!;
                }
            }
            catch (InvalidOperationException)
            {
                // No valid layout to walk: the whole box approximates the range.
                rects.Clear();
                rects.Add(new Rect(0, 0, Element.ActualWidth, Element.ActualHeight));
            }

            return rects;
        }

        public override string TextFor(int start, int end)
        {
            var text = new StringBuilder();
            TextPointer? position = At(start);
            var endPosition = At(end);
            while (position is not null && position.CompareTo(endPosition) < 0)
            {
                switch (position.GetPointerContext(LogicalDirection.Forward))
                {
                    case TextPointerContext.Text:
                        var run = position.GetTextInRun(LogicalDirection.Forward);
                        int keep = Math.Min(run.Length, position.GetOffsetToPosition(endPosition));
                        text.Append(run, 0, Math.Max(0, keep));
                        position = position.GetPositionAtOffset(run.Length);
                        break;
                    case TextPointerContext.EmbeddedElement:
                        text.Append(EmbeddedText(position.GetAdjacentElement(LogicalDirection.Forward)));
                        position = position.GetNextContextPosition(LogicalDirection.Forward);
                        break;
                    case TextPointerContext.ElementStart
                        when position.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    case TextPointerContext.ElementEnd when position.Parent is Paragraph:
                        text.Append('\n');
                        position = position.GetNextContextPosition(LogicalDirection.Forward);
                        break;
                    default:
                        position = position.GetNextContextPosition(LogicalDirection.Forward);
                        break;
                }
            }

            return text.ToString();
        }

        public override (int Start, int End) WordAt(int offset)
        {
            var position = At(offset);
            string back = position.GetTextInRun(LogicalDirection.Backward);
            string forward = position.GetTextInRun(LogicalDirection.Forward);
            int b = 0;
            while (b < back.Length && IsWordChar(back[^(b + 1)]))
            {
                b++;
            }

            int f = 0;
            while (f < forward.Length && IsWordChar(forward[f]))
            {
                f++;
            }

            if (b + f == 0 && forward.Length > 0)
            {
                f = 1;
            }

            return (offset - b, offset + f);
        }
    }

    private sealed class TextBlockIsland(TextBlock text)
        : PointerIsland(text, text.ContentStart, text.ContentEnd)
    {
        protected override TextPointer? FromPoint(Point p, bool snap) => text.GetPositionFromPoint(p, snap);
    }

    /// <summary>
    /// A paragraph carrying inline code chips. WPF counts an <c>InlineUIContainer</c>
    /// as one symbol, so a chip used to be an atomic blob: a drag inside it selected
    /// the whole thing and a double-click on it selected nothing at all. The reference
    /// renders a chip as ordinary <c>&lt;code&gt;</c> text, where both work per
    /// character, so this island lays a second offset space over the paragraph — its
    /// runs at their own length, each chip expanded to the length of its own text —
    /// and maps points, highlights, words and copied text through it.
    /// </summary>
    private sealed class ChipTextIsland : Island
    {
        /// <summary>
        /// One stretch of the paragraph in island offsets. A run carries its text; a
        /// chip carries the element that holds it; anything else the paragraph embeds
        /// stays one offset wide with its source text, exactly as before.
        /// </summary>
        private sealed class Chunk
        {
            public int Start;
            public int Length;
            public int PointerStart;
            public int PointerLength;
            public TextPointer From = null!;
            public TextBlock? Inner;
            public UIElement? Element;
            public string? Fixed;
        }

        private const int WalkGuard = 20000;

        private readonly TextBlock _text;
        private readonly List<Chunk> _chunks;
        private readonly int _end;

        public ChipTextIsland(TextBlock text)
            : base(text)
        {
            _text = text;
            _chunks = BuildChunks(text);
            _end = _chunks.Count == 0 ? 0 : _chunks[^1].Start + _chunks[^1].Length;
        }

        public override int End => _end;

        private static List<Chunk> BuildChunks(TextBlock text)
        {
            var chunks = new List<Chunk>();
            var position = text.ContentStart;
            var end = text.ContentEnd;
            int offset = 0;
            int pointer = 0;
            int guard = 0;
            while (position is not null && position.CompareTo(end) < 0 && guard++ < WalkGuard)
            {
                var at = position;
                switch (position.GetPointerContext(LogicalDirection.Forward))
                {
                    case TextPointerContext.Text:
                    {
                        var run = position.GetTextInRun(LogicalDirection.Forward);
                        if (run.Length == 0)
                        {
                            position = position.GetNextContextPosition(LogicalDirection.Forward);
                            pointer = text.ContentStart.GetOffsetToPosition(position ?? end);
                            break;
                        }

                        chunks.Add(new Chunk
                        {
                            Start = offset,
                            Length = run.Length,
                            PointerStart = pointer,
                            PointerLength = run.Length,
                            From = at,
                        });
                        offset += run.Length;
                        pointer += run.Length;
                        position = position.GetPositionAtOffset(run.Length);
                        break;
                    }

                    case TextPointerContext.EmbeddedElement:
                    {
                        var element = position.GetAdjacentElement(LogicalDirection.Forward);
                        var inner = ChipTextOf(element);
                        var chunk = new Chunk
                        {
                            Start = offset,
                            PointerStart = pointer,
                            PointerLength = 1,
                            From = at,
                            Element = element as UIElement,
                        };
                        if (inner is { Text.Length: > 0 })
                        {
                            chunk.Inner = inner;
                            chunk.Length = inner.Text.Length;
                        }
                        else
                        {
                            // Math, a kbd key, an image placeholder: one offset wide,
                            // carrying its whole source, which is what it was before.
                            chunk.Length = 1;
                            chunk.Fixed = EmbeddedText(element);
                        }

                        chunks.Add(chunk);
                        offset += chunk.Length;
                        pointer += 1;
                        position = position.GetNextContextPosition(LogicalDirection.Forward);
                        break;
                    }

                    case TextPointerContext.ElementStart
                        when position.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    {
                        chunks.Add(new Chunk
                        {
                            Start = offset,
                            Length = 1,
                            PointerStart = pointer,
                            PointerLength = 1,
                            From = at,
                            Fixed = "\n",
                        });
                        offset += 1;
                        position = position.GetNextContextPosition(LogicalDirection.Forward);
                        pointer = text.ContentStart.GetOffsetToPosition(position ?? end);
                        break;
                    }

                    default:
                        position = position.GetNextContextPosition(LogicalDirection.Forward);
                        pointer = text.ContentStart.GetOffsetToPosition(position ?? end);
                        break;
                }
            }

            return chunks;
        }

        private Chunk? ChunkAt(int offset)
        {
            for (int i = 0; i < _chunks.Count; i++)
            {
                var chunk = _chunks[i];
                if (offset < chunk.Start + chunk.Length)
                {
                    return chunk;
                }
            }

            return _chunks.Count > 0 ? _chunks[^1] : null;
        }

        /// <summary>The chip whose own text the point falls in, in that chip's coordinates.</summary>
        private (Chunk Chunk, Point Local)? ChipAt(Point p)
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.Inner is not { } inner)
                {
                    continue;
                }

                try
                {
                    var local = _text.TransformToDescendant(inner).Transform(p);
                    if (local.X >= 0 && local.Y >= 0 &&
                        local.X <= inner.ActualWidth && local.Y <= inner.ActualHeight)
                    {
                        return (chunk, local);
                    }
                }
                catch (InvalidOperationException)
                {
                    // No shared layout yet; the paragraph's own mapping answers instead.
                }
            }

            return null;
        }

        public override int OffsetAtPoint(Point p)
        {
            if (ChipAt(p) is { } hit)
            {
                var inner = hit.Chunk.Inner!;
                var position = inner.GetPositionFromPoint(hit.Local, snapToText: true);
                int index = position is null ? 0 : inner.ContentStart.GetOffsetToPosition(position);
                return hit.Chunk.Start + Math.Clamp(index, 0, hit.Chunk.Length);
            }

            var clamped = new Point(
                Math.Clamp(p.X, 1, Math.Max(1, _text.ActualWidth - 1)),
                Math.Clamp(p.Y, 1, Math.Max(1, _text.ActualHeight - 1)));
            var parent = _text.GetPositionFromPoint(clamped, snapToText: true);
            if (parent is null)
            {
                return p.Y <= 0 ? 0 : End;
            }

            int pointer = _text.ContentStart.GetOffsetToPosition(parent);
            foreach (var chunk in _chunks)
            {
                if (pointer < chunk.PointerStart)
                {
                    return chunk.Start;
                }

                if (pointer < chunk.PointerStart + chunk.PointerLength)
                {
                    if (chunk.PointerLength == chunk.Length)
                    {
                        return chunk.Start + (pointer - chunk.PointerStart);
                    }

                    // A chip landed on as one symbol: the nearer of its two edges.
                    return p.X > ChipBounds(chunk).X + ChipBounds(chunk).Width / 2
                        ? chunk.Start + chunk.Length
                        : chunk.Start;
                }
            }

            return End;
        }

        private Rect ChipBounds(Chunk chunk)
        {
            if (chunk.Element is not { } element)
            {
                return Rect.Empty;
            }

            try
            {
                return element.TransformToAncestor(_text).TransformBounds(new Rect(element.RenderSize));
            }
            catch (InvalidOperationException)
            {
                return Rect.Empty;
            }
        }

        public override bool IsTextAt(Point p) =>
            ChipAt(p) is not null || _text.GetPositionFromPoint(p, snapToText: false) is not null;

        public override List<Rect> RectsFor(int start, int end)
        {
            var rects = new List<Rect>();
            foreach (var chunk in _chunks)
            {
                int from = Math.Max(start, chunk.Start);
                int to = Math.Min(end, chunk.Start + chunk.Length);
                if (to <= from)
                {
                    continue;
                }

                if (chunk.Inner is { } inner)
                {
                    // A chip covered end to end highlights as the reference's inline
                    // box does, padding included; a partial one from its own glyphs.
                    if (from == chunk.Start && to == chunk.Start + chunk.Length &&
                        ChipBounds(chunk) is { IsEmpty: false } whole)
                    {
                        rects.Add(whole);
                        continue;
                    }

                    AddInnerRect(rects, inner, from - chunk.Start, to - chunk.Start);
                    continue;
                }

                if (chunk.Fixed is not null)
                {
                    if (ChipBounds(chunk) is { IsEmpty: false } box)
                    {
                        rects.Add(box);
                        continue;
                    }

                    AddSpanRects(rects, chunk.From, chunk.From.GetPositionAtOffset(chunk.PointerLength));
                    continue;
                }

                AddSpanRects(
                    rects,
                    chunk.From.GetPositionAtOffset(from - chunk.Start),
                    chunk.From.GetPositionAtOffset(to - chunk.Start));
            }

            return rects;
        }

        /// <summary>A chip never wraps, so a partial cover is one rectangle.</summary>
        private void AddInnerRect(List<Rect> rects, TextBlock inner, int from, int to)
        {
            try
            {
                var a = inner.ContentStart.GetPositionAtOffset(from);
                var b = inner.ContentStart.GetPositionAtOffset(to);
                if (a is null || b is null)
                {
                    return;
                }

                var r1 = a.GetCharacterRect(LogicalDirection.Forward);
                var r2 = b.GetCharacterRect(LogicalDirection.Backward);
                if (r1.IsEmpty || r2.IsEmpty)
                {
                    return;
                }

                var box = new Rect(
                    r1.X,
                    Math.Min(r1.Top, r2.Top),
                    Math.Max(r2.X - r1.X, 1),
                    Math.Max(r1.Height, r2.Bottom - Math.Min(r1.Top, r2.Top)));
                rects.Add(inner.TransformToAncestor(_text).TransformBounds(box));
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>Line-by-line rectangles for a stretch of the paragraph's own text.</summary>
        private void AddSpanRects(List<Rect> rects, TextPointer? from, TextPointer? to)
        {
            if (from is null || to is null || from.CompareTo(to) >= 0)
            {
                return;
            }

            try
            {
                var position = from;
                for (int guard = 0; guard < 2048; guard++)
                {
                    var nextLine = position.GetLineStartPosition(1);
                    bool lastLine = nextLine is null || nextLine.CompareTo(to) >= 0;
                    AddLineRect(rects, position, lastLine ? to : nextLine!, padEol: !lastLine);
                    if (lastLine)
                    {
                        return;
                    }

                    position = nextLine!;
                }
            }
            catch (InvalidOperationException)
            {
                rects.Add(new Rect(0, 0, _text.ActualWidth, _text.ActualHeight));
            }
        }

        public override string TextFor(int start, int end)
        {
            var text = new StringBuilder();
            foreach (var chunk in _chunks)
            {
                int from = Math.Max(start, chunk.Start);
                int to = Math.Min(end, chunk.Start + chunk.Length);
                if (to <= from)
                {
                    continue;
                }

                if (chunk.Fixed is { } fixedText)
                {
                    text.Append(fixedText);
                    continue;
                }

                var source = chunk.Inner is { } inner
                    ? inner.Text
                    : chunk.From.GetTextInRun(LogicalDirection.Forward);
                int a = Math.Clamp(from - chunk.Start, 0, source.Length);
                int b = Math.Clamp(to - chunk.Start, a, source.Length);
                text.Append(source, a, b - a);
            }

            return text.ToString();
        }

        public override (int Start, int End) WordAt(int offset)
        {
            if (ChunkAt(offset) is not { } chunk)
            {
                return (offset, offset);
            }

            if (chunk.Fixed is not null)
            {
                // Math and the like select whole, which is all there is of them.
                return (chunk.Start, chunk.Start + chunk.Length);
            }

            var source = chunk.Inner is { } inner
                ? inner.Text
                : chunk.From.GetTextInRun(LogicalDirection.Forward);
            int local = Math.Clamp(offset - chunk.Start, 0, source.Length);
            int start = local;
            while (start > 0 && IsWordChar(source[start - 1]))
            {
                start--;
            }

            int endIndex = local;
            while (endIndex < source.Length && IsWordChar(source[endIndex]))
            {
                endIndex++;
            }

            if (start == endIndex && endIndex < source.Length)
            {
                endIndex++;
            }

            return (chunk.Start + start, chunk.Start + endIndex);
        }
    }

    private sealed class RichBoxIsland(RichTextBox box)
        : PointerIsland(box, box.Document.ContentStart, box.Document.ContentEnd)
    {
        protected override TextPointer? FromPoint(Point p, bool snap) => box.GetPositionFromPoint(p, snap);
    }

    /// <summary>Read-only plain TextBox (tool results): character-index based.</summary>
    private sealed class BoxIsland(TextBox box) : Island(box)
    {
        public override int End => box.Text.Length;

        public override int OffsetAtPoint(Point p)
        {
            int index = box.GetCharacterIndexFromPoint(p, true /* snap to nearest */);
            if (index < 0)
            {
                return p.Y <= 0 ? 0 : End;
            }

            var rect = box.GetRectFromCharacterIndex(index);
            if (!rect.IsEmpty && p.X > rect.X + rect.Width / 2)
            {
                index++;
            }

            return Math.Clamp(index, 0, End);
        }

        public override bool IsTextAt(Point p) => box.GetCharacterIndexFromPoint(p, false) >= 0;

        public override List<Rect> RectsFor(int start, int end)
        {
            var rects = new List<Rect>();
            if (end <= start || box.Text.Length == 0)
            {
                return rects;
            }

            int firstLine = box.GetLineIndexFromCharacterIndex(start);
            int lastLine = box.GetLineIndexFromCharacterIndex(Math.Max(start, end - 1));
            if (firstLine < 0 || lastLine < 0)
            {
                rects.Add(new Rect(0, 0, box.ActualWidth, box.ActualHeight));
                return rects;
            }

            for (int line = firstLine; line <= lastLine && line - firstLine < 2048; line++)
            {
                int lineStart = box.GetCharacterIndexFromLineIndex(line);
                int lineLength = box.GetLineLength(line);
                int from = Math.Max(start, lineStart);
                int to = Math.Min(end, lineStart + lineLength);
                if (to <= from)
                {
                    continue;
                }

                var r1 = box.GetRectFromCharacterIndex(from);
                var r2 = box.GetRectFromCharacterIndex(to - 1, trailingEdge: true);
                if (r1.IsEmpty || r2.IsEmpty)
                {
                    continue;
                }

                double width = Math.Max(r2.X - r1.X, line < lastLine ? 4 : 1);
                rects.Add(new Rect(r1.X, r1.Top, width, Math.Max(r1.Height, r2.Bottom - r1.Top)));
            }

            return rects;
        }

        public override string TextFor(int start, int end)
        {
            start = Math.Clamp(start, 0, End);
            end = Math.Clamp(end, start, End);
            return box.Text[start..end];
        }

        public override (int Start, int End) WordAt(int offset)
        {
            var text = box.Text;
            offset = Math.Clamp(offset, 0, text.Length);
            int start = offset;
            while (start > 0 && IsWordChar(text[start - 1]))
            {
                start--;
            }

            int end = offset;
            while (end < text.Length && IsWordChar(text[end]))
            {
                end++;
            }

            if (start == end && end < text.Length)
            {
                end++;
            }

            return (start, end);
        }
    }

    /// <summary>An element whose text is geometry (display math): all or nothing.</summary>
    private sealed class OpaqueIsland(FrameworkElement element, string text) : Island(element)
    {
        public override int End => 1;

        public override int OffsetAtPoint(Point p) => p.X > Element.ActualWidth / 2 || p.Y > Element.ActualHeight ? 1 : 0;

        public override bool IsTextAt(Point p) => true;

        public override List<Rect> RectsFor(int start, int end) =>
            end > start ? [new Rect(0, 0, Element.ActualWidth, Element.ActualHeight)] : [];

        public override string TextFor(int start, int end) => end > start ? text : "";

        public override (int Start, int End) WordAt(int offset) => (0, 1);
    }
}
