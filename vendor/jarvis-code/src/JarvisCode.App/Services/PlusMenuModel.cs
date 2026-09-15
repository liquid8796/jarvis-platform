namespace JarvisCode.App.Services;

/// <summary>What a row of the composer's "+" menu does.</summary>
public enum PlusMenuAction
{
    None,
    AddFiles,
    AddFolder,
    ImportGithubIssue,
    ImportLinearIssue,
    SlashCommands,
    ToggleConnector,
    ReconnectConnector,
    ManageConnectors,
    BrowseConnectors,
    AddConnectors,
    InsertSkill,
    ManagePlugins,
    BrowsePlugins,
    AddPlugins,
}

/// <summary>One row of the "+" menu: a plain row, a submenu or a rule.</summary>
public sealed record PlusMenuRow(
    string Label,
    PlusMenuAction Action = PlusMenuAction.None,
    string? Shortcut = null,
    string? Argument = null,
    string? Description = null,
    string? Tooltip = null,
    bool Disabled = false,
    bool SeparatorBefore = false,
    bool? Checked = null,
    IReadOnlyList<PlusMenuRow>? Children = null)
{
    public bool IsSubmenu => Children is { Count: > 0 };
}

/// <summary>A connector the menu can switch on or off for the turn.</summary>
public sealed record PlusMenuConnector(string Name, bool Connected, bool Enabled, bool NeedsReconnect, int ToolCount);

/// <summary>A plugin and the skills it offers, for the Plugins submenu.</summary>
public sealed record PlusMenuPlugin(string Name, IReadOnlyList<PlusMenuSkill> Skills);

/// <summary>One skill row inside a plugin's submenu.</summary>
public sealed record PlusMenuSkill(string Command, string? Description);

/// <summary>What the "+" menu reads about the session.</summary>
public sealed record PlusMenuContext
{
    /// <summary>The composer takes files as well as images (the reference's `supportsFileAttachments`).</summary>
    public bool SupportsFileAttachments { get; init; } = true;

    /// <summary>The chord shown beside "Add files or photos".</summary>
    public string? AddFilesShortcut { get; init; }

    public bool CanAddFolder { get; init; } = true;

    /// <summary>The session has not started yet — the reference's `phase === "draft"`.</summary>
    public bool IsDraft { get; init; }

    public bool GithubIssueAvailable { get; init; }

    /// <summary>"no-github-remote" or "not-authenticated", the reference's two reasons.</summary>
    public string? GithubIssueUnavailableReason { get; init; }

    public bool LinearIssueAvailable { get; init; }

    /// <summary>
    /// The session can switch a connector on and off for the turn. The reference's
    /// rows carry a switch because it edits the session's own mcp config; this build
    /// configures connectors per installation, so its rows open the page instead.
    /// </summary>
    public bool CanToggleConnectors { get; init; }

    public IReadOnlyList<PlusMenuConnector> Connectors { get; init; } = [];

    public IReadOnlyList<PlusMenuPlugin> Plugins { get; init; } = [];
}

/// <summary>
/// The composer's "+" menu, row for row (desktop 1.40609.1.0, ccd chunk
/// `cd089cf92`: the trigger `Jc`@93300 which prepends "Add files or photos", the
/// prefix rows at 96400, the label table `jc`@86030 and the Connectors/Plugins
/// builder `$c`@86951). Pure so the order and every gate are unit-testable.
/// </summary>
public static class PlusMenuModel
{
    public const string AddFilesOrPhotos = "Add files or photos";
    public const string AddImage = "Add image";
    public const string AddFolder = "Add folder";
    public const string ImportGithubIssue = "Import GitHub issue";
    public const string ImportLinearIssue = "Import Linear issue";
    public const string SlashCommands = "Slash commands";
    public const string Connectors = "Connectors";
    public const string ManageConnectors = "Manage connectors";
    public const string AddConnectors = "Add connectors";
    public const string BrowseConnectors = "Browse connectors";
    public const string Reconnect = "Reconnect";
    public const string Connecting = "Connecting…";
    public const string NoTools = "This connector has no tools available";
    public const string Plugins = "Plugins";
    public const string ManagePlugins = "Manage plugins";
    public const string BrowsePlugins = "Browse plugins";
    public const string AddPlugins = "Add plugins…";
    public const string NoGithubRemote = "This folder doesn’t have a GitHub remote";
    public const string ConnectGithub = "Connect GitHub in Settings or run gh auth login";

