using System.Windows;
using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The About window, ported from the reference's own (app.asar
/// <c>.vite/renderer/about_window/assets/AboutWindow-*.js</c>, opened by
/// <c>dxn()</c> at 320x428, not resizable, modal to the main window and out of the
/// taskbar): the app mark at 84px, the name with an italic "for" before the
/// platform, a version line that copies to the clipboard when clicked and says so
/// for two seconds, the build's timestamp, then Help and Get support.
///
/// One deliberate difference: the reference's version line shows Electron's
/// <c>process.version</c> — the Node runtime it is built on — beside the commit.
/// This app has no such runtime version to report, so the line carries the app's
/// own version, which is what the reference's label promises.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>How long the reference leaves the "Copied" wording up.</summary>
    private static readonly TimeSpan CopiedDwell = TimeSpan.FromSeconds(2);

    public const string AppName = "Jarvis";
    public const string VersionLabelFormat = "Version {0}";
    public const string CopiedLabel = "Copied version to clipboard";
    public const string CopyAccessibleNameFormat = "Copy version {0} to clipboard";

    private readonly string _version;
    private readonly string? _builtAt;
    private System.Windows.Threading.DispatcherTimer? _copiedTimer;

    public AboutWindow()
    {
        InitializeComponent();
        AppNameRun.Text = AppName;
        _version = BuildVersion();
        _builtAt = BuildTimestamp();
        BuiltText.Text = _builtAt ?? "";
        ShowVersion();
    }

    /// <summary>
    /// The app version with the six-character commit the reference shows beside it.
    /// The commit is the informational version's git hash when the build recorded
    /// one, and "Unknown" otherwise — which is the reference's own fallback.
    /// </summary>
    private static string BuildVersion()
    {
        var assembly = typeof(AboutWindow).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "dev";
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var commit = informational?.Split('+') is { Length: > 1 } parts && parts[^1].Length >= 6
            ? parts[^1][..6]
            : "Unknown";
        return $"{version} ({commit})";
    }

    private static string? BuildTimestamp()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is null)
            {
                return null;
            }

            // The reference formats this one with dateStyle "medium" and timeStyle
            // "short". .NET has no medium date, so the date is spelled out and the
            // time uses the standard short pattern — which has to be its own call,
            // since "t" inside a custom pattern means the AM/PM initial instead.
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            var written = System.IO.File.GetLastWriteTime(path);
            return $"{written.ToString("MMM d, yyyy", culture)}, {written.ToString("t", culture)}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ShowVersion()
    {
        VersionButton.Content = string.Format(VersionLabelFormat, _version);
        System.Windows.Automation.AutomationProperties.SetName(
            VersionButton, string.Format(CopyAccessibleNameFormat, _version));
    }

    private void OnCopyVersionClick(object sender, RoutedEventArgs e)
    {
        var payload = string.Join(' ', new[] { AppName, _version, _builtAt }
            .Where(static part => !string.IsNullOrEmpty(part)));
        try
        {
            Clipboard.SetText(payload);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; the reference logs and moves on.
            return;
        }

        VersionButton.Content = CopiedLabel;
        _copiedTimer?.Stop();
        _copiedTimer = new System.Windows.Threading.DispatcherTimer { Interval = CopiedDwell };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer?.Stop();
            _copiedTimer = null;
            ShowVersion();
        };
        _copiedTimer.Start();
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) =>
        ExternalLinks.Open(this, AppMenu.DocumentationUrl);

    private void OnSupportClick(object sender, RoutedEventArgs e) =>
        ExternalLinks.Open(this, AppMenu.SupportUrl);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>Opens the window, or brings the one already up forward.</summary>
    public static void ShowFor(Window owner)
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is AboutWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        new AboutWindow { Owner = owner }.Show();
    }
}
