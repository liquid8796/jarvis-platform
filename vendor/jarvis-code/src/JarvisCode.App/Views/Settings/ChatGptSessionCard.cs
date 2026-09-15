using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// The credential editor for the one provider that has no API key: import the cookies of a
/// signed-in chatgpt.com tab and talk to that session. The export is a live credential, so it
/// is stored the way keys are (DPAPI) and the page never prints a cookie value back — only
/// what the jar contains.
/// </summary>
internal static class ChatGptSessionCard
{
    /// <summary>Long enough for the browser to start and chatgpt.com to come up.</summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMinutes(3);

    public static IEnumerable<FrameworkElement> Build(
        AppServices services, Action onChanged, Action? onModelsChanged = null)
    {
        var settings = services.Settings.Current;
        var providerId = ChatGptWebProvider.ProviderId;

        yield return SettingsUi.SectionHeader("Browser session");
        yield return SettingsUi.Caption(
            "Export the cookies from a signed-in chatgpt.com tab (Cookie-Editor JSON, a cookies.txt, or a "
            + "\"name=value; …\" line) and paste them here. Messages are sent by typing into the real ChatGPT "
            + "page in an embedded browser, so ChatGPT's own client handles its checks; this app never touches "
            + "them. Cookies are encrypted with DPAPI like a key — but they are a live session, so treat them "
            + "as one. Tools work on the Code surface, carried inside the message itself, and pictures ride "
            + "the composer's attachments. Sessions and agents use separate browser pages, with up to four "
            + "active browsers. Model and thinking choices are read from ChatGPT's live composer, so new "
            + "options appear without an app update. Token counts and the configured context limit are "
            + "estimates, not account usage or billing.");

        var status = SettingsUi.Caption("");
        void RefreshStatus()
        {
            var saved = settings.GetApiKeys(providerId).FirstOrDefault();
            status.Text = saved is null
                ? "No cookies saved."
                : "Saved: " + ChatGptCookieJar.Parse(saved).Describe();
            onChanged();
        }

        RefreshStatus();

        var cookieBox = new TextBox
        {
            AcceptsReturn = true,
            Margin = new Thickness(0, 6, 0, 0),
            MinHeight = 60,
            MaxHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        cookieBox.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        AutomationProperties.SetName(cookieBox, "ChatGPT cookies");

        void SaveCookies(string raw)
        {
            var jar = ChatGptCookieJar.Parse(raw);
            if (jar.Count == 0)
            {
                status.Text = "Nothing recognisable in that text — " + jar.Describe();
                return;
            }

            settings.ApiKeySets[providerId] = [raw.Trim()];
            services.Settings.Save();
            cookieBox.Clear();
            RefreshStatus();
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(SettingsUi.PrimaryButton("Save cookies", () => SaveCookies(cookieBox.Text)));
        buttons.Children.Add(SettingsUi.Spaced(SettingsUi.GhostButton("Import file…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import ChatGPT cookies",
                Filter = "Cookie export (*.json;*.txt)|*.json;*.txt|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                SaveCookies(File.ReadAllText(dialog.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = $"Could not read that file: {ex.Message}";
            }
        })));
        buttons.Children.Add(SettingsUi.Spaced(BuildTestButton(services, status)));
        buttons.Children.Add(SettingsUi.Spaced(BuildDiscoverButton(services, status, onModelsChanged)));
        buttons.Children.Add(SettingsUi.Spaced(SettingsUi.GhostButton("Show ChatGPT window", () =>
        {
            if (!Services.ChatGptWebViewTransport.ShowWindow())
            {
                status.Text = "The ChatGPT browser is not running. Press Test connection, or send a message, and it starts.";
            }
        })));
        buttons.Children.Add(SettingsUi.Spaced(SettingsUi.ConfirmButton("Remove", "Click again to remove", () =>
        {
            settings.ApiKeySets.Remove(providerId);
            services.Settings.Save();
            RefreshStatus();
        })));

        yield return cookieBox;
        yield return buttons;
        yield return status;

        var projectBox = SettingsUi.Input(settings.ChatGptProjectName, width: 240);
        AutomationProperties.SetName(projectBox, "Project");
        SettingsUi.AutoSave(projectBox, (text, final) =>
        {
            var typed = text.Trim();
            if (final && !string.Equals(projectBox.Text, typed, StringComparison.Ordinal))
            {
                projectBox.Text = typed;
            }

            JarvisCode.Core.Utilities.DiagnosticLog.Write(
                $"settings: project box commit (final={final}, typed={typed.Length} chars, "
                + $"stored={settings.ChatGptProjectName.Length} chars)");

            if (string.Equals(typed, settings.ChatGptProjectName, StringComparison.Ordinal))
            {
                JarvisCode.Core.Utilities.DiagnosticLog.Write("settings: project box unchanged; nothing to save");
                return;
            }

            // The remembered id belongs to the old name; keeping it would file chats into the
            // wrong project. Editing the name away and back costs one lookup, not a wrong project.
            settings.ChatGptProjectId = "";
            settings.ChatGptProjectName = typed;
            services.Settings.Save();
        });
        yield return SettingsUi.Row(
            "Project",
            "Every chat is created inside this ChatGPT project. The project is made on first use if "
            + "you do not have one by that name. Leave blank to keep chats outside any project.",
            projectBox);

        var rotateBox = SettingsUi.Input(
            settings.ChatGptRotateAfterMessages.ToString(CultureInfo.InvariantCulture), width: 90);
        AutomationProperties.SetName(rotateBox, "Rotate after");
        SettingsUi.AutoSave(rotateBox, (text, final) =>
        {
            if (ProviderCatalog.ParseRotateAfter(text, final) is not { } value)
            {
                return;
            }

            if (value != settings.ChatGptRotateAfterMessages)
            {
                settings.ChatGptRotateAfterMessages = value;
                services.Settings.Save();
            }

            if (final)
            {
                rotateBox.Text = value.ToString(CultureInfo.InvariantCulture);
            }
        });
        yield return SettingsUi.Row(
            "Rotate after",
            "Messages this app may send into one chat before it deletes that chat and starts a fresh "
            + "one in the same project. Counts what the app sends, not ChatGPT's replies. Minimum 20.",
            rotateBox);
    }

