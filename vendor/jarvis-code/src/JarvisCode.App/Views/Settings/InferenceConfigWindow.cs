using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// The reference desktop's "Configure third-party inference" window (ion-dist
/// <c>c71860c77-DuPx-LoQ.js</c>), opened from the Providers page. Its shape is the
/// reference's: a 50px header carrying the title (<c>on79ZcGd72</c>), the
/// Configurations picker (<c>B+wjm8G8Vk</c>, with New configuration
/// <c>RCKklDYDtX</c>, Duplicate… <c>uTR9Wyzw/s</c>, Rename… <c>O2GVivD//g</c>,
/// Import configuration… <c>lFh9slwhSO</c>, Show in Explorer <c>ySAk9qhogV</c> and
/// Delete <c>K3r6DQW7h+</c>, the applied one badged <c>q47AaW/siS</c>) and an
/// Export button whose menu carries the Templates group (<c>A3ptulOTB3</c>) and the
/// sensitive-values warning (<c>xgQtG2vu7a</c>); a 200px nav with its own
/// "Search settings" box (<c>kNDaX599qM</c>); the section's own rows; and a footer
/// of Discard Changes (<c>sLnDObsise</c>) / Save Changes (<c>3VI9mt1zm0</c>) /
/// Apply Changes (<c>xJcf9HjLHk</c>) with the Save &amp; Restart confirmation
/// (<c>p2/mqTogRW</c> / <c>gw2uM8JWzu</c> / <c>b5joBI/DR1</c> / <c>uQDRsYlg+S</c>).
///
/// Three of the reference's sections are declared rather than drawn, each because
/// the channel behind it does not exist here: the MDM-profile and bootstrap-URL
/// read-only rows (<c>ZI2sllkKCJ</c>, <c>7xYRPYgjLF</c>) need a managed-settings
/// backend, and its organization-plugins mount folder needs a device-management
/// tool to mount one.
/// </summary>
internal sealed class InferenceConfigWindow(AppServices services)
{
    private const string Title = "Configure third-party inference";

    private readonly List<InferenceConfigEntry> _entries = [];
    private string? _currentId;
    private Window? _window;
    private StackPanel? _sections;
    private TextBlock? _pickerLabel;
    private string _section = "connection";
    private CancellationTokenSource? _work;

    public void Show(Window? owner)
    {
        Reload();
        var root = new DockPanel();

        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(BuildNav());
        _sections = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        var scroll = new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);
        root.Children.Add(body);

        RenderSection();

        _window = new Window
        {
            Title = Title,
            Content = root,
            Width = 900,
            Height = 620,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        // A window that sets no background takes the system window colour, so on a dark
        // theme its themed children — near-white labels, a --bg-000 field fill — land on
        // white: the labels vanish and every input reads as a black block.
        _window.SetResourceReference(Window.BackgroundProperty, "Bg100Brush");
        _window.SetResourceReference(Window.ForegroundProperty, "Text100Brush");
        _window.Closed += (_, _) => _work?.Cancel();
        _window.ShowDialog();
    }

    private void Reload()
    {
        _entries.Clear();
        _entries.AddRange(InferenceConfigurations.List(services.Paths));
        if (_entries.Count == 0)
        {
            var id = InferenceConfigurations.Save(
                services.Paths, null, InferenceConfigurations.UntitledName, services.Settings.Current);
            _entries.AddRange(InferenceConfigurations.List(services.Paths));
            _currentId = id;
        }

        _currentId ??= InferenceConfigurations.AppliedId(services.Paths) ?? _entries[0].Id;
        if (!_entries.Any(e => e.Id == _currentId))
        {
            _currentId = _entries[0].Id;
        }

        if (_pickerLabel is not null)
        {
            _pickerLabel.Text = Current?.Name ?? "";
        }
    }

    private InferenceConfigEntry? Current => _entries.FirstOrDefault(e => e.Id == _currentId);

    // ---- header ----

    private FrameworkElement BuildHeader()
    {
        var bar = new Grid { Height = 50 };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        bar.Children.Add(title);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        actions.Children.Add(BuildConfigurationPicker());
        var export = SettingsRows.SecondaryButton("Export", Export);
        export.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(export);
        Grid.SetColumn(actions, 1);
        bar.Children.Add(actions);

        var border = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return border;
    }

