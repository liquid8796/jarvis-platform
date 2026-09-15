using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.Theming;
using ThemeMode = JarvisCode.App.Theming.ThemeMode;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// Settings › General — the reference desktop's own General page (ion-dist
/// <c>c0db37792-CMcO5owL.js</c>) in its order: Profile (<c>itPgxdbBzC</c>),
/// Appearance (<c>2GURQYNPp3</c>) and Notifications (<c>NAidKbB0vi</c>), then the
/// engine defaults this build keeps on the same page.
///
/// The reference's Profile rows and its notification switches are stored on the
/// claude.ai account and applied server-side — its <c>conversation_preferences</c>
/// reaches a turn as the request field <c>include_conversation_preferences</c>, never
/// as a prompt block the client writes — so this build stores the same values in
/// <see cref="UiSettings"/> and the Chat prompt is what reads them.
/// </summary>
internal sealed class GeneralPage(AppServices services)
{
    public const string PageTitle = "General";

    /// <summary>
    /// The reference's work-function options, in its own order — the value list in
    /// <c>shared-15</c> (its <c>Dn</c>) paired with the labels in
    /// <c>c9c7c6781-CBLFdW29.js</c>, where the stored value is the title-cased key
    /// and the label is its sentence-cased message.
    /// </summary>
    public static readonly IReadOnlyList<(string Value, string Label)> WorkFunctions =
    [
        ("Product Management", "Product management"),
        ("Engineering", "Engineering"),
        ("Human Resources", "Human resources"),
        ("Finance", "Finance"),
        ("Marketing", "Marketing"),
        ("Sales", "Sales"),
        ("Operations", "Operations"),
        ("Data Science", "Data science"),
        ("Design", "Design"),
        ("Legal", "Legal"),
        ("Scientist", "Scientist"),
        ("Student", "Student"),
        ("Founder", "Founder"),
        ("Healthcare", "Healthcare"),
        ("Writer", "Writer"),
        ("Educator", "Educator"),
        ("Consultant", "Consultant"),
        ("Researcher", "Researcher"),
        ("Software Engineer", "Software engineer"),
        ("Other", "Other"),
    ];

    /// <summary>The four hints the reference cycles through the empty instructions box.</summary>
    public static readonly IReadOnlyList<string> InstructionHints =
    [
        "e.g. keep explanations brief and to the point",
        "e.g. when learning new concepts, I find analogies particularly helpful",
        "e.g. ask clarifying questions before giving detailed answers",
        "e.g. I primarily code in Python (not a coding beginner)",
    ];

    private UiSettings Ui => services.UiSettings.Current;

    private void Save() => services.UiSettings.Save();

    public FrameworkElement Build()
    {
        var page = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        page.Children.Add(BuildProfile());
        page.Children.Add(BuildAppearance());
        page.Children.Add(BuildNotifications());
        page.Children.Add(BuildEngineDefaults());
        return page;
    }

    // ---- Profile (the reference's ba) ----

    private FrameworkElement BuildProfile()
    {
        var section = SettingsRows.Section("Profile");

        var avatar = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
        };
        var initials = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        initials.SetResourceReference(TextBlock.ForegroundProperty, "Oncolor100Brush");
        avatar.Child = initials;
        avatar.SetResourceReference(Border.BackgroundProperty, "AccentMain000Brush");
        System.Windows.Automation.AutomationProperties.SetName(avatar, "Avatar");

        void PaintAvatar()
        {
            var name = Ui.FullName.Length > 0 ? Ui.FullName : Ui.UserDisplayName;
            initials.Text = Avatars.Initials(name);
            avatar.Background = Avatars.Brush(Ui.AvatarVariant, avatar.Background);
        }

        var avatarButtons = new StackPanel { Orientation = Orientation.Horizontal };
        avatarButtons.Children.Add(SettingsRows.SecondaryButton("Randomize avatar", () =>
        {
            Ui.AvatarVariant = Avatars.Randomize(Ui.AvatarVariant);
            Save();
            PaintAvatar();
        }));
        var clearAvatar = SettingsRows.SecondaryButton("Clear avatar", () =>
        {
            Ui.AvatarVariant = 0;
            Save();
            PaintAvatar();
        });
        clearAvatar.Margin = new Thickness(8, 0, 0, 0);
        avatarButtons.Children.Add(clearAvatar);

        var avatarRow = new StackPanel { Orientation = Orientation.Horizontal };
        avatarRow.Children.Add(avatar);
        avatarButtons.Margin = new Thickness(16, 0, 0, 0);
        avatarButtons.VerticalAlignment = VerticalAlignment.Center;
        avatarRow.Children.Add(avatarButtons);
        PaintAvatar();
        section.Children.Add(SettingsRows.Block(avatarRow));

