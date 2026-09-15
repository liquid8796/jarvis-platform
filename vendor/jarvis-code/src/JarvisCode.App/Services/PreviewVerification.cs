using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's auto-verify loop, which is two pieces rather than one:
/// a system-prompt block telling the model how to verify a change in the
/// Browser pane, and built-in PostToolUse hooks that remind it to before the
/// turn ends. The reminder fires once per turn, after the first edit to a
/// previewable file, and the Stop hook arms it again for the next turn.
/// </summary>
public sealed class PreviewVerification(
    Func<bool> autoVerifyEnabled,
    Func<bool> previewRunningHere,
    Func<bool> previewRunningElsewhere,
    Func<string, bool>? openHtmlPreview = null)
{
    /// <summary>Source extensions the preview can exercise (the reference's own list).</summary>
    private static readonly string[] PreviewableExtensions =
        [".js", ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".astro", ".css", ".scss", ".less", ".html", ".htm"];

    /// <summary>Files the pane can show directly rather than through a dev server.</summary>
    private static readonly string[] ViewableExtensions =
    [
        ".html", ".htm", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif",
        ".pdf", ".mp4", ".webm", ".m4v", ".mov", ".ogv",
    ];

    /// <summary>The reference's nudge policy name: at most one per turn, after the first write.</summary>
    public const string NudgePolicy = "after_first_write";

    private bool _nudgedThisTurn;

    /// <summary>The hooks to join into a Code turn.</summary>
    public IReadOnlyList<FunctionHook> Hooks =>
    [
        new FunctionHook(HookEvent.PostToolUse, PostToolUseAsync),
        new FunctionHook(HookEvent.Stop, StopAsync),
    ];

    private Task<FunctionHookResult> StopAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        // A finished turn arms the nudge again for the next one.
        _nudgedThisTurn = false;
        return Task.FromResult(FunctionHookResult.None);
    }

    private Task<FunctionHookResult> PostToolUseAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var tool = JsonArgs.GetString(payload, "tool") ?? "";
        var arguments = payload["arguments"] as JsonObject;

        // An approved plan carries its own verification steps; the reference
        // feeds them back so the implementation ends where the plan said it would.
        if (tool == "ExitPlanMode")
        {
            if (!autoVerifyEnabled())
                return Task.FromResult(FunctionHookResult.None);
            var plan = JsonArgs.GetString(payload["result"] as JsonObject, "content") ?? "";
            return Task.FromResult(VerificationSection(plan) is { Length: > 0 } steps
                ? new FunctionHookResult(
                    "After implementing the plan, follow <verification_workflow> to verify.\n\n" +
                    "Verification steps from the plan:\n" + steps)
                : FunctionHookResult.None);
        }

        if (tool is not ("Edit" or "Write" or "MultiEdit" or "NotebookEdit"))
            return Task.FromResult(FunctionHookResult.None);

        var path = JsonArgs.GetString(arguments, "file_path") ?? "";
        if (path.Length == 0)
            return Task.FromResult(FunctionHookResult.None);

        // A file the pane can show directly is shown instead of being verified
        // through a dev server — unless an HTML file is already being served by one.
        bool isHtml = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
        if (ViewableExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) &&
            !(isHtml && previewRunningHere()) &&
            openHtmlPreview is not null)
        {
            bool opened;
            try
            {
                opened = openHtmlPreview(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException)
            {
                opened = false;
            }

            if (opened && !previewRunningHere())
                return Task.FromResult(new FunctionHookResult($"{path} is now visible in the Browser pane."));
        }

        if (!PreviewableExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(FunctionHookResult.None);

        if (_nudgedThisTurn || !autoVerifyEnabled())
            return Task.FromResult(FunctionHookResult.None);

        _nudgedThisTurn = true;
        return Task.FromResult(new FunctionHookResult(Nudge()));
    }

    private string Nudge()
    {
        if (previewRunningHere())
        {
            return "A preview server is running. Before ending your turn, if this change is observable in the " +
                   "Browser pane (per <when_to_verify>), follow <verification_workflow>.";
        }

        if (previewRunningElsewhere())
        {
            return "Another chat's dev server is running in this folder; the Browser tools in this session won't " +
                   "reach it. Before ending your turn, if this change is observable in the Browser pane (per " +
                   "<when_to_verify>), call preview_start to start this session's own server and follow " +
                   "<verification_workflow>.";
        }

        return "No preview server is running. Before ending your turn, if this change is observable in the " +
               "Browser pane (per <when_to_verify>), call preview_start and follow <verification_workflow>.";
    }

    /// <summary>
    /// The plan's verification section: the reference reads the body under a
    /// "## Verification", "## Test Plan" or "## Testing" heading, up to the next
    /// heading of any level.
    /// </summary>
    public static string? VerificationSection(string plan)
    {
        var heading = System.Text.RegularExpressions.Regex.Match(
            plan,
            @"^##\s+(?:Verification|Test\s*[Pp]lan|Testing)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!heading.Success)
        {
            return null;
        }

        var rest = plan[(heading.Index + heading.Length)..];
        var next = System.Text.RegularExpressions.Regex.Match(rest, @"\n#{1,6}\s");
        var body = (next.Success ? rest[..next.Index] : rest).Trim();
        return body.Length > 0 ? body : null;
    }

    /// <summary>
    /// The block the reference appends to the system prompt while auto-verify is
    /// on. Empty when it is off, which is also what the reference does — the
    /// hooks above stay registered either way and check the flag when they fire.
    /// </summary>
    public static string PromptBlock(bool autoVerifyEnabled) => autoVerifyEnabled
        ? """

        <preview_tools>
        The Browser pane's tools drive an in-app browser with TABS — one pane per session, and every dev server or external site you open is a tab on it. Use them to browse the web (research, docs, staging, the deployed app) or to run and verify the project's dev server. Never use Bash to run dev servers.

        preview_start opens a tab: `{url: "https://…"}` opens a browser tab at that URL (no dev server needed); `{name: "…"}` starts the named dev server from .jarvis/launch.json and opens a tab at its localhost port. The result includes a `tabId` — pass it to read_page / computer / navigate / etc. to target that tab. `tabs_context` lists every open tab; omitting tabId acts on the fronted tab. `serverId` in the result is the PROCESS id, used only for preview_stop and preview_logs.

        <when_to_verify>
        Run the verification workflow only when the change would be observable in the browser preview — something the dev server renders, serves, or logs. If the change affects code the preview can't exercise (a different runtime, tests, types, tooling, or work that isn't ready to run yet), skip verification — don't start a server that won't prove anything.
        </when_to_verify>

        <verification_workflow>
        After editing code that is previewable, verify it works. Never ask the user to check manually — verify and share proof directly.

        1. Ensure a preview is open: preview_start with `{name}` for the dev server (or `{url}` for an external site).
        2. Reload if needed (navigate to the current URL again, or javascript_tool: window.location.reload()). Skip if HMR is active.

        Check for issues using text-based tools:
        3. read_console_messages, preview_logs, or read_network_requests for errors.
        4. read_page for content and structure (returns refs you can pass to computer/form_input).
        5. javascript_tool for computed CSS values.
        6. computer (click/type) or form_input to test interactions, then read_page to confirm.
        7. resize_window for responsive or dark mode.

        If issues are found, read source code to diagnose, edit source files to fix, then re-check from step 3. Use javascript_tool for debugging only.

        Once everything is working, share proof with the user:
        8. computer {action: "screenshot"} for visual changes, read_network_requests for API changes, or preview_logs for server changes.

        Skip steps that aren't relevant — e.g. skip step 5 for non-CSS changes, skip step 7 unless layout or theming changed.
        </verification_workflow>
        </preview_tools>

        """
        : "";
}