    private FrameworkElement BuildConfigurationPicker()
    {
        _pickerLabel = new TextBlock
        {
            Text = Current?.Name ?? "",
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var button = new Button { Content = _pickerLabel };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        System.Windows.Automation.AutomationProperties.SetName(button, $"Select configuration: {Current?.Name}");

        button.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            var group = new MenuItem { Header = "Configurations", IsEnabled = false };
            menu.Items.Add(group);
            var applied = InferenceConfigurations.AppliedId(services.Paths);
            foreach (var entry in _entries)
            {
                var captured = entry;
                var header = entry.Id == applied ? $"{entry.Name} · applied" : entry.Name;
                var item = new MenuItem
                {
                    Header = entry.Provider is null ? header : $"{header}  ({entry.Provider}{(entry.Note is null ? "" : " · " + entry.Note)})",
                    IsCheckable = true,
                    IsChecked = entry.Id == _currentId,
                };
                item.Click += (_, _) =>
                {
                    _currentId = captured.Id;
                    Reload();
                    RenderSection();
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            AddCommand(menu, "Duplicate…", () =>
            {
                if (Current is { } current && Prompt("Configuration name", InferenceConfigurations.CopyName(current.Name)) is { } name)
                {
                    _currentId = InferenceConfigurations.Duplicate(services.Paths, current, name);
                    Reload();
                }
            });
            AddCommand(menu, "Rename…", () =>
            {
                if (Current is { } current && Prompt("Configuration name", current.Name) is { } name)
                {
                    InferenceConfigurations.Rename(current, name);
                    Reload();
                }
            });
            AddCommand(menu, "New configuration", () =>
            {
                if (Prompt("Configuration name", InferenceConfigurations.UntitledName) is { } name)
                {
                    _currentId = InferenceConfigurations.Save(services.Paths, null, name, services.Settings.Current);
                    Reload();
                }
            });
            AddCommand(menu, "Import configuration…", Import);
            menu.Items.Add(new Separator());
            AddCommand(menu, "Show in Explorer", () => Reveal(InferenceConfigurations.Root(services.Paths)));
            var delete = new MenuItem { Header = "Delete", IsEnabled = _entries.Count > 1 };
            delete.Click += (_, _) =>
            {
                if (Current is { } current)
                {
                    InferenceConfigurations.Delete(services.Paths, current);
                    _currentId = null;
                    Reload();
                    RenderSection();
                }
            };
            menu.Items.Add(delete);

            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };

        return button;
    }

    private static void AddCommand(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    // ---- nav ----

    private static readonly (string Id, string Title)[] Sections =
    [
        ("connection", "Connection"),
        ("models", "Models"),
        ("credentials", "Credentials"),
        ("network", "Network"),
    ];

    private FrameworkElement BuildNav()
    {
        var nav = new StackPanel { Margin = new Thickness(8, 14, 8, 14) };
        var search = SettingsRows.Input("", width: double.NaN, placeholder: "Search settings", accessibleName: "Search settings");
        search.Margin = new Thickness(0, 0, 0, 6);
        nav.Children.Add(search);

        var buttons = new List<(string Id, Button Button)>();
        foreach (var (id, title) in Sections)
        {
            var captured = id;
            var button = new Button
            {
                Content = title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 10, 8),
                FontSize = 12,
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
            button.Click += (_, _) =>
            {
                _section = captured;
                foreach (var (otherId, other) in buttons)
                {
                    if (otherId == captured)
                    {
                        other.SetResourceReference(Control.BackgroundProperty, "SelectedOverlayBrush");
                    }
                    else
                    {
                        other.ClearValue(Control.BackgroundProperty);
                    }
                }

                RenderSection();
            };
            if (id == _section)
            {
                button.SetResourceReference(Control.BackgroundProperty, "SelectedOverlayBrush");
            }

            buttons.Add((id, button));
            nav.Children.Add(button);
        }

        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            foreach (var (id, button) in buttons)
            {
                var title = Sections.First(s => s.Id == id).Title;
                button.Visibility = query.Length == 0 || title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        };

        var border = new Border { Child = new ScrollViewer { Content = nav }, BorderThickness = new Thickness(0, 0, 1, 0) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        border.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        return border;
    }

    // ---- sections ----

    private void RenderSection()
    {
        if (_sections is null)
        {
            return;
        }

        _sections.Children.Clear();
        _sections.Children.Add(_section switch
        {
            "models" => BuildModels(),
            "credentials" => BuildCredentials(),
            "network" => BuildNetwork(),
            _ => BuildConnection(),
        });
    }

    private FrameworkElement BuildConnection()
    {
        var settings = services.Settings.Current;
        var section = SettingsRows.Section("Connection");

        foreach (var (label, get, set) in new (string, Func<string>, Action<string>)[]
                 {
                     ("Ollama", () => settings.OllamaBaseUrl, v => settings.OllamaBaseUrl = v),
                     ("NVIDIA Build", () => settings.NvidiaBaseUrl, v => settings.NvidiaBaseUrl = v),
                     ("OpenRouter", () => settings.OpenRouterBaseUrl, v => settings.OpenRouterBaseUrl = v),
                     ("TokenRouter", () => settings.TokenRouterBaseUrl, v => settings.TokenRouterBaseUrl = v),
                     ("DeepSeek", () => settings.DeepSeekBaseUrl, v => settings.DeepSeekBaseUrl = v),
                     ("Zhipu AI", () => settings.ZhipuBaseUrl, v => settings.ZhipuBaseUrl = v),
                     ("MiniMax", () => settings.MiniMaxBaseUrl, v => settings.MiniMaxBaseUrl = v),
                     ("LLM API", () => settings.LlmApiBaseUrl, v => settings.LlmApiBaseUrl = v),
                 })
        {
            var box = SettingsRows.Input(get(), width: 320);
            var commit = set;
            SettingsUi.AutoSave(box, (text, _) => commit(text.Trim()));
            section.Children.Add(SettingsRows.Row(label, null, box));
        }

        var result = SettingsRows.Muted("");
        result.Visibility = Visibility.Collapsed;
        section.Children.Add(SettingsRows.Row(
            "Test connection",
            "Runs one real turn through the default model.",
            SettingsRows.SecondaryButton("Test connection", () => TestConnection(result)),
            below: result));
        return section;
    }

    private void TestConnection(TextBlock result)
    {
        result.Visibility = Visibility.Visible;
        result.Text = "Running…";
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        var settings = services.Settings.Current;
        var model = settings.DefaultModelId;
        if (model is null)
        {
            result.Text = "Set a model first";
            return;
        }

        _ = Task.Run(async () =>
        {
            var started = Stopwatch.StartNew();
            var line = await ProviderConnectionCheck.RunAsync(
                services.Providers,
                ProviderConnectionPlan.ForApiKey(
                    JarvisCode.Core.Settings.ModelCatalog.Find(services.Settings.Models, model)?.ProviderId ?? "anthropic",
                    "the endpoint",
                    model));
            var ms = (int)started.Elapsed.TotalMilliseconds;
            await result.Dispatcher.InvokeAsync(() =>
            {
                result.Text = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    ? line
                    : $"Inference · 1-token completion in {ms} ms ({model})";
            });
        }, token);
    }

    private FrameworkElement BuildModels()
    {
        var section = SettingsRows.Section("Models");
        var configured = services.Settings.Current.CustomModels.Select(m => m.ModelId).ToList();
        section.Children.Add(SettingsRows.Block(SettingsRows.Footnote(
            configured.Count == 0
                ? "No models added yet — add them on the Providers page."
                : string.Join(", ", configured))));

        var result = SettingsRows.Muted("");
        result.Visibility = Visibility.Collapsed;
        var missingLine = SettingsRows.Footnote("");
        missingLine.Visibility = Visibility.Collapsed;
        var below = new StackPanel();
        below.Children.Add(result);
        below.Children.Add(missingLine);

        section.Children.Add(SettingsRows.Row(
            "Model discovery",
            "Asks the endpoint what it serves and compares it with the ids you added.",
            SettingsRows.SecondaryButton("Test model discovery", () =>
                Discover(result, missingLine, configured)),
            below: below));
        return section;
    }

    private void Discover(TextBlock result, TextBlock missingLine, IReadOnlyList<string> configured)
    {
        result.Visibility = Visibility.Visible;
        result.Text = "Running…";
        missingLine.Visibility = Visibility.Collapsed;
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        var settings = services.Settings.Current;

        _ = Task.Run(async () =>
        {
            var probe = await ModelDiscovery.ProbeAsync(
                services.Http, settings.OpenRouterBaseUrl, settings.GetApiKey("openrouter"), token);
            await result.Dispatcher.InvokeAsync(() =>
            {
                if (probe.Error is not null)
                {
                    result.Text = probe.Error;
                    return;
                }

                var missing = ModelDiscovery.Missing(configured, probe.Ids);
                result.Text = missing.Count == 0
                    ? ModelDiscovery.Found(probe.Ids.Count)
                    : ModelDiscovery.FoundWithMissing(probe.Ids.Count, missing.Count);
                if (missing.Count > 0)
                {
                    var named = missing.Take(ModelDiscovery.MaxNamedMissing).ToList();
                    var text = ModelDiscovery.NotReturned(named);
                    if (missing.Count > named.Count)
                    {
                        text += " " + ModelDiscovery.AndMore(missing.Count - named.Count);
                    }

                    missingLine.Text = text;
                    missingLine.Visibility = Visibility.Visible;
                }
            });
        }, token);
    }

    private FrameworkElement BuildCredentials()
    {
        var section = SettingsRows.Section("Credentials");
        var ui = services.UiSettings.Current;

        var scriptBox = SettingsRows.Input(ui.CredentialHelperScript, width: 320, accessibleName: "Credential helper script");
        SettingsUi.AutoSave(scriptBox, (text, _) =>
        {
            ui.CredentialHelperScript = text.Trim();
            services.UiSettings.Save();
        });
        section.Children.Add(SettingsRows.Row(
            "Credential helper",
            "A command that prints a credential on stdout — a bare token, or JSON naming auth headers.",
            scriptBox));

        var ttlBox = SettingsRows.Input(ui.CredentialHelperTimeoutSeconds.ToString(), width: 90, accessibleName: "Timeout");
        SettingsUi.AutoSave(ttlBox, (text, final) =>
        {
            if (int.TryParse(text, out var seconds) && seconds is >= 1 and <= 300)
            {
                ui.CredentialHelperTimeoutSeconds = seconds;
                services.UiSettings.Save();
            }
            else if (final)
            {
                ttlBox.Text = ui.CredentialHelperTimeoutSeconds.ToString();
            }
        });
        section.Children.Add(SettingsRows.Row("Timeout (seconds)", null, ttlBox));

        var headline = SettingsRows.Muted("");
        var detail = SettingsRows.Footnote("");
        var stderr = SettingsRows.Danger("");
        var below = new StackPanel();
        below.Children.Add(headline);
        below.Children.Add(detail);
        below.Children.Add(stderr);
        below.Visibility = Visibility.Collapsed;

        section.Children.Add(SettingsRows.Row(
            "Test script",
            null,
            SettingsRows.SecondaryButton("Test script", () => TestHelper(below, headline, detail, stderr)),
            below: below));
        return section;
    }

    private void TestHelper(FrameworkElement below, TextBlock headline, TextBlock detail, TextBlock stderr)
    {
        var ui = services.UiSettings.Current;
        if (ui.CredentialHelperScript.Trim().Length == 0)
        {
            return;
        }

        below.Visibility = Visibility.Visible;
        headline.Text = CredentialHelpers.Running(0);
        detail.Text = "";
        stderr.Text = "";
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;

        _ = Task.Run(async () =>
        {
            var run = await CredentialHelpers.RunAsync(ui.CredentialHelperScript, ui.CredentialHelperTimeoutSeconds, token);
            await headline.Dispatcher.InvokeAsync(() =>
            {
                headline.Text = $"{run.Headline} · {CredentialHelpers.Elapsed(run.ElapsedSeconds, run.Headline == CredentialHelpers.HeadlineTimedOut)}";
                detail.Text = run.Detail;
                stderr.Text = run.StderrRedacted;
            });
        }, token);
    }

    private FrameworkElement BuildNetwork()
    {
        var section = SettingsRows.Section("Network");
        var hosts = FirewallAllowlist.Hosts(services.Settings.Current);
        var list = SettingsRows.Footnote(string.Join(Environment.NewLine, hosts));
        var summary = SettingsRows.Muted("");
        summary.Visibility = Visibility.Collapsed;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(SettingsRows.SecondaryButton("Test connectivity", () =>
        {
            summary.Visibility = Visibility.Visible;
            summary.Text = "Running…";
            _work?.Cancel();
            _work = new CancellationTokenSource();
            var token = _work.Token;
            _ = Task.Run(async () =>
            {
                var results = await FirewallAllowlist.TestAsync(hosts, token);
                await summary.Dispatcher.InvokeAsync(() =>
                {
                    var reachable = results.Count(r => r.Reachable == true);
                    summary.Text = reachable == results.Count
                        ? FirewallAllowlist.Summary(reachable, results.Count)
                        : FirewallAllowlist.Summary(reachable, results.Count) + " · Connectivity test failed.";
                    list.Text = string.Join(
                        Environment.NewLine,
                        results.Select(r => $"{r.Host}  {(r.Reachable == true ? "✓" : "✕")}"));
                });
            }, token);
        }));

        var copy = SettingsRows.SecondaryButton("Copy hostnames", () =>
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, hosts));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another process owning the clipboard is not actionable here.
            }
        });
        copy.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(copy);

        var download = SettingsRows.SecondaryButton("Download .txt", () =>
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "jarvis-firewall-allowlist.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, hosts);
                Reveal(path, select: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only Downloads folder is not actionable here.
            }
        });
        download.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(download);

