using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The live emulator pane the reference's <c>attach</c> opens — its own
/// <c>EpitaxySimulatorPanel</c> (ion-dist chunk <c>c49d37e5f-WlOcFq3Q.js</c>),
/// titled by its <c>$H</c> ("Android Emulator") and filed under its
/// <c>simulator</c> pane kind.
///
/// Its three states are the reference's: the attach prompt with its picker, the
/// live view, and the control bar under both — Back · Home · Recents |
/// Save screenshot | Detach emulator, which is exactly the row its
/// <c>SimulatorControlBar</c> draws when <c>isAndroid</c> (Record, Rotate and
/// Shut down are the iOS half and are not drawn).
///
/// Two measured deltas, both declared in <c>reference-surface-deltas.tsv</c>:
/// the reference streams h264 out of <c>adb exec-out screenrecord</c> and
/// decodes it in Chromium, which WPF has no decoder for, so this pane pumps
/// <c>screencap</c> frames instead; and its device bezel, side buttons and edge
/// gestures are an SVG frame around the stream that this pane does not draw.
/// </summary>
public sealed class EmulatorPanel : UserControl, IAndroidEmulatorPanel
{
    /// <summary>How often a frame is pulled. The reference streams, so this interval is this build's own.</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(250);

    private readonly Image _view = new()
    {
        Stretch = Stretch.Uniform,
        Focusable = true,
        Cursor = Cursors.Arrow,
    };

    private readonly ContentControl _host = new() { Focusable = false };
    private readonly StackPanel _controls = new() { Orientation = Orientation.Horizontal };
    private readonly Border _controlBar;
    private readonly ComboBox _picker = new();
    private readonly Button _attachButton;
    private readonly TextBlock _pickerEmpty;

    private AndroidEmulatorBridge? _bridge;
    private CancellationTokenSource? _pump;
    private AndroidPanelAttachment? _attached;
    private byte[]? _lastFrame;
    private (int Width, int Height)? _frameSize;
    private (int Width, int Height)? _displaySize;
    private Point? _dragStart;

    public EmulatorPanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PanePrimitives.HeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(PanePrimitives.Title(SidePanes.Title(SidePanes.Simulator)));

        _view.SetValue(AutomationProperties.NameProperty, TouchSurfaceLabel);
        _view.MouseLeftButtonDown += OnViewPressed;
        _view.MouseLeftButtonUp += OnViewReleased;
        _view.TextInput += OnViewTextInput;
        _view.KeyDown += OnViewKeyDown;

        var body = new Border { Padding = new Thickness(12), Child = _host };
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        // The rows carry "{AVD name} ({serial})", so the picker sizes to its
        // widest one rather than clipping the serial off the end.
        _picker.MinWidth = 200;
        _picker.Margin = new Thickness(0, 0, 8, 0);
        _picker.SetValue(AutomationProperties.NameProperty, ChoosePicker);
        _pickerEmpty = PanePrimitives.Muted(NoEmulators);
        _pickerEmpty.Visibility = Visibility.Collapsed;

        _attachButton = new Button { Content = AttachLabel, MinWidth = 132 };
        _attachButton.SetResourceReference(StyleProperty, "PrimaryButton");
        _attachButton.Click += (_, _) => _ = AttachSelectedAsync();

        _controlBar = new Border
        {
            Padding = new Thickness(10, 6, 10, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _controls,
        };
        _controlBar.SetResourceReference(Border.BorderBrushProperty, "Border300Brush");
        Grid.SetRow(_controlBar, 2);
        grid.Children.Add(_controlBar);

        BuildControlBar();
        Content = grid;
        ShowAttachPrompt();
    }

    // ---- the reference's strings ----

    /// <summary>Its <c>ZloVmgwt+/</c>, with the assistant named the way this app names it.</summary>
    internal const string AttachHeading = "Attach an emulator so Jarvis can see your app";

    /// <summary>Its <c>4JtC9MSOdU</c>.</summary>
    internal const string AttachBody = "Jarvis will control this emulator and take screenshots of its entire screen.";

    /// <summary>Its <c>VVkeaJI969</c>.</summary>
    internal const string BootsAutomatically = "Shut-down devices boot automatically.";

    /// <summary>Its <c>zc+Xyl0Uea</c>.</summary>
    internal const string AttachLabel = "Attach emulator";

    /// <summary>Its <c>8w8SOeyOJA</c>.</summary>
    internal const string AttachingLabel = "Attaching…";

