using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Jarvis.Agent.Desktop.Infrastructure;

/// <summary>New session pages use dynamic semantic tokens and follow Windows high-contrast changes.</summary>
public static class SessionPalette
{
    private static Application? _application;
    private static readonly Dictionary<string, object> Original = new();
    public static void EnsureInitialized()
    {
        if (Application.Current is not { } app || ReferenceEquals(_application, app)) return;
        _application = app;
        foreach (var key in new[] { "Bg", "Panel", "Text", "Muted", "Line", "Accent", "OnAccent", "Danger", "Warning" })
            Original[key] = app.FindResource(key);
        SystemParameters.StaticPropertyChanged += Changed;
        app.Exit += (_, _) => SystemParameters.StaticPropertyChanged -= Changed;
        Apply();
    }
    private static void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) _application?.Dispatcher.InvokeAsync(Apply);
    }
    private static void Apply()
    {
        if (_application is not { } app) return;
        if (!SystemParameters.HighContrast)
        {
            foreach (var pair in Original) app.Resources[pair.Key] = pair.Value;
            return;
        }
        foreach (var key in new[] { "Bg", "Panel" }) app.Resources[key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "Text", "Muted", "Line", "Danger", "Warning" }) app.Resources[key] = SystemColors.WindowTextBrush;
        app.Resources["Accent"] = SystemColors.HighlightBrush;
        app.Resources["OnAccent"] = SystemColors.HighlightTextBrush;
    }
}
