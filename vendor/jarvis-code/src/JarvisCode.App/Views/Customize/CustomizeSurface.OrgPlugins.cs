using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// Customize › Organization plugins (c71860c77-DuPx-LoQ): a mount folder the
/// plugins are read from, one card per plugin with its manifest status and
/// contents, and the per-server tool policy underneath. The reference has a
/// device-management tool mount that folder; here the user points at one, and
/// the policy it carries becomes rules this app's permission gate already
/// speaks.
/// </summary>
public partial class CustomizeSurface
{
    private DateTimeOffset? _orgScannedAt;

    public void ShowOrgPlugins()
    {
        if (_services is null)
            return;

        var page = NewPage();
        page.Children.Add(CustomizeUi.PageHeader(
            "Organization plugins", null, null, "",
            CustomizeUi.Ghost("Select a folder", ChooseOrgFolder)));

        var folder = Services.UiSettings.Current.OrganizationPluginsFolder;
        var blurb = CustomizeUi.Text(
            "Mount plugin bundles to this folder using your device-management tool and Jarvis will load them at " +
            "launch. The folder is read-only; tool policies you set below are saved in this configuration.",
            12, "Text400Brush", wrap: true);
        blurb.Margin = new Thickness(0, 0, 0, 10);
        page.Children.Add(blurb);

        if (folder.Length == 0)
        {
            page.Children.Add(CustomizeUi.EmptyState(
                "Organization plugins",
                "Plugins that are managed by your org will appear here.",
                "Select a folder",
                ChooseOrgFolder));
            Show(page);
            return;
        }

        var path = CustomizeUi.Text(folder, 11.5, "Text500Brush");
        path.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");
        page.Children.Add(path);

        var scan = OrganizationPluginScanner.Scan(folder);
        _orgScannedAt ??= DateTimeOffset.Now;
        var summary = CustomizeUi.Text(
            $"{scan.Summary} · Last synced {OrganizationPluginScanner.LastSynced(_orgScannedAt, DateTimeOffset.Now)}",
            12, scan.ReadError is null ? "Text400Brush" : "Danger100Brush", wrap: true);
        summary.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(summary);
        if (scan.SkippedNote is { } note)
            page.Children.Add(CustomizeUi.Notice(note));

        foreach (var entry in scan.Plugins)
            page.Children.Add(OrgPluginRow(entry));

        var detected = scan.Plugins.SelectMany(p => p.McpServerNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        RenderToolPolicies(page, detected);

        Show(page);
    }

    private void ChooseOrgFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        Services.UiSettings.Current.OrganizationPluginsFolder = dialog.FolderName;
        Services.UiSettings.Save();
        _orgScannedAt = DateTimeOffset.Now;
        ShowOrgPlugins();
    }

    private FrameworkElement OrgPluginRow(OrgPluginEntry entry)
    {
        var parts = new List<string>();
        if (entry.SkillCount > 0)
            parts.Add(PluginListPresentation.SkillCountLabel(entry.SkillCount));
        if (entry.AgentCount > 0)
            parts.Add($"{entry.AgentCount} agent{(entry.AgentCount == 1 ? "" : "s")}");
        if (entry.CommandCount > 0)
            parts.Add($"{entry.CommandCount} command{(entry.CommandCount == 1 ? "" : "s")}");
        if (entry.HasHooks)
            parts.Add("hooks");
        if (entry.McpServerNames.Count > 0)
            parts.Add($"{entry.McpServerNames.Count} MCP server{(entry.McpServerNames.Count == 1 ? "" : "s")}");

        var meta = new List<string> { OrganizationPluginScanner.ManifestLine(entry) };
        if (entry.Manifest?.Version is { Length: > 0 } version)
            meta.Add(version);
        if (entry.Manifest?.Author is { Length: > 0 } author)
            meta.Add(author);

        var chips = new List<FrameworkElement?>();
        if (entry.Health != "ok")
            chips.Add(CustomizeUi.Chip(entry.Health == "error" ? "Manifest error" : "Incomplete"));

        var body = CustomizeUi.Body(
            entry.Manifest?.Name ?? entry.Name,
            parts.Count > 0 ? string.Join(" · ", parts) : null,
            string.Join(" · ", meta),
            [.. chips]);
        return CustomizeUi.ListRow(
            body, CustomizeUi.Ghost("Open folder", () => OpenInShell(entry.Directory)), null, entry.Name);
    }