    /// <summary>Its <c>6IjSTkGhXh</c>.</summary>
    internal const string BootingLabel = "Booting…";

    /// <summary>Its <c>cZNSizCtNf</c>.</summary>
    internal const string ChoosePicker = "Choose emulator";

    /// <summary>Its <c>egKQwMcf7q</c>.</summary>
    internal const string NoEmulators = "No emulators found. Create one in Android Studio";

    /// <summary>Its <c>pUe25MEkoL</c>.</summary>
    internal const string BootedGroup = "Booted";

    /// <summary>Its <c>OhXU1lP/03</c>.</summary>
    internal const string AvailableGroup = "Available (will boot)";

    /// <summary>Its <c>/irxd4uvx/</c>.</summary>
    internal const string TouchSurfaceLabel =
        "Simulator touch surface. Click to focus, then type to send keystrokes";

    /// <summary>Its <c>Z66Q0ok8AH</c>.</summary>
    internal static string LiveStreamLabel(string deviceName) => $"Live stream of {deviceName} emulator";

    /// <summary>Its control-bar rows, in its own order for <c>isAndroid</c>.</summary>
    internal const string BackLabel = "Back";

    internal const string HomeLabel = "Home";

    internal const string RecentsLabel = "Recents";

    internal const string SaveScreenshotLabel = "Save screenshot";

    internal const string DetachLabel = "Detach emulator";

    // ---- IAndroidEmulatorPanel ----

    public IReadOnlyList<AndroidPanelAttachment> Attachments =>
        _attached is null ? [] : [_attached];

    /// <summary>Raised when the pane wants the workspace to bring it on screen.</summary>
    public event EventHandler? AttachRequested;

    public async Task<string?> AttachAsync(string serial, string? deviceName, CancellationToken cancellationToken)
    {
        if (Bridge() is not { } bridge)
        {
            return AndroidEmulatorMessages.AdbNotFound;
        }

        var devices = await bridge.ListDevicesAsync(cancellationToken);
        var device = devices.FirstOrDefault(d => d.Serial == serial);
        if (device is null || device.State != "Booted")
        {
            return $"{serial} is not a booted emulator.";
        }

        await Dispatcher.InvokeAsync(() =>
        {
            StopPump();
            _attached = new AndroidPanelAttachment(
                serial, deviceName ?? AndroidEmulator.StripSerialSuffix(device.Name));
            _view.SetValue(AutomationProperties.HelpTextProperty, LiveStreamLabel(_attached.DeviceName ?? serial));
            _host.Content = _view;
            SetControlsEnabled(true);
            AttachRequested?.Invoke(this, EventArgs.Empty);
            StartPump(bridge, serial);
        });

        return null;
    }

    public IReadOnlyList<AndroidPanelAttachment> Detach()
    {
        var was = Attachments;
        Dispatcher.Invoke(() =>
        {
            StopPump();
            _attached = null;
            _lastFrame = null;
            _frameSize = null;
            _displaySize = null;
            _view.Source = null;
            SetControlsEnabled(false);
            ShowAttachPrompt();
        });
        return was;
    }

    /// <summary>Fills the picker when the pane opens, the way the reference lists its devices.</summary>
    public void Load()
    {
        if (_attached is not null)
        {
            return;
        }

        _ = RefreshPickerAsync();
    }

    // ---- the attach prompt ----

