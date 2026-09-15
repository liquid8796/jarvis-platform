using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.Core.Customization;
using Path = System.IO.Path;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// The Customize content area, at the shape of the reference's own
/// /customize routes: Skills, Plugins, Connectors and Memory, each a tabbed
/// list over the Directory, plus the detail pages a row opens. The nav beside
/// it (<see cref="CustomizeNavView"/>) names the same four.
/// </summary>
public partial class CustomizeSurface : UserControl
{
    private AppServices? _services;
    private Func<string>? _workingDirectory;

    /// <summary>Raised when a plugin was installed, removed or switched, so the nav and the turn reload.</summary>
    public event EventHandler? PluginsChanged;

    /// <summary>Raised when a skill was created, edited, uploaded or removed.</summary>
    public event EventHandler? SkillsChanged;

    /// <summary>Asks the shell to open a Code session carrying this prompt ("Create with Claude").</summary>
    public event EventHandler<string>? CodeSessionRequested;

    public CustomizeSurface()
    {
        InitializeComponent();
    }

    public void Initialize(AppServices services, Func<string> workingDirectory)
    {
        _services = services;
        _workingDirectory = workingDirectory;
        ShowHub();
    }

    private AppServices Services => _services ?? throw new InvalidOperationException("Customize is not initialized.");

    private string Cwd => _workingDirectory?.Invoke() is { Length: > 0 } cwd
        ? cwd
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The version store lives beside ui-settings.json, out of every skill folder.</summary>
    private SkillVersions Versions => new(Path.Combine(
        Path.GetDirectoryName(Services.UiSettings.FilePath) ?? Services.Paths.Root, SkillVersions.DirectoryName));

    private void Show(FrameworkElement page)
    {
        PageHost.Content = page;
        PageScroller.ScrollToTop();
    }

    /// <summary>A page body: the header and everything under it, in one column.</summary>
    private static StackPanel NewPage() => new() { Margin = new Thickness(0, 28, 0, 40) };

    // ---------- Hub ----------

    /// <summary>
    /// The reference's Customize entry has no hub of its own — its nav lands on
    /// Skills. This build keeps a hub because its nav is a sidebar the user can
    /// arrive at with nothing selected, and the three cards carry the reference's
    /// own section copy.
    /// </summary>
    public void ShowHub()
    {
        if (_services is null)
            return;

        var panel = new StackPanel { Margin = new Thickness(0, 56, 0, 40), HorizontalAlignment = HorizontalAlignment.Center };

        var title = CustomizeUi.Text("Customize Jarvis", 28, "Text100Brush", semibold: true);
        title.FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFontFamily");
        title.FontWeight = FontWeights.Normal;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(title);

        var subtitle = CustomizeUi.Text(
            "Skills, connectors, and plugins shape how Jarvis works with you.", 13.5, "Text400Brush");
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        subtitle.Margin = new Thickness(0, 0, 0, 26);
        panel.Children.Add(subtitle);

        panel.Children.Add(HubCard(
            "Skills teach Jarvis to do a task exactly the way you want, every time.", "Skills", ShowSkills));
        panel.Children.Add(HubCard(
            "Connectors let Jarvis work with your team’s tools and data.", "Connectors", () => ShowConnectors()));
        panel.Children.Add(HubCard(
            "Give Jarvis role-level expertise with plugins.", "Browse plugins", () => ShowPlugins()));
        panel.Children.Add(HubCard(
            "Jarvis saves what it learns about you and your work during Code sessions. " +
            "These files are stored on this device.", "Memory", ShowMemory));

        Show(panel);
    }

    private FrameworkElement HubCard(string description, string title, Action onClick)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(CustomizeUi.Text(title, 14.5, "Text100Brush", semibold: true));
        var body = CustomizeUi.Text(description, 12.5, "Text400Brush", wrap: true);
        body.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(body);

        var card = new Button
        {
            Content = text,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 13, 16, 13),
            Margin = new Thickness(0, 0, 0, 10),
            MinWidth = 520,
            MaxWidth = 560,
        };
        card.SetResourceReference(StyleProperty, "RowButton");
        System.Windows.Automation.AutomationProperties.SetName(card, title);
        card.Template = CardTemplate();
        card.Click += (_, _) => onClick();
        return card;
    }

    private static ControlTemplate CardTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Root");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter
        {
            Property = Border.BorderBrushProperty,
            TargetName = "Root",
            Value = new DynamicResourceExtension("BorderStrongBrush"),
        });
        template.Triggers.Add(hover);
        return template;
    }

    // ---------- shared plumbing ----------

    /// <summary>Reloads the skill catalogue and tells the shell a skill moved.</summary>
    private void SkillsWereChanged()
    {
        SkillCatalog.InvalidateCache();
        SkillsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reconnects the MCP servers off the UI thread after a connector edit.</summary>
    private void RefreshMcpNow()
    {
        if (_services is null)
            return;
        var cwd = Cwd;
        var paths = _services.Paths;
        var ui = _services.UiSettings.Current;
        var manager = _services.Mcp;
        _ = Task.Run(async () =>
        {
            try
            {
                var plugins = PluginLibrary.LoadActive(paths, cwd, ui);
                await manager.RefreshAsync(cwd, paths.UserMcpFile, CancellationToken.None,
                    JarvisCode.App.Services.DesktopExtensions.ConfigFiles(paths, plugins.McpFiles));
            }
            catch (Exception ex) when (ex is IOException or JarvisCode.Core.Mcp.McpException)
            {
                // A server that will not start shows as "Failed to connect" on its row.
            }
        });
    }

    private void Warn(string message) =>
        MessageBox.Show(Window.GetWindow(this), message, "Jarvis Code");

    private bool Confirm(string title, string message, string confirmLabel, string? subject = null) =>
        ConfirmDialog.Ask(Window.GetWindow(this), title, message, confirmLabel, subject, focusCancel: true);

    private static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // No handler for the file type; nothing sensible to do.
        }
    }

    /// <summary>Reveals a file in Explorer rather than opening it with its handler.</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
        }
    }

    /// <summary>The plugins on disk, whatever their scope or switch — the Plugins page's list.</summary>
    private Plugins.PluginContent AllPlugins() => PluginLibrary.LoadAllScopes(Services.Paths, Cwd);
}
