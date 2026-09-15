using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>
/// What the reference's computer-use server answers a grant question with —
/// its JSON shapes and its sentences, ported from the installed desktop
/// (app.asar 1.40609.1.0, <c>.vite/build/index.chunk-BBGcne9A.js</c>: the tool
/// dispatch's <c>tn</c>, <c>on</c>, <c>jn</c> and the two clipboard handlers).
///
/// These are results the model reads, so they are verbatim. They live here
/// rather than inside the tools because the shaping is what is worth testing:
/// which sentence a tier draws, and which key an answer carries.
/// </summary>
public static class ComputerUseGrantResults
{
    /// <summary>
    /// The reference's <c>Ve</c>, closing every tier-guidance block: the
    /// restriction is the point, and a workaround defeats it.
    /// </summary>
    public const string NoWorkaround =
        " Do not attempt to work around this restriction — never use AppleScript, System Events, shell " +
        "commands, or any other method to send clicks or keystrokes to this app.";

    /// <summary>The reference's <c>B</c>: one refusal for every string argument.</summary>
    public static string MustBeAString(string argument) => $"\"{argument}\" must be a string.";

    /// <summary>
    /// The reference's <c>tn</c>: one paragraph per restricted kind, in its
    /// order — browsers, other read-tier apps, terminals and IDEs, the Windows
    /// shell — joined by blank lines and closed by <see cref="NoWorkaround"/>.
    /// Empty when nothing was granted at a restricted tier.
    /// </summary>
    public static string TierGuidance(IReadOnlyList<(string App, AppTier Tier, string Category)> granted)
    {
        var browsers = granted.Where(static g => g.Tier == AppTier.Read && g.Category == "browser").ToList();
        var otherRead = granted.Where(static g => g.Tier == AppTier.Read && g.Category != "browser").ToList();
        var tools = granted.Where(static g => g.Tier == AppTier.Click && g.Category != "shell").ToList();
        var shell = granted.Where(static g => g.Tier == AppTier.Click && g.Category == "shell").ToList();

        List<string> paragraphs = [];
        if (browsers.Count > 0)
        {
            paragraphs.Add(
                $"{Quote(browsers)} {(browsers.Count == 1 ? "is a browser" : "are browsers")} — granted at tier " +
                "\"read\" (visible in screenshots only; no clicks or typing). You can read what's on screen but " +
                $"cannot navigate, click, or type into {(browsers.Count == 1 ? "it" : "them")}. For browser " +
                "interaction, use the Claude-in-Chrome MCP (tools named `mcp__claude-in-chrome__*`; load via " +
                "ToolSearch if deferred).");
        }

        if (otherRead.Count > 0)
        {
            paragraphs.Add(
                $"{Quote(otherRead)} {(otherRead.Count == 1 ? "is" : "are")} granted at tier \"read\" (visible in " +
                "screenshots only; no clicks or typing). You can read what's on screen but cannot interact. Ask " +
                $"the user to take any actions in {(otherRead.Count == 1 ? "this app" : "these apps")} themselves.");
        }

        if (tools.Count > 0)
        {
            paragraphs.Add(
                $"{Quote(tools)} {(tools.Count == 1 ? "has" : "have")} terminal or IDE capabilities — granted at " +
                "tier \"click\" (visible + plain left-click only; NO typing, key presses, right-click, " +
                "modifier-clicks, or drag-drop). You can click buttons and scroll output, but " +
                $"{(tools.Count == 1 ? "its" : "their")} integrated terminal and editor are off-limits to " +
                "keyboard input. Right-click (context-menu Paste) and dragging text onto " +
                $"{(tools.Count == 1 ? "it" : "them")} require tier \"full\". For shell commands, use the Bash tool.");
        }

        if (shell.Count > 0)
        {
            paragraphs.Add(
                $"{Quote(shell)} {(shell.Count == 1 ? "is" : "are")} the Windows desktop shell — granted at tier " +
                "\"click\" (visible + plain left-click only; NO typing, key presses, right-click, " +
                "modifier-clicks, or drag-drop). You can click to open folders and items, but typing is blocked: " +
                "the address bar, Search box, and Run dialog hand typed text to ShellExecute. For shell " +
                "commands, use the Bash tool.");
        }

        return paragraphs.Count == 0 ? "" : string.Join("\n\n", paragraphs) + NoWorkaround;
    }