    /// <summary>
    /// The whole menu in the reference's order: Add files or photos, Add folder,
    /// Import GitHub issue, Import Linear issue, Slash commands, Connectors, Plugins.
    /// </summary>
    public static IReadOnlyList<PlusMenuRow> Build(PlusMenuContext c)
    {
        var rows = new List<PlusMenuRow>
        {
            new(c.SupportsFileAttachments ? AddFilesOrPhotos : AddImage, PlusMenuAction.AddFiles,
                Shortcut: c.AddFilesShortcut),
        };

        if (c.CanAddFolder)
        {
            rows.Add(new PlusMenuRow(AddFolder, PlusMenuAction.AddFolder));
        }

        if (c.IsDraft && c.GithubIssueAvailable)
        {
            rows.Add(new PlusMenuRow(ImportGithubIssue, PlusMenuAction.ImportGithubIssue));
        }
        else if (c.IsDraft && c.GithubIssueUnavailableReason is { } reason)
        {
            rows.Add(new PlusMenuRow(ImportGithubIssue, PlusMenuAction.ImportGithubIssue, Disabled: true,
                Tooltip: reason == "no-github-remote" ? NoGithubRemote : ConnectGithub));
        }

        if (c.IsDraft && c.LinearIssueAvailable)
        {
            rows.Add(new PlusMenuRow(ImportLinearIssue, PlusMenuAction.ImportLinearIssue));
        }

        rows.Add(new PlusMenuRow(SlashCommands, PlusMenuAction.SlashCommands));

        if (BuildConnectors(c) is { } connectors)
        {
            rows.Add(connectors);
        }

        if (BuildPlugins(c) is { } plugins)
        {
            rows.Add(plugins);
        }

        return rows;
    }

    /// <summary>
    /// The Connectors row (`j`): a flat "Add connectors" while nothing is configured,
    /// otherwise a submenu of switch rows, then a rule, "Manage connectors" and
    /// "Browse connectors". A connector still coming up contributes a disabled
    /// "Connecting…" row, and one whose auth lapsed a "Reconnect" row.
    /// </summary>
    public static PlusMenuRow? BuildConnectors(PlusMenuContext c)
    {
        var rows = new List<PlusMenuRow>();
        var reconnect = new List<PlusMenuRow>();
        var connecting = 0;
        foreach (var connector in c.Connectors)
        {
            if (connector.NeedsReconnect)
            {
                reconnect.Add(new PlusMenuRow(connector.Name, PlusMenuAction.ReconnectConnector,
                    Argument: connector.Name, Description: Reconnect));
                continue;
            }

            if (!connector.Connected)
            {
                connecting++;
                continue;
            }

            if (connector.ToolCount == 0)
            {
                rows.Add(new PlusMenuRow(connector.Name, PlusMenuAction.ToggleConnector,
                    Argument: connector.Name, Disabled: true, Tooltip: NoTools));
                continue;
            }

            rows.Add(c.CanToggleConnectors
                ? new PlusMenuRow(connector.Name, PlusMenuAction.ToggleConnector,
                    Argument: connector.Name, Checked: connector.Enabled)
                : new PlusMenuRow(connector.Name, PlusMenuAction.ManageConnectors,
                    Argument: connector.Name));
        }

        var any = rows.Count > 0 || reconnect.Count > 0;
        if (!any && connecting == 0)
        {
            return new PlusMenuRow(AddConnectors, PlusMenuAction.AddConnectors);
        }

        var children = new List<PlusMenuRow>(rows);
        children.AddRange(reconnect);
        if (connecting > 0 && !any)
        {
            children.Add(new PlusMenuRow(Connecting, Disabled: true));
        }

        children.Add(new PlusMenuRow(ManageConnectors, PlusMenuAction.ManageConnectors, SeparatorBefore: true));
        children.Add(new PlusMenuRow(BrowseConnectors, PlusMenuAction.BrowseConnectors));
        return new PlusMenuRow(Connectors, Children: children);
    }

    /// <summary>
    /// The Plugins row (`B`, its local branch): a flat "Add plugins…" while no
    /// installed plugin carries a skill, otherwise one submenu per plugin listing its
    /// skills, then a rule, "Manage plugins" and "Browse plugins".
    /// </summary>
    public static PlusMenuRow? BuildPlugins(PlusMenuContext c)
    {
        var withSkills = c.Plugins.Where(static p => p.Skills.Count > 0).ToList();
        if (withSkills.Count == 0)
        {
            return new PlusMenuRow(AddPlugins, PlusMenuAction.AddPlugins);
        }

        var children = new List<PlusMenuRow>();
        foreach (var plugin in withSkills)
        {
            children.Add(new PlusMenuRow(plugin.Name, Children:
            [
                .. plugin.Skills.Select(skill => new PlusMenuRow(skill.Command, PlusMenuAction.InsertSkill,
                    Argument: skill.Command, Description: skill.Description)),
            ]));
        }

        children.Add(new PlusMenuRow(ManagePlugins, PlusMenuAction.ManagePlugins, SeparatorBefore: true));
        children.Add(new PlusMenuRow(BrowsePlugins, PlusMenuAction.BrowsePlugins));
        return new PlusMenuRow(Plugins, Children: children);
    }
}
