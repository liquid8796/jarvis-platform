using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// Settings › Desktop app › General — the reference desktop's own Desktop settings
/// page (ion-dist <c>c71860c77-j8GBEGCP.js</c>, its <c>ue</c>), rendered as a Windows
/// build renders it. Three sections in its order: General desktop settings
/// (<c>v7i2fnB2+A</c>), Browser Use (<c>de</c>) and Computer use (<c>ae</c>), followed
/// by the rows this build adds for itself.
///
/// Two of the reference's shortcut rows are absent here because the reference itself
/// does not draw them outside macOS: its "Quick access shortcut" picker is gated on
/// <c>nativeQuickEntry</c> and its "Voice shortcut" on <c>quickEntryDictation</c>, and
/// both features answer <c>{status:"unavailable"}</c> for any platform but darwin
/// (app.asar <c>index.chunk-DnlgCaT3.js</c>, its <c>Qon</c> and <c>nsn</c>). What
/// Windows gets instead is the recorder row this page draws — the branch the reference
/// takes once <c>quickEntryGlobalShortcut</c> is supported, which its <c>$on</c> makes
/// true everywhere but a Wayland session with no GlobalShortcuts portal.
/// </summary>
internal sealed class DesktopPage(AppServices services, Action<string> openPage)
{
    public const string PageTitle = "Desktop app";

    private UiSettings Ui => services.UiSettings.Current;

    private void Save() => services.UiSettings.Save();

    public FrameworkElement Build()
    {
        var page = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        page.Children.Add(BuildGeneralDesktopSettings());
        page.Children.Add(BuildBrowserUse());
        page.Children.Add(BuildComputerUse());
        page.Children.Add(BuildThisBuild());
        return page;
    }

    // ---- General desktop settings (the reference's ue) ----

    private FrameworkElement BuildGeneralDesktopSettings()
    {
        var section = SettingsRows.Section("General desktop settings");

        section.Children.Add(SettingsRows.Row(
            "Run on startup",
            "Automatically start Jarvis when you log in to your computer.",
            SettingsUi.Switch(StartupService.IsEnabled(services.Paths), on => StartupService.SetEnabled(services.Paths, on))));

        section.Children.Add(SettingsRows.Row(
            "Quick Entry keyboard shortcut",
            "Quickly open Jarvis from anywhere.",
            BuildShortcutRecorder()));

        section.Children.Add(SettingsRows.Row(
            "System tray",
            "Keep Jarvis running in the system tray.",
            SettingsUi.Switch(Ui.ShowInSystemTray, on =>
            {
                Ui.ShowInSystemTray = on;
                Save();
                (Application.Current.MainWindow as MainWindow)?.ApplyTrayVisibility();
            })));

        section.Children.Add(SettingsRows.Row(
            "Keep computer awake",
            "Prevent your computer from idle-sleeping while Jarvis is open so scheduled tasks can run. Your display can still turn off. Closing the laptop lid will still put it to sleep.",
            SettingsUi.Switch(Ui.KeepComputerAwake, on => { Ui.KeepComputerAwake = on; Save(); })));

        return section;
    }

    /// <summary>
    /// The reference's shortcut recorder (<c>ne</c>): a focusable box showing the
    /// chord, a clear button once one is set, and the three refusal sentences —
    /// reserved, already in use, unsupported.
    /// </summary>
    private FrameworkElement BuildShortcutRecorder()
    {
        var host = new StackPanel { Width = 220 };
        var display = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MinWidth = 120,
            Height = 28,
            Focusable = true,
            Padding = new Thickness(8, 0, 8, 0),
        };
        box.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        box.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        System.Windows.Automation.AutomationProperties.SetName(box, "Quick Entry keyboard shortcut");

        var clear = new Button
        {
            Content = "✕",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Clear shortcut",
        };
        clear.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");

        var content = new Grid();
        content.Children.Add(display);
        content.Children.Add(clear);
        box.Child = content;

        var error = SettingsRows.Danger("");
        error.Margin = new Thickness(0, 4, 0, 0);
        error.Visibility = Visibility.Collapsed;

        void Show()
        {
            var shown = QuickEntryShortcuts.Display(Ui.QuickEntryShortcut);
            display.Text = shown.Length > 0 ? shown : QuickEntryShortcuts.Placeholder;
            if (shown.Length > 0)
            {
                display.ClearValue(TextBlock.ForegroundProperty);
                display.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            }
            else
            {
                display.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            }

            clear.Visibility = shown.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Fail(string message)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
        }

