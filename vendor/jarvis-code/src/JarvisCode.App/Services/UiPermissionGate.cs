using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Security;

namespace JarvisCode.App.Services;

/// <summary>
/// What the permission prompt needs to render one request. The risk travels with it
/// because it decides which approval scopes the card may offer: an escalated call is
/// re-prompted whatever the answer was.
/// </summary>
public sealed record PermissionPrompt(
    string ToolName,
    string? Detail,
    string? SubjectText,
    JarvisCode.Core.Utilities.ToolDiffPreview.Preview? DiffPreview,
    string? Warning,
    CallRisk Risk,
    string? CallId = null,
    string? ArgumentsJson = null,
    string? CallDescription = null)
{
    /// <summary>
    /// Whether the card may offer a standing approval. False for a tool an
    /// organization tool policy set to "Ask each time": the answer is good for
    /// this call and no other, so the button that would remember it is left off.
    /// </summary>
    public bool AllowAlwaysOffered { get; init; } = true;
}

/// <summary>
/// The host's IPermissionGate: applies deny/ask/allow precedence across settings scopes,
/// the active permission mode, and session-scoped "always allow"
/// grants; anything left over is escalated to the UI prompt callback.
/// </summary>
public sealed class UiPermissionGate : IPermissionGate, IDenialReasonSource
{
    private readonly object _lock = new();
    private readonly HashSet<string> _sessionAllowed = new(StringComparer.Ordinal);
    private long _sessionRevision;

    public PermissionMode Mode { get; set; } = PermissionMode.Auto;

    public string WorkingDirectory { get; set; } = "";

    public IReadOnlyList<string> AdditionalDirectories { get; set; } = [];

    /// <summary>
    /// Memory directories the file tools may write to (the team's shared
    /// store). Scoped like a working directory, but never shown as one.
    /// </summary>
    public IReadOnlyList<string> MemoryDirectories { get; set; } = [];

    public IReadOnlyList<string> GlobalRuleLines { get; set; } = [];

    /// <summary>
    /// Turn-scoped rule lines the host sets up for a mode (plan mode's plan-file
    /// writes); evaluated before the persisted rules.
    /// </summary>
    public IReadOnlyList<string> ExtraRuleLines { get; set; } = [];
    /// <summary>SDK updates own this layer; replacing it cannot remove configured or CLI deny rules.</summary>
    public IReadOnlyList<string> SdkRuleLines { get; set; } = [];
    /// <summary>Already-resolved host configuration; unlike automatic settings loading this honors the caller's selected tiers.</summary>
    public IReadOnlyList<string> SuppliedRuleLines { get; set; } = [];

    /// <summary>
    /// The session's plan file: the one path Write and Edit may touch in plan
    /// mode without being asked about.
    /// </summary>
    public string? PlanFilePath { get; set; }

    /// <summary>
    /// The reference's <c>permissions.blockReadsOutsideWorkingDirectories</c>: a
    /// read-only tool reaching outside the working directories is refused with
    /// the reference's sentence instead of asked about.
    /// </summary>
    public bool BlockReadsOutsideWorkingDirectories { get; set; }

    /// <summary>
    /// Persists <see cref="BlockReadsOutsideWorkingDirectories"/> when the user
    /// answers the one-time question with "block from now on"; the reference
    /// writes it to user settings.
    /// </summary>
    public Func<Task>? PersistBlockReadsAsync { get; set; }

    /// <summary>
    /// The reference's <c>--restricted</c>: file tools stay inside the working
    /// directory (a path outside is denied, not asked about) and no settings file
    /// contributes rules.
    /// </summary>
    public bool RestrictToWorkspace { get; set; }

    /// <summary>--restricted's "ignores user, project and local settings files": only the session's own rules apply.</summary>
    public bool IgnoreSettingsFiles { get; set; }

    private bool _outsideReadsAllowed;
    private readonly Dictionary<string, string> _denialReasons = new(StringComparer.Ordinal);

