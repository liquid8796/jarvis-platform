namespace JarvisCode.App.Services;

/// <summary>What a composer-menu row is.</summary>
public enum ChatMenuKind
{
    Button,
    Checkbox,
    Submenu,
    Separator,
}

/// <summary>One row of the composer's "+" menu.</summary>
/// <param name="Kind">Button, checkbox, submenu or separator.</param>
/// <param name="Label">What the row says.</param>
/// <param name="Id">A stable handle for the surface to act on.</param>
/// <param name="Subtitle">The reference's second line, where a row has one.</param>
/// <param name="Checked">A checkbox row's state.</param>
/// <param name="Suffix">Trailing text, e.g. the reconnection count on Connectors.</param>
/// <param name="Items">A submenu's rows.</param>
public sealed record ChatMenuRow(
    ChatMenuKind Kind,
    string Label,
    string? Id = null,
    string? Subtitle = null,
    bool Checked = false,
    string? Suffix = null,
    IReadOnlyList<ChatMenuRow>? Items = null);

/// <summary>One connector as the menu lists it.</summary>
/// <param name="Name">The server's name.</param>
/// <param name="Connected">Whether it is connected right now.</param>
/// <param name="Enabled">Whether this conversation has it switched on.</param>
public sealed record ChatConnectorRow(string Name, bool Connected, bool Enabled);

/// <summary>
/// The Chat composer's "+" menu, ported from the reference desktop's own
/// (<c>c752b32f8-DqlexaAe.js</c>, its <c>Za</c> builder) with the grouping it hands
/// to <c>shared-10-3-tqq7pk.js</c>'s <c>JS</c>: three groups in a fixed order —
/// <c>["uploadFile","teach","screenshot","project","github"]</c>, then
/// <c>["skills","connectors","plugins","devices"]</c>, then
/// <c>["haystack","research","webSearch","memory"]</c> — separated by the
/// "separator-tools" and "separator-modes" rules, and a group with nothing in it
/// takes no separator with it.
///
/// Only the rows this build can honour are produced; the rest are declared in the
/// parity manifest rather than rendered as dead entries.
/// </summary>
public static class ChatComposerMenu
{
    public const string AddFilesOrPhotos = "Add files or photos";      // 0S+iNJivmx
    public const string TakeAScreenshot = "Take a screenshot";          // aTaOpCB71M
    public const string CouldNotCaptureScreen = "Could not capture screen."; // uGOETn5bfV
    public const string AddToProject = "Add to project";                // +mqxxzVczn
    public const string AddFromGitHub = "Add from GitHub";              // h3J3E4bRfj
    public const string Skills = "Skills";                              // EJSVsOA19u
    public const string Connectors = "Connectors";                      // 2mMJRvsAg1
    public const string AddConnector = "Add connector";                 // QDa8Q+GNMs
    public const string ManageConnectors = "Manage connectors";         // cqx+RdJnHp
    public const string Plugins = "Plugins";                            // GVkVQAl/Q1
    public const string WebSearch = "Web search";                       // x0W7xxxEcv
    public const string ToolAccess = "Tool access";                     // qCcDQ3Zz4w
    public const string LoadToolsWhenNeeded = "Load tools when needed";  // TQakOWx/s6
    public const string ToolsAlreadyLoaded = "Tools already loaded";     // 2L6XHrHX3B
    public const string LoadWhenNeededHint =
        "Chats compact less since tools aren’t pre-loaded.";             // 2HdJ3H52A7
    public const string AlreadyLoadedHint =
        "Chats compact more often since tools are always there.";        // hLphZYtEL1
    public const string SearchProjects = "Search projects";              // doPT7U8q21
    public const string NoMatches = "No matches";                        // 96GJ5wSQam
    public const string NoProjectsYet = "No projects yet";               // gkvdf7tU5r
    public const string StartANewProject = "Start a new project";        // 0y7bg6nA29
    public const string ExtendedThinking = "Extended thinking";          // 5j4WakKa2u

    /// <summary>"{count} needs reconnection" — the suffix on the Connectors row.</summary>
    public static string NeedsReconnection(int count) =>
        // meR9n7Np9W
        count == 1 ? "1 needs reconnection" : $"{count} need reconnection";