    private void ShowAttachPrompt()
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360,
        };

        var heading = new TextBlock
        {
            Text = AttachHeading,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        stack.Children.Add(heading);

        foreach (var line in new[] { AttachBody, BootsAutomatically })
        {
            var body = PanePrimitives.Muted(line);
            body.TextAlignment = TextAlignment.Center;
            body.Margin = new Thickness(0, 0, 0, 4);
            stack.Children.Add(body);
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        row.Children.Add(_picker);
        row.Children.Add(_attachButton);
        stack.Children.Add(row);

        _pickerEmpty.TextAlignment = TextAlignment.Center;
        _pickerEmpty.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_pickerEmpty);

        _host.Content = stack;
        _ = RefreshPickerAsync();
    }

    private async Task RefreshPickerAsync()
    {
        if (Bridge() is not { } bridge)
        {
            _picker.Visibility = Visibility.Collapsed;
            _attachButton.IsEnabled = false;
            _pickerEmpty.Text = AndroidEmulatorMessages.AdbNotFound;
            _pickerEmpty.Visibility = Visibility.Visible;
            return;
        }

        var devices = await bridge.ListDevicesAsync(CancellationToken.None);
        var avds = await AndroidEmulatorBridge.ListAvdsAsync(CancellationToken.None);

        _picker.Items.Clear();
        foreach (var device in devices.Where(static d => d.State == "Booted"))
        {
            _picker.Items.Add(new PickerRow(device.Serial, $"{device.Name} — {BootedGroup}"));
        }

        var running = devices.Select(static d => AndroidEmulator.StripSerialSuffix(d.Name)).ToList();
        foreach (var avd in avds.Where(avd => !running.Any(name => AndroidEmulator.AvdNamesMatch(avd, name))))
        {
            _picker.Items.Add(new PickerRow(avd, $"{avd.Replace('_', ' ')} — {AvailableGroup}"));
        }

        var any = _picker.Items.Count > 0;
        _picker.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _attachButton.IsEnabled = any;
        _pickerEmpty.Text = NoEmulators;
        _pickerEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (any)
        {
            _picker.SelectedIndex = 0;
        }
    }

    private sealed record PickerRow(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// What the pane's own Attach button does — the reference's user-initiated
    /// attach, including the cold boot its own "Shut-down devices boot
    /// automatically" promises: the button reads Booting… while the AVD comes
    /// up, then Attaching… once there is a serial to attach.
    /// </summary>
    private async Task AttachSelectedAsync()
    {
        if (_picker.SelectedItem is not PickerRow row || Bridge() is not { } bridge)
        {
            return;
        }

        var serial = row.Id;
        _attachButton.Content = AndroidEmulator.IsEmulatorSerial(serial) ? AttachingLabel : BootingLabel;
        _attachButton.IsEnabled = false;
        try
        {
            if (!AndroidEmulator.IsEmulatorSerial(serial))
            {
                var known = (await bridge.ListDevicesAsync(CancellationToken.None))
                    .Select(static d => d.Serial)
                    .ToHashSet(StringComparer.Ordinal);
                var (booted, error) = await bridge.BootAvdAsync(serial, known, CancellationToken.None);
                if (booted is null)
                {
                    ToastQueue.Current?.AddWarning(error!);
                    return;
                }

                serial = booted;
                _attachButton.Content = AttachingLabel;
            }

            if (await AttachAsync(serial, null, CancellationToken.None) is { } failure)
            {
                ToastQueue.Current?.AddWarning(failure);
            }
        }
        finally
        {
            _attachButton.Content = AttachLabel;
            _attachButton.IsEnabled = true;
        }
    }

    // ---- the control bar ----

    private void BuildControlBar()
    {
        _controls.Children.Add(ControlButton(BackLabel, () => SendButton("BACK")));
        _controls.Children.Add(ControlButton(HomeLabel, () => SendButton("HOME")));
        _controls.Children.Add(ControlButton(RecentsLabel, () => SendButton("RECENTS")));
        _controls.Children.Add(Separator());
        _controls.Children.Add(ControlButton(SaveScreenshotLabel, SaveScreenshot));
        _controls.Children.Add(Separator());
        _controls.Children.Add(ControlButton(DetachLabel, () => Detach()));
        SetControlsEnabled(false);
    }

    private static Button ControlButton(string label, Action click)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(9, 3, 9, 3) };
        button.SetResourceReference(StyleProperty, "SecondaryButton");
        button.SetValue(AutomationProperties.NameProperty, label);
        button.Click += (_, _) => click();
        return button;
    }

    private static Border Separator()
    {
        var rule = new Border { Width = 1, Margin = new Thickness(2, 4, 8, 4) };
        rule.SetResourceReference(Border.BackgroundProperty, "Border300Brush");
        return rule;
    }

    private void SetControlsEnabled(bool enabled)
    {
        foreach (var child in _controls.Children.OfType<Button>())
        {
            child.IsEnabled = enabled;
        }
    }

    // ---- the frame pump ----

    private AndroidEmulatorBridge? Bridge()
    {
        if (_bridge is not null)
        {
            return _bridge;
        }

        return _bridge = AndroidEmulator.FindAdb() is { } adb ? new AndroidEmulatorBridge(adb) : null;
    }

    private void StartPump(AndroidEmulatorBridge bridge, string serial)
    {
        var pump = new CancellationTokenSource();
        _pump = pump;
        _ = Task.Run(async () =>
        {
            _displaySize = await bridge.DisplaySizeAsync(serial, pump.Token);
            while (!pump.IsCancellationRequested)
            {
                try
                {
                    var (exit, bytes, _) = await bridge.RunBinaryAsync(
                        TimeSpan.FromSeconds(15), pump.Token, "-s", serial, "exec-out", "screencap", "-p");
                    if (exit == 0 && bytes.Length > 24)
                    {
                        await Dispatcher.InvokeAsync(() => ShowFrame(bytes));
                    }

                    await Task.Delay(FrameInterval, pump.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // A frame that could not be pulled is skipped; the next one tries again.
                    await Task.Delay(FrameInterval, CancellationToken.None);
                }
            }
        }, pump.Token);
    }

    private void StopPump()
    {
        _pump?.Cancel();
        _pump?.Dispose();
        _pump = null;
    }

    private void ShowFrame(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            _view.Source = frame;
            _lastFrame = png;
            _frameSize = (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            // A torn capture is dropped rather than drawn.
        }
    }

    // ---- input ----

    private void OnViewPressed(object sender, MouseButtonEventArgs e)
    {
        _view.Focus();
        _dragStart = e.GetPosition(_view);
    }

    private void OnViewReleased(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start || _attached is not { } attachment || Bridge() is not { } bridge)
        {
            return;
        }

        var end = e.GetPosition(_view);
        _dragStart = null;
        if (DevicePoint(start) is not { } from || DevicePoint(end) is not { } to)
        {
            return;
        }

        var drag = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y);
        _ = drag < 6
            ? bridge.ShellAsync(attachment.Serial, CancellationToken.None, "input", "tap", $"{from.X}", $"{from.Y}")
            : bridge.ShellAsync(attachment.Serial, CancellationToken.None,
                "input", "swipe", $"{from.X}", $"{from.Y}", $"{to.X}", $"{to.Y}", "300");
    }

    /// <summary>A point on the drawn frame, in the device's own pixels.</summary>
    private (int X, int Y)? DevicePoint(Point point)
    {
        if (_frameSize is not { } frame || _view.ActualWidth <= 0 || _view.ActualHeight <= 0)
        {
            return null;
        }

        // Stretch.Uniform letterboxes; the drawn rectangle is what a click maps through.
        var scale = Math.Min(_view.ActualWidth / frame.Width, _view.ActualHeight / frame.Height);
        var drawnWidth = frame.Width * scale;
        var drawnHeight = frame.Height * scale;
        var x = (point.X - ((_view.ActualWidth - drawnWidth) / 2)) / scale;
        var y = (point.Y - ((_view.ActualHeight - drawnHeight) / 2)) / scale;
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
        {
            return null;
        }

        var display = _displaySize ?? frame;
        return (AndroidEmulator.MapCoordinate(x, frame.Width, display.Width),
            AndroidEmulator.MapCoordinate(y, frame.Height, display.Height));
    }

    private void OnViewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_attached is not { } attachment || Bridge() is not { } bridge)
        {
            return;
        }

        var sent = AndroidEmulator.SanitizeText(e.Text);
        if (sent.Length > 0 && AndroidEmulator.EncodeInputText(sent) is { } encoded)
        {
            _ = bridge.ShellAsync(attachment.Serial, CancellationToken.None, "input", "text", encoded);
        }

        e.Handled = true;
    }

    private void OnViewKeyDown(object sender, KeyEventArgs e)
    {
        var keycode = e.Key switch
        {
            Key.Back => 67,
            Key.Enter => 66,
            Key.Tab => 61,
            Key.Escape => 111,
            Key.Left => 21,
            Key.Right => 22,
            Key.Up => 19,
            Key.Down => 20,
            _ => 0,
        };
        if (keycode == 0 || _attached is not { } attachment || Bridge() is not { } bridge)
        {
            return;
        }

        _ = bridge.ShellAsync(attachment.Serial, CancellationToken.None, "input", "keyevent", $"{keycode}");
        e.Handled = true;
    }

    private void SendButton(string name)
    {
        if (_attached is not { } attachment ||
            Bridge() is not { } bridge ||
            !AndroidEmulator.ButtonKeycodes.TryGetValue(name, out var keycode))
        {
            return;
        }

        _ = bridge.ShellAsync(attachment.Serial, CancellationToken.None, "input", "keyevent", $"{keycode}");
    }

    private void SaveScreenshot()
    {
        if (_lastFrame is not { } png)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image|*.png",
            FileName = $"emulator-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ToastQueue.Current?.AddDanger(ex.Message);
        }
    }
}