        var fullName = SettingsRows.Input(Ui.FullName, accessibleName: "Full name");
        SettingsUi.AutoSave(fullName, (text, _) =>
        {
            Ui.FullName = text.Trim();
            Save();
            PaintAvatar();
        });
        section.Children.Add(SettingsRows.Row("Full name", null, fullName));

        var callMe = SettingsRows.Input(Ui.UserDisplayName, accessibleName: "What should Jarvis call you?");
        SettingsUi.AutoSave(callMe, (text, _) =>
        {
            Ui.UserDisplayName = text.Trim();
            Save();
            PaintAvatar();
        });
        section.Children.Add(SettingsRows.Row("What should Jarvis call you?", null, callMe));

        section.Children.Add(SettingsRows.Row(
            "What best describes your work?",
            null,
            SettingsRows.Select(
                [("", "Select", null), .. WorkFunctions.Select(w => (w.Value, w.Label, (string?)null))],
                Ui.WorkFunction,
                value => { Ui.WorkFunction = value; Save(); },
                width: 200,
                accessibleName: "What best describes your work?")));

        var instructions = new TextBox
        {
            Text = Ui.AssistantInstructions,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 88,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        instructions.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        System.Windows.Automation.AutomationProperties.SetName(instructions, "Instructions for Jarvis");
        Controls.PlaceholderText.SetText(instructions, InstructionHints[0]);
        var hintIndex = 0;
        var hintTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        hintTimer.Tick += (_, _) =>
        {
            hintIndex = (hintIndex + 1) % InstructionHints.Count;
            Controls.PlaceholderText.SetText(instructions, InstructionHints[hintIndex]);
        };
        hintTimer.Start();
        instructions.Unloaded += (_, _) => hintTimer.Stop();
        SettingsUi.AutoSave(instructions, (text, _) => { Ui.AssistantInstructions = text; Save(); });

        var instructionsHost = new Grid();
        instructionsHost.Children.Add(instructions);
        section.Children.Add(SettingsRows.Row(
            "Instructions for Jarvis",
            "Jarvis will keep these in mind across chats on this computer.",
            null,
            below: instructionsHost));

        return section;
    }

    // ---- Appearance (the reference's Ma) ----

    private FrameworkElement BuildAppearance()
    {
        var section = SettingsRows.Section("Appearance");

        section.Children.Add(SettingsRows.Row(
            "Appearance",
            null,
            SettingsRows.Segmented(
                [("auto", "System"), ("light", "Light"), ("dark", "Dark")],
                Ui.ThemeMode switch { ThemeMode.Light => "light", ThemeMode.Dark => "dark", _ => "auto" },
                value =>
                {
                    Ui.ThemeMode = value switch
                    {
                        "light" => ThemeMode.Light,
                        "dark" => ThemeMode.Dark,
                        _ => ThemeMode.System,
                    };
                    Save();
                    services.Theme.Apply(Ui.ActiveTheme, Ui.ThemeMode);
                },
                accessibleName: "Appearance")));

        section.Children.Add(SettingsRows.Row(
            "Chat font",
            null,
            SettingsRows.Select(
                [.. ChatFonts.Options.Select(o => (o.Value, o.Label, (string?)null))],
                ChatFonts.Resolve(Ui.ChatFont),
                value =>
                {
                    Ui.ChatFont = value;
                    Save();
                    App.ApplyChatFont(value);
                },
                width: 200,
                accessibleName: "Chat font")));

        section.Children.Add(SettingsRows.Row(
            "Motion",
            "Reduce animation in streaming responses and other interface elements.",
            SettingsRows.Segmented(
                [("system", "System"), ("reduce", "Reduced")],
                Ui.ReduceMotion ? "reduce" : "system",
                value => { Ui.ReduceMotion = value == "reduce"; Save(); },
                accessibleName: "Motion")));

        section.Children.Add(SettingsRows.Row(
            "New chat view",
            "Start new chats in the side-by-side model comparison view.",
            SettingsUi.Switch(Ui.NewChatComparisonView, on => { Ui.NewChatComparisonView = on; Save(); })));

        return section;
    }

    // ---- Notifications (the reference's Pa) ----

