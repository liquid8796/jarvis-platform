using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// Settings › Jarvis Code — the reference desktop's Claude Code settings page
/// (ion-dist <c>c71860c77-D1Dqt2F3.js</c>), section by section in its order as a
/// Windows build renders it: Code appearance (<c>Ea</c>), Appearance (<c>la</c>),
/// Local sessions (<c>wa</c>), Browser (<c>qa</c>), Mobile simulators (<c>Ia</c>),
/// Pull requests (<c>Oa</c>) and the Plugins row (<c>Ca</c>). The rows the page
/// gates on a claude.ai account, a rollout flag or macOS are declared in
/// <c>Deltas/reference-surface-deltas.tsv</c> rather than drawn.
/// </summary>
internal sealed class ClaudeCodePage(AppServices services, Action openPlugins)
{
    public const string PageTitle = "Jarvis Code";

    private UiSettings Ui => services.UiSettings.Current;

    private void Save() => services.UiSettings.Save();

    public FrameworkElement Build()
    {
        var page = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        page.Children.Add(BuildCodeAppearance());
        page.Children.Add(BuildAppearance());
        page.Children.Add(BuildLocalSessions());
        page.Children.Add(BuildBrowser());
        page.Children.Add(BuildMobileSimulators());
        page.Children.Add(BuildPullRequests());
        page.Children.Add(BuildPlugins());
        return page;
    }

    // ---- Code appearance (the reference's Ea) ----

    private FrameworkElement BuildCodeAppearance()
    {
        var section = SettingsRows.Section("Code appearance");

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var lightHost = new StackPanel();
        var darkHost = new StackPanel();
        Grid.SetColumn(darkHost, 2);
        grid.Children.Add(lightHost);
        grid.Children.Add(darkHost);

        void FillColumn(StackPanel host, bool dark)
        {
            host.Children.Clear();
            var themes = dark ? CodeThemes.DarkThemes : CodeThemes.LightThemes;
            var current = CodeThemes.Resolve(dark ? Ui.CodeThemeDark : Ui.CodeThemeLight, dark);
            var select = SettingsRows.Select(
                [.. themes.Select(t => (t.Id, t.Label, (string?)null))],
                current.Id,
                id =>
                {
                    if (dark) Ui.CodeThemeDark = id; else Ui.CodeThemeLight = id;
                    Save();
                    CodeThemes.Apply(Ui, services.Theme.IsDark);
                    FillColumn(host, dark);
                },
                width: double.NaN,
                accessibleName: dark ? "Dark code theme" : "Light code theme");
            select.HorizontalAlignment = HorizontalAlignment.Stretch;
            host.Children.Add(select);
            var preview = CodePreviewCard.Build(current, Ui.CodeFont);
            preview.Margin = new Thickness(0, 8, 0, 0);
            host.Children.Add(preview);
        }

        FillColumn(lightHost, dark: false);
        FillColumn(darkHost, dark: true);
        section.Children.Add(grid);

        var fontBox = SettingsRows.Input(Ui.CodeFont, width: 224, placeholder: "e.g. JetBrains Mono", accessibleName: "Code font family");
        SettingsUi.AutoSave(fontBox, (text, _) =>
        {
            Ui.CodeFont = text.Trim();
            Save();
            App.ApplyCodeFont(Ui.CodeFont);
            FillColumn(lightHost, dark: false);
            FillColumn(darkHost, dark: true);
        });
        section.Children.Add(SettingsRows.Row(
            "Code font",
            "Set a custom monospace font for code and terminal.",
            SettingsRows.WithPlaceholder(fontBox, "e.g. JetBrains Mono")));
        return section;
    }

    // ---- Appearance (the reference's la) ----