    private void RenderToolPolicies(Panel host, IReadOnlyList<string> detectedServers)
    {
        var policies = Services.UiSettings.Current.McpToolPolicies;
        var (detected, pending) = ToolPolicies.Split(detectedServers, policies);

        host.Children.Add(CustomizeUi.SectionHeader("Tool policy", 0));
        foreach (var server in detected)
            host.Children.Add(ToolPolicyRow(server, fixedName: true));
        if (detected.Count > 0)
        {
            host.Children.Add(CustomizeUi.Notice(
                "Unlisted tools stay user-controlled. Policy is keyed by server name and applies even if the plugin is later updated."));
        }

        if (pending.Count > 0)
        {
            host.Children.Add(CustomizeUi.SectionHeader("Pending policies", pending.Count));
            foreach (var server in pending)
                host.Children.Add(ToolPolicyRow(server, fixedName: false));
            host.Children.Add(CustomizeUi.Notice(
                "Applies once a plugin ships an MCP server with this name."));
        }

        var add = CustomizeUi.Ghost("+ Add server policy", AddServerPolicy);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 12, 0, 0);
        host.Children.Add(add);
    }

    private FrameworkElement ToolPolicyRow(string server, bool fixedName)
    {
        var policies = Services.UiSettings.Current.McpToolPolicies;
        policies.TryGetValue(server, out var tools);
        tools ??= [];

        var body = new StackPanel();
        var name = CustomizeUi.Text(server, 13, "Text100Brush");
        name.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");
        body.Children.Add(name);

        foreach (var (tool, value) in tools.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var toolName = CustomizeUi.Text(tool, 12, "Text400Brush");
            toolName.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");
            toolName.MinWidth = 200;
            row.Children.Add(toolName);
            row.Children.Add(CustomizeUi.Picker(
                "Policy",
                [.. ToolPolicyValues.All.Select(v => (v, ToolPolicyValues.Label(v)))],
                ToolPolicyValues.Normalize(value),
                picked => SetToolPolicy(server, tool, picked),
                held: true));
            var remove = CustomizeUi.Ghost("Remove policy", () => SetToolPolicy(server, tool, null));
            remove.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(remove);
            body.Children.Add(row);
        }

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(CustomizeUi.Ghost("Add policy", () => AddToolPolicy(server)));
        if (!fixedName)
        {
            controls.Children.Add(CustomizeUi.Kebab($"Tool policy for {server}",
            [
                ("Remove policy", true, () => RemoveServerPolicy(server)),
            ]));
        }
        return CustomizeUi.ListRow(body, controls, null, $"Tool policy for {server}");
    }

    private void AddServerPolicy()
    {
        if (InputDialog.Prompt(Window.GetWindow(this), "Server name", "", "Add") is not { } server)
            return;
        var policies = Services.UiSettings.Current.McpToolPolicies;
        if (!policies.ContainsKey(server))
            policies[server] = [];
        Services.UiSettings.Save();
        ApplyToolPolicies();
        ShowOrgPlugins();
    }

    private void AddToolPolicy(string server)
    {
        if (InputDialog.Prompt(Window.GetWindow(this), "tool name", "", "Add") is not { } tool)
            return;
        SetToolPolicy(server, tool, ToolPolicyValues.Ask);
    }

    private void SetToolPolicy(string server, string tool, string? value)
    {
        var policies = Services.UiSettings.Current.McpToolPolicies;
        if (!policies.TryGetValue(server, out var tools))
        {
            tools = [];
            policies[server] = tools;
        }
        if (value is null)
            tools.Remove(tool);
        else
            tools[tool] = ToolPolicyValues.Normalize(value);
        Services.UiSettings.Save();
        ApplyToolPolicies();
        ShowOrgPlugins();
    }

    private void RemoveServerPolicy(string server)
    {
        Services.UiSettings.Current.McpToolPolicies.Remove(server);
        Services.UiSettings.Save();
        ApplyToolPolicies();
        ShowOrgPlugins();
    }

    /// <summary>Tells the shell the policy moved, so the gate takes the new rules.</summary>
    private void ApplyToolPolicies() => ToolPoliciesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when a tool policy changed, so the session's permission gate reloads.</summary>
    public event EventHandler? ToolPoliciesChanged;
}