    /// <summary>
    /// Calls the user was asked about and personally refused. A rule, a setting,
    /// a hook and a run with nobody to ask all deny too, and none of those is a
    /// refusal: the reference stands the batching reminder down only for a
    /// refusal or an interruption, and leaves it riding a configuration denial.
    /// </summary>
    private readonly HashSet<string> _userRefused = new(StringComparer.Ordinal);

    /// <summary>The reference's one-time question in auto mode, and its three answers.</summary>
    public const string ReadOutsideTitle = "Read outside the working directories";
    public const string ReadOutsideQuestion = "Allow reads outside the working directories?";
    public const string ReadOutsideExplanation =
        "Yes or Block settles this question; Ask again asks on the next outside read. Block: the file tools refuse " +
        "reads outside the working directories in every project.";
    public const string ReadOutsideDenied = "The user did not allow this read outside the working directories.";
    public const string ReadOutsideBlocked =
        "The user chose to block reads outside the working directories " +
        "(permissions.blockReadsOutsideWorkingDirectories). Ask the user to add the directory with /add-dir, or to " +
        "remove that setting.";

    public string? TakeDenialReason(string? callId)
    {
        if (callId is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (_denialReasons.Remove(callId, out var reason))
            {
                return reason;
            }
        }

        return null;
    }

    public bool TakeDenialWasUserRefusal(string? callId)
    {
        if (callId is null)
        {
            return false;
        }

        lock (_lock)
        {
            return _userRefused.Remove(callId);
        }
    }

    private PermissionDecision DenyWithReason(PermissionRequest request, string reason, bool userRefused = false)
    {
        if (request.CallId is { } id)
        {
            lock (_lock)
            {
                _denialReasons[id] = reason;
                if (userRefused)
                {
                    _userRefused.Add(id);
                }
            }
        }

        return PermissionDecision.Deny;
    }

    /// <summary>
    /// The user was asked and said no. Recorded so the batching reminder stands
    /// down, which a denial from a rule or a setting does not do.
    /// </summary>
    private PermissionDecision DeniedByUser(PermissionRequest request)
    {
        if (request.CallId is { } id)
        {
            lock (_lock)
            {
                _userRefused.Add(id);
            }
        }

        return PermissionDecision.Deny;
    }

    private static bool ReachesOutsideWorkspace(ToolCallAssessment assessment) =>
        assessment.Risk == CallRisk.Escalated &&
        assessment.Warning is { } warning &&
        warning.StartsWith("Touches a path outside the workspace", StringComparison.Ordinal);

    /// <summary>
    /// The mode the session was in before plan mode, and the rule lines it had.
    /// Leaving plan mode restores both — the reference returns to prePlanMode
    /// rather than to a fixed mode, and puts back the permissions plan mode
    /// stripped while it was on.
    /// </summary>
    private PermissionMode? _prePlanMode;
    private IReadOnlyList<string>? _prePlanRuleLines;

    /// <summary>The mode leaving plan mode will return to, or null when not in plan mode.</summary>
    public PermissionMode? PrePlanMode => _prePlanMode;

    /// <summary>
    /// Enters plan mode, remembering what to come back to. Entering twice keeps
    /// the first answer, so a second /plan cannot make plan mode its own
    /// predecessor.
    /// </summary>
    public void EnterPlanMode(IReadOnlyList<string>? planRuleLines = null)
    {
        if (Mode != PermissionMode.Plan)
        {
            _prePlanMode = Mode;
            _prePlanRuleLines = ExtraRuleLines;
        }

        if (planRuleLines is not null)
        {
            ExtraRuleLines = planRuleLines;
        }

        Mode = PermissionMode.Plan;
    }

    /// <summary>
    /// Leaves plan mode for the mode it was entered from, defaulting to Auto
    /// when nothing recorded one (a session that opened straight into plan mode).
    /// </summary>
    public PermissionMode ExitPlanMode()
    {
        var restored = _prePlanMode ?? PermissionMode.Auto;
        ExtraRuleLines = _prePlanRuleLines ?? [];
        _prePlanMode = null;
        _prePlanRuleLines = null;
        Mode = restored;
        return restored;
    }

