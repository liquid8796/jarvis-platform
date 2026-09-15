using JarvisCode.Core.Permissions;

namespace JarvisCode.App.Services;

/// <summary>
/// The notices that ride the tail of the harness system turn: what the session's
/// permission mode and output style tell the model, as opposed to what its
/// tools and skills do.
/// </summary>
/// <remarks>
/// Measured 2026-09-01 against CLI 2.1.251 and re-read out of 2.1.257, where the
/// three permission-mode texts are one <c>auto_mode</c> attachment with three
/// branches: <c>bypass</c> → "While bypass permissions mode is active:" over the
/// bash-first paragraph, <c>steerOnly</c> → "While auto mode is active:" over the
/// same paragraph, and otherwise the <c>## Auto Mode Active</c> block with its
/// two paragraphs, an optional consent-flow paragraph, and the bash-first
/// paragraph appended when the session is bash-first.
///
/// <b>The channel.</b> None of these ride the user's message. They are appended
/// to the <c>role: system</c> turn that already carries the agent roster and the
/// skill listing; a model without that role gets them as
/// <c>&lt;system-reminder&gt;</c> blocks on its user message.
///
/// <b>What repeats.</b> Captured across two turns of one session
/// (<c>-p</c> then <c>--continue -p</c>): the mode notices do not repeat, the
/// plan workflow does not repeat, and the style reminder <em>does</em>.
///
/// <b>Bash-first is model-gated.</b> The reference's <c>uKt()</c> is forced on
/// for fable-5-1 and otherwise reads a cohort flag that ships off, so a clean
/// opus-5 bypass run sends no notice at all — this port used to send it to
/// every model. The consent-flow paragraph describes the reference's auto-mode
/// classifier, which this app's local risk gate is not; it is declared rather
/// than carried.
/// </remarks>
internal static class SessionModeNotices
{
    /// <summary>
    /// The bash-first paragraph, verbatim. The reference interpolates its shell
    /// and file-tool names here and this harness registers the same four, so
    /// nothing is adapted.
    /// </summary>
    internal const string BashFirst =
        "Do your work through the Bash tool wherever it can accomplish the job: read files with cat, " +
        "head, or sed -n, search with grep and find, and make file changes with sed, heredocs, or short " +
        "scripts, rather than using the dedicated Read, Edit, or Write tools. Fall back to a dedicated " +
        "tool only when Bash genuinely cannot do the job.";

    /// <summary>The bypass branch, verbatim.</summary>
    internal const string BypassPermissions =
        "While bypass permissions mode is active:\n\n" + BashFirst;

    /// <summary>The auto-mode block's two paragraphs, verbatim (the reference's <c>o</c>).</summary>
    internal const string AutoModeActive =
        "## Auto Mode Active\n\n" +
        "Bias toward working without stopping for clarifying questions — when you'd normally pause to check, " +
        "make the reasonable call and keep going; they'll redirect you if needed. If the user, a skill, or the " +
        "shape of the task suggests they want you to ask (with AskUserQuestion or otherwise), do so. And even " +
        "absent that signal, it's still fine to stop when you're genuinely blocked — unclear direction, " +
        "missing input, a decision only they can make.\n\n" +
        "Before any command that could discard uncommitted work — `git checkout`/`restore`/`reset`/`clean`, " +
        "`rm -rf` in the repo, restoring from a snapshot — run `git status` first and stash (with `-u` for " +
        "untracked) or commit anything that's there. When staging or committing, review what's included " +
        "(`git status` after a broad `git add`), and if you see anything suspicious that might reveal secrets " +
        "— even if the filename looks innocuous — double-check the file's contents before pushing.";

    /// <summary>
    /// The notice a permission mode contributes on the turn it first applies, or
    /// null when the mode says nothing.
    /// </summary>
    /// <param name="hasFileToolsAndShell">
    /// The reference's bash-first eligibility: a shell tool present, and at
    /// least one of the file-editing tools present. That is exactly the Code
    /// surface here — the Chat surface is toolless.
    /// </param>
    /// <param name="bashFirstForced">
    /// The model half of that eligibility (<see cref="PromptModelProfile.BypassNoticeForced"/>):
    /// fable-5-1 is forced bash-first; every other model's cohort flag ships off.
    /// </param>
    internal static string? ForMode(PermissionMode mode, bool hasFileToolsAndShell, bool bashFirstForced = false)
    {
        var bashFirst = hasFileToolsAndShell && bashFirstForced;
        return mode switch
        {
            PermissionMode.Bypass => bashFirst ? BypassPermissions : null,
            PermissionMode.Auto => bashFirst ? AutoModeActive + "\n\n" + BashFirst : AutoModeActive,
            _ => null,
        };
    }
}