    /// <summary>
    /// The reference's <c>on</c>: the refusal for names its application index
    /// does not know. It says plainly that nothing was shown to the user, which
    /// is the fact the model needs — a silent failure would read as a denial.
    /// </summary>
    /// <param name="flagsRequested">
    /// Whether the call also asked for clipboard or system-key flags. The
    /// reference names them, because they were short-circuited too.
    /// </param>
    public static string NotInstalled(
        IReadOnlyList<(string Requested, IReadOnlyList<string> DidYouMean)> unknown, bool flagsRequested)
    {
        var names = string.Join(", ", unknown.Select(static u => $"\"{u.Requested}\""));
        var one = unknown.Count == 1;
        var suggestions = unknown.Where(static u => u.DidYouMean.Count > 0).ToList();
        var didYouMean = suggestions.Count > 0
            ? " Did you mean: " + string.Join("; ", suggestions.Select(static u =>
                $"{u.Requested} → {string.Join(" or ", u.DidYouMean.Select(static d => $"\"{d}\""))}")) + "?"
            : "";

        return
            $"{names} {(one ? "doesn't" : "don't")} match any installed or running application. The request was " +
            $"NOT shown to the user.{didYouMean} Retry request_access with the corrected " +
            $"name{(one ? "" : "s")} (include any other apps from this call too — the whole call was " +
            $"short-circuited{(flagsRequested ? ", as were the clipboard/systemKeyCombos flags you passed" : "")}). " +
            "If you're unsure of the exact name, ask the user.";
    }

    /// <summary>
    /// The reference's <c>jn</c>: list_granted_applications answers with JSON,
    /// not prose — <c>{"allowedApps":[…],"grantFlags":{…}}</c>.
    /// </summary>
    public static string GrantedApplications(
        IReadOnlyList<(string App, AppTier Tier)> granted,
        bool clipboardRead,
        bool clipboardWrite,
        bool systemKeyCombos) =>
        new JsonObject
        {
            ["allowedApps"] = new JsonArray([.. granted.Select(static g => (JsonNode)new JsonObject
            {
                // On Windows the reference's bundleId is the executable name and
                // its displayName the Start-menu one; this app knows the two by
                // the same string, so both carry it rather than one being blank.
                ["bundleId"] = g.App,
                ["displayName"] = g.App,
                ["tier"] = ComputerUseGrants.TierName(g.Tier),
            })]),
            ["grantFlags"] = new JsonObject
            {
                ["clipboardRead"] = clipboardRead,
                ["clipboardWrite"] = clipboardWrite,
                ["systemKeyCombos"] = systemKeyCombos,
            },
        }.ToJsonString();

    /// <summary>The reference's request_access answer, with the keys it only adds when they apply.</summary>
    public static string AccessGranted(
        IReadOnlyList<(string App, AppTier Tier)> granted,
        IReadOnlyList<string> denied,
        string tierGuidance)
    {
        var body = new JsonObject
        {
            ["granted"] = new JsonArray([.. granted.Select(static g => (JsonNode)new JsonObject
            {
                ["bundleId"] = g.App,
                ["displayName"] = g.App,
                ["tier"] = ComputerUseGrants.TierName(g.Tier),
            })]),
            ["denied"] = new JsonArray([.. denied.Select(static d => (JsonNode)d)]),
        };

        if (tierGuidance.Length > 0)
        {
            body["tierGuidance"] = tierGuidance;
        }

        // Windows filters an ungranted window by painting over it; the reference
        // reports the capability so the model knows what a screenshot will show.
        body["screenshotFiltering"] = "mask";
        return body.ToJsonString();
    }

    // ---- clipboard -------------------------------------------------------------

    public const string ClipboardReadNotGranted =
        "Clipboard read is not granted. Request `clipboardRead` via request_access.";

    public const string ClipboardWriteNotGranted =
        "Clipboard write is not granted. Request `clipboardWrite` via request_access.";

    public const string ClipboardWritten = "Clipboard written.";

    /// <summary>Read answers with JSON, as the reference's <c>L({text})</c> does.</summary>
    public static string ClipboardText(string text) =>
        new JsonObject { ["text"] = text }.ToJsonString();

    /// <summary>
    /// The reference blocks a clipboard write while a tier-"click" app is in
    /// front: the only way that app could use what was written is a Paste it is
    /// not allowed to reach.
    /// </summary>
    public static string ClipboardWriteBlockedByClickTier(string app) =>
        $"\"{app}\" is a tier-\"click\" app and currently frontmost. write_clipboard is blocked because the " +
        "next action would clear the clipboard anyway — a UI Paste button in this app cannot be used to " +
        "inject text. Bring a tier-\"full\" app forward before writing to the clipboard.";

    // ---- open_application / switch_display -------------------------------------

    public static string NotGranted(string app) =>
        $"\"{app}\" is not granted for this session. Call request_access first.";

    public static string Opened(string app) => $"Opened \"{app}\".";

    /// <summary>What the reference adds when more than one monitor is attached.</summary>
    public static string OpenedWithMonitorNote(string app) =>
        $"Opened \"{app}\". If it isn't visible in the next screenshot, it may have opened on a different " +
        "monitor — use switch_display to check.";

    public const string DisplaySwitchingUnavailable =
        "Display switching is not available in this session.";

    public const string ReturnedToAutomaticDisplay =
        "Returned to automatic monitor selection. Call screenshot to continue.";

    public const string OnlyOneMonitor =
        "Only one monitor is connected. There is nothing to switch to.";

    public static string SwitchedToMonitor(string name) =>
        $"Switched to monitor \"{name}\". Call screenshot to see it.";

    public static string NoSuchMonitor(string requested, IEnumerable<string> available) =>
        $"No monitor named \"{requested}\" is connected. Available monitors: " +
        string.Join(", ", available.Select(static name => $"\"{name}\"")) + ".";

    /// <summary>
    /// The reference's <c>Xt</c>: a request for the assistant's own application
    /// can never be granted, because operating it would let the model change the
    /// permissions that bound it.
    /// </summary>
    public const string SelfAppDenied =
        "You requested access to Jarvis's own application. Jarvis cannot be granted control of its own " +
        "window: doing so would let you operate Jarvis's own interface and change your own permissions, " +
        "settings, and allowed behaviors. This can never be granted — do not request it again. To operate a " +
        "different application, request access to that application by name instead.";

    /// <summary>
    /// The reference's <c>Zt</c>, the tail both restricted-category refusals
    /// carry: the refusal is a confirmation step, not a block, and it lasts one
    /// turn.
    /// </summary>
    private const string RetryThisTurn =
        " If you genuinely need this restricted access, call request_access again right now, in THIS SAME " +
        "turn — do not stop to reply to the user first. This is a one-time confirmation that only lasts for " +
        "the current turn: if you respond to the user and retry in a later turn, you will get this same " +
        "message again (it is not a permanent block). The user still approves the grant in the dialog that " +
        "the retry brings up.";

    /// <summary>The reference's <c>Qt</c>: the first request that names a browser.</summary>
    public const string BrowserFirstRequest =
        "You requested access to a browser. It is rare for this to be required: browser applications can " +
        "only ever be granted in 'read' mode, so you cannot use them to interact with websites — you can " +
        "only see what is already on screen. Only request browser access if the user specifically wants you " +
        "to see exactly what they are looking at. For all other browser interaction (navigating, clicking, " +
        "typing, filling forms), you must use the claude-in-chrome MCP instead." + RetryThisTurn;

    /// <summary>
    /// The reference's <c>$t</c>: the first request that names a terminal or IDE.
    /// Its own sentence names the Bash tool; this build registers two shells and
    /// names the one Windows always has, as its other tier refusals do.
    /// </summary>
    public const string TerminalFirstRequest =
        "You requested access to a terminal or IDE. It is rare for this to be required: these applications " +
        "can only ever be granted in 'click' mode — you can see them and left-click, but you cannot type, " +
        "press keys, or paste into them. To run shell commands, use the PowerShell tool instead." +
        RetryThisTurn;

    /// <summary>
    /// The refusal for a category this session has not been warned about yet.
    /// The reference warns once per category per session (its
    /// <c>getAccessWarned</c> / <c>onAccessWarned</c>) and joins two of them with
    /// a blank line.
    /// </summary>
    public static string FirstRequestWarning(IEnumerable<string> categories) =>
        string.Join("\n\n", categories.Select(static category => category switch
        {
            "browser" => BrowserFirstRequest,
            _ => TerminalFirstRequest,
        }));

    private static string Quote(IEnumerable<(string App, AppTier Tier, string Category)> apps) =>
        string.Join(", ", apps.Select(static a => $"\"{a.App}\""));
}
