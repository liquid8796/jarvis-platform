using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.App.Services;

/// <summary>
/// Per-site consent for the browser tools, the way the reference extension gates
/// them: the first action on a site asks the user, and the answer holds for the
/// rest of the session. Navigation is checked against where it is going, every
/// other command against the site the tab is already on.
/// </summary>
public sealed class BrowserOriginGate(BrowserBridge bridge, UiSettingsStore? settings = null)
{
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sites granted for this session — the ones "Allowed sites" persists plus the
    /// ones answered here. Settings › Jarvis Code › Browser › Allowed sites edits
    /// the persisted half (the reference's <c>launchPreviewAllowedOrigins</c>).
    /// </summary>
    public IReadOnlyCollection<string> Allowed =>
        settings is null ? _allowed : [.. _allowed.Concat(settings.Current.BrowserAllowedOrigins)];

    /// <summary>Grants a site without asking — the Settings page and tests use this.</summary>
    public void Allow(string origin) => _allowed.Add(origin);

    // ---- a scheduled run's own grant ----

    private string? _routineMode;
    private readonly List<string> _routineDomains = [];
    private bool _routineActive;

    /// <summary>
    /// Raised whenever a run's browser grant changes, so <see cref="RoutineRunner"/>
    /// can write it back onto the routine — the reference's
    /// <c>updateChromePermissions</c> on the scheduled task.
    /// </summary>
    public event Action<string?, IReadOnlyList<string>>? RoutineGrantChanged;

    /// <summary>
    /// Applies the browser grant a routine's earlier runs collected, for the length
    /// of one run. The reference reads the same two fields off the scheduled task
    /// when the session it dispatches carries no grant of its own.
    /// </summary>
    public void ApplyRoutineGrant(string? mode, IReadOnlyList<string> domains)
    {
        _routineActive = true;
        _routineMode = mode;
        _routineDomains.Clear();
        _routineDomains.AddRange(domains);
    }

    /// <summary>Ends the run's grant, leaving the app-run consent as it was.</summary>
    public void ClearRoutineGrant()
    {
        _routineActive = false;
        _routineMode = null;
        _routineDomains.Clear();
    }

    /// <summary>Records a site the run was allowed, and reports the new grant.</summary>
    private void RecordRoutineOrigin(string origin)
    {
        if (!_routineActive ||
            _routineMode == ChromePermissionModes.SkipAllPermissionChecks ||
            _routineDomains.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _routineDomains.Add(origin);
        _routineMode = ChromePermissionModes.FollowAPlan;
        RoutineGrantChanged?.Invoke(_routineMode, [.. _routineDomains]);
    }

    private bool RoutineAllows(string origin) =>
        _routineActive &&
        (_routineMode == ChromePermissionModes.SkipAllPermissionChecks ||
         _routineDomains.Contains(origin, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Settings › Desktop app › Browser Use › "Allow all browser actions": every
    /// site is granted without asking (the reference's <c>allowAllBrowserActions</c>).
    /// </summary>
    private bool AllowsEverySite => settings?.Current.AllowAllBrowserActions == true;

    private bool IsPersistedAllowed(string origin) =>
        settings is not null && settings.Current.BrowserAllowedOrigins
            .Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when the command may run; otherwise the refusal the model reads.</summary>
    public async Task<string?> CheckAsync(
        string toolName,
        JsonObject arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.AskUserAsync is null)
        {
            // No UI to ask in (the CLI, a subagent): the permission gate that let
            // this tool run at all is the only consent available, so honour it —
            // and skip the round trip that would only have fed a question.
            return null;
        }

        // Navigation is checked against where it is going; every other command
        // against the site the tab is already on.
        var origin = toolName is "navigate" or "qa"
            ? OriginOf(toolName == "qa" ? arguments["spec"]?["url"]?.GetValue<string>() : JsonArgs.GetString(arguments, "url"))
            : await CurrentOriginAsync(arguments, cancellationToken);

        // A blank tab, a file:// page or a browser-internal page has no site to
        // consent to; the tool call itself was already gated by the session.
        if (origin is null)
        {
            return null;
        }

        if (AllowsEverySite || RoutineAllows(origin) || _allowed.Contains(origin) || IsPersistedAllowed(origin))
        {
            return null;
        }

        if (_denied.Contains(origin))
        {
            return Refusal(origin);
        }

        var question = new UserQuestion(
            $"Let Jarvis work on {origin}?",
            "Site",
            [
                new UserQuestionOption("Allow for this session", $"Every browser tool may act on {origin}"),
                new UserQuestionOption("Deny", "Refuse this and later actions on the site"),
            ],
            MultiSelect: false);

        var answers = await context.AskUserAsync([question], cancellationToken);
        var picked = answers is not null && answers.Answers.TryGetValue(question.Question, out var choice)
            ? choice
            : "";
        if (picked.StartsWith("Allow", StringComparison.OrdinalIgnoreCase))
        {
            _allowed.Add(origin);
            RecordRoutineOrigin(origin);
            return null;
        }

        _denied.Add(origin);
        return Refusal(origin);
    }

    private static string Refusal(string origin) =>
        $"The user has not allowed browser actions on {origin}. Ask them to allow it, or work on a site " +
        "they have already allowed.";

    private async Task<string?> CurrentOriginAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            var data = await bridge.RequestAsync("tab_origin", JarvisBrowserTools.TabArgs(arguments), cancellationToken);
            return OriginOf(data["url"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // The command itself is about to fail with the same problem; let it
            // report the real error rather than masking it as a consent refusal.
            return null;
        }
    }

    /// <summary>The site to ask about, or null for pages that carry no site.</summary>
    internal static string? OriginOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url is "back" or "forward")
        {
            return null;
        }

        var text = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : null;
    }
}

/// <summary>Wraps one browser tool so the site it would act on is consented to first.</summary>
public sealed class OriginGatedTool(ITool inner, BrowserOriginGate gate) : ITool
{
    public string Name => inner.Name;

    public string Description => inner.Description;

    public JsonObject InputSchema => inner.InputSchema;

    public bool IsReadOnly => inner.IsReadOnly;

    public string DescribeCall(JsonObject arguments) => inner.DescribeCall(arguments);

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (await gate.CheckAsync(Name, arguments, context, cancellationToken) is { } refusal)
        {
            return ToolResult.Error(refusal);
        }

        return await inner.ExecuteAsync(arguments, context, cancellationToken);
    }
}