    private FrameworkElement BuildAppearance()
    {
        var section = SettingsRows.Section("Appearance");

        section.Children.Add(SettingsRows.Row(
            "Interface font",
            "Font for the Jarvis Code interface — menus, sidebar, and chat.",
            SettingsRows.Segmented(
                [("anthropic", "Anthropic Sans"), ("system", "System")],
                Ui.InterfaceFont == "system" ? "system" : "anthropic",
                value =>
                {
                    Ui.InterfaceFont = value;
                    Save();
                    App.ApplyInterfaceFont(value);
                },
                accessibleName: "Interface font")));

        section.Children.Add(SettingsRows.Row(
            "Transcript text size",
            "Size of the conversation transcript text.",
            SettingsRows.Segmented(
                [("s", "Small"), ("m", "Medium"), ("l", "Large")],
                Ui.TranscriptTextSize switch { "small" => "s", "large" => "l", _ => "m" },
                value =>
                {
                    Ui.TranscriptTextSize = value switch { "s" => "small", "l" => "large", _ => "medium" };
                    Save();
                    Controls.MarkdownView.TranscriptTextSize = Controls.TranscriptTextSizes.FromName(Ui.TranscriptTextSize);
                },
                accessibleName: "Transcript text size")));

        section.Children.Add(SettingsRows.Row(
            "Transcript width",
            "Maximum width of the transcript and composer columns.",
            SettingsRows.Segmented(
                [("s", "Narrow"), ("m", "Medium"), ("l", "Wide")],
                Ui.TranscriptWidth is "m" or "l" ? Ui.TranscriptWidth : "s",
                value =>
                {
                    Ui.TranscriptWidth = value;
                    Save();
                    App.ApplyTranscriptWidth(value);
                },
                accessibleName: "Transcript width")));

        return section;
    }

    // ---- Local sessions (the reference's wa) ----

    private FrameworkElement BuildLocalSessions()
    {
        var section = SettingsRows.Section("Local sessions");

        section.Children.Add(SettingsRows.RowWithProse(
            "Allow bypass permissions mode",
            SettingsProse.Description(
                "Bypass all permission checks and let Jarvis work uninterrupted. This works well for workflows like fixing lint errors or generating boilerplate code. Letting Jarvis run arbitrary commands is risky and can result in data loss, system corruption, or data exfiltration (e.g., via prompt injection attacks). <link>See best practices for safe usage</link>",
                ("link", "https://code.claude.com/docs/en/security")),
            SettingsUi.Switch(Ui.BypassPermissionsModeEnabled, on =>
            {
                Ui.BypassPermissionsModeEnabled = on;
                Save();
            })));

        section.Children.Add(SettingsRows.Row(
            "Dynamic workflows",
            "Let Jarvis run multiple agents in parallel for complex tasks. Workflows can use a lot of your usage limit quickly.",
            SettingsUi.Switch(Ui.DynamicWorkflowsEnabled, on =>
            {
                Ui.DynamicWorkflowsEnabled = on;
                Save();
            })));

        CheckBox? attention = null;
        void RefreshAttention()
        {
            if (attention is not null)
            {
                attention.IsEnabled = !NotificationPolicy.AttentionSwitchDisabled(Ui.NotificationLevels);
            }
        }

        foreach (var type in NotificationPolicy.Types)
        {
            var captured = type;
            section.Children.Add(SettingsRows.Row(
                NotificationPolicy.Label(type),
                NotificationPolicy.Description(type),
                SettingsRows.Select(
                    [.. NotificationPolicy.LevelsFor(type).Select(l => (l, NotificationPolicy.LevelLabel(l), (string?)null))],
                    NotificationPolicy.LevelFor(Ui.NotificationLevels, type),
                    level =>
                    {
                        Ui.NotificationLevels[captured] = level;
                        Save();
                        RefreshAttention();
                    },
                    width: 160,
                    accessibleName: $"{NotificationPolicy.Label(type)} notification level")));
        }

        section.Children.Add(SettingsRows.Row(
            "Notification sound",
            "Played when an OS banner is shown.",
            SettingsRows.Select(
                [(NotificationPolicy.SoundSystem, "System sound", null), (NotificationPolicy.SoundNone, "None", null)],
                Ui.NotificationSound == NotificationPolicy.SoundNone ? NotificationPolicy.SoundNone : NotificationPolicy.SoundSystem,
                sound =>
                {
                    Ui.NotificationSound = sound;
                    Save();
                },
                width: 160,
                accessibleName: "Notification sound")));

        attention = SettingsUi.Switch(Ui.DrawAttentionOnNotifications, on =>
        {
            Ui.DrawAttentionOnNotifications = on;
            Save();
        });
        RefreshAttention();
        section.Children.Add(SettingsRows.Row(
            "Draw attention on notifications",
            "Flash the taskbar button when Jarvis needs your attention and the app isn’t focused.",
            attention));

        var archiveChoices = ArchiveChoices(Ui.AutoArchiveDays);
        section.Children.Add(SettingsRows.Row(
            "Archive inactive sessions",
            "Automatically archive local sessions after a period of no activity. Sessions that are running or have background work are never archived, and a worktree with uncommitted changes is kept on disk.",
            SettingsRows.Select(
                [.. archiveChoices.Select(d => (d.ToString(), ArchiveLabel(d), (string?)null))],
                Ui.AutoArchiveDays.ToString(),
                value =>
                {
                    Ui.AutoArchiveDays = int.Parse(value);
                    Save();
                },
                width: 160,
                accessibleName: "Archive inactive sessions")));

        var custom = Ui.WorktreeLocation is { Length: > 0 } location && location != "default";
        var worktreeHost = new StackPanel { Width = 220 };
        var pathLine = new TextBlock
        {
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = custom ? Visibility.Visible : Visibility.Collapsed,
            Text = custom ? Ui.WorktreeLocation : "",
            ToolTip = custom ? Ui.WorktreeLocation : null,
        };
        pathLine.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        var worktreeSelect = SettingsRows.Select(
            [("default", "Inside project", WorktreeTools.WorktreesFolder), ("custom", "Custom…", null)],
            custom ? "custom" : "default",
            value =>
            {
                if (value == "custom")
                {
                    using var picker = new System.Windows.Forms.FolderBrowserDialog();
                    if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK && picker.SelectedPath.Length > 0)
                    {
                        Ui.WorktreeLocation = picker.SelectedPath;
                        Save();
                        pathLine.Text = picker.SelectedPath;
                        pathLine.ToolTip = picker.SelectedPath;
                        pathLine.Visibility = Visibility.Visible;
                    }

                    return;
                }

                Ui.WorktreeLocation = "default";
                Save();
                pathLine.Visibility = Visibility.Collapsed;
            },
            width: 220,
            accessibleName: "Worktree location");
        worktreeSelect.Width = double.NaN;
        worktreeHost.Children.Add(worktreeSelect);
        worktreeHost.Children.Add(pathLine);
        section.Children.Add(SettingsRows.Row(
            "Worktree location",
            "Where to store Git worktrees for isolated coding sessions.",
            worktreeHost));

