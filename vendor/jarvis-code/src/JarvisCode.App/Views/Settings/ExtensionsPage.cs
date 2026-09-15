using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;

namespace JarvisCode.App.Services
{
    /// <summary>Where an extension's "Configure" values are read and written from the page.</summary>
    internal static class ExtensionConfigEditing
    {
        public static InstalledExtension WithConfig(
            InstalledExtension extension, IReadOnlyDictionary<string, JsonNode?> config) =>
            extension with { UserConfig = config };

        public static InstalledExtension WithEnabled(InstalledExtension extension, bool enabled) =>
            extension with { Enabled = enabled };
    }
}

namespace JarvisCode.App.Views.Settings
{
    /// <summary>
    /// Settings › Desktop app › Extensions — the reference's Extensions page
    /// (ion-dist <c>c71860c77-ulwg9iK6.js</c>, its <c>Y</c>): the section title
    /// (<c>nb2FlN/G2m</c>) with its description (<c>t0eCW57c4v</c>) and the
    /// "Browse extensions" action (<c>IOJv7Fvf16</c>), the installed list with
    /// Configure / More options → Details / Uninstall (<c>VyyoA+XMVN</c>,
    /// <c>WDBdrgEGU2</c>, <c>euu2s475KB</c>, <c>AaJVslgu7i</c>), the drag hint
    /// (<c>5px1rJwipu</c>), the empty state (<c>Z3o8vwfzpy</c>) and Advanced
    /// settings (<c>V72EhnbTFu</c>), which is where its auto-update switch
    /// (<c>xBU5hYIiNs</c>) lives.
    ///
    /// A bundle is installed by dropping a .MCPB or .DXT file on the page, which is
    /// how the reference installs one on the desktop, and its resolved server joins
    /// the session's MCP config through <see cref="DesktopExtensions.McpFile"/>.
    /// </summary>
    internal sealed class ExtensionsPage(AppServices services)
    {
        public const string PageTitle = "Extensions";

        private readonly List<InstalledExtension> _extensions = [];
        private StackPanel? _list;
        private StackPanel? _page;

        public FrameworkElement Build()
        {
            _page = new StackPanel { Margin = new Thickness(0, 4, 0, 0), AllowDrop = true };
            _page.Loaded += (_, _) => services.ExtensionUpdates.Changed += OnUpdatesChanged;
            _page.Unloaded += (_, _) => services.ExtensionUpdates.Changed -= OnUpdatesChanged;
            _page.Drop += OnDrop;
            _page.DragOver += (_, e) =>
            {
                e.Effects = HasBundle(e) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };

            var action = SettingsRows.SecondaryButton("Browse extensions", () =>
                SettingsProse.Open("https://modelcontextprotocol.io/examples"));
            var section = SettingsRows.Section(
                PageTitle,
                "Allow Jarvis to directly interact with apps, data, and tools on your computer.",
                action);

            _list = new StackPanel();
            section.Children.Add(_list);

            var advanced = SettingsRows.SecondaryButton("Advanced settings", ShowAdvanced);
            advanced.HorizontalAlignment = HorizontalAlignment.Left;
            advanced.Margin = new Thickness(0, 12, 0, 8);
            section.Children.Add(advanced);

            var hint = SettingsRows.Muted("Drag .MCPB or .DXT files here to install");
            hint.Margin = new Thickness(0, 4, 0, 0);
            section.Children.Add(hint);

            _page.Children.Add(section);
            Reload();
            return _page;
        }

        private void OnUpdatesChanged() => _page?.Dispatcher.BeginInvoke(Reload);

        private static bool HasBundle(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Any(DesktopExtensions.IsBundle);

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!HasBundle(e))
            {
                return;
            }

            e.Handled = true;
            foreach (var file in ((string[])e.Data.GetData(DataFormats.FileDrop)!).Where(DesktopExtensions.IsBundle))
            {
                Install(file);
            }
        }

        private void Install(string path)
        {
            var (extension, error) = DesktopExtensions.Install(services.Paths, path);
            if (extension is null)
            {
                ShowError(error ?? "Failed to open the extension file.");
                return;
            }

            Reload();
        }