        void Commit(string accelerator)
        {
            error.Visibility = Visibility.Collapsed;
            var previous = Ui.QuickEntryShortcut;
            Ui.QuickEntryShortcut = accelerator;
            var outcome = (Application.Current.MainWindow as MainWindow)?.ReregisterQuickEntry() ?? HotkeyOutcome.None;
            if (outcome is HotkeyOutcome.RegistrationFailed or HotkeyOutcome.InvalidAccelerator)
            {
                Ui.QuickEntryShortcut = previous;
                (Application.Current.MainWindow as MainWindow)?.ReregisterQuickEntry();
                Fail(outcome == HotkeyOutcome.RegistrationFailed
                    ? QuickEntryShortcuts.InUseMessage
                    : QuickEntryShortcuts.UnsupportedMessage);
                Show();
                return;
            }

            Save();
            Show();
        }

        box.MouseLeftButtonDown += (_, e) => { e.Handled = true; box.Focus(); };
        box.PreviewKeyDown += (_, e) =>
        {
            // Tab without a modifier keeps moving focus, as the reference lets it.
            var modifiers = System.Windows.Input.Keyboard.Modifiers;
            if (e.Key == System.Windows.Input.Key.Tab && modifiers == System.Windows.Input.ModifierKeys.None)
            {
                return;
            }

            e.Handled = true;
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (QuickEntryShortcuts.KeyToken(key) is not { } token)
            {
                return;
            }

            var names = QuickEntryShortcuts.Modifiers(modifiers);
            if (QuickEntryShortcuts.IsReserved(names, token))
            {
                Fail(QuickEntryShortcuts.ReservedMessage);
                return;
            }

            Commit(QuickEntryShortcuts.Compose(names, token));
        };
        clear.Click += (_, _) =>
        {
            error.Visibility = Visibility.Collapsed;
            Ui.QuickEntryShortcut = "";
            (Application.Current.MainWindow as MainWindow)?.ReregisterQuickEntry();
            Save();
            Show();
        };