    private readonly List<string> _sessionRuleLines = [];

    /// <summary>
    /// Session-scoped rule lines granted mid-session (a skill's allowed-tools /
    /// disallowed-tools). Like the reference's permission layers they live only
    /// as long as the session and are never persisted.
    /// </summary>
    public void AddSessionRuleLines(IEnumerable<string> lines)
    {
        lock (_lock)
        {
            foreach (var line in lines)
            {
                if (!_sessionRuleLines.Contains(line, StringComparer.Ordinal))
                    _sessionRuleLines.Add(line);
            }
        }
    }

    /// <summary>
    /// Tools an organization tool policy set to "Ask each time": the call is put
    /// to the user however the rules and the mode would otherwise settle it, and
    /// the answer is never remembered. Its siblings need no entry here —
    /// "Always allow" and "Blocked" are ordinary allow/deny rule lines, and
    /// "Ask once per session" is the card's own standing approval.
    /// </summary>
    public IReadOnlyCollection<string> AlwaysAskTools { get; set; } = [];

    private bool AlwaysAsks(string toolName) =>
        AlwaysAskTools.Count > 0 && AlwaysAskTools.Contains(toolName);

    /// <summary>Asks the user; must marshal to the UI thread itself.</summary>
    public Func<PermissionPrompt, CancellationToken, Task<PermissionDecision>>? PromptAsync { get; set; }

    public Func<PermissionRequest, CancellationToken, Task<AutoPermissionVerdict>>? AutoClassifyAsync { get; set; }
    public bool PrAutoFixActive { get; set; }

    private int _pendingPrompts;

    /// <summary>
    /// True while a permission card is on screen. The in-process MCP shell's
    /// stall timeout consults it: the reference re-arms its timer instead of
    /// firing whenever <c>hasPendingPermission()</c> is true, so a call the user
    /// is still deciding about never reads as a stuck subsystem.
    /// </summary>
    public bool HasPendingPermission => Volatile.Read(ref _pendingPrompts) > 0;

    /// <summary>
    /// The permission_request hooks, consulted only when a prompt is about to be
    /// shown (tool name, arguments, risk). A decisive result settles the call in
    /// place of the prompt — the user wrote the hook, so it may approve even an
    /// escalated call, but only ever one call at a time (never "always allow").
    /// Set per turn by the turn factory; null when no such hooks exist.
    /// </summary>
    public Func<string, JsonObject, string, CancellationToken, Task<JarvisCode.Core.Hooks.PermissionRequestHookResult?>>? PermissionRequestHookAsync { get; set; }

    public void ResetSessionGrants()
    {
        Interlocked.Increment(ref _sessionRevision);
        lock (_lock)
        {
            _sessionAllowed.Clear();
        }
    }

    /// <summary>
    /// Server names whose tools never take a mode's blanket approval — the
    /// reference's <c>kx</c> floor. Bypass is a statement about this machine's
    /// files and shell; it is not consent to drive a browser that is signed in
    /// to the user's accounts, so a call to one of these servers falls back to
    /// the ordinary policy and is asked about.
    ///
    /// The reference floors these two unconditionally and floors
    /// <c>claude-in-chrome</c> only behind a server-side flag whose value could
    /// not be read here — see Deltas/reference-surface-deltas.tsv.
    /// </summary>
    private static readonly string[] FlooredMcpServers = ["Claude_Browser", "Claude_Preview"];

    /// <summary>
    /// The mode this call is judged in: the session's, unless the call belongs
    /// to a server the floor applies to and the mode would have waved it
    /// through.
    /// </summary>
    internal PermissionMode EffectiveMode(string toolName) =>
        Mode == PermissionMode.Bypass && IsFlooredMcpTool(toolName)
            ? PermissionMode.Auto
            : Mode;

