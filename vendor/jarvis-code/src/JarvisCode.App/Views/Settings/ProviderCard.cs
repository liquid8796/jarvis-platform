using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisCode.App.Composition;
using JarvisCode.Core.Providers;
using AppSettings = JarvisCode.Core.Settings.AppSettings;
using ModelCatalog = JarvisCode.Core.Settings.ModelCatalog;
using ModelInfo = JarvisCode.Core.Models.ModelInfo;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// One provider on the Providers page: a header that says at a glance whether it is usable,
/// and an expanded body with its credential editor, its endpoint (when it has one) and its
/// models. Everything it draws comes from a <see cref="ProviderEntry"/>, so a new provider
/// needs no code here — see <see cref="ProviderCatalog.Presentations"/>.
/// </summary>
internal sealed class ProviderCard
{
    private const string ChevronCollapsed = "";
    private const string ChevronExpanded = "";

    private readonly AppServices _services;
    private readonly Action<ProviderCard> _onExpanded;

    /// <summary>
    /// Raised when this card removed the provider it belongs to, so the page can drop the
    /// card rather than leave one whose entry no longer exists.
    /// </summary>
    private readonly Action? _onProvidersChanged;
    private readonly StackPanel _body = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 2) };
    private readonly StackPanel _modelsHost = new();
    private readonly TextBlock _statusLine;
    private readonly TextBlock _chevron;
    private readonly Ellipse _dot;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly ToggleButton _header;

    private ProviderEntry _entry;
    private bool _bodyBuilt;

    /// <summary>
    /// Rewrites the line under the key box. Held as a hook because the endpoint decides
    /// whether a key is needed at all: switching Ollama to the cloud has to move that line
    /// from "needs no key" to the same thing the header now says.
    /// </summary>
    private Action? _refreshKeyHint;

    /// <summary>
    /// Rewrites the "requests go to …" line. A provider whose base URL only names a root —
    /// llmapi.pro serves three protocols under one host — is worth showing the resolved
    /// endpoint for, because that is the part the user cannot see.
    /// </summary>
    private Action? _refreshEndpointHint;

    public ProviderCard(
        ProviderEntry entry,
        AppServices services,
        Action<ProviderCard> onExpanded,
        Action? onProvidersChanged = null)
    {
        _entry = entry;
        _services = services;
        _onExpanded = onExpanded;
        _onProvidersChanged = onProvidersChanged;

        _dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var name = new TextBlock { Text = entry.Name, FontSize = 14, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");

        _badgeText = SettingsUi.ChipText(entry.Badge);
        _badge = SettingsUi.Chip(_badgeText);

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(name);
        nameRow.Children.Add(_badge);

        _statusLine = new TextBlock { FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
        _statusLine.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");

        var text = new StackPanel();
        text.Children.Add(nameRow);
        text.Children.Add(_statusLine);

        _chevron = new TextBlock
        {
            Text = ChevronCollapsed,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        _chevron.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
        _chevron.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_dot);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        Grid.SetColumn(_chevron, 2);
        header.Children.Add(_chevron);

        _header = new ToggleButton
        {
            Content = header,
            Padding = new Thickness(14, 11, 14, 11),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _header.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
        AutomationProperties.SetName(_header, $"{entry.Name} provider settings");
        // Driven by the checked state rather than by Click, so a screen reader toggling the
        // header through its TogglePattern opens the card exactly like a mouse click does.
        _header.Checked += (_, _) => SetExpanded(true);
        _header.Unchecked += (_, _) => SetExpanded(false);

        var content = new StackPanel();
        content.Children.Add(_header);
        content.Children.Add(_body);

        Root = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 8),
        };
        Root.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        Root.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");

        RefreshHeader();
    }

    public Border Root { get; }

    public string Id => _entry.Id;

    public ProviderEntry Entry => _entry;

    public bool IsExpanded => _body.Visibility == Visibility.Visible;

    public void SetExpanded(bool expanded)
    {
        if (_header.IsChecked != expanded)
        {
            // The toggle is the source of truth; flipping it re-enters here through its
            // Checked/Unchecked event, and that pass does the work.
            _header.IsChecked = expanded;
            return;
        }

        if (expanded && !_bodyBuilt)
        {
            BuildBody();
            _bodyBuilt = true;
        }

        _body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _body.Margin = new Thickness(14, 0, 14, expanded ? 12 : 0);
        _chevron.Text = expanded ? ChevronExpanded : ChevronCollapsed;
        if (!expanded)
        {
            return;
        }

        _onExpanded(this);

        // The body only has a height once it has been laid out, so the scroll waits for it —
        // otherwise a card opened near the bottom stays below the fold.
        Root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Root.BringIntoView());
    }

    /// <summary>
    /// Re-reads what the header shows. Keys and the endpoint both move it: a local base URL
    /// makes a key unnecessary, which the provider itself decides, so the answer is asked of
    /// the live provider rather than remembered.
    /// </summary>
    private void RefreshHeader()
    {
        var settings = _services.Settings.Current;
        _entry = _entry with
        {
            CredentialCount = settings.GetApiKeys(_entry.Id).Count,
            RequiresCredential = TryGetProvider()?.RequiresApiKey ?? _entry.RequiresCredential,
            Models = ProviderCatalog.ModelsFor(_entry.Id, settings, _services.Settings.Models),
        };

        _statusLine.Text = _entry.StatusLine;
        _dot.SetResourceReference(Shape.FillProperty, _entry.Readiness switch
        {
            ProviderReadiness.Ready => "Success100Brush",
            ProviderReadiness.NeedsCredential => "Warning100Brush",
            _ => "Text500Brush",
        });

        _badgeText.Text = _entry.Badge;
        _badge.Visibility = _entry.Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ILlmProvider? TryGetProvider()
    {
        try
        {
            return _services.Providers.Get(_entry.Id);
        }
        catch (ProviderException)
        {
            return null;
        }
    }

    private void BuildBody()
    {
        if (_entry.Tagline.Length > 0)
        {
            var tagline = new TextBlock
            {
                Text = _entry.Tagline,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            };
            tagline.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            _body.Children.Add(tagline);
        }

        if (_entry.Credential == ProviderCredentialKind.CookieExport)
        {
            foreach (var element in ChatGptSessionCard.Build(_services, RefreshHeader, RebuildModels))
            {
                _body.Children.Add(element);
            }
        }
        else if (_entry.IsRegistered || _entry.CredentialCount > 0)
        {
            // A provider that is gone and stored nothing has nothing to edit — only its
            // leftover models, listed below.
            BuildApiKeyEditor();
        }

        if (_entry.Choice is { } choice)
        {
            BuildChoiceEditor(choice);
        }

        if (_entry.Fields is { Count: > 0 } fields)
        {
            foreach (var field in fields)
            {
                BuildFieldEditor(field);
            }
        }

        if (_entry.Toggles is { Count: > 0 } toggles)
        {
            foreach (var toggle in toggles)
            {
                BuildToggleEditor(toggle);
            }
        }

        if (_entry.Endpoint is { } endpoint)
        {
            BuildEndpointEditor(endpoint);
        }

        if (_entry.IsRegistered || _entry.Models.Count > 0)
        {
            _body.Children.Add(SettingsUi.SectionHeader("Models"));
            _body.Children.Add(_modelsHost);
            RebuildModels();
        }

        if (_entry.IsUserDefined)
        {
            BuildRemoveProvider();
        }
    }

    /// <summary>
    /// Deleting a provider the user added. It takes its keys and its models with it: they
    /// are stored against the id and nothing would serve them afterwards, so leaving them
    /// would show a card saying this build has no such provider — a confusing way to
    /// describe something just deleted. The confirm step is what makes that safe.
    /// </summary>
    private void BuildRemoveProvider()
    {
        _body.Children.Add(SettingsUi.SectionHeader("Remove"));
        var remove = SettingsUi.ConfirmButton(
            "Remove provider",
            $"Remove {_entry.Name}, its keys and its models?",
            () =>
            {
                ProviderCatalog.RemoveCustom(_services.Settings.Current, _entry.Id);
                _services.Settings.Save();
                _onProvidersChanged?.Invoke();
            });
        AutomationProperties.SetName(remove, $"Remove the {_entry.Name} provider");
        _body.Children.Add(remove);
        _body.Children.Add(SettingsUi.Caption(
            "Sessions already answered by this provider keep their transcripts; only the "
            + "endpoint, its keys and its model list go."));
    }

    // ---- Credentials ----

    private void BuildApiKeyEditor()
    {
        var settings = _services.Settings.Current;
        _body.Children.Add(SettingsUi.SectionHeader("API keys"));

        if (!_entry.IsRegistered)
        {
            BuildLeftoverKeyEditor(settings);
            return;
        }

        var keyBox = new TextBox
        {
            AcceptsReturn = true,
            Margin = new Thickness(0, 2, 0, 0),
            MinHeight = 34,
            MaxHeight = 90,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        keyBox.SetResourceReference(FrameworkElement.StyleProperty, "InputTextBox");
        AutomationProperties.SetName(keyBox, $"{_entry.Name} API keys");

        var feedback = SettingsUi.Caption("");
        void Say(string message) => feedback.Text = message;

        _refreshKeyHint = () => Say(KeyHint(_entry));
        _refreshKeyHint();

        var save = SettingsUi.PrimaryButton("Save keys", () =>
        {
            var keys = keyBox.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (keys.Count == 0)
            {
                Say("Nothing to save — paste at least one key first. The saved keys are untouched.");
                return;
            }

            settings.ApiKeySets[_entry.Id] = keys;
            _services.Settings.Save();
            keyBox.Clear();
            Say(keys.Count == 1 ? "1 key saved." : $"{keys.Count} keys saved.");
            RefreshHeader();
        });

        var remove = SettingsUi.ConfirmButton("Remove all", "Click again to remove", () =>
        {
            if (!settings.ApiKeySets.Remove(_entry.Id))
            {
                Say("There was no key to remove.");
                return;
            }

            _services.Settings.Save();
            Say("Removed. This provider has no key saved.");
            RefreshHeader();
        });

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(save);
        actions.Children.Add(SettingsUi.Spaced(BuildTestButton(Say)));
        if (_entry.CredentialUrl is { Length: > 0 } url)
        {
            actions.Children.Add(SettingsUi.Spaced(SettingsUi.GhostButton("Get a key ↗", () => OpenUrl(url, Say))));
        }

        // Removing keys sits last: a mis-click on the row's edge lands on something harmless.
        actions.Children.Add(SettingsUi.Spaced(remove));

        _body.Children.Add(keyBox);
        _body.Children.Add(actions);
        _body.Children.Add(feedback);
    }

    private static string KeyHint(ProviderEntry entry) => entry.CredentialCount switch
    {
        0 when !entry.RequiresCredential => "This endpoint needs no key. Paste one anyway if you talk to a hosted server.",
        0 => "No key saved. Paste one or more keys, one per line.",
        1 => "1 key saved. Paste a new key to replace it, or leave this empty to keep it.",
        var count => $"{count} keys saved, rotated when one is rate limited. Paste new keys to replace them.",
    };

    /// <summary>
    /// The editor for a provider settings still mention but this build cannot talk to: saving a
    /// key or a model would be typing into a void, so only the way out is offered.
    /// </summary>
    private void BuildLeftoverKeyEditor(AppSettings settings)
    {
        var feedback = SettingsUi.Caption(_entry.CredentialCount == 1
            ? "1 key is still stored for this id. Remove it unless the provider is coming back."
            : $"{_entry.CredentialCount} keys are still stored for this id. Remove them unless the provider is coming back.");

        var remove = SettingsUi.ConfirmButton("Remove all", "Click again to remove", () =>
        {
            if (!settings.ApiKeySets.Remove(_entry.Id))
            {
                feedback.Text = "There was no key to remove.";
                return;
            }

            _services.Settings.Save();
            feedback.Text = "Removed.";
            RefreshHeader();
        });
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.Margin = new Thickness(0, 6, 0, 0);

        _body.Children.Add(remove);
        _body.Children.Add(feedback);
    }

    /// <summary>Uses the shared capability-aware provider connection check.</summary>
    private Button BuildTestButton(Action<string> say)
    {
        Button? button = null;
        button = SettingsUi.GhostButton("Test connection", () =>
        {
            if (!_entry.IsRegistered)
            {
                say("This build has no provider with that id, so there is nothing to test.");
                return;
            }

            if (_entry.Models.Count == 0)
            {
                say("Add a model below first — a test needs one to ask.");
                return;
            }

            button!.IsEnabled = false;
            say("Testing…");
            _ = TestAsync(_entry.Models[0].Info.ModelId, message =>
            {
                say(message);
                button.IsEnabled = true;
            });
        });
        return button;
    }

    private async Task TestAsync(string modelId, Action<string> report)
    {
        report(await ProviderConnectionCheck.RunAsync(_services.Providers,
            ProviderConnectionPlan.ForApiKey(_entry.Id, _entry.Name, modelId)));
    }

    // ---- Provider-specific choice ----

    private void BuildChoiceEditor(ProviderChoiceSpec choice)
    {
        var settings = _services.Settings.Current;
        var combo = SettingsUi.Combo(choice.Options, choice.Read(settings), value =>
        {
            choice.Write(settings, value);
            _services.Settings.Save();
            RefreshHeader();
            // The choice can move where requests go, so the resolved line follows it.
            _refreshEndpointHint?.Invoke();
        }, width: 150);
        AutomationProperties.SetName(combo, $"{_entry.Name} {choice.Label.ToLowerInvariant()}");

        _body.Children.Add(SettingsUi.Row(choice.Label, choice.Description, combo));
    }

    // ---- Provider-specific fields (regions, project ids) ----

    private void BuildFieldEditor(ProviderFieldSpec field)
    {
        var settings = _services.Settings.Current;
        var box = SettingsUi.Input(field.Read(settings), width: 280);
        if (field.Read(settings).Length == 0 && field.Placeholder.Length > 0)
        {
            box.ToolTip = $"e.g. {field.Placeholder}";
        }

        AutomationProperties.SetName(box, $"{_entry.Name} {field.Label.ToLowerInvariant()}");
        box.LostFocus += (_, _) =>
        {
            var value = box.Text.Trim();
            if (string.Equals(value, field.Read(settings), StringComparison.Ordinal))
            {
                return;
            }

            field.Write(settings, value);
            _services.Settings.Save();
            box.Text = value;
            RefreshHeader();
        };
        _body.Children.Add(SettingsUi.Row(field.Label, field.Description, box));
    }

    // ---- Provider-specific switches ----

    private void BuildToggleEditor(ProviderToggleSpec toggle)
    {
        var settings = _services.Settings.Current;
        var control = SettingsUi.Switch(toggle.Read(settings), value =>
        {
            toggle.Write(settings, value);
            _services.Settings.Save();
            RefreshHeader();
            _refreshKeyHint?.Invoke();
        });
        AutomationProperties.SetName(control, $"{_entry.Name} {toggle.Label.ToLowerInvariant()}");
        _body.Children.Add(SettingsUi.Row(toggle.Label, toggle.Description, control));
    }

    // ---- Endpoint ----

    private void BuildEndpointEditor(ProviderEndpointSpec endpoint)
    {
        var settings = _services.Settings.Current;
        _body.Children.Add(SettingsUi.SectionHeader("Endpoint"));

        var urlBox = SettingsUi.Input(endpoint.Read(settings), width: 280);
        AutomationProperties.SetName(urlBox, $"{_entry.Name} base URL");
        var feedback = SettingsUi.Caption("");

        void Commit()
        {
            var url = urlBox.Text.Trim();
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                urlBox.Text = endpoint.Read(settings);
                feedback.Text = "That is not a full URL (it needs http:// or https://), so the old one is back.";
                return;
            }

            if (string.Equals(url, endpoint.Read(settings), StringComparison.Ordinal))
            {
                return;
            }

            endpoint.Write(settings, url);
            _services.Settings.Save();
            urlBox.Text = url;
            feedback.Text = "Saved.";
            RefreshHeader();
            _refreshKeyHint?.Invoke();
            _refreshEndpointHint?.Invoke();
        }

        urlBox.LostFocus += (_, _) => Commit();

        var presets = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var preset in endpoint.Presets)
        {
            var button = SettingsUi.GhostButton(preset.Label, () =>
            {
                endpoint.Write(settings, preset.Url);
                _services.Settings.Save();
                urlBox.Text = preset.Url;
                feedback.Text = "Saved.";
                RefreshHeader();
                _refreshKeyHint?.Invoke();
                _refreshEndpointHint?.Invoke();
            });
            button.Margin = new Thickness(0, 0, 6, 0);
            presets.Children.Add(button);
        }

        _body.Children.Add(SettingsUi.Row("Base URL", endpoint.Description, urlBox));
        _body.Children.Add(presets);

        if (endpoint.Resolve is { } resolve)
        {
            var resolved = SettingsUi.Caption("");
            _refreshEndpointHint = () => resolved.Text = $"Requests go to {resolve(settings)}";
            _refreshEndpointHint();
            _body.Children.Add(resolved);
        }

        _body.Children.Add(feedback);
    }

    // ---- Models ----

    private void RebuildModels()
    {
        var settings = _services.Settings.Current;
        _modelsHost.Children.Clear();

        if (_entry.Models.Count > 0)
        {
            var hint = SettingsUi.Caption(
                "The box on each row is the context window in tokens. It drives the composer's "
                + "context bar and when a session is compacted — edit it if your plan serves a "
                + "different window than the catalog assumes.");
            hint.Margin = new Thickness(0, 0, 0, 6);
            _modelsHost.Children.Add(hint);
        }

        foreach (var model in _entry.Models)
        {
            _modelsHost.Children.Add(BuildModelRow(model));
        }

        if (_entry.Models.Count == 0)
        {
            _modelsHost.Children.Add(SettingsUi.Caption(
                "No models yet. Add the ids you want to use — Ollama, for instance, serves whatever you have pulled."));
        }

        if (!_entry.IsRegistered)
        {
            // Nothing would serve a model added here, so the list is only for clearing leftovers.
            return;
        }

        var idBox = SettingsUi.Input("", width: 170);
        AutomationProperties.SetName(idBox, "New model id");
        var nameBox = SettingsUi.Input("", width: 140);
        AutomationProperties.SetName(nameBox, "New model display name");
        var contextBox = SettingsUi.Input("128000", width: 92);
        AutomationProperties.SetName(contextBox, "New model context window");
        var feedback = SettingsUi.Caption("");

        var add = SettingsUi.GhostButton("Add model", () =>
        {
            var modelId = idBox.Text.Trim();
            if (modelId.Length == 0)
            {
                feedback.Text = "Type the model id the provider expects, e.g. \"qwen3:32b\".";
                return;
            }

            modelId = ProviderCatalog.ApplyModelIdPrefix(_entry.ModelIdPrefix, modelId);

            // A model id is unique across the whole catalog, not just this card: a second
            // entry sharing one is dropped when the list is assembled, so adding it here
            // would store a model that never appears anywhere.
            if (ModelCatalog.Find(_services.Settings.Models, modelId) is { } owner)
            {
                feedback.Text = owner.ProviderId.Equals(_entry.Id, StringComparison.OrdinalIgnoreCase)
                    ? $"“{modelId}” is already in this list."
                    : $"“{modelId}” is already added under {owner.ProviderId}. "
                      + "Remove it there first, or add it under a different id.";
                return;
            }

            var context = int.TryParse(contextBox.Text.Trim(), out var tokens) && tokens > 0 ? tokens : 128_000;
            var displayName = nameBox.Text.Trim();
            settings.CustomModels.Add(new ModelInfo(
                _entry.Id, modelId, displayName.Length > 0 ? displayName : modelId, context));
            _services.Settings.Save();
            RefreshHeader();
            RebuildModels();
        });

        var addRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (element, hint) in new (FrameworkElement Element, string Hint)[]
                 {
                     (idBox, "model id"), (nameBox, "display name"), (contextBox, "context"), (add, ""),
                 })
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            cell.Children.Add(element);
            if (hint.Length > 0)
            {
                cell.Children.Add(SettingsUi.Caption(hint));
            }

            addRow.Children.Add(cell);
        }

        _modelsHost.Children.Add(addRow);
        _modelsHost.Children.Add(feedback);
    }

    private FrameworkElement BuildModelRow(ProviderModel model)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock
        {
            Text = model.Info.DisplayName,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        nameRow.Children.Add(name);
        if (model.IsCustom)
        {
            nameRow.Children.Add(SettingsUi.Chip(SettingsUi.ChipText("yours")));
        }

        var editedChip = SettingsUi.Chip(SettingsUi.ChipText("context edited"));
        editedChip.Visibility = model.IsContextOverridden ? Visibility.Visible : Visibility.Collapsed;
        nameRow.Children.Add(editedChip);

        text.Children.Add(nameRow);

        var detail = new TextBlock
        {
            Text = model.Info.ModelId,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 8, 0),
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        text.Children.Add(detail);
        row.Children.Add(text);

        var contextBox = BuildContextEditor(model, editedChip);
        Grid.SetColumn(contextBox, 1);
        row.Children.Add(contextBox);

        if (model.IsCustom)
        {
            var remove = SettingsUi.GhostButton("Remove", () =>
            {
                var settings = _services.Settings.Current;
                settings.CustomModels.RemoveAll(m =>
                    m.ModelId.Equals(model.Info.ModelId, StringComparison.OrdinalIgnoreCase)
                    && m.ProviderId.Equals(model.Info.ProviderId, StringComparison.OrdinalIgnoreCase));
                _services.Settings.Save();
                RefreshHeader();
                RebuildModels();
            });
            remove.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(remove, $"Remove {model.Info.DisplayName}");
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
        }

        return row;
    }

    /// <summary>
    /// The context window as an editable field. The catalog carries what each vendor publishes,
    /// but a plan or a relay can serve a different window and a built-in entry cannot be replaced
    /// by a custom one (the catalog keeps the first entry with an id), so this is the only way to
    /// correct it. The number drives the composer's context bar and the auto-compaction
    /// threshold; the wire never sees it.
    /// </summary>
    private FrameworkElement BuildContextEditor(ProviderModel model, UIElement editedChip)
    {
        var settings = _services.Settings.Current;
        var modelId = model.Info.ModelId;
        var box = SettingsUi.Input(
            model.Info.MaxContextTokens.ToString(CultureInfo.InvariantCulture), width: 96);
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Margin = new Thickness(0, 0, 8, 0);
        box.ToolTip = "Context window in tokens";
        AutomationProperties.SetName(box, $"Context window for {model.Info.DisplayName}");

        var current = model.Info.MaxContextTokens;
        void Apply(int tokens)
        {
            if (tokens == current)
            {
                return;
            }

            current = tokens;

            // The user's own entry carries its window itself; keep the two from disagreeing.
            var customIndex = model.IsCustom
                ? settings.CustomModels.FindIndex(m =>
                    m.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase)
                    && m.ProviderId.Equals(model.Info.ProviderId, StringComparison.OrdinalIgnoreCase))
                : -1;

            if (customIndex >= 0)
            {
                settings.CustomModels[customIndex] =
                    settings.CustomModels[customIndex] with { MaxContextTokens = tokens };
            }
            else
            {
                settings.ModelContextOverrides[modelId] = tokens;
            }

            _services.Settings.Save();
            editedChip.Visibility = settings.ModelContextOverrides.ContainsKey(modelId)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        SettingsUi.AutoSave(box, (text, final) =>
        {
            if (ProviderCatalog.ParseContextWindow(text) is { } tokens)
            {
                Apply(tokens);
            }
            else if (final)
            {
                // A typo would change when conversations get compacted, so the old number stands.
                box.Text = current.ToString(CultureInfo.InvariantCulture);
            }
        });

        return box;
    }

    private static void OpenUrl(string url, Action<string> report)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            report($"Could not open {url}: {ex.Message}");
        }
    }

}