        return section;
    }

    /// <summary>The reference's <c>va</c> — [0, 1, 2, 7, 14, 30] — plus a stored value not in it, kept sorted.</summary>
    public static IReadOnlyList<int> ArchiveChoices(int current)
    {
        int[] choices = [0, 1, 2, 7, 14, 30];
        return choices.Contains(current) ? choices : [.. choices.Append(current).OrderBy(d => d)];
    }

    /// <summary>"Never" for 0, else the reference's "{count} day(s)".</summary>
    public static string ArchiveLabel(int days) => days == 0 ? "Never" : days == 1 ? "1 day" : $"{days} days";

    // ---- Browser (the reference's qa) ----

    private FrameworkElement BuildBrowser()
    {
        var section = SettingsRows.Section("Browser");
        var dependents = new StackPanel { Visibility = Ui.BrowserPaneLaunchEnabled ? Visibility.Visible : Visibility.Collapsed };

        section.Children.Add(SettingsRows.Row(
            "Browser tools",
            "Jarvis can start dev servers, open a live preview, and verify code changes with screenshots, snapshots, and DOM inspection.",
            SettingsUi.Switch(Ui.BrowserPaneLaunchEnabled, on =>
            {
                Ui.BrowserPaneLaunchEnabled = on;
                Save();
                dependents.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            })));

        dependents.Children.Add(SettingsRows.Row(
            "Open links in built-in browser",
            "Links from Jarvis open in the built-in browser instead of your default browser.",
            SettingsUi.Switch(Ui.BrowserOpenLinksInPanel, on =>
            {
                Ui.BrowserOpenLinksInPanel = on;
                Save();
            })));

        dependents.Children.Add(SettingsRows.Row(
            "Persist sessions",
            "Save cookies, local storage, and login sessions for Browser tabs across app restarts. Shared uses the same data for every session in a project. Separate gives each session its own copy, so sessions never see each other’s logins.",
            SettingsRows.Select(
                [("none", "Don’t keep", null), ("shared", "Shared", null), ("session", "Separate", null)],
                Ui.BrowserPreviewStorage is "shared" or "session" ? Ui.BrowserPreviewStorage : "none",
                value =>
                {
                    Ui.BrowserPreviewStorage = value;
                    Ui.BrowserPersistSessions = value != "none";
                    Save();
                },
                width: 220,
                accessibleName: "Persist sessions")));

        if (BrowserStorageProfile.SharedPartition(BrowserStorageProfile.EngineDirectory(services.Paths.Root), services.Paths.Root).Length == 0)
            dependents.Children.Add(SettingsRows.Block(SettingsUi.Caption(
                "Existing Shared browsing data uses this profile’s original account store across projects. Choose Separate to isolate session data; existing logins are kept.")));

        dependents.Children.Add(SettingsRows.Row(
            "Allowed sites",
            "Sites where Jarvis can use its Browser tools without a permission prompt.",
            SettingsRows.SecondaryButton("Manage", () =>
            {
                var dialog = new AllowedSitesDialog(services.UiSettings)
                {
                    Owner = Application.Current.MainWindow,
                };
                dialog.ShowDialog();
            })));

        section.Children.Add(dependents);
        return section;
    }

    // ---- Mobile simulators (the reference's Ia) ----

    private FrameworkElement BuildMobileSimulators()
    {
        var section = SettingsRows.Section("Mobile simulators");
        section.Children.Add(SettingsRows.Row(
            "Android Emulator",
            "Let Jarvis verify your changes in Android emulators on this computer: running your app, driving it through flows, and capturing screenshots and recordings. You will be asked before Jarvis uses each device. When off, Jarvis doesn’t get its emulator tools, and you can still use the emulator in the app yourself.",
            SettingsUi.Switch(Ui.AndroidToolsEnabled, on =>
            {
                Ui.AndroidToolsEnabled = on;
                Save();
            })));
        return section;
    }

    // ---- Pull requests (the reference's Oa) ----

    private FrameworkElement BuildPullRequests()
    {
        var section = SettingsRows.Section("Pull requests");

        var prefixHost = new StackPanel { Width = 220 };
        var prefixBox = SettingsRows.Input(Ui.BranchPrefix, width: double.NaN, placeholder: UiSettings.DefaultBranchPrefix, accessibleName: "Branch prefix");
        prefixBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        var prefixError = SettingsRows.Danger("");
        prefixError.Margin = new Thickness(0, 4, 0, 0);
        prefixError.Visibility = Visibility.Collapsed;
        prefixBox.TextChanged += (_, _) =>
        {
            var message = BranchPrefixes.ProblemMessage(prefixBox.Text.Trim());
            prefixError.Text = message ?? "";
            prefixError.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        };
        prefixBox.LostFocus += (_, _) =>
        {
            if (BranchPrefixes.Commit(prefixBox.Text, UiSettings.DefaultBranchPrefix) is { } committed)
            {
                Ui.BranchPrefix = committed;
                prefixBox.Text = committed;
                Save();
            }
        };
        prefixHost.Children.Add(prefixBox);
        prefixHost.Children.Add(prefixError);
        section.Children.Add(SettingsRows.Row(
            "Branch prefix",
            "Prefix added to branch names for both local and cloud sessions",
            prefixHost));

        FrameworkElement? draftRow = null;
        section.Children.Add(SettingsRows.Row(
            "Create pull requests automatically",
            "When Jarvis pushes changes to a branch, it automatically opens a pull request without asking first. Applies to remote sessions only.",
            SettingsUi.Switch(Ui.CreatePullRequestsAutomatically, on =>
            {
                Ui.CreatePullRequestsAutomatically = on;
                Save();
                if (draftRow is not null)
                {
                    draftRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                }
            })));

        draftRow = SettingsRows.Row(
            "Create as draft",
            "Open auto-created pull requests as drafts instead of ready for review.",
            SettingsUi.Switch(Ui.CreatePullRequestsAsDraft, on =>
            {
                Ui.CreatePullRequestsAsDraft = on;
                Save();
            }),
            indent: true);
        draftRow.Visibility = Ui.CreatePullRequestsAutomatically ? Visibility.Visible : Visibility.Collapsed;
        section.Children.Add(draftRow);

        section.Children.Add(SettingsRows.Row(
            "Auto-archive after PR merge or close",
            "Automatically archive desktop sessions when the associated pull request is merged or closed. Archiving also removes the session’s worktree and deletes its branch once the pull request is merged. Work that isn’t pushed is kept.",
            SettingsUi.Switch(Ui.AutoArchiveOnPrClose, on =>
            {
                Ui.AutoArchiveOnPrClose = on;
                Save();
            })));

        return section;
    }

    // ---- Plugins (the reference's Ca) ----

    private FrameworkElement BuildPlugins()
    {
        var section = SettingsRows.Section(null);
        section.Children.Add(SettingsRows.RowWithProse(
            "Plugins",
            SettingsProse.DescriptionWithTrailingLink(
                "Manage Jarvis Code plugins, agents, hooks, and connectors.",
                "Learn more",
                "https://code.claude.com/docs/en/discover-plugins"),
            SettingsRows.SecondaryButton("Manage", openPlugins)));
        return section;
    }
}