    /// <summary>True for a tool of a server the floor names.</summary>
    internal static bool IsFlooredMcpTool(string toolName) =>
        FlooredMcpServers.Any(server =>
            toolName.StartsWith(
                JarvisCode.Core.Mcp.McpBuiltInServers.WirePrefix(server), StringComparison.Ordinal));

    public async ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        var toolName = request.Tool.Name;
        var arguments = request.Arguments;
        var mode = EffectiveMode(toolName);

        // Deny rules are absolute; allow rules are held back until the risk is known,
        // because escalated calls must always reach a human.
        var matched = PermissionRuleEngine.Evaluate(LoadRules(), toolName, arguments);
        var sdkMatched = PermissionRuleEngine.Evaluate(PermissionRuleEngine.ParseAll(SdkRuleLines), toolName, arguments);
        if (matched?.Action == RuleAction.Deny || sdkMatched?.Action == RuleAction.Deny)
        {
            return PermissionDecision.Deny;
        }

        var allowRuleMatched = matched?.Action == RuleAction.Allow || sdkMatched?.Action == RuleAction.Allow;

        var scope = AdditionalDirectories.Count > 0 || MemoryDirectories.Count > 0
            ? WorkspacePathScope.FromRoots(
                [WorkingDirectory, .. AdditionalDirectories, .. MemoryDirectories])
            : WorkspacePathScope.FromRoot(WorkingDirectory);
        var assessment = ToolCallPolicy.Assess(toolName, request.Tool.IsReadOnly, arguments, scope, WorkingDirectory);

        if (RestrictToWorkspace && ReachesOutsideWorkspace(assessment))
        {
            // --restricted keeps the file tools inside the working directory: no
            // question, a refusal that says so.
            return DenyWithReason(request,
                $"{toolName} may not touch a path outside the working directory: this session runs with --restricted.");
        }

        // An SDK PreToolUse callback may authorize or request confirmation for
        // this one call. Explicit deny rules and the workspace boundary above
        // remain authoritative, including after the hook rewrote its input.
        if (matched?.Action == RuleAction.Ask || sdkMatched?.Action == RuleAction.Ask)
            return await AskAsync(request, assessment, "The permission rule requires confirmation.", cancellationToken).ConfigureAwait(false);

        if (request.HookPermissionDecision is "allow" or "ask")
        {
            if (request.Tool.IsReadOnly && ReachesOutsideWorkspace(assessment) && BlockReadsOutsideWorkingDirectories)
                return DenyWithReason(request, ReadOutsideBlocked);
            if (request.HookPermissionDecision == "allow" && !AlwaysAsks(toolName))
                return PermissionDecision.Allow;
            if (request.HookPermissionDecision == "ask")
                return await AskAsync(request, assessment, "The pre-tool hook requested confirmation.", cancellationToken)
                    .ConfigureAwait(false);
        }

        if (mode == PermissionMode.Bypass)
        {
            return PermissionDecision.Allow;
        }

        if (request.Tool.IsReadOnly && ReachesOutsideWorkspace(assessment))
        {
            // The reference's block, and its one-time auto-mode question. Outside
            // the auto mode an outside read stays the ordinary escalated prompt.
            if (BlockReadsOutsideWorkingDirectories)
            {
                return DenyWithReason(request, ReadOutsideBlocked);
            }

            if (mode == PermissionMode.Auto)
            {
                lock (_lock)
                {
                    if (_outsideReadsAllowed)
                    {
                        return PermissionDecision.Allow;
                    }
                }

                var answer = await AskAsync(
                    request, assessment, ReadOutsideExplanation, cancellationToken,
                    detail: ReadOutsideTitle + " — " + ReadOutsideQuestion, allowAlways: true).ConfigureAwait(false);
                switch (answer)
                {
                    case PermissionDecision.AllowAlways:
                        // "Yes, keep allowing reads outside the working directories"
                        lock (_lock)
                        {
                            _outsideReadsAllowed = true;
                        }

                        return PermissionDecision.Allow;
                    case PermissionDecision.Allow:
                        // "No, ask again next time"
                        return PermissionDecision.Allow;
                    default:
                        // "No, block reads outside the working directories from now on"
                        BlockReadsOutsideWorkingDirectories = true;
                        if (PersistBlockReadsAsync is { } persist)
                        {
                            await persist().ConfigureAwait(false);
                        }

                        return DenyWithReason(request, ReadOutsideDenied, userRefused: true);
                }
            }
        }