        Show();
        host.Children.Add(box);
        host.Children.Add(error);
        return host;
    }

    // ---- Browser Use (the reference's de) ----

    private FrameworkElement BuildBrowserUse()
    {
        var section = SettingsRows.Section("Browser use");
        section.Children.Add(SettingsRows.RowWithProse(
            "Allow all browser actions",
            SettingsProse.Description(
                "Jarvis will browse and interact with any website in Chrome without asking. Applies to new sessions. This setting can put your data at risk. <link>Learn more</link>",
                ("link", "https://support.claude.com/en/articles/12902428-using-claude-in-chrome-safely")),
            SettingsUi.Switch(Ui.AllowAllBrowserActions, on => { Ui.AllowAllBrowserActions = on; Save(); })));
        return section;
    }

    // ---- Computer use (the reference's ae) ----

    private FrameworkElement BuildComputerUse()
    {
        var badge = SettingsRows.Badge("Beta");
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var heading = new TextBlock { Text = "Computer use", FontSize = 16, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        title.Children.Add(heading);
        badge.Margin = new Thickness(8, 0, 0, 0);
        title.Children.Add(badge);

        var section = SettingsRows.Section(null);
        title.Margin = new Thickness(0, 0, 0, 12);
        section.Children.Insert(0, title);

        CheckBox? unhide = null;
        StackPanel? deniedHost = null;

        section.Children.Add(SettingsRows.RowWithProse(
            "Enable computer use",
            SettingsProse.DescriptionWithTrailingLink(
                "Let Jarvis take control of your screen, mouse, and keyboard to work in the apps you allow.",
                "Learn more",
                "https://support.claude.com/en/articles/12530024-getting-started-with-claude-for-chrome"),
            SettingsUi.Switch(Ui.ComputerUseEnabled, on =>
            {
                Ui.ComputerUseEnabled = on;
                Save();
                if (unhide is not null)
                {
                    unhide.IsEnabled = on;
                }
            })));

        unhide = SettingsUi.Switch(Ui.ComputerUseAutoUnhide, on => { Ui.ComputerUseAutoUnhide = on; Save(); });
        unhide.IsEnabled = Ui.ComputerUseEnabled;
        section.Children.Add(SettingsRows.Row(
            "Unhide apps when Jarvis finishes",
            "When Jarvis has full control of your screen, apps hidden during a task are restored when Jarvis stops.",
            unhide));

        deniedHost = new StackPanel();
        section.Children.Add(BuildDeniedApps(deniedHost));
        return section;
    }

    /// <summary>The reference's <c>$</c>: the deny-list row, its "Add app" menu and the chips beneath it.</summary>
    private FrameworkElement BuildDeniedApps(StackPanel host)
    {
        var block = new StackPanel();

        var addButton = new Button { Content = "Add app" };
        addButton.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        addButton.Padding = new Thickness(10, 4, 10, 4);

        void Refresh()
        {
            host.Children.Clear();
            var denied = Ui.ComputerUseDeniedApps;
            if (denied.Count == 0)
            {
                var empty = SettingsRows.Footnote("No apps denied. Add an app to automatically reject Jarvis’s requests for it.");
                host.Children.Add(empty);
            }
            else
            {
                var chips = new WrapPanel();
                foreach (var app in denied.ToList())
                {
                    var name = app;
                    var label = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                    label.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
                    var remove = new Button
                    {
                        Content = "✕",
                        Width = 16,
                        Height = 16,
                        Padding = new Thickness(0),
                        Margin = new Thickness(6, 0, -2, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    remove.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
                    System.Windows.Automation.AutomationProperties.SetName(remove, "Remove from deny list");
                    remove.Click += (_, _) =>
                    {
                        ComputerUseAppPolicy.Allow(Ui, name);
                        Save();
                        Refresh();
                    };

                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(label);
                    row.Children.Add(remove);
                    var chip = new Border
                    {
                        Child = row,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8, 4, 8, 4),
                        Margin = new Thickness(0, 0, 6, 6),
                    };
                    chip.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
                    chip.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
                    chips.Children.Add(chip);
                }

                host.Children.Add(chips);
            }

            var candidates = ComputerUseAppPolicy.Candidates(Ui, ComputerUseAppPolicy.InstalledApps());
            addButton.IsEnabled = Ui.ComputerUseEnabled && candidates.Count > 0;
            var menu = new ContextMenu();
            foreach (var candidate in candidates)
            {
                var name = candidate;
                var item = new MenuItem { Header = name };
                item.Click += (_, _) =>
                {
                    ComputerUseAppPolicy.Deny(Ui, name);
                    Save();
                    Refresh();
                };
                menu.Items.Add(item);
            }

            addButton.ContextMenu = menu;
        }

        addButton.Click += (_, _) =>
        {
            if (addButton.ContextMenu is { } menu)
            {
                menu.PlacementTarget = addButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        };

        block.Children.Add(SettingsRows.Row(
            "Denied apps",
            "Any request Jarvis makes to access these apps is automatically rejected. Jarvis may still affect them indirectly through actions in allowed apps.",
            addButton));
        block.Children.Add(host);
        Refresh();
        return block;
    }

    // ---- This build's own desktop rows ----

    private FrameworkElement BuildThisBuild()
    {
        var section = SettingsRows.Section("This app");

        section.Children.Add(SettingsRows.Row("Close to tray",
            "The close button hides the window to the system tray instead of quitting.",
            SettingsUi.Switch(Ui.CloseToTray, on => { Ui.CloseToTray = on; Save(); })));

        section.Children.Add(SettingsRows.Row("Start in tray",
            "Launch hidden; open from the tray icon or Quick Entry.",
            SettingsUi.Switch(Ui.StartInTray, on => { Ui.StartInTray = on; Save(); })));

        section.Children.Add(SettingsRows.Row("Explorer context menu",
            "Adds an Open in Jarvis Code entry to the right-click menu of folders (current user only; removed when switched off).",
            SettingsUi.Switch(ExplorerContextMenu.IsEnabled(),
                on => ExplorerContextMenu.SetEnabled(on, services.Paths.ProfileName))));

        section.Children.Add(SettingsRows.Row("Handle jarvis-code:// links",
            "Lets a link like jarvis-code://code/new open a session in this app. Registered for the current user only, and removed when switched off.",
            SettingsUi.Switch(Ui.DeepLinksRegistered, on =>
            {
                Ui.DeepLinksRegistered = on;
                Save();
                ProtocolRegistration.SetRegistered(on, services.Paths.ProfileName);
            })));

        section.Children.Add(SettingsRows.Row("Check for updates automatically",
            $"Looks at the releases of {AppUpdateService.Repository} and downloads a newer build in the background. Help ▸ Check for Updates… asks now.",
            SettingsUi.Switch(Ui.AutoUpdateEnabled, on => { Ui.AutoUpdateEnabled = on; Save(); })));

        section.Children.Add(SettingsRows.Row("Ping when done",
            "Jarvis can ping you when it finishes work or needs your input while the window is in the background.",
            SettingsUi.Switch(Ui.NotifyWhenDone, on => { Ui.NotifyWhenDone = on; Save(); })));

        section.Children.Add(SettingsRows.Row("Import & export",
            "Bring over sessions from another install, or save this one's as a zip.",
            SettingsRows.SecondaryButton("Open", () => openPage("Import & export"))));

        if (services.Paths.ProfileName is { } profile)
        {
            section.Children.Add(SettingsRows.Block(SettingsUi.Caption(
                $"Running as profile \"{profile}\" — settings, sessions and themes are isolated in {services.Paths.Root}. " +
                "Launch other profiles with:  JarvisCode.App.exe --profile=NAME")));
        }
        else
        {
            section.Children.Add(SettingsRows.Block(SettingsUi.Caption(
                "Run several isolated instances side by side (separate keys, sessions, themes):  JarvisCode.App.exe --profile=work")));
        }

        return section;
    }
}