    private FrameworkElement BuildNotifications()
    {
        var section = SettingsRows.Section("Notifications");

        section.Children.Add(SettingsRows.Row(
            "Response completions",
            "Get notified when Jarvis has finished a response. Useful for long-running tasks.",
            SettingsUi.Switch(Ui.NotifyWhenDone, on => { Ui.NotifyWhenDone = on; Save(); })));

        section.Children.Add(SettingsRows.Row(
            "Code notifications",
            "Jarvis can choose to notify you about important updates from a Code session.",
            SettingsUi.Switch(Ui.CodeNotifications, on => { Ui.CodeNotifications = on; Save(); })));

        section.Children.Add(SettingsRows.Row(
            "Code permission requests",
            "Get a notification when Jarvis needs your approval to run a command in a Code session.",
            SettingsUi.Switch(Ui.CodePermissionRequestNotifications, on =>
            {
                Ui.CodePermissionRequestNotifications = on;
                Save();
            })));

        return section;
    }

    // ---- Engine defaults (this build's own) ----

    private FrameworkElement BuildEngineDefaults()
    {
        var settings = services.Settings.Current;
        var section = SettingsRows.Section("Engine defaults");

        var modelNames = services.Settings.Models.Select(static m => m.DisplayName).ToList();
        var currentModel = JarvisCode.Core.Settings.ModelCatalog
            .Find(services.Settings.Models, settings.DefaultModelId)?.DisplayName ?? "";
        section.Children.Add(SettingsRows.Row("Default model", "New conversations start with this model.",
            SettingsUi.Combo(modelNames, currentModel, name =>
            {
                var model = services.Settings.Models.FirstOrDefault(m => m.DisplayName == name);
                if (model is not null)
                {
                    settings.DefaultModelId = model.ModelId;
                    services.Settings.Save();
                }
            }, width: 220)));

        section.Children.Add(SettingsRows.Row("Effort", "How much the model thinks before answering.",
            SettingsUi.Combo(EffortLevels.Names, EffortLevels.Resolve(settings.ThinkingEffortName),
                value =>
                {
                    settings.ThinkingEffortName = value;
                    services.Settings.Save();
                }, width: 140)));

        // Anthropic's own models are decided by the reference's family rule and
        // never by this row; it answers only for a model that rule has never
        // seen, which is every model on another provider.
        section.Children.Add(SettingsRows.Row(
            "Prompt for other providers",
            "Which system prompt a model outside Anthropic's catalog receives. " +
            "Classic spells out how to work; Lean is the shorter harness prompt for models tuned to it. " +
            "Anthropic models are unaffected.",
            SettingsUi.Combo(
                [PromptFormNames.Classic, PromptFormNames.Lean],
                PromptFormPreference.IsLean(settings.OtherProviderPromptForm)
                    ? PromptFormNames.Lean
                    : PromptFormNames.Classic,
                value =>
                {
                    settings.OtherProviderPromptForm = value == PromptFormNames.Lean
                        ? PromptFormPreference.Lean
                        : PromptFormPreference.Classic;
                    services.Settings.Save();
                }, width: 140)));

        // The reference resolves its two post-tool-result reminders through an
        // environment variable, then a client_data map its config endpoint
        // serves per account, then the model's own text. There is no such
        // endpoint here, so this pair says which of the tiers this build carries.
        var overrideRows = new StackPanel
        {
            Visibility = settings.ReminderOverridesEnabled ? Visibility.Visible : Visibility.Collapsed,
        };

        section.Children.Add(SettingsRows.Row(
            "Reminder text overrides",
            "Lets the two reminders Jarvis adds after a batch of tool calls take their text from outside the model. " +
            "Off leaves whatever text the model itself carries.",
            SettingsUi.Switch(settings.ReminderOverridesEnabled, on =>
            {
                settings.ReminderOverridesEnabled = on;
                services.Settings.Save();
                overrideRows.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            })));

        var fileRow = new StackPanel
        {
            Visibility = ReminderOverrides.UsesFile(settings) ? Visibility.Visible : Visibility.Collapsed,
        };

        overrideRows.Children.Add(SettingsRows.Row(
            "Override source",
            "Environment variables alone is the default. Adding a local file lets you set the text per model, " +
            "standing in for the per-account config this build has no endpoint to fetch.",
            SettingsUi.Combo(
                [ReminderSourceNames.Environment, ReminderSourceNames.File],
                ReminderOverrides.UsesFile(settings)
                    ? ReminderSourceNames.File
                    : ReminderSourceNames.Environment,
                value =>
                {
                    settings.ReminderOverrideSource = value == ReminderSourceNames.File
                        ? ReminderOverrides.FileSource
                        : ReminderOverrides.EnvironmentSource;
                    services.Settings.Save();
                    fileRow.Visibility = value == ReminderSourceNames.File
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }, width: 260)));

        fileRow.Children.Add(SettingsRows.Row(
            "Override file",
            "A JSON object of model patterns to reminder text. Show file writes a commented starter " +
            "when there is none yet.",
            RevealOverrideFileButton()));

        overrideRows.Children.Add(fileRow);
        section.Children.Add(overrideRows);

        section.Children.Add(SettingsRows.Row(
            "Web search",
            "Search through the endpoint below; off falls back to Anthropic's server-side search where the provider has it.",
            SettingsUi.Switch(settings.EnableWebSearch, on =>
            {
                settings.EnableWebSearch = on;
                services.Settings.Save();
            })));

        var searchUrlBox = SettingsUi.Input(settings.SearxngBaseUrl, width: 280);
        searchUrlBox.LostFocus += (_, _) =>
        {
            var url = searchUrlBox.Text.Trim();
            if (url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                settings.SearxngBaseUrl = url;
                services.Settings.Save();
            }
            else
            {
                searchUrlBox.Text = settings.SearxngBaseUrl;
            }
        };
        section.Children.Add(SettingsRows.Row(
            "Search endpoint",
            "SearXNG instance for the WebSearch tool; it must have the json format enabled.",
            searchUrlBox));

        var timeoutBox = SettingsUi.Input(settings.ShellTimeoutSeconds.ToString(), width: 90);
        timeoutBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(timeoutBox.Text, out var seconds) && seconds is >= 5 and <= 3600)
            {
                settings.ShellTimeoutSeconds = seconds;
                services.Settings.Save();
            }
            else
            {
                timeoutBox.Text = settings.ShellTimeoutSeconds.ToString();
            }
        };
        section.Children.Add(SettingsRows.Row(
            "Shell timeout (seconds)",
            "Commands are cancelled after this long.",
            timeoutBox));

        return section;
    }

    /// <summary>
    /// Reveals the override file, writing a starter when there is none. Without
    /// it the file source is a dead end: the path is inside the profile and the
    /// shape is the reference's, so neither is guessable.
    /// </summary>
    private static Button RevealOverrideFileButton() =>
        SettingsRows.SecondaryButton("Show file", () =>
        {
            var path = ReminderOverrides.FilePath;
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    System.IO.File.WriteAllText(path, ReminderOverrides.StarterFile);
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    // /select, takes the path as one argument; quoting keeps spaces.
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                ToastQueue.Current?.AddDanger($"Couldn't open {path}.");
            }
        });
}

