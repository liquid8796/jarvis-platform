using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.Theming;
using JarvisCode.Core.Sessions;
using JarvisCode.Core.Settings;
using ThemeMode = JarvisCode.App.Theming.ThemeMode;

namespace JarvisCode.App.Views.Settings;

/// <summary>Builds the content of each settings panel against live services.</summary>
public sealed class SettingsPanels(AppServices services, Action openThemePicker)
{
    // ---- Settings / General ----

    public FrameworkElement BuildGeneral() => new GeneralPage(services).Build();

    /// <summary>Settings › Jarvis Code — the reference's Claude Code page.</summary>
    public FrameworkElement BuildJarvisCode(Action openPlugins) => new ClaudeCodePage(services, openPlugins).Build();

    /// <summary>Settings › Import &amp; export.</summary>
    public FrameworkElement BuildImportExport() => new ImportExportPage(services).Build();

    /// <summary>Settings › Desktop app › Extensions.</summary>
    public FrameworkElement BuildExtensions() => new ExtensionsPage(services).Build();

    // ---- Settings / Providers ----

    /// <summary>
    /// One card per provider: everything registered in this build, then the ones the user
    /// added, then anything settings still mention. The rows come from
    /// <see cref="ProviderCatalog"/>, so this method never learns a provider's name —
    /// adding one is a registration plus a presentation entry.
    /// </summary>
    public FrameworkElement BuildProviders()
    {
        var panel = NewPanel(
            "Providers",
            "Where models come from. Keys are encrypted with Windows DPAPI and never shown again.");

        var cards = new List<ProviderCard>();
        var list = new StackPanel();

        var noMatches = SettingsUi.Caption("");
        noMatches.Visibility = Visibility.Collapsed;

        var search = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        search.SetResourceReference(FrameworkElement.StyleProperty, "SettingsSearchBox");
        System.Windows.Automation.AutomationProperties.SetName(search, "Search providers");

        void ApplySearch()
        {
            var matches = 0;
            foreach (var card in cards)
            {
                var visible = ProviderCatalog.Matches(card.Entry, search.Text);
                card.Root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                matches += visible ? 1 : 0;
            }

            noMatches.Text =
                $"No provider matches “{search.Text.Trim()}”. Clear the search to see all {cards.Count}.";
            noMatches.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Adding or removing a hand-written provider changes which cards exist, so the list
        // is rebuilt rather than patched — the registry has already been refreshed by the
        // save that preceded it.
        void Rebuild()
        {
            cards.Clear();
            list.Children.Clear();
            var entries = ProviderCatalog.Build(
                services.Providers.All.Select(ProviderRuntime.From),
                services.Settings.Current,
                services.Settings.Models);

            foreach (var entry in entries)
            {
                // One card open at a time: the page stays a list you can scan rather than a
                // stack of open editors.
                var card = new ProviderCard(entry, services, expanded =>
                {
                    foreach (var other in cards.Where(c => !ReferenceEquals(c, expanded)))
                    {
                        other.SetExpanded(false);
                    }
                }, Rebuild);
                cards.Add(card);
                list.Children.Add(card.Root);
            }

            ApplySearch();
        }

        search.TextChanged += (_, _) => ApplySearch();
        Rebuild();

        panel.Children.Add(search);
        panel.Children.Add(list);
        panel.Children.Add(noMatches);
        panel.Children.Add(BuildAddCustomProvider(Rebuild));

        // The reference keeps the whole third-party inference profile — named
        // configurations, model discovery, credential helpers and the firewall
        // allowlist — in a window of its own; this is the button that opens it.
        panel.Children.Add(SettingsRows.Row(
            "Third-party inference",
            "Named configurations of the endpoints above, with model discovery, credential helpers and the firewall allowlist.",
            SettingsRows.SecondaryButton("Configure…", () =>
                new InferenceConfigWindow(services).Show(Application.Current.MainWindow))));
        return panel;
    }

    /// <summary>
    /// The form that defines a provider by hand: an id to store things against, a name, the
    /// endpoint and which of the two wires it answers on. Everything after that — keys,
    /// models, the connection test — is the ordinary provider card, because a custom
    /// provider is a real registration and not a special case.
    /// </summary>
    private FrameworkElement BuildAddCustomProvider(Action onAdded)
    {
        var host = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        host.Children.Add(SettingsUi.SectionHeader("Add your own"));
        host.Children.Add(SettingsUi.Caption(
            "Any endpoint that speaks one of the two protocols this app already talks. The id is "
            + "what keys, models and sessions are stored against, so it cannot be changed later."));

        var idBox = SettingsUi.Input("", width: 150);
        System.Windows.Automation.AutomationProperties.SetName(idBox, "New provider id");
        idBox.ToolTip = "e.g. my-relay";
        var nameBox = SettingsUi.Input("", width: 170);
        System.Windows.Automation.AutomationProperties.SetName(nameBox, "New provider name");
        nameBox.ToolTip = "e.g. My relay";
        var urlBox = SettingsUi.Input("", width: 260);
        System.Windows.Automation.AutomationProperties.SetName(urlBox, "New provider base URL");
        urlBox.ToolTip = "e.g. https://api.example.com/v1";

        var protocol = CustomProviderProtocols.OpenAi;
        var protocolBox = SettingsUi.Combo(
            CustomProviderProtocols.All, protocol, value => protocol = value, width: 130);
        System.Windows.Automation.AutomationProperties.SetName(protocolBox, "New provider protocol");

        var needsKey = true;
        var needsKeyBox = SettingsUi.Switch(needsKey, value => needsKey = value);
        System.Windows.Automation.AutomationProperties.SetName(needsKeyBox, "New provider needs an API key");

        var feedback = SettingsUi.Caption("");

        var add = SettingsUi.GhostButton("Add provider", () =>
        {
            var settings = services.Settings.Current;
            var taken = services.Providers.All.Select(p => p.Id)
                .Concat(settings.CustomProviders.Select(spec => spec.Id));

            if (CustomProviders.DescribeIdProblem(idBox.Text, taken) is { } idProblem)
            {
                feedback.Text = idProblem;
                return;
            }

            if (CustomProviders.DescribeBaseUrlProblem(urlBox.Text) is { } urlProblem)
            {
                feedback.Text = urlProblem;
                return;
            }

            var spec = CustomProviders.Normalize(new CustomProviderSpec
            {
                Id = idBox.Text,
                DisplayName = nameBox.Text,
                BaseUrl = urlBox.Text,
                ProtocolName = protocol,
                RequiresApiKey = needsKey,
            });
            settings.CustomProviders.Add(spec);
            services.Settings.Save();

            idBox.Text = "";
            nameBox.Text = "";
            urlBox.Text = "";
            feedback.Text = $"Added {spec.DisplayName}. Open its card to add the key and its model ids.";
            onAdded();
        });

        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (element, hint) in new (FrameworkElement Element, string Hint)[]
                 {
                     (idBox, "id"),
                     (nameBox, "name"),
                     (urlBox, "base URL"),
                     (protocolBox, "protocol"),
                     (needsKeyBox, "needs a key"),
                     (add, ""),
                 })
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 6) };
            cell.Children.Add(element);
            if (hint.Length > 0)
            {
                cell.Children.Add(SettingsUi.Caption(hint));
            }

            row.Children.Add(cell);
        }

        host.Children.Add(row);
        host.Children.Add(feedback);
        return host;
    }

    // ---- Settings / Permissions ----

    public FrameworkElement BuildPermissions()
    {
        var settings = services.Settings.Current;
        var panel = NewPanel("Permissions", "What Jarvis may do without asking. Project rules in .jarvis/settings.json are applied first.");

        // "Not set" is the state the reference's own settings file is in until someone
        // writes defaultPermissionMode into it, and it is what lets the mode a session
        // picks in a folder be remembered for that folder — a configured default sits
        // above the folder in the layering and would otherwise always win.
        const string NoDefaultMode = "Not set";
        var configuredMode = Enum.TryParse<JarvisCode.Core.Permissions.PermissionMode>(settings.PermissionModeName, ignoreCase: true, out var parsed)
            ? parsed.ToString()
            : NoDefaultMode;
        panel.Children.Add(SettingsUi.Row("Default mode", "The mode new sessions start in. Not set remembers the last mode picked in each folder.",
            SettingsUi.Combo([NoDefaultMode, "Auto", "Manual", "AcceptEdits", "Plan", "Bypass"], configuredMode, value =>
            {
                settings.PermissionModeName = value == NoDefaultMode ? "" : value;
                services.Settings.Save();
            }, width: 150)));

        panel.Children.Add(SettingsUi.SectionHeader("Global rules"));
        var rulesBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, settings.PermissionRuleLines),
            AcceptsReturn = true,
            MinHeight = 120,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        rulesBox.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        rulesBox.FontFamily = (FontFamily)Application.Current.Resources["MonoFontFamily"];
        rulesBox.LostFocus += (_, _) =>
        {
            settings.PermissionRuleLines = [.. rulesBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            services.Settings.Save();
        };
        panel.Children.Add(rulesBox);
        panel.Children.Add(SettingsUi.Caption(
            "One rule per line: \"allow <tool> [pattern]\" or \"deny <tool> [pattern]\". " +
            "Examples: allow PowerShell git *   ·   deny WebFetch *   ·   allow Read. First match wins."));

        return panel;
    }

    // ---- Settings / Usage ----

    public FrameworkElement BuildUsage() => new UsagePage(services).Build();

    // ---- Extra / Themes ----

    public FrameworkElement BuildThemes()
    {
        var ui = services.UiSettings.Current;
        var panel = NewPanel("Themes", "Appearance of the whole app. The gallery has 97 palettes — open it with Ctrl+Shift+T.");

        panel.Children.Add(SettingsUi.Row("Mode", "Follow Windows, or force light/dark.",
            SettingsUi.Combo(["System", "Light", "Dark"], ui.ThemeMode.ToString(), value =>
            {
                ui.ThemeMode = Enum.Parse<ThemeMode>(value);
                services.UiSettings.Save();
                services.Theme.Apply(ui.ActiveTheme, ui.ThemeMode);
            }, width: 130)));

        var current = new TextBlock
        {
            Text = services.Theme.ActiveTheme.Key.Length == 0 ? "Default" : services.Theme.ActiveTheme.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        current.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        var open = SettingsUi.PrimaryButton("Open theme gallery", openThemePicker);
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal };
        themeRow.Children.Add(current);
        themeRow.Children.Add(open);
        panel.Children.Add(SettingsUi.Row("Theme", "Applies live to every window.", themeRow));

        panel.Children.Add(SettingsUi.Row("Transcript text size",
            "Size of the text in a Code session. Headings, code and spacing follow it.",
            SettingsUi.Combo(["Small", "Medium", "Large"],
                char.ToUpperInvariant(ui.TranscriptTextSize[0]) + ui.TranscriptTextSize[1..],
                value =>
                {
                    ui.TranscriptTextSize = value.ToLowerInvariant();
                    services.UiSettings.Save();
                    Controls.MarkdownView.TranscriptTextSize =
                        Controls.TranscriptTextSizes.FromName(ui.TranscriptTextSize);
                }, width: 130)));

        panel.Children.Add(SettingsUi.Row("Chat font", "Font used for conversation text.",
            SettingsUi.Combo(["default", "sans", "system", "dyslexia"], ui.ChatFont, value =>
            {
                ui.ChatFont = value;
                services.UiSettings.Save();
                App.ApplyChatFont(value);
            }, width: 130)));

        return panel;
    }

    // ---- Extra / Features ----

    public FrameworkElement BuildFeatures()
    {
        var ui = services.UiSettings.Current;
        var panel = NewPanel("Features", "Optional features layered on top of the core app — each one applies live.");

        panel.Children.Add(SettingsUi.SectionHeader("Desktop control"));
        panel.Children.Add(SettingsUi.Row("Computer use",
            "Gives the model a screenshot tool for your displays and a mouse/keyboard tool to act on what it sees. Every click and keystroke still asks for permission first.",
            SettingsUi.Switch(ui.ComputerUseEnabled, on => { ui.ComputerUseEnabled = on; services.UiSettings.Save(); })));
        panel.Children.Add(SettingsUi.Row("Computer use in Chat",
            "Offers those two tools on the Chat surface as well, which otherwise has no tools at all. Chat still gets no file, shell or git access.",
            SettingsUi.Switch(ui.ComputerUseInChat, on => { ui.ComputerUseInChat = on; services.UiSettings.Save(); })));
        panel.Children.Add(SettingsUi.Row("Require app grants",
            "Refuses clicks and typing while the foreground application has no grant. Jarvis asks for grants through request_access; granted apps persist here in ui-settings.",
            SettingsUi.Switch(ui.ComputerUseRequireGrants, on => { ui.ComputerUseRequireGrants = on; services.UiSettings.Save(); })));
        panel.Children.Add(SettingsUi.Row("Android Emulator tools",
            "Lets Jarvis drive a running Android emulator over adb: screenshots, taps and typing, logcat, apk installs. Emulators only — physical devices are never touched. Needs the Android SDK's adb on this machine.",
            SettingsUi.Switch(ui.AndroidToolsEnabled, on => { ui.AndroidToolsEnabled = on; services.UiSettings.Save(); })));

        panel.Children.Add(SettingsUi.SectionHeader("Orchestration"));
        panel.Children.Add(SettingsUi.Row("Dynamic workflows",
            "Offers the workflow tool: Jarvis writes a deterministic JavaScript script that orchestrates many subagents — loops, fan-out and conditionals decided by code rather than by the model. Off withholds the tool.",
            SettingsUi.Switch(ui.DynamicWorkflowsEnabled, on =>
            {
                ui.DynamicWorkflowsEnabled = on;
                services.UiSettings.Save();
            })));
        panel.Children.Add(SettingsUi.Row("Dynamic workflow size",
            "Advisory scale for the workflows Jarvis writes: small aims for fewer than 5 agents, medium fewer than 15, large fewer than 50. Unrestricted sends no guideline. It is a guideline, not an enforced limit.",
            SettingsUi.Combo(["default (medium)", "unrestricted", "small", "medium", "large"],
                string.IsNullOrEmpty(ui.WorkflowSize) ? "default (medium)" : ui.WorkflowSize,
                value =>
                {
                    ui.WorkflowSize = value == "default (medium)" ? "" : value;
                    services.UiSettings.Save();
                })));
        panel.Children.Add(SettingsUi.Row("Teammate mode",
            "How a named agent runs. In-process is the only backend on this machine; tmux and iTerm2 need tmux (WSL on Windows) or macOS, and fall back to in-process with a note.",
            SettingsUi.Combo(["auto", "in-process", "tmux", "iterm2"], ui.TeammateMode, value =>
            {
                ui.TeammateMode = value;
                services.UiSettings.Save();
            })));

        panel.Children.Add(SettingsUi.SectionHeader("Instruction files"));
        var appSettings = services.Settings.Current;
        var excludeBox = SettingsUi.Input(string.Join(", ", appSettings.InstructionFileExcludes), width: 280);
        excludeBox.LostFocus += (_, _) =>
        {
            appSettings.InstructionFileExcludes =
            [
                .. excludeBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ];
            services.Settings.Save();
        };
        panel.Children.Add(SettingsUi.Row("Never load",
            "Comma-separated globs or absolute paths of instruction files to skip, matched against the whole path (e.g. **/vendor/JARVIS.md). Organization policy files are always loaded.",
            excludeBox));

        panel.Children.Add(SettingsUi.SectionHeader("Shortcuts"));
        panel.Children.Add(SettingsUi.Row("Theme picker",
            "Ctrl+Shift+T opens the theme gallery from anywhere in the app.",
            SettingsUi.Switch(ui.ThemePickerHotkeyEnabled, on => { ui.ThemePickerHotkeyEnabled = on; services.UiSettings.Save(); })));
        panel.Children.Add(SettingsUi.Row("Quick Entry",
            "Ctrl+Alt+Space opens a small prompt window on the monitor with your cursor, even while the app is in the tray.",
            SettingsUi.Switch(ui.QuickEntryEnabled, on => { ui.QuickEntryEnabled = on; services.UiSettings.Save(); })));

        panel.Children.Add(SettingsUi.SectionHeader("Motion"));
        panel.Children.Add(SettingsUi.Row("Reduce motion",
            "Stops decorative animation (spinners still indicate activity).",
            SettingsUi.Switch(ui.ReduceMotion, on => { ui.ReduceMotion = on; services.UiSettings.Save(); })));

        return panel;
    }

    // ---- Extra / Fingerprints ----

    public FrameworkElement BuildFingerprints()
    {
        var ui = services.UiSettings.Current;
        var panel = NewPanel(
            "Fingerprints",
            "Identity the reference CLI attaches to every request. Jarvis needs none of it to work, " +
            "so all of it is off; switch on only what something you run actually reads.");

        panel.Children.Add(SettingsUi.SectionHeader("What rides each request"));

        panel.Children.Add(SettingsUi.Row("Attribution block",
            "Leads the system prompt with the reference's x-anthropic-billing-header line: which client " +
            "sent this, and three hex digits fingerprinting the conversation's first message. Stable for " +
            "the whole session, so it does not disturb the prompt cache.",
            SettingsUi.Switch(ui.SendAttributionBlock, on =>
            {
                ui.SendAttributionBlock = on;
                services.UiSettings.Save();
            })));

        panel.Children.Add(SettingsUi.Row("Request metadata",
            "Adds metadata.user_id — an id for this install and this session, in the reference's shape. " +
            "The install id is generated here on first use; nothing is read from the machine or from " +
            "another client.",
            SettingsUi.Switch(ui.SendRequestMetadata, on =>
            {
                ui.SendRequestMetadata = on;
                services.UiSettings.Save();
            })));

        panel.Children.Add(SettingsUi.Row("Session id header",
            $"Sends {ClientAttribution.SessionIdHeaderName} on Anthropic-wire requests, so a proxy or " +
            "gateway you run can group a conversation's calls. The endpoint learns nothing it could not " +
            "already group by connection.",
            SettingsUi.Switch(ui.SendSessionIdHeader, on =>
            {
                ui.SendSessionIdHeader = on;
                services.UiSettings.Save();
            })));

        panel.Children.Add(SettingsUi.Caption(
            $"Sent as {ClientAttribution.Product}/{ClientAttribution.Version}, never as another client's " +
            "name or version. The reference's remaining fields — its account id, its previous-request and " +
            "prompt ids — identify a first-party session that does not exist here, and are not sent at all."));

        return panel;
    }

    // ---- Desktop app / General ----

    public FrameworkElement BuildDesktop(Action<string> openPage) => new DesktopPage(services, openPage).Build();

    // ---- Desktop app / Developer ----

    public FrameworkElement BuildDeveloper() => new DeveloperPage(services).Build();

    // ---- Desktop app / Debug ----

    public FrameworkElement BuildDebug() => new DebugPanel(services.ModelTraffic);

    // ---- helpers ----

    private static StackPanel NewPanel(string title, string subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(SettingsUi.PanelTitle(title));
        panel.Children.Add(SettingsUi.PanelSubtitle(subtitle));
        return panel;
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString(),
    };

    private static string ShortModel(string? modelId)
        => string.IsNullOrEmpty(modelId) ? "—" : modelId.Length > 22 ? modelId[..22] + "…" : modelId;
}