        if (mode == PermissionMode.Plan)
        {
            // The reference's plan-mode rule (its permission pipeline, CLI
            // 2.1.257): a non-read-only call that is not the plan file is not
            // denied but *asked* — "Cannot write to {path} while in plan mode."
            // for a file write, "Cannot call {tool} while in plan mode." for
            // anything else — and a run with nobody to ask answers no. Allow
            // rules and session grants do not apply; nothing is remembered.
            // The plan file itself is the one write plan mode exists for.
            if (PlanModeQuestion(request, arguments) is { } planQuestion)
            {
                return await AskAsync(request, assessment, planQuestion, cancellationToken).ConfigureAwait(false);
            }

            if (IsPlanFileWrite(toolName, arguments))
            {
                return PermissionDecision.Allow;
            }
        }

        if (assessment.Risk != CallRisk.Escalated)
        {
            // Provably benign calls run silently in every mode.
            if (assessment.Risk == CallRisk.AutoAllowable && !AlwaysAsks(toolName))
            {
                return PermissionDecision.Allow;
            }

            // Manual ignores allow rules and session approvals for mutating calls,
            // and so does a tool the organization's policy marked "Ask each time".
            if (!ToolCallPolicy.ModeForcesPrompt(mode, assessment) && !AlwaysAsks(toolName))
            {
                if (allowRuleMatched || ToolCallPolicy.ModeAutoAllows(mode, toolName, assessment))
                {
                    return PermissionDecision.Allow;
                }

                lock (_lock)
                {
                    if (_sessionAllowed.Contains(GrantKey(toolName, arguments)))
                    {
                        return PermissionDecision.Allow;
                    }
                }
            }
        }

        if (request.HookPermissionDecision is null && PermissionRequestHookAsync is { } permissionHook)
        {
            var hookResult = await permissionHook(
                toolName, arguments, assessment.Risk.ToString(), cancellationToken).ConfigureAwait(false);
            if (hookResult is not null)
            {
                if (hookResult.Allow && hookResult.UpdatedInput is { } updated)
                {
                    var validation = JarvisCode.Core.Validation.JsonSchemaValidation.ValidateInstance(request.Tool.InputSchema, updated);
                    if (!validation.IsValid)
                        return DenyWithReason(request, "The permission hook supplied invalid updatedInput: " + validation.Error);
                    arguments.Clear();
                    foreach (var (key, value) in updated) arguments[key] = value?.DeepClone();
                    return await RequestAsync(request with
                    {
                        HookPermissionDecision = "allow",
                        CallDescription = request.Tool.DescribeCall(arguments),
                    }, cancellationToken).ConfigureAwait(false);
                }
                return hookResult.Allow ? PermissionDecision.Allow : PermissionDecision.Deny;
            }
        }

