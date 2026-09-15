using System.Globalization;
using JarvisCode.Core.Permissions;

namespace JarvisCode.App.Services;

/// <summary>
/// The composer's permission-mode picker, ported from the reference desktop's Code
/// surface (Claude 1.40609.0.0). The descriptor and the order table live in ion-dist
/// <c>shared-10-3-tqq7pk.js</c> (its <c>WP</c>/<c>VP</c>/<c>GP</c>); the item builder,
/// the danger predicate and the digit assignment live in <c>ca80fca8d-DkeN2GSR.js</c>
/// (its <c>permissionItems</c>/<c>Pm</c>/<c>Iv</c>/<c>Ev</c>). Kept pure so the order,
/// the wording and the digits are testable without a window.
/// </summary>
public static class PermissionModeMenu
{
    /// <summary>
    /// What one row says. The reference descriptor also carries an icon and a short
    /// trigger label, but the Code item builder passes neither — its rows are label
    /// plus description — so neither is modelled here.
    /// </summary>
    public sealed record ModeInfo(string Label, string Description);

    /// <summary>The popup's group label, above the rows.</summary>
    public const string Header = "Mode";

    /// <summary>Trailing badge on the row the settings default names.</summary>
    public const string DefaultBadge = "Default";

    /// <summary>The chip's tooltip while the session sits in a dangerous mode.</summary>
    public const string DangerTooltip = "Jarvis can modify or delete files without asking";

    /// <summary>Shown when changing the mode did not take.</summary>
    public const string ChangeFailed = "Permission mode couldn’t be changed. You can try again.";

    /// <summary>The workspace key a session with no folder of its own is filed under.</summary>
    public const string ScratchWorkspace = "scratch:";

    /// <summary>
    /// The reference's <c>WP</c> for a local environment outside a Cowork session,
    /// which is what the Code surface renders. Its Cowork wording ("Manually approve",
    /// "Skip all approvals", …) and its remote-environment branch, where "default"
    /// reads as Accept edits because a cloud runner cannot prompt, are the two arms
    /// this build never takes.
    /// </summary>
    public static ModeInfo Describe(PermissionMode mode) => mode switch
    {
        PermissionMode.Plan => new("Plan", "Create a plan before making changes"),
        PermissionMode.AcceptEdits => new("Accept edits", "Automatically accept all file edits"),
        PermissionMode.Auto => new("Auto", "Jarvis handles permission decisions"),
        PermissionMode.Bypass => new("Bypass permissions", "Accepts all permissions"),
        _ => new("Manual", "Always ask before making changes"),
    };

    /// <summary>
    /// The reference's <c>VP("local", …)</c>: a fixed base of three, then auto at
    /// whichever end its rollout flag names, then bypass last. The order is the
    /// reference's and not this app's preference — a row that moves is a row the
    /// user's muscle memory misses.
    /// </summary>
    public static IReadOnlyList<PermissionMode> Order(bool autoAvailable, bool autoFirst, bool bypassAvailable)
    {
        var modes = new List<PermissionMode>
        {
            PermissionMode.Manual,
            PermissionMode.AcceptEdits,
            PermissionMode.Plan,
        };

        if (autoAvailable)
        {
            if (autoFirst)
            {
                modes.Insert(0, PermissionMode.Auto);
            }
            else
            {
                modes.Add(PermissionMode.Auto);
            }
        }

        if (bypassAvailable)
        {
            modes.Add(PermissionMode.Bypass);
        }

        return modes;
    }

    /// <summary>
    /// What this build offers, in the reference's order. Both availability flags are
    /// true here: what turns them off there is an organization policy, an Anthropic
    /// -internal build or a model with no auto classifier, none of which this app has —
    /// see the surface manifest for the disabled rows that go with them. autoFirst is
    /// the reference's rollout flag, off outside its cohorts.
    /// </summary>
    public static IReadOnlyList<PermissionMode> Offered { get; } =
        Order(autoAvailable: true, autoFirst: false, bypassAvailable: true);

