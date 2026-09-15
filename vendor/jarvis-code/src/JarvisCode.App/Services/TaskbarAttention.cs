using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JarvisCode.App.Services;

/// <summary>
/// The two ways the reference asks for attention without an OS banner: a count on
/// the taskbar button (its <c>app.setBadgeCount</c>, driven by
/// <c>updateBadge</c>) and a flashing taskbar button while something waits and the
/// window is not in front (its <c>requestUserAttention</c> /
/// <c>stopFlashFrameIfNoneBlocking</c>, gated on <c>dockBounceEnabled</c>).
///
/// Electron draws the badge as an overlay icon on Windows; WPF exposes the same
/// shell slot as <c>TaskbarItemInfo.Overlay</c>, so the count is rendered here in
/// the theme's accent rather than borrowing another app's asset.
/// </summary>
public static class TaskbarAttention
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private const uint FlashStop = 0;
    private const uint FlashTray = 2;
    private const uint FlashTimerUntilForeground = 12;

    /// <summary>Sets or clears the taskbar badge. A count of zero removes it.</summary>
    public static void SetBadgeCount(Window window, int count)
    {
        window.TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        if (count <= 0)
        {
            window.TaskbarItemInfo.Overlay = null;
            window.TaskbarItemInfo.Description = null;
            return;
        }

        window.TaskbarItemInfo.Overlay = BadgeIcon(count, AccentColor());
        window.TaskbarItemInfo.Description = count == 1
            ? "1 item needs your attention"
            : $"{count} items need your attention";
    }

    /// <summary>
    /// Starts or stops the taskbar flash. The reference only ever starts it while
    /// the window is neither focused nor visible, and stops it unconditionally.
    /// </summary>
    public static void SetFlashing(Window window, bool flashing)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = flashing ? FlashTray | FlashTimerUntilForeground : FlashStop,
            Count = flashing ? uint.MaxValue : 0,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private static Color AccentColor() =>
        Application.Current?.Resources["AccentBrandColor"] is Color accent
            ? accent
            : Colors.Coral;

    /// <summary>A filled disc carrying the count, at the 16px the shell overlay uses.</summary>
    private static BitmapSource BadgeIcon(int count, Color accent)
    {
        const int size = 16;
        var text = count > 9 ? "9+" : count.ToString();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawEllipse(
                new SolidColorBrush(accent), null, new Point(size / 2.0, size / 2.0), 8, 8);
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                count > 9 ? 9 : 10,
                Brushes.White,
                1.0);
            context.DrawText(
                formatted,
                new Point((size - formatted.Width) / 2, (size - formatted.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
