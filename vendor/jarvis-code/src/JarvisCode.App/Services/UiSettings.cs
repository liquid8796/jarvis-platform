using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JarvisCode.App.Theming;

namespace JarvisCode.App.Services;

/// <summary>
/// Presentation-only state (theme, window bounds, hotkeys, panel layout).
/// Deliberately separate from JarvisCode.Core's AppSettings, which carries no
/// window/layout/theme fields by design.
/// </summary>
public sealed class UiSettings
{
    public string ActiveTheme { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    /// <summary>default | sans | system | dyslexia</summary>
    public string ChatFont { get; set; } = "default";

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public bool SidebarCollapsed { get; set; }

    /// <summary>Sidebar column width (drag-resizable, 200–400).</summary>
    public double SidebarWidth { get; set; } = 288;

    /// <summary>
    /// The Code surface's pane mosaic, as JSON. The reference keeps one tileLayout in its
    /// own store rather than one per session, and so does this.
    /// </summary>
    public string TileLayout { get; set; } = "";

    /// <summary>Content zoom (Ctrl+= / Ctrl+- / Ctrl+0), 1.0 = actual size.</summary>
    public double ZoomFactor { get; set; } = 1.0;

    /// <summary>Wheel scroll multiplier for the transcript (/scroll-speed), 0.25–5.</summary>
    public double ScrollSpeed { get; set; } = 1.0;

    /// <summary>/tui fullscreen — a chromeless maximized window instead of the normal frame.</summary>
    public bool FullscreenMode { get; set; }

    /// <summary>
    /// /statusline — a shell command whose first stdout line renders under the
    /// composer. It receives the session context as JSON on stdin.
    /// </summary>
    public string StatuslineCommand { get; set; } = "";

    /// <summary>
    /// The active output style, by name, or "default" for none. The style adds
    /// a block to the system prompt and, for two of the four, a reminder on
    /// every turn (<see cref="OutputStyles"/>).
    /// </summary>
    public string OutputStyle { get; set; } = OutputStyles.DefaultName;

    /// <summary>
    /// The Code transcript's text size: "small", "medium" or "large". The
    /// reference calls this "Transcript text size" and applies it with
    /// data-chat-text-size; every other value in that renderer derives from it.
    /// </summary>
    public string TranscriptTextSize { get; set; } = "medium";

    /// <summary>chat | code — the surface tab restored at startup.</summary>
    public string LastSurface { get; set; } = "chat";

    public string? LastSessionId { get; set; }

    public bool QuickEntryEnabled { get; set; } = true;
    public bool ThemePickerHotkeyEnabled { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool StartInTray { get; set; }
    public bool ReduceMotion { get; set; }

    /// <summary>The reference's "Dynamic workflows" switch in /config.</summary>
    public bool DynamicWorkflowsEnabled { get; set; } = true;

    /// <summary>
    /// The reference's <c>editorMode</c>: "normal" or "vim". The CLI's composer
    /// reads it to decide whether the vim layer is live.
    /// </summary>
    public string EditorMode { get; set; } = "normal";

    /// <summary>
    /// How many times a session has been started here. The reference measures
    /// its startup tips' cooldowns in this, not in wall-clock time.
    /// </summary>
    public int StartupCount { get; set; }

    /// <summary>The startup count each tip was last shown at.</summary>
    public Dictionary<string, int> TipLastShown { get; set; } = [];

    /// <summary>How many times each tip has ever been shown, for its lifetime cap.</summary>
    public Dictionary<string, int> TipShownCount { get; set; } = [];

    /// <summary>How far through the /powerup lessons this profile has read.</summary>
    public int PowerupIndex { get; set; }

    /// <summary>How spawned teammates execute (tmux, iterm2, in-process, auto).</summary>
    public string TeammateMode { get; set; } = "auto";

    /// <summary>
    /// The reference's "Dynamic workflow size" choice: unrestricted, small,
    /// medium or large. Empty means the default (medium) with its /config hint.
    /// </summary>
    public string WorkflowSize { get; set; } = "";

    /// <summary>Ping (tray balloon) when Jarvis finishes work or needs input while the window is in the background.</summary>
    public bool NotifyWhenDone { get; set; } = true;

    /// <summary>
    /// Software rendering, the reference's "Disable Hardware Acceleration". Read at
    /// start-up only, which is why the menu row asks for a restart.
    /// </summary>
    public bool HardwareAccelerationDisabled { get; set; }

    /// <summary>
    /// Whether this profile owns the jarvis-code:// protocol registration. On by
    /// default: a copied session link that opens nothing is a broken feature, and
    /// the switch is there to take the registration away, not to grant it.
    /// </summary>
    public bool DeepLinksRegistered { get; set; } = true;

    /// <summary>Check GitHub releases for a newer build (the reference's auto-update).</summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>Whether the one-time "Auto mode is now the default" notice has been acknowledged.</summary>
    public bool AutoModeNoticeSeen { get; set; }

    /// <summary>Prevents system sleep while a response or routine is running.</summary>
    public bool KeepComputerAwake { get; set; }

    /// <summary>
    /// Auto-archive local Code sessions after this many days of no activity; 0 is
    /// the reference's "Never" (its <c>ccAutoArchiveInactiveDays</c> defaults to 0).
    /// </summary>
    public int AutoArchiveDays { get; set; }

    /// <summary>Auto-archive a session when a PR it subscribed to is merged or closed (the reference's <c>ccAutoArchiveOnPrClose</c>).</summary>
    public bool AutoArchiveOnPrClose { get; set; }

    // ---- Settings › Claude Code (the reference's claude-code page) ----

    /// <summary>
    /// "Interface font": "anthropic" or "system" — the reference's <c>systemFont</c>
    /// sidebar-store boolean, exposed under the two labels its segmented control shows.
    /// </summary>
    public string InterfaceFont { get; set; } = "anthropic";

    /// <summary>
    /// "Transcript width": s | m | l — the reference's <c>transcriptWidthChoice</c>,
    /// which is null until the user picks one and reads as "s" (Narrow) then
    /// (its <c>Zb</c>: <c>transcriptWidthChoice ?? "s"</c>).
    /// </summary>
    public string TranscriptWidth { get; set; } = "s";

    /// <summary>The Shiki theme id for code in light mode (the reference's <c>codeThemeLight</c>).</summary>
    public string CodeThemeLight { get; set; } = "claude-light";

    /// <summary>The Shiki theme id for code in dark mode (the reference's <c>codeThemeDark</c>).</summary>
    public string CodeThemeDark { get; set; } = "claude-dark";

    /// <summary>"Code font": a monospace family for code and the terminal (the reference's <c>editorFont</c>); empty keeps the app's own.</summary>
    public string CodeFont { get; set; } = "";

    /// <summary>
    /// Per-type notification level — permission | question | idle → off | badge | banner
    /// (the reference's <c>notificationLevels</c>). A type with no entry is "banner".
    /// </summary>
    public Dictionary<string, string> NotificationLevels { get; set; } = [];

    /// <summary>"Notification sound": system | none (the reference's <c>notificationSound</c>).</summary>
    public string NotificationSound { get; set; } = "system";

    /// <summary>
    /// "Draw attention on notifications": flash the taskbar button when Jarvis needs
    /// attention and the window is not focused (the reference's <c>dockBounceEnabled</c>, off).
    /// </summary>
    public bool DrawAttentionOnNotifications { get; set; }

    /// <summary>
    /// "Worktree location": "default" keeps worktrees beside the project; anything
    /// else is the absolute folder the user picked (the reference's
    /// <c>chillingSlothLocation</c>, "default" or {customPath}).
    /// </summary>
    public string WorktreeLocation { get; set; } = "default";

    /// <summary>"Branch prefix" for the branches this app creates (the reference's <c>ccBranchPrefix</c>, which it defaults to its own name).</summary>
    public string BranchPrefix { get; set; } = DefaultBranchPrefix;

    public const string DefaultBranchPrefix = "jarvis";

    /// <summary>"Create pull requests automatically" (the reference's <c>ccr_auto_create_pr_on_push</c>, off).</summary>
    public bool CreatePullRequestsAutomatically { get; set; }

    /// <summary>"Create as draft" for auto-created pull requests (the reference's <c>ccr_auto_create_pr_as_draft</c>, on).</summary>
    public bool CreatePullRequestsAsDraft { get; set; } = true;

    /// <summary>
    /// "Allow bypass permissions mode": whether the composer's mode menu offers
    /// Bypass at all (the reference's <c>bypassPermissionsModeEnabled</c>, off).
    /// </summary>
    public bool BypassPermissionsModeEnabled { get; set; }

    /// <summary>
    /// "Persist sessions": none | shared | session (the reference's
    /// <c>launchPreviewStorage</c>: Don't keep / Shared / Separate).
    /// </summary>
    public string BrowserPreviewStorage { get; set; } = "none";

    /// <summary>"Allowed sites": origins the Browser tools may act on without asking (the reference's <c>launchPreviewAllowedOrigins</c>).</summary>
    public List<string> BrowserAllowedOrigins { get; set; } = [];

    // ---- Settings › General ----

    /// <summary>"Full name" on the profile (the reference account's <c>full_name</c>).</summary>
    public string FullName { get; set; } = "";

    /// <summary>"What best describes your work?" — one of the reference's work-function list, or empty.</summary>
    public string WorkFunction { get; set; } = "";

    /// <summary>
    /// "Instructions for Jarvis": free text the user wants kept in mind across chats
    /// (the reference account's <c>conversation_preferences</c>).
    /// </summary>
    public string AssistantInstructions { get; set; } = "";

    /// <summary>Avatar variant 1–72 as the reference's randomizer picks it; 0 shows initials.</summary>
    public int AvatarVariant { get; set; }

    /// <summary>"New chat view": start new chats in the side-by-side model comparison view (the reference's <c>iron_swift_default_opt_out</c>, inverted).</summary>
    public bool NewChatComparisonView { get; set; } = true;

    /// <summary>
    /// The comparison view's "Hide model names" switch (the reference's account
    /// setting <c>iron_swift_hide_model_names</c>, which defaults on).
    /// </summary>
    public bool ComparisonHideModelNames { get; set; } = true;

    /// <summary>
    /// Its "Hold responses" switch (the reference's <c>iron_swift_hold_responses</c>,
    /// which defaults off): both answers stay covered until both have finished.
    /// </summary>
    public bool ComparisonHoldResponses { get; set; }

    /// <summary>"Code notifications": notify about important updates from a Code session (the reference's <c>bogosort.enable_push</c>).</summary>
    public bool CodeNotifications { get; set; }

    /// <summary>"Code permission requests": notify when Jarvis needs approval in a Code session (the reference's <c>code_requires_action.enable_push</c>).</summary>
    public bool CodePermissionRequestNotifications { get; set; }

    // ---- Settings › Desktop app ----

    /// <summary>
    /// "Quick Entry keyboard shortcut": an Electron-style accelerator such as
    /// "Ctrl+Alt+Space" (the reference's <c>globalShortcut</c>); empty means no shortcut.
    /// </summary>
    public string QuickEntryShortcut { get; set; } = QuickEntryShortcuts.DefaultAccelerator;

    /// <summary>"Keep Jarvis running in the system tray" (the reference's <c>menuBarEnabled</c>, on).</summary>
    public bool ShowInSystemTray { get; set; } = true;

    /// <summary>"Unhide apps when Jarvis finishes" (the reference's <c>chicagoAutoUnhide</c>, on).</summary>
    public bool ComputerUseAutoUnhide { get; set; } = true;

    /// <summary>
    /// Hide the windows this session may not drive before it acts — the
    /// reference's <c>hideBeforeAction</c> sub-gate, which its own defaults
    /// table ships on.
    /// </summary>
    public bool ComputerUseHideBeforeAction { get; set; } = true;

    /// <summary>"Denied apps": process names whose access requests are refused outright (the reference's <c>chicagoUserDeniedBundleIds</c>).</summary>
    public List<string> ComputerUseDeniedApps { get; set; } = [];

    /// <summary>"Allow all browser actions" (the reference's <c>allowAllBrowserActions</c>, off).</summary>
    public bool AllowAllBrowserActions { get; set; }

    /// <summary>"Enable auto-updates for extensions" (the reference's <c>isDxtAutoUpdatesEnabled</c>, on).</summary>
    public bool ExtensionsAutoUpdate { get; set; } = true;

    /// <summary>
    /// The credential helper the third-party inference window can run instead of a
    /// stored key: a command that prints the credential on stdout (the reference's
    /// helper script). Empty means none.
    /// </summary>
    public string CredentialHelperScript { get; set; } = "";

    /// <summary>How long the helper may run before it is stopped (the reference's helper timeout).</summary>
    public int CredentialHelperTimeoutSeconds { get; set; } = CredentialHelpers.DefaultTimeoutSeconds;

    /// <summary>Offer the screenshot + computer tools to the model at all.</summary>
    public bool ComputerUseEnabled { get; set; } = true;

    /// <summary>Offer them on the Chat surface too, which otherwise runs without tools.</summary>
    public bool ComputerUseInChat { get; set; }

    public string UserDisplayName { get; set; } = "";

    /// <summary>/color — per-session composer accent (session id → color string); absent = theme default.</summary>
    public Dictionary<string, string> SessionPromptColors { get; set; } = [];

    /// <summary>
    /// The pull requests a session has been bound to, which is where the PR bar's
    /// "related" rows come from. The reference keeps the same list on its own
    /// session record and draws every entry but the branch's current one.
    /// </summary>
    public Dictionary<string, List<SessionPullRequest>> SessionPullRequests { get; set; } = [];

    /// <summary>
    /// Transcript view per session (session id → normal|thinking|verbose|summary),
    /// newest last and capped at 100 rows like the reference's sticky map; a
    /// session with no row opens in Normal.
    /// </summary>
    public Dictionary<string, string> TranscriptViewBySession { get; set; } = [];

    /// <summary>
    /// Per-conversation switches on the Chat surface — web search, extended
    /// thinking, tool access and the connectors switched on — keyed by session id.
    /// The reference keeps these on the conversation rather than on the account
    /// (<see cref="ChatConversationSettings"/>).
    /// </summary>
    public Dictionary<string, ChatConversationSetting> ChatConversations { get; set; } = [];

    /// <summary>
    /// Whether a project has said yes to instruction-file imports that reach
    /// outside it, keyed by working directory. A row means the question has been
    /// asked, which is what stops it being asked again: the reference stores the
    /// same two facts per project as hasClaudeMdExternalIncludesApproved and
    /// hasClaudeMdExternalIncludesWarningShown.
    /// </summary>
    public Dictionary<string, bool> ExternalInstructionImportsByProject { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the Background tasks pane's Finished section is open, per session
    /// — the reference's finishedExpandedBySession, which likewise starts closed.
    /// </summary>
    public Dictionary<string, bool> BackgroundFinishedExpandedBySession { get; set; } = [];

    /// <summary>
    /// The permission mode last picked in a workspace (working directory → mode name),
    /// the reference's epitaxy-folder-permission-mode. Plan is never written here and a
    /// stored Plan is ignored on read, as it is there: plan mode is how a turn starts,
    /// not how a folder is worked in. Consulted only when no default mode is configured,
    /// which is where the reference's own layering puts it.
    /// </summary>
    public Dictionary<string, string> FolderPermissionModes { get; set; } = [];

    /// <summary>
    /// Consents already given for a dangerous permission mode, as the reference's
    /// epitaxy-perm-mode-acks stores them: one "{workspace}:{mode}" entry per
    /// workspace, so approving Bypass in one checkout does not approve it in another.
    /// </summary>
    public List<string> PermissionModeAcks { get; set; } = [];

    /// <summary>/wellbeing — break-reminder interval in minutes; 0 = off.</summary>
    public int WellbeingMinutes { get; set; }

    /// <summary>/powerup — the next lesson shown.</summary>
    public int PowerupLessonIndex { get; set; }

    /// <summary>/advisor — model id Code turns may consult through the advisor tool; null/empty = off.</summary>
    public string? AdvisorModelId { get; set; }

    /// <summary>Refuse computer-tool input while the foreground app has no grant (reference tiers, simplified).</summary>
    public bool ComputerUseRequireGrants { get; set; }

    /// <summary>Optional host callback evaluated for the current invocation, never serialized.</summary>
    [JsonIgnore] public Func<bool>? ComputerUseFullPermissionProvider { get; set; }
    [JsonIgnore] public bool RequireComputerUseGrants =>
        ComputerUseRequireGrants && ComputerUseFullPermissionProvider?.Invoke() != true;

    /// <summary>
    /// Process names granted input access by hand, in force in every session.
    /// The reference has no such list — a grant lands on the session
    /// (<see cref="ComputerUseGrantsBySession"/>) — so this is the standing
    /// allowlist the Settings page edits, unioned with the session's own.
    /// </summary>
    public List<string> ComputerUseGrantedApps { get; set; } = [];

    /// <summary>
    /// What each session was granted through request_access, keyed by session id:
    /// the reference's <c>cuAllowedApps</c> / <c>cuGrantFlags</c>, which live on
    /// the session and not on the installation.
    /// </summary>
    public Dictionary<string, ComputerUseGrantSet> ComputerUseGrantsBySession { get; set; } = [];

    /// <summary>
    /// Per-app tier ("read" | "click" | "full") for the granted apps. A name missing
    /// from here was granted before tiers existed and keeps the full access it had.
    /// </summary>
    public Dictionary<string, string> ComputerUseGrantedTiers { get; set; } = [];

    /// <summary>
    /// Per-tool switches for the in-process MCP servers, keyed the reference's
    /// way — <c>local:{server}:{tool}</c>. Only false is meaningful: a tool with
    /// no entry is on, which is why an empty map offers the full surface.
    /// </summary>
    public Dictionary<string, bool> InternalMcpTools { get; set; } = [];

    /// <summary>read_clipboard is allowed this session (the reference's clipboardRead grant flag).</summary>
    public bool ComputerUseClipboardRead { get; set; }

    /// <summary>write_clipboard is allowed (clipboardWrite grant flag).</summary>
    public bool ComputerUseClipboardWrite { get; set; }

    /// <summary>OS-level chords (alt+tab, win+r, …) are allowed (systemKeyCombos grant flag).</summary>
    public bool ComputerUseSystemKeyCombos { get; set; }

    /// <summary>Android Emulator tools (adb) on Code turns; the tools also need adb on the machine.</summary>
    public bool AndroidToolsEnabled { get; set; } = true;

    /// <summary>
    /// "Get a desktop notification when a routine run fails" — the reference
    /// shipped this in 1.9255.0 and leaves it on, since a routine that failed
    /// unattended is exactly the case nobody is watching for.
    /// </summary>
    public bool NotifyOnRoutineFailure { get; set; } = true;

    /// <summary>The Scheduled list's sort, "nextRun" or "name". Sticky, as the reference's is.</summary>
    public string ScheduledSort { get; set; } = "nextRun";

    /// <summary>
    /// The reference CLI's attribution system block
    /// (<c>x-anthropic-billing-header: cc_version=…; cc_entrypoint=…;</c>), carrying
    /// this app's own identity. Off by default: it tells the endpoint which client
    /// and which conversation a request belongs to, which nothing here needs.
    /// </summary>
    public bool SendAttributionBlock { get; set; }

    /// <summary>
    /// The reference CLI's <c>metadata.user_id</c> — a JSON blob naming the install,
    /// the account and the session. Off by default.
    /// </summary>
    public bool SendRequestMetadata { get; set; }

    /// <summary>
    /// The reference CLI's <c>X-Claude-Code-Session-Id</c> request header. Off by
    /// default; useful only to a proxy or gateway the user runs themselves.
    /// </summary>
    public bool SendSessionIdHeader { get; set; }

    /// <summary>
    /// This install's pseudonymous id for <see cref="SendRequestMetadata"/> —
    /// generated here, like the reference generates its own, so nothing derived
    /// from the machine or from another client's config ever leaves.
    /// </summary>
    public string? ClientDeviceId { get; set; }

    /// <summary>
    /// Browser pane: keep cookies/local storage across app restarts. Off (the
    /// reference default) scrubs the profile on the next launch instead.
    /// </summary>
    public bool BrowserPersistSessions { get; set; }

    /// <summary>Browser pane: target=_blank/window.open opens a new pane tab instead of the system browser.</summary>
    public bool BrowserOpenLinksInPanel { get; set; } = true;

    /// <summary>Browser pane color scheme: "light" | "dark" | "system".</summary>
    public string BrowserColorScheme { get; set; } = "system";

    /// <summary>
    /// Browser pane: whether the model may drive it at all. The reference's own
    /// <c>launchEnabled</c>, read as <c>!== false</c> — an unset value leaves
    /// the pane's whole tool server on, and only an explicit false removes it.
    /// </summary>
    public bool BrowserPaneLaunchEnabled { get; set; } = true;

    /// <summary>
    /// Whether the Jarvis Browser extension family is offered. The reference
    /// gates its claude-in-chrome server on
    /// <c>shouldEnableChromeExtensionBridge() &amp;&amp; !isDisabled()</c> — the
    /// bridge being wanted at all, not on the session type.
    /// </summary>
    public bool ChromeExtensionEnabled { get; set; } = true;

    /// <summary>
    /// Per-skill override (skill name → "on" | "off" | "user-invocable-only"),
    /// the reference skillOverrides tri-state. Absent = on.
    /// </summary>
    public Dictionary<string, string> SkillOverrides { get; set; } = [];

    /// <summary>Lifetime per-skill usage (reference skillUsage): never reset, recency-weighted by readers.</summary>
    public Dictionary<string, SkillUsageEntry> SkillUsage { get; set; } = [];

    /// <summary>
    /// Plugins switched off on the Plugins page, keyed "{scope}:{name}" so a
    /// project copy is its own switch. A disabled plugin stays on disk and
    /// contributes no skills, commands, agents, hooks or connectors to a turn.
    /// </summary>
    public List<string> DisabledPlugins { get; set; } = [];

    /// <summary>
    /// The folder the Organization plugins page scans. The reference has a
    /// device-management tool mount plugin bundles there; here the user points
    /// at one. Empty means the page offers to choose a folder.
    /// </summary>
    public string OrganizationPluginsFolder { get; set; } = "";

    /// <summary>
    /// Per-server tool policy, the reference's <c>mcpServers.{server}.toolPolicy</c>:
    /// server name → tool name → allow | ask | ask-session | blocked. Keyed by
    /// server name, so it applies even if the plugin that ships it is updated.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> McpToolPolicies { get; set; } = [];

    /// <summary>
    /// The Memory page's "Use memory in sessions". Off keeps the files and stops
    /// reading or updating them, which is what the reference's paused state says.
    /// </summary>
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>
    /// Chapter titles the user renamed in place, keyed "{sessionId}/{chapterId}"
    /// — the reference keeps its renames and hides per session the same way.
    /// </summary>
    public Dictionary<string, string> ChapterRenames { get; set; } = [];

    /// <summary>Chapters the user hid, keyed the same way.</summary>
    public List<string> HiddenChapters { get; set; } = [];

    /// <summary>
    /// Folders the user answered the workspace-trust question for. The
    /// reference asks once per workspace before it starts a session there.
    /// </summary>
    public List<string> TrustedWorkspaces { get; set; } = [];

    /// <summary>Imported sessions the user agreed to resume, by session id.</summary>
    public List<string> ResumedImportedSessions { get; set; } = [];
    /// The Code sidebar's filter, as the reference stores it per mode
    /// (`groupByByMode`/`sortByByMode`/`recentsStatusFilter`/`showPrStatus`/
    /// `showEmptyProjects`). The defaults are its own: the desktop code sidebar
    /// groups by folder and sorts by last activity.
    /// </summary>
    public string SidebarGroupBy { get; set; } = SidebarFilterModel.DefaultGroupBy;

    public string SidebarSortBy { get; set; } = SidebarFilterModel.DefaultSortBy;

    public string SidebarStatus { get; set; } = SidebarFilterModel.DefaultStatus;

    public bool SidebarShowEmptyFolders { get; set; }

    public bool SidebarShowPrStatus { get; set; } = true;

    /// <summary>
    /// The Last-activity window the State grouping filters by — the reference's
    /// `stateActivityDays`, seven days by default and zero for "All".
    /// </summary>
    public int SidebarStateActivityDays { get; set; } = SidebarFilterModel.DefaultStateActivityDays;

    /// <summary>
    /// Whether the sidebar has already taught that a row can be dragged onto the pin
    /// target — the reference shows that tip once, on the first pin from a menu.
    /// </summary>
    public bool DragPinHintShown { get; set; }

    /// <summary>
    /// The output style one session runs at, where the reference's session menu puts
    /// it. A session with no entry takes <see cref="OutputStyle"/>, which is the
    /// account-wide choice `/output-style` sets.
    /// </summary>
    public Dictionary<string, string> SessionOutputStyles { get; set; } = [];
    public Dictionary<string, PrAutoFixBinding> SessionPrAutoFix { get; set; } = [];

    /// <summary>The folders a session has been started in, newest first (the sidebar's Recent list).</summary>
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>The reference's `epitaxy-pr-create-mode`: what the PR bar's split button does.</summary>
    public string PrCreateMode { get; set; } = "create";

    /// <summary>The reference's `epitaxy-gh-install-dismissed`.</summary>
    public bool GhInstallDismissed { get; set; }
}

/// <summary>One skill's persisted usage: a lifetime counter plus the last invocation time.</summary>
public sealed class SkillUsageEntry
{
    public int UsageCount { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    public UiSettingsStore(string filePath)
    {
        _filePath = filePath;
        Current = LoadFrom(filePath);
    }

    /// <summary>Where this store lives; siblings (screenshots, exports) hang off its folder.</summary>
    public string FilePath => _filePath;

    public UiSettings Current { get; }

    public event EventHandler? Saved;

    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
        File.Move(tmp, _filePath, overwrite: true);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private static UiSettings LoadFrom(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(filePath), Options) ?? new UiSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt UI settings fall back to defaults; engine settings are unaffected.
        }

        return new UiSettings();
    }
}