        if (mode == PermissionMode.Auto && assessment.Risk == CallRisk.Standard && !AlwaysAsks(toolName)
            && !IsFlooredMcpTool(toolName) && AutoClassifyAsync is { } classify)
        {
            var reviewedDirectory = WorkingDirectory;
            var reviewedSession = Interlocked.Read(ref _sessionRevision);
            var reviewedPolicy = AutomaticReviewPolicy(toolName);
            var verdict = await classify(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (reviewedSession != Interlocked.Read(ref _sessionRevision) ||
                !string.Equals(reviewedDirectory, WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                return DenyWithReason(request, "The session or working directory changed during automatic permission review.");
            if (reviewedPolicy != AutomaticReviewPolicy(toolName))
                return await RequestAsync(request, cancellationToken).ConfigureAwait(false);
            if (verdict.Decision == "allow") return PermissionDecision.Allow;
            if (verdict.Decision == "deny") return DenyWithReason(request, verdict.Reason);
            // Asking, malformed results and provider failures leave the normal
            // permission card (or its headless host callback) authoritative.
        }

        var prompt = PromptAsync;
        if (prompt is null)
        {
            return PermissionDecision.Deny;
        }

        PermissionDecision decision;
        Interlocked.Increment(ref _pendingPrompts);
        try
        {
            decision = await prompt(BuildPrompt(request, assessment), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingPrompts);
        }

        if (decision == PermissionDecision.AllowAlways && assessment.Risk != CallRisk.Escalated &&
            !AlwaysAsks(toolName))
        {
            lock (_lock)
            {
                _sessionAllowed.Add(GrantKey(toolName, arguments));
            }
        }

        return decision == PermissionDecision.Deny ? DeniedByUser(request) : decision;
    }

    private string AutomaticReviewPolicy(string toolName) => System.Text.Json.JsonSerializer.Serialize(new
    {
        Mode = EffectiveMode(toolName), WorkingDirectory, AdditionalDirectories, MemoryDirectories,
        Rules = LoadRules(), SdkRuleLines, AlwaysAskTools, RestrictToWorkspace, IgnoreSettingsFiles,
        BlockReadsOutsideWorkingDirectories, PlanFilePath, PrAutoFixActive,
    });

    /// <summary>
    /// The plan-mode question for this call, or null when plan mode has nothing
    /// to ask: read-only tools, the plan-mode tools themselves, and Write/Edit of
    /// the plan file run as they would in any mode.
    /// </summary>
    private string? PlanModeQuestion(PermissionRequest request, JsonObject arguments)
    {
        var toolName = request.Tool.Name;
        if (request.Tool.IsReadOnly || toolName is "ExitPlanMode" or "EnterPlanMode" or "AskUserQuestion")
        {
            return null;
        }

        if (toolName is "Write" or "Edit" or "NotebookEdit")
        {
            if (IsPlanFileWrite(toolName, arguments))
            {
                return null;
            }

            var path = AbsolutePath(arguments["file_path"]?.GetValue<string>() ?? arguments["notebook_path"]?.GetValue<string>());
            return $"Cannot write to {path ?? "the file"} while in plan mode.";
        }

        return $"Cannot call {toolName} while in plan mode.";
    }

    private bool IsPlanFileWrite(string toolName, JsonObject arguments)
    {
        if (toolName is not ("Write" or "Edit" or "NotebookEdit") || PlanFilePath is null)
        {
            return false;
        }

        var path = AbsolutePath(arguments["file_path"]?.GetValue<string>() ?? arguments["notebook_path"]?.GetValue<string>());
        return path is not null &&
               string.Equals(NormalizePath(path), NormalizePath(PlanFilePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// Puts a call to the user (or the permission_request hook) with a reason
    /// the mode supplies, offering no "always allow": the answer is for this
    /// call alone.
    /// </summary>
    private async ValueTask<PermissionDecision> AskAsync(
        PermissionRequest request, ToolCallAssessment assessment, string reason, CancellationToken cancellationToken,
        string? detail = null, bool allowAlways = false)
    {
        if (PermissionRequestHookAsync is { } permissionHook)
        {
            var hookResult = await permissionHook(
                request.Tool.Name, request.Arguments, assessment.Risk.ToString(), cancellationToken).ConfigureAwait(false);
            if (hookResult is not null)
            {
                return hookResult.Allow ? PermissionDecision.Allow : PermissionDecision.Deny;
            }
        }

        var prompt = PromptAsync;
        if (prompt is null)
        {
            return PermissionDecision.Deny;
        }

        Interlocked.Increment(ref _pendingPrompts);
        try
        {
            var decision = await prompt(BuildPrompt(request, assessment, reason, detail, allowAlways), cancellationToken)
                .ConfigureAwait(false);
            if (allowAlways)
            {
                return decision;
            }

            return decision == PermissionDecision.Deny ? DeniedByUser(request) : PermissionDecision.Allow;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingPrompts);
        }
    }

    private PermissionPrompt BuildPrompt(
        PermissionRequest request, ToolCallAssessment assessment, string? reason = null, string? detail = null,
        bool allowAlways = false)
    {
        var toolName = request.Tool.Name;
        // The argument a human reasons about for this tool — the same one permission
        // rules are written against — shown verbatim in the card's monospace box.
        string? subject = toolName switch
        {
            "PowerShell" => request.Arguments["command"]?.GetValue<string>(),
            "Write" or "Edit" or "Read" =>
                AbsolutePath(request.Arguments["file_path"]?.GetValue<string>()),
            "WebFetch" => request.Arguments["url"]?.GetValue<string>(),
            "TaskStop" => request.Arguments["task_id"]?.GetValue<string>(),
            _ => null,
        };
        var diff = JarvisCode.Core.Utilities.ToolDiffPreview.TryCreate(toolName, request.Arguments, WorkingDirectory);
        // A mode-supplied reason (plan mode's question) is shown where an
        // escalation warning would be, and the card drops "Always allow" for it
        // the way it does for an escalated call.
        return new PermissionPrompt(
            toolName, detail ?? DetailLine(toolName, subject, request.CallDescription),
            subject, diff, reason ?? assessment.Warning,
            reason is null || allowAlways ? assessment.Risk : CallRisk.Escalated,
            request.CallId, request.Arguments.ToJsonString(), request.CallDescription)
        {
            AllowAlwaysOffered = !AlwaysAsks(toolName),
        };
    }

    /// <summary>
    /// The box shows where the file really is, so a relative argument is resolved against
    /// the workspace; an unusable path is left exactly as the model wrote it.
    /// </summary>
    private string? AbsolutePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(WorkingDirectory))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path, WorkingDirectory);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// The line under the title: the workspace-relative path while the file is inside the
    /// workspace and the absolute one once it escapes, nothing at all for shell — there the
    /// command sits in the box immediately below.
    /// </summary>
    private string? DetailLine(string toolName, string? subject, string callDescription)
    {
        if (toolName == "PowerShell")
        {
            return null;
        }

        if (toolName is not ("Write" or "Edit" or "Read") || subject is null)
        {
            return callDescription;
        }

        if (string.IsNullOrEmpty(WorkingDirectory))
        {
            return subject;
        }

        try
        {
            var relative = Path.GetRelativePath(WorkingDirectory, subject);
            return relative.StartsWith("..", StringComparison.Ordinal) ? subject : relative;
        }
        catch (ArgumentException)
        {
            return subject;
        }
    }

    private IReadOnlyList<PermissionRule> LoadRules()
    {
        // Stable source ordering; Evaluate enforces deny > ask > allow across every collected scope.
        var lines = new List<string>(ExtraRuleLines);
        lines.AddRange(SuppliedRuleLines);
        lock (_lock)
        {
            lines.AddRange(_sessionRuleLines);
        }

        // --restricted ignores user, project and local settings files.
        if (!IgnoreSettingsFiles)
        {
            if (!string.IsNullOrEmpty(WorkingDirectory))
            {
                lines.AddRange(ProjectPermissions.LoadRuleLines(WorkingDirectory));
            }

            lines.AddRange(GlobalRuleLines);
        }

        return PermissionRuleEngine.ParseAll(lines);
    }

    private static string GrantKey(string toolName, JsonObject arguments)
    {
        // Shell grants are per-command; everything else is per-tool.
        if (toolName is "PowerShell" or "Bash" && arguments["command"]?.GetValue<string>() is { } command)
        {
            return $"shell::{command.Trim()}";
        }

        return toolName;
    }
}