/// <summary>The four chat fonts the reference's "Chat font" listbox offers, by its own labels.</summary>
internal static class ChatFonts
{
    public static readonly IReadOnlyList<(string Value, string Label)> Options =
    [
        ("default", "Anthropic Serif"),
        ("sans", "Anthropic Sans"),
        ("system", "System"),
        ("dyslexia", "Dyslexic friendly"),
    ];

    public static string Resolve(string? stored) =>
        Options.Any(o => o.Value == stored) ? stored! : "default";
}

/// <summary>
/// The profile avatar: initials over one of the tints the reference's randomizer
/// cycles. The reference stores a generated image on the account; with no account
/// here the variant is a local index and 0 keeps the app's accent.
/// </summary>
internal static class Avatars
{
    private static readonly string[] Tints =
        ["#C15F3C", "#6A8D73", "#4A6FA5", "#8A6BAF", "#B08968", "#3F7C85", "#A15C7B", "#7A8450"];

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "J",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    public static int Randomize(int current)
    {
        var next = Random.Shared.Next(1, Tints.Length + 1);
        return next == current ? next % Tints.Length + 1 : next;
    }

    public static System.Windows.Media.Brush Brush(int variant, System.Windows.Media.Brush fallback) =>
        variant >= 1 && variant <= Tints.Length && CssColor.TryParse(Tints[variant - 1], out var color)
            ? new System.Windows.Media.SolidColorBrush(color)
            : fallback;
}

/// <summary>
/// The two prompt forms as the picker spells them, apart from the values
/// <see cref="JarvisCode.App.Services.PromptFormPreference"/> stores.
/// </summary>
internal static class PromptFormNames
{
    public const string Classic = "Classic";
    public const string Lean = "Lean";
}

/// <summary>
/// The two override sources as the picker spells them, apart from the values
/// <see cref="JarvisCode.App.Services.ReminderOverrides"/> stores.
/// </summary>
internal static class ReminderSourceNames
{
    public const string Environment = "Environment variables";
    public const string File = "Environment variables and a local file";
}