        section.Children.Add(SettingsRows.Row("Firewall allowlist", null, buttons, below: summary));
        section.Children.Add(SettingsRows.Block(list));
        return section;
    }

    // ---- footer ----

    private FrameworkElement BuildFooter()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 10, 16, 10),
        };

        bar.Children.Add(SettingsRows.SecondaryButton("Discard Changes", () =>
        {
            if (MessageBox.Show(
                    "This configuration has changes that haven’t been saved. They will be lost.",
                    "Discard unsaved changes?",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                RenderSection();
            }
        }));

        var save = SettingsRows.SecondaryButton("Save Changes", () =>
        {
            if (Current is { } current)
            {
                InferenceConfigurations.Save(services.Paths, current.Id, current.Name, services.Settings.Current);
                services.Settings.Save();
                Reload();
            }
        });
        save.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(save);

        var apply = SettingsRows.PrimaryButton("Apply Changes", () =>
        {
            if (Current is not { } current)
            {
                return;
            }

            InferenceConfigurations.Apply(services.Paths, current, services.Settings.Current);
            services.Settings.Save();
            Reload();
            if (MessageBox.Show(
                    "Your changes have been saved. Jarvis needs to relaunch to start using them.",
                    "Relaunch Jarvis?",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information) == MessageBoxResult.OK)
            {
                Relaunch();
            }
        });
        apply.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(apply);

        var border = new Border { Child = bar, BorderThickness = new Thickness(0, 1, 0, 0) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        return border;
    }

    private static void Relaunch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }

            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Relaunching is a convenience; the settings are already saved.
        }
    }

    // ---- import / export ----

    private void Import()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Configuration (*.json)|*.json|All files (*.*)|*.*",
            Title = "Import configuration",
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        var (id, error) = InferenceConfigurations.Import(services.Paths, picker.FileName);
        if (error is not null)
        {
            MessageBox.Show(error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _currentId = id;
        Reload();
        RenderSection();
    }

    private void Export()
    {
        if (Current is not { } current)
        {
            return;
        }

        var document = InferenceConfigurations.Read(current);
        if (InferenceConfigurations.HasSensitiveValues(document) &&
            MessageBox.Show(
                "This configuration contains sensitive values. They will be written to the exported file in plain text.",
                "Export",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Configuration (*.json)|*.json",
            FileName = current.Name + ".json",
            Title = "Export",
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(
                picker.FileName,
                (document ?? []).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Couldn’t export the configuration profile.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void Reveal(string path, bool select = false)
    {
        try
        {
            Directory.CreateDirectory(select ? Path.GetDirectoryName(path)! : path);
            Process.Start(new ProcessStartInfo("explorer.exe",
                select ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // Explorer failing to open is not actionable here.
        }
    }

    /// <summary>
    /// The reference's "Configuration name" prompt (<c>wpSJrTIFJu</c>) with Cancel and
    /// Confirm, over the app's own input dialog rather than a second one assembled here:
    /// the hand-built window gave its label whatever width the 300px field left over, so
    /// "Configuration name" wrapped to three lines and pushed both buttons past the
    /// bottom edge of a window whose height was fixed.
    /// </summary>
    private string? Prompt(string label, string initial)
        => InputDialog.Prompt(_window, label, initial, "Confirm");
}