        private void ShowError(string message)
        {
            if (_list is null)
            {
                return;
            }

            var line = SettingsRows.Danger(message);
            line.Margin = new Thickness(0, 8, 0, 0);
            _list.Children.Insert(0, line);
        }

        private void Reload()
        {
            _extensions.Clear();
            _extensions.AddRange(DesktopExtensions.List(services.Paths));
            Render();
        }

        private void Render()
        {
            if (_list is null)
            {
                return;
            }

            _list.Children.Clear();
            if (_extensions.Count == 0)
            {
                var empty = SettingsRows.Muted("No extensions installed");
                empty.Margin = new Thickness(0, 12, 0, 4);
                _list.Children.Add(empty);
                return;
            }

            var heading = SettingsRows.Footnote("Installed on your computer");
            heading.Margin = new Thickness(0, 8, 0, 4);
            _list.Children.Add(heading);

            foreach (var extension in _extensions.ToList())
            {
                _list.Children.Add(BuildCard(extension));
            }
        }

        private FrameworkElement BuildCard(InstalledExtension extension)
        {
            var text = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            var name = new TextBlock { Text = extension.Manifest.Title, FontSize = 14, FontWeight = FontWeights.Medium };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            title.Children.Add(name);
            if (!extension.Enabled)
            {
                var badge = SettingsRows.Badge("Disabled");
                badge.Margin = new Thickness(8, 0, 0, 0);
                title.Children.Add(badge);
            }

            text.Children.Add(title);
            var description = SettingsRows.Footnote(extension.Manifest.Description);
            description.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(description);
            var pending = services.ExtensionUpdates.Pending().FirstOrDefault(update => update.ExtensionId == extension.Id);
            var updateStatus = pending is not null
                ? $"Update {pending.Version} ready · restart Jarvis Code to install"
                : !DesktopExtensionUpdates.IsEligible(extension)
                    ? "Installed from a local file · update manually"
                    : !services.ExtensionUpdates.DirectoryAvailable ? "Extension directory is not connected" : $"Version {extension.Manifest.Version}";
            text.Children.Add(SettingsRows.Footnote(updateStatus));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (extension.Manifest.UserConfig.Count > 0)
            {
                buttons.Children.Add(SettingsRows.SecondaryButton("Configure", () => Configure(extension)));
            }

            var more = SettingsRows.SecondaryButton("More options", () => { });
            more.Margin = new Thickness(6, 0, 0, 0);
            System.Windows.Automation.AutomationProperties.SetName(more, "More options");
            var menu = new ContextMenu();
            if (pending is { IsInternal: true, Approved: false })
            {
                var review = new MenuItem { Header = "Review update" };
                review.Click += (_, _) =>
                {
                    if (ConfirmDialog.Ask(Window.GetWindow(_page), "Update extension?",
                        $"Update {extension.Manifest.Title} from {extension.Manifest.Version} to {pending.Version} on the next restart?",
                        "Allow update", focusCancel: true))
                        services.ExtensionUpdates.ApproveInternalUpdate(extension.Id);
                };
                menu.Items.Add(review);
            }
            var details = new MenuItem { Header = "Details" };
            details.Click += (_, _) => ShowDetails(extension);
            menu.Items.Add(details);
            var uninstall = new MenuItem { Header = "Uninstall" };
            uninstall.Click += (_, _) => Uninstall(extension);
            menu.Items.Add(uninstall);
            more.ContextMenu = menu;
            more.Click += (_, _) =>
            {
                menu.PlacementTarget = more;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            buttons.Children.Add(more);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            return SettingsUi.Card(row);
        }

        private void Uninstall(InstalledExtension extension)
        {
            if (MessageBox.Show(
                    $"{extension.Manifest.Title} and its configuration will be removed from Jarvis Code.",
                    $"Uninstall {extension.Manifest.Title}?",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            DesktopExtensions.Uninstall(services.Paths, extension);
            Reload();
        }

        private void ShowDetails(InstalledExtension extension)
        {
            var manifest = extension.Manifest;
            var lines = new List<string>
            {
                $"Version: {manifest.Version}",
            };
            if (manifest.AuthorName is { Length: > 0 } author)
            {
                lines.Add($"Author: {author}");
            }

            lines.Add($"Installation: {extension.Directory}");
            if (manifest.License is { Length: > 0 } license)
            {
                lines.Add($"License: {license}");
            }

            if (manifest.LongDescription is { Length: > 0 } longDescription)
            {
                lines.Add("");
                lines.Add(longDescription);
            }

            MessageBox.Show(string.Join(Environment.NewLine, lines), manifest.Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// The "Configure" sheet: one input per <c>user_config</c> field, in the
        /// manifest's own order, with a sensitive field masked and a required one
        /// named as required.
        /// </summary>
        private void Configure(InstalledExtension extension)
        {
            var panel = new StackPanel { Margin = new Thickness(20) };
            var editors = new Dictionary<string, Func<JsonNode?>>(StringComparer.Ordinal);

            foreach (var field in extension.Manifest.UserConfig)
            {
                var stored = extension.UserConfig.GetValueOrDefault(field.Key) ?? field.Default;
                FrameworkElement control;
                if (field.Type == "boolean")
                {
                    var toggle = SettingsUi.Switch(stored is JsonValue b && b.TryGetValue<bool>(out var on) && on, _ => { });
                    editors[field.Key] = () => JsonValue.Create(toggle.IsChecked == true);
                    control = toggle;
                }
                else
                {
                    var box = SettingsRows.Input(Scalar(stored), width: 260, accessibleName: field.Title);
                    if (field.Sensitive)
                    {
                        box.FontFamily = new System.Windows.Media.FontFamily("Consolas");
                    }

                    editors[field.Key] = () => field.Type == "number" && double.TryParse(box.Text, out var number)
                        ? JsonValue.Create(number)
                        : JsonValue.Create(box.Text);
                    control = box;
                }

                panel.Children.Add(SettingsRows.Row(
                    field.Required ? field.Title + " *" : field.Title,
                    field.Description,
                    control));
            }

            var save = SettingsRows.PrimaryButton("Save", () => { });
            save.HorizontalAlignment = HorizontalAlignment.Right;
            panel.Children.Add(save);

            var window = new Window
            {
                Title = extension.Manifest.Title,
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                Width = 520,
                Height = 420,
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            save.Click += (_, _) =>
            {
                var config = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                foreach (var (key, read) in editors)
                {
                    config[key] = read();
                }

                var index = _extensions.FindIndex(e => e.Id == extension.Id);
                if (index >= 0)
                {
                    _extensions[index] = ExtensionConfigEditing.WithConfig(extension, config);
                }

                DesktopExtensions.Save(services.Paths, _extensions);
                window.Close();
                Render();
            };
            window.ShowDialog();
        }

        private static string Scalar(JsonNode? node) => node switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString().Trim('"'),
        };

        /// <summary>The reference's Advanced settings subpage, which carries the auto-update switch.</summary>
        private void ShowAdvanced()
        {
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(SettingsRows.Row(
                "Enable auto-updates for extensions",
                "Check directory-installed extensions every six hours and install downloaded updates on restart. Extensions installed from local files are updated manually.",
                SettingsUi.Switch(services.UiSettings.Current.ExtensionsAutoUpdate, on =>
                {
                    services.UiSettings.Current.ExtensionsAutoUpdate = on;
                    services.UiSettings.Save();
                })));
            panel.Children.Add(SettingsRows.Block(SettingsUi.Caption(
                $"Extensions are unpacked into {DesktopExtensions.Root(services.Paths)} and their servers are written to " +
                $"{Path.GetFileName(DesktopExtensions.McpFile(services.Paths))}, which every Code session loads.")));

            new Window
            {
                Title = "Advanced settings",
                Content = new ScrollViewer { Content = panel },
                Width = 520,
                Height = 260,
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            }.ShowDialog();
        }
    }
}