    /// <summary>
    /// Reads the models this account can actually pick and adds them to the card's list.
    ///
    /// There is no endpoint to ask: the provider reads ChatGPT's live composer controls, retaining
    /// both the visible names and the opaque option keys the page uses when it clicks a choice.
    /// </summary>
    private static Button BuildDiscoverButton(AppServices services, TextBlock status, Action? onModelsChanged)
    {
        Button? button = null;
        button = SettingsUi.GhostButton("Read models from ChatGPT", () =>
        {
            button!.IsEnabled = false;
            status.Text = "Reading the model picker — the browser may take a few seconds to start…";
            _ = RunAsync();
        });
        return button;

        async Task RunAsync()
        {
            try
            {
                status.Text = await DiscoverAsync(services, onModelsChanged);
            }
            finally
            {
                button!.IsEnabled = true;
            }
        }
    }

    private static async Task<string> DiscoverAsync(AppServices services, Action? onModelsChanged)
    {
        var settings = services.Settings.Current;
        var jar = ChatGptCookieJar.Parse(settings.GetApiKeys(ChatGptWebProvider.ProviderId).FirstOrDefault());
        if (!jar.IsUsable)
        {
            return $"No usable cookies to read the picker with — {jar.Describe()}";
        }

        ChatGptComposerControls controls;
        try
        {
            using var deadline = new CancellationTokenSource(DiscoveryTimeout);
            var provider = Services.ChatGptComposerDiscovery.Provider(services);
            if (provider is null)
            {
                return "The ChatGPT browser-session provider is not registered.";
            }

            controls = await provider.ReadComposerControlsAsync(
                scopeId: null, modelId: null, includeEfforts: false,
                cancellationToken: deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return "The picker did not answer in time. Use “Show ChatGPT window” to see what it is showing.";
        }
        catch (Exception ex)
        {
            return $"The models could not be read: {ex.Message}";
        }

        if (controls.Models.Count == 0)
        {
            return "The picker offered nothing to read. Use “Show ChatGPT window” to see what it is showing.";
        }

        var merged = Services.ChatGptComposerDiscovery.MergeModels(services, controls);
        if (merged.Changed)
        {
            services.Settings.Save();
            onModelsChanged?.Invoke();
        }

        if (!merged.Changed)
        {
            return $"All {merged.Offered} model(s) from the live picker are already listed.";
        }

        var updated = merged.Updated > 0 ? $" and refreshed {merged.Updated}" : "";
        return $"Added {merged.Added}{updated} of the {merged.Offered} model(s) the live picker offers.";
    }

    /// <summary>
    /// Reads model-picker availability without sending a prompt or claiming authentication. The button is held down
    /// while the browser starts so a second press cannot drive the same page concurrently.
    /// </summary>
    private static Button BuildTestButton(AppServices services, TextBlock status)
    {
        Button? button = null;
        button = SettingsUi.GhostButton("Test connection", () =>
        {
            button!.IsEnabled = false;
            status.Text = "Testing — the browser may take a few seconds to start…";
            _ = RunTestAsync();
        });
        return button;

        async Task RunTestAsync()
        {
            status.Text = await ProviderConnectionCheck.RunAsync(
                services.Providers, ProviderConnectionPlan.ForBrowserSession());
            button!.IsEnabled = true;
        }
    }
}