    /// <summary>
    /// What the menu offers a session: the same list, minus Bypass while Settings ›
    /// Jarvis Code › Local sessions › "Allow bypass permissions mode" is off. That
    /// switch is the reference's <c>bypassPermissionsModeEnabled</c>, and its own
    /// menu builds the list with <c>bypassAvailable</c> false when it is not set.
    /// </summary>
    public static IReadOnlyList<PermissionMode> OfferedFor(UiSettings settings) =>
        Order(autoAvailable: true, autoFirst: false, bypassAvailable: settings.BypassPermissionsModeEnabled);

    /// <summary>
    /// The reference's <c>GP</c>: a mode the offered list does not carry falls back to
    /// Accept edits when it was Auto, otherwise to the first offered mode that is not
    /// Auto — so a narrowed list never silently promotes a session into Auto.
    /// </summary>
    public static PermissionMode Clamp(PermissionMode mode, IReadOnlyList<PermissionMode> offered)
    {
        if (offered.Contains(mode))
        {
            return mode;
        }

        if (mode == PermissionMode.Auto && offered.Contains(PermissionMode.AcceptEdits))
        {
            return PermissionMode.AcceptEdits;
        }

        foreach (var candidate in offered)
        {
            if (candidate != PermissionMode.Auto)
            {
                return candidate;
            }
        }

        return offered.Count > 0 ? offered[0] : PermissionMode.AcceptEdits;
    }

    /// <summary>
    /// The reference's <c>Pm</c>: the modes that need consent before the session moves
    /// into them. Auto counts only where it is not already the default, which is the
    /// reference asking about something newly rolled out rather than about risk.
    /// </summary>
    public static bool IsDangerous(PermissionMode mode, bool autoIsDefault) =>
        mode == PermissionMode.Bypass || (mode == PermissionMode.Auto && !autoIsDefault);

    /// <summary>
    /// The reference's <c>Iv</c> over <c>Ev</c>: digits 1-9 handed out in row order to
    /// the rows that can actually be picked, so a disabled row never spends a number.
    /// </summary>
    public static IReadOnlyList<string?> Shortcuts(IReadOnlyList<bool> quickKeyEligible)
    {
        var assigned = new string?[quickKeyEligible.Count];
        var used = 0;
        for (var i = 0; i < quickKeyEligible.Count; i++)
        {
            if (!quickKeyEligible[i] || used >= 9)
            {
                continue;
            }

            used++;
            assigned[i] = used.ToString(CultureInfo.InvariantCulture);
        }

        return assigned;
    }

    /// <summary>The consent dialog the reference raises before a dangerous mode takes effect.</summary>
    public sealed record Consent(string Title, string Description, string ConfirmLabel, string? Workspace, string? Footnote);

    /// <summary>
    /// The reference's <c>MA</c> for the bypass arm. Its auto arm ("Enable auto mode?")
    /// is deliberately not carried — see the surface manifest: that copy promises a
    /// model-side classifier with prompt-injection safeguards, which this build's Auto
    /// (a local risk-class gate) is not, and Auto is this build's default anyway, so
    /// <see cref="IsDangerous"/> never reaches it. The reference's security-guide link
    /// is dropped with the same reasoning: this app publishes no such guide.
    /// </summary>
    public static Consent BypassConsent(string workspaceKey)
    {
        var workspace = workspaceKey == ScratchWorkspace ? null : workspaceKey;
        return new Consent(
            "Bypass all permissions?",
            // One literal rather than a concatenation: the manifest check reads the file
            // as text, and a sentence split across two lines is a sentence it cannot find.
            "Jarvis will read, edit, and execute files without asking — including potentially destructive commands. Only use this in isolated or disposable environments.",
            "Bypass permissions",
            workspace,
            workspace is null ? null : "You won’t be asked again for this workspace.");
    }

    /// <summary>The ack a confirmed consent records, keyed the reference's way.</summary>
    public static string AckKey(string workspaceKey, PermissionMode mode) => $"{workspaceKey}:{WireName(mode)}";

    /// <summary>The reference's own spelling for a mode, which is what an ack key stores.</summary>
    public static string WireName(PermissionMode mode) => mode switch
    {
        PermissionMode.Manual => "default",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.Auto => "auto",
        PermissionMode.Bypass => "bypassPermissions",
        _ => mode.ToString(),
    };
}