    /// <summary>The Chat composer's "+" menu, in the reference's groups and order.</summary>
    public static IReadOnlyList<ChatMenuRow> Plus(
        bool screenshotAvailable,
        bool projectsAvailable,
        bool gitHubAvailable,
        IReadOnlyList<ChatConnectorRow> connectors,
        ChatToolAccess toolAccess,
        bool webSearch)
    {
        List<ChatMenuRow> main =
        [
            new(ChatMenuKind.Button, AddFilesOrPhotos, "files"),
        ];
        if (screenshotAvailable)
        {
            main.Add(new(ChatMenuKind.Button, TakeAScreenshot, "screenshot"));
        }

        if (projectsAvailable)
        {
            main.Add(new(ChatMenuKind.Submenu, AddToProject, "project"));
        }

        if (gitHubAvailable)
        {
            main.Add(new(ChatMenuKind.Button, AddFromGitHub, "github"));
        }

        List<ChatMenuRow> tools =
        [
            new(ChatMenuKind.Submenu, Skills, "skills"),
            ConnectorsRow(connectors, toolAccess),
            new(ChatMenuKind.Submenu, Plugins, "plugins"),
        ];

        List<ChatMenuRow> modes =
        [
            new(ChatMenuKind.Checkbox, WebSearch, "web-search", Checked: webSearch),
        ];

        return [.. Join(main, tools, modes)];
    }

    /// <summary>
    /// The Connectors submenu: the connectors themselves, then "Manage connectors"
    /// in the header slot and the Tool access submenu in the footer, which is where
    /// the reference puts each.
    /// </summary>
    public static ChatMenuRow ConnectorsRow(
        IReadOnlyList<ChatConnectorRow> connectors, ChatToolAccess toolAccess)
    {
        var disconnected = connectors.Count(static c => !c.Connected);
        List<ChatMenuRow> items =
        [
            new(ChatMenuKind.Button, ManageConnectors, "manage-connectors"),
        ];
        if (connectors.Count > 0)
        {
            items.Add(new(ChatMenuKind.Separator, ""));
            items.AddRange(connectors.Select(static c =>
                new ChatMenuRow(ChatMenuKind.Checkbox, c.Name, "connector:" + c.Name, Checked: c.Enabled)));
        }

        items.Add(new(ChatMenuKind.Separator, ""));
        items.Add(ToolAccessRow(toolAccess));

        return new ChatMenuRow(
            ChatMenuKind.Submenu,
            connectors.Count > 0 ? Connectors : AddConnector,
            "connectors",
            Suffix: disconnected > 0 ? NeedsReconnection(disconnected) : null,
            Items: items);
    }

    /// <summary>The Tool access submenu, with the reference's two rows and their hints.</summary>
    public static ChatMenuRow ToolAccessRow(ChatToolAccess current) =>
        new(ChatMenuKind.Submenu, ToolAccess, "tool-access", Items:
        [
            new(ChatMenuKind.Checkbox, LoadToolsWhenNeeded, "tool-access:on",
                LoadWhenNeededHint, current == ChatToolAccess.LoadWhenNeeded),
            new(ChatMenuKind.Checkbox, ToolsAlreadyLoaded, "tool-access:off",
                AlreadyLoadedHint, current == ChatToolAccess.AlreadyLoaded),
        ]);

    /// <summary>
    /// The "Add to project" submenu: the projects, then "Start a new project" in
    /// the footer, and the reference's empty and no-match rows in between.
    /// </summary>
    public static IReadOnlyList<ChatMenuRow> ProjectPicker(
        IReadOnlyList<string> projects, string? currentProjectName, string query, bool anyExist)
    {
        List<ChatMenuRow> rows = [];
        if (projects.Count > 0)
        {
            rows.AddRange(projects.Select(name => new ChatMenuRow(
                ChatMenuKind.Checkbox, name, "project:" + name,
                Checked: string.Equals(name, currentProjectName, StringComparison.Ordinal))));
        }
        else
        {
            rows.Add(new(ChatMenuKind.Button,
                query.Trim().Length > 0 ? NoMatches : NoProjectsYet, "none"));
        }

        if (!anyExist || query.Trim().Length > 0)
        {
            rows.Add(new(ChatMenuKind.Separator, ""));
        }

        rows.Add(new(ChatMenuKind.Button, StartANewProject, "new-project"));
        return rows;
    }

    /// <summary>Joins the three groups with the reference's two separators, skipping empty groups.</summary>
    private static IEnumerable<ChatMenuRow> Join(
        IReadOnlyList<ChatMenuRow> main, IReadOnlyList<ChatMenuRow> tools, IReadOnlyList<ChatMenuRow> modes)
    {
        foreach (var row in main)
        {
            yield return row;
        }

        if (tools.Count > 0)
        {
            yield return new ChatMenuRow(ChatMenuKind.Separator, "");
            foreach (var row in tools)
            {
                yield return row;
            }
        }

        if (modes.Count > 0)
        {
            yield return new ChatMenuRow(ChatMenuKind.Separator, "");
            foreach (var row in modes)
            {
                yield return row;
            }
        }
    }
}
