using System.Runtime.InteropServices;
using System.Windows.Media;

namespace JarvisCode.App.Services;

/// <summary>
/// System-tray icon with the app menu. The icon is drawn at runtime (an
/// eight-ray star in the accent color) so it follows the active theme.
/// </summary>
public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly System.Windows.Forms.NotifyIcon _icon;
    private IntPtr _iconHandle = IntPtr.Zero;

    public event EventHandler? OpenRequested;
    public event EventHandler? QuitRequested;

    public TrayService(string tooltip)
    {
        // The reference's Windows tray menu is Show App, a separator, and Exit —
        // the session entries it used to carry here live in the jump list, which is
        // where Windows puts them. Its remaining rows are plan and usage readouts
        // that need a claude.ai account.
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show App", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        UpdateIcon(Colors.Coral);
    }

    /// <summary>
    /// Whether the tray icon is on screen at all — the reference's "Keep Jarvis
    /// running in the system tray" switch (its <c>setMenuBarEnabled</c>), which the
    /// settings page turns off to hide it.
    /// </summary>
    public bool IsVisible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>Redraws the tray glyph in the given accent color.</summary>
    public void UpdateIcon(Color accent)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new System.Drawing.SolidBrush(
                System.Drawing.Color.FromArgb(accent.R, accent.G, accent.B));

            // Eight-ray star: four long cardinal rays + four short diagonals.
            const float cx = 16f, cy = 16f;
            for (var i = 0; i < 8; i++)
            {
                var angle = Math.PI / 4 * i;
                var length = i % 2 == 0 ? 14f : 9.5f;
                var halfWidth = i % 2 == 0 ? 3.4f : 2.6f;
                var (sin, cos) = (Math.Sin(angle), Math.Cos(angle));
                var tip = Point(cx + cos * length, cy + sin * length);
                var left = Point(cx - sin * halfWidth, cy + cos * halfWidth);
                var right = Point(cx + sin * halfWidth, cy - cos * halfWidth);
                g.FillPolygon(brush, [left, tip, right, Point(cx, cy)]);
            }
        }

        var newHandle = bitmap.GetHicon();
        var oldHandle = _iconHandle;
        _iconHandle = newHandle;
        _icon.Icon = System.Drawing.Icon.FromHandle(newHandle);
        if (oldHandle != IntPtr.Zero)
        {
            DestroyIcon(oldHandle);
        }

        static System.Drawing.PointF Point(double x, double y) => new((float)x, (float)y);
    }

    /// <summary>
    /// Balloon notification (routine finished, background task done…).
    /// <paramref name="silent"/> carries the reference's notification-sound setting
    /// and drops the balloon's icon, which is as far as it goes here: WinForms'
    /// NotifyIcon owns the NOTIFYICONDATA it fills in and exposes no NIIF_NOSOUND,
    /// so whether Windows plays a sound stays the OS's decision.
    /// </summary>
    public void ShowNotification(string title, string message, bool silent = false)
    {
        try
        {
            _icon.ShowBalloonTip(
                5000,
                title,
                message,
                silent ? System.Windows.Forms.ToolTipIcon.None : System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (InvalidOperationException)
        {
            // The tray icon is gone (shutdown race); the notification is best-effort.
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }
}
