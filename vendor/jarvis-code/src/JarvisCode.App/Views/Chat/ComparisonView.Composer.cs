using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Views.Chat;

public sealed partial class ComparisonView
{
    private readonly List<ComposerAttachment> _attachments = [];
    private readonly WrapPanel _attachmentStrip = new() { Margin = new Thickness(8, 4, 8, 0) };
    private readonly TextBlock _composerError = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 8, 0) };
    private VoiceInput? _voice;
    private Button? _dictateButton;
    private StackPanel? _composerActions;

    private void InitializeComposerInputs()
    {
        DataObject.AddPastingHandler(_input, OnComposerPaste);
        AllowDrop = true;
        Drop += (_, args) =>
        {
            if (args.Data.GetData(DataFormats.FileDrop) is string[] files)
            { AttachFiles(files); args.Handled = true; }
        };
        Unloaded += (_, _) => { _voice?.Dispose(); _voice = null; };
    }

    private FrameworkElement BuildRichComposer(FrameworkElement textInput)
    {
        var panel = new StackPanel();
        panel.Children.Add(_attachmentStrip);
        panel.Children.Add(textInput);
        _composerError.SetResourceReference(TextBlock.ForegroundProperty, "Danger100Brush");
        panel.Children.Add(_composerError);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 4) };
        _composerActions = actions;
        var add = new Button { Content = "+", ToolTip = "Add files", Width = 28, Height = 28 };
        add.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(add, "Add files");
        add.Click += (_, _) => OpenComposerMenu(add);
        actions.Children.Add(add);
        _dictateButton = new Button { Content = "", Width = 28, Height = 28, ToolTip = "Dictate" };
        _dictateButton.FontFamily = (System.Windows.Media.FontFamily)FindResourceOrDefault("IconFontFamily");
        _dictateButton.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(_dictateButton, "Dictate");
        _dictateButton.Click += (_, _) => ToggleDictation();
        actions.Children.Add(_dictateButton);
        panel.Children.Add(actions);
        return panel;
    }

    public void AttachFiles(IEnumerable<string> paths)
    {
        if (_session.Phase == ComparisonPhase.Streaming) return;
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            if (!File.Exists(path) || _attachments.Any(attachment => string.Equals(attachment.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (ImageAttachments.IsAcceptedExtension(path) && new FileInfo(path).Length > ImageAttachments.MaxSourceBytes)
            { _composerError.Text = "Images must be smaller than 30 MB."; continue; }
            _attachments.Add(new ComposerAttachment
            {
                Kind = ImageAttachments.IsAcceptedExtension(path) ? ComposerAttachmentKind.Image : ComposerAttachmentKind.File,
                FilePath = path,
            });
        }
        RenderAttachments();
    }

    private void OnComposerPaste(object sender, DataObjectPastingEventArgs args)
    {
        if (_session.Phase == ComparisonPhase.Streaming) return;
        try
        {
            if (args.DataObject.GetDataPresent(DataFormats.FileDrop) && args.DataObject.GetData(DataFormats.FileDrop) is string[] files)
            { AttachFiles(files); args.CancelCommand(); return; }
            if (args.DataObject.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bitmap && _services is not null)
            {
                var directory = Path.Combine(_services.Paths.Root, "attachments");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "paste-" + Guid.NewGuid().ToString("N") + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(path)) encoder.Save(output);
                AttachFiles([path]); args.CancelCommand(); return;
            }
            if (args.DataObject.GetData(DataFormats.UnicodeText) is string text && text.Length > 2000)
            {
                _attachments.Add(new ComposerAttachment { Kind = ComposerAttachmentKind.PastedText, Text = text });
                RenderAttachments(); args.CancelCommand();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { _composerError.Text = ex.Message; args.CancelCommand(); }
    }

    private void RenderAttachments()
    {
        _attachmentStrip.Children.Clear();
        foreach (var attachment in _attachments)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var preview = new Button { Content = attachment.Label, ToolTip = attachment.Tooltip, Padding = new Thickness(6, 3, 6, 3) };
            preview.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(preview, "Preview " + attachment.Label);
            if (attachment.IsImage && attachment.FilePath is { } imagePath)
            {
                try
                {
                    using var source = File.OpenRead(imagePath);
                    var bitmap = BitmapFrame.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    bitmap.Freeze();
                    preview.Content = new Image { Source = bitmap, Width = 44, Height = 44, Stretch = System.Windows.Media.Stretch.UniformToFill };
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException) { }
                preview.Click += (_, _) => ImageLightboxWindow.OpenFile(Window.GetWindow(this), imagePath);
            }
            else if (attachment.Kind is ComposerAttachmentKind.PastedText or ComposerAttachmentKind.Context)
            {
                preview.Click += (_, _) => new Window
                {
                    Title = attachment.Label, Owner = Window.GetWindow(this), Width = 720, Height = 500,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new TextBox { Text = attachment.Text, IsReadOnly = true, AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) },
                }.ShowDialog();
            }
            else preview.IsEnabled = false;
            content.Children.Add(preview);
            var remove = new Button { Content = "×", Width = 24, Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Top };
            remove.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(remove, "Remove " + attachment.Label);
            remove.Click += (_, _) => { _attachments.Remove(attachment); RenderAttachments(); };
            content.Children.Add(remove);
            var chip = new Border { Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 4) };
            chip.SetResourceReference(Border.BorderBrushProperty, "Border300Brush");
            _attachmentStrip.Children.Add(chip);
        }
    }

    private void ClearComposerAttachments()
    {
        _attachments.Clear();
        _composerError.Text = "";
        RenderAttachments();
    }

    private void ToggleDictation()
    {
        if (_session.Phase == ComparisonPhase.Streaming) return;
        if (_voice?.IsListening == true)
        { _voice.Stop(); _dictateButton!.ToolTip = "Dictate"; return; }
        if (_voice is null)
        {
            _voice = new VoiceInput();
            _voice.Recognized += text => Dispatcher.InvokeAsync(() =>
            {
                if (_voice?.IsListening != true || _session.Phase == ComparisonPhase.Streaming) return;
                _input.SelectedText = (_input.CaretIndex > 0 ? " " : "") + text;
                _input.CaretIndex = _input.Text.Length;
            });
        }
        _composerError.Text = _voice.Start() ?? "";
        _dictateButton!.ToolTip = _voice.IsListening ? "Stop dictation" : "Dictate";
    }
}
