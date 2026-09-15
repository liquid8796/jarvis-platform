using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's own API-error categories (its <c>Wx</c> table in
/// <c>c360a9e1c-DUoNQd2W.js</c>), in its order. Two of its rows are not carried
/// and are declared in the parity suite's surface manifest.
/// </summary>
public enum ApiErrorCategory
{
    RateLimit,
    ServerRateLimit,
    ImageDimension,
    Overloaded,
    ServerError,
    StreamIdleTimeout,
    Network,
    Auth,
    ContextLength,
    UsagePolicy,
    OrgPolicyDenied,
    OrgDisabled,
    InvalidRequest,
    ImageInvalid,
    RequestTooLarge,
    ModelNotFound,
    OutputTokenLimit,
    ExtraUsageRequired,
    Billing,
    Permission,
    Unknown,
}

/// <summary>
/// What the API-error card says and offers for one category: the reference's
/// <c>headline</c> / <c>markerLabel</c> / <c>hint</c> / <c>hintNoRewind</c> and
/// the three "what would help" flags its buttons are gated on.
/// </summary>
public sealed record ApiErrorPresentation(
    string Headline,
    string Hint,
    bool RetryHelps,
    bool RewindHelps,
    bool CompactHelps,
    string? MarkerLabel = null,
    string? HintNoRewind = null);

/// <summary>
/// The error cards the transcript shows, ported from the reference desktop
/// 1.40609.1.0: the API-error classifier and its copy (<c>c360a9e1c</c>'s
/// <c>qx</c>/<c>$x</c>/<c>Bx</c>/<c>Wx</c> tables and <c>shared-13</c>'s
/// <c>mg</c>/<c>yg</c> classifier), and the session-error card's title and body
/// tables (<c>shared-13</c>'s <c>Mg</c>/<c>Cg</c>).
///
/// Free of WPF so the classification is unit-testable; the transcript renders
/// whatever <see cref="Classify"/> and <see cref="Describe"/> return.
/// </summary>
public static class ErrorCards
{
    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// The reference's <c>mg</c> table, in its order — the first pattern that
    /// matches the error text decides the category, so the order is the rule.
    /// </summary>
    private static readonly (Regex Pattern, ApiErrorCategory Category)[] MessagePatterns =
    [
        (new Regex(@"exceeds the dimension limit|\bImage was too large\b", Flags), ApiErrorCategory.ImageDimension),
        (new Regex(
            @"hit your (?:org's )?(?:monthly )?(?:usage )?limit|out of (?:extra )?usage|usage allocation|monthly usage limit|group's usage limit|(?<!not your )usage limit",
            Flags), ApiErrorCategory.RateLimit),
        (new Regex(@"rate_limit_error|\btemporarily limiting\b", Flags), ApiErrorCategory.ServerRateLimit),
        (new Regex(
            @"\bStream idle timeout\b|\bresponse (?:stopped arriving|stalled before)|\bwent to sleep (?:mid-response|before a response)",
            Flags), ApiErrorCategory.StreamIdleTimeout),
        (new Regex(@"overloaded_error|\bOverloaded\b", Flags), ApiErrorCategory.Overloaded),
        (new Regex(@"\bPrompt is too long\b|Autocompact is thrashing", Flags), ApiErrorCategory.ContextLength),
        (new Regex(@"\bRequest too large\b", Flags), ApiErrorCategory.RequestTooLarge),
        (new Regex(
            @"Usage Policy|content filtering policy|specific safety measures|safeguards flagged this message|Cyber Verification Program|anthropic\.com/legal/aup",
            Flags), ApiErrorCategory.UsagePolicy),
        (new Regex(
            @"organization has been disabled|organization does not have access|disabled Claude subscription|service is disabled for your org",
            Flags), ApiErrorCategory.OrgDisabled),
        (new Regex(@"Could not process image|image .* could not be processed|PDF is password protected", Flags),
            ApiErrorCategory.ImageInvalid),
        (new Regex(@"selected model|model not found|is not a valid model|model does not exist|has no .+ in its model picker", Flags),
            ApiErrorCategory.ModelNotFound),
        (new Regex(@"exceeded the \d+ output token", Flags), ApiErrorCategory.OutputTokenLimit),
        (new Regex(@"Extra usage is required|Usage credits required for 1M context", Flags),
            ApiErrorCategory.ExtraUsageRequired),
        (new Regex(@"credit balance is too low|\bInsufficient credits\b|billing_error", Flags),
            ApiErrorCategory.Billing),
        (new Regex(
            @"socket connection was closed|ECONNRESET|ECONNREFUSED|ConnectionRefused|Unable to connect|ETIMEDOUT|ENOTFOUND|EAI_AGAIN|certificate verification|Request timed out|Connection lost (?:mid-response|before a response)",
            Flags), ApiErrorCategory.Network),
        (new Regex(@"authentication_error|Failed to authenticate|Invalid API Key|\bOAuth token has expired\b", Flags),
            ApiErrorCategory.Auth),
        (new Regex(@"Internal server error|Server error mid-response|Bad Gateway|""api_error""", Flags),
            ApiErrorCategory.ServerError),
        (new Regex(@"permission_error", Flags), ApiErrorCategory.Permission),
        (new Regex(@"invalid_request_error", Flags), ApiErrorCategory.InvalidRequest),
    ];

    /// <summary>The reference's <c>yg</c>: the error <c>type</c> a provider names, when it names one.</summary>
    private static readonly Dictionary<string, ApiErrorCategory> ByErrorType = new(StringComparer.Ordinal)
    {
        ["rate_limit"] = ApiErrorCategory.ServerRateLimit,
        ["authentication_failed"] = ApiErrorCategory.Auth,
        ["billing_error"] = ApiErrorCategory.Billing,
        ["invalid_request"] = ApiErrorCategory.InvalidRequest,
        ["server_error"] = ApiErrorCategory.ServerError,
        ["max_output_tokens"] = ApiErrorCategory.OutputTokenLimit,
        ["overloaded_error"] = ApiErrorCategory.Overloaded,
        ["overloaded"] = ApiErrorCategory.Overloaded,
    };

    /// <summary>The status code the reference reads back out of the error text.</summary>
    private static readonly Regex StatusPattern = new(
        @"(?:API error:?|HTTP(?:/\d(?:\.\d)?)?|status(?:\s*code)?:?)\s*(4\d{2}|5\d{2})\b", Flags);

    /// <summary>The request id the card prints under the hint.</summary>
    private static readonly Regex RequestIdPattern = new(
        @"^Request ID:\s*(\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The reference's <c>kg</c>, in its order: a refusal is a usage-policy
    /// block whatever it says, then the message patterns, then the error type a
    /// provider named, then the HTTP status, then unknown.
    /// </summary>
    public static ApiErrorCategory Classify(string message, string? errorType = null, string? stopReason = null)
    {
        if (string.Equals(stopReason, "refusal", StringComparison.Ordinal))
        {
            return ApiErrorCategory.UsagePolicy;
        }

        message ??= "";
        foreach (var (pattern, category) in MessagePatterns)
        {
            if (pattern.IsMatch(message))
            {
                return category;
            }
        }

        if (errorType is { Length: > 0 } && ByErrorType.TryGetValue(errorType, out var byType))
        {
            return byType;
        }

        return TryReadStatus(message) is { } status ? FromStatus(status) : ApiErrorCategory.Unknown;
    }

    /// <summary>The reference's <c>Sg</c>: the 4xx/5xx it can find in the text.</summary>
    public static int? TryReadStatus(string message)
    {
        var match = StatusPattern.Match(message ?? "");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>The reference's status ladder, which runs when nothing else matched.</summary>
    public static ApiErrorCategory FromStatus(int status) => status switch
    {
        429 => ApiErrorCategory.ServerRateLimit,
        529 => ApiErrorCategory.Overloaded,
        >= 500 => ApiErrorCategory.ServerError,
        401 => ApiErrorCategory.Auth,
        402 => ApiErrorCategory.Billing,
        403 => ApiErrorCategory.Permission,
        413 => ApiErrorCategory.RequestTooLarge,
        >= 400 => ApiErrorCategory.InvalidRequest,
        _ => ApiErrorCategory.Unknown,
    };

    public static string? RequestIdIn(string message)
    {
        var match = RequestIdPattern.Match(message ?? "");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The reference's <c>Wx</c> table. <c>rewindHelps</c> is its <c>Qx</c> set
    /// and <c>compactHelps</c> is the context-length row alone.
    /// </summary>
    private static readonly Dictionary<ApiErrorCategory, ApiErrorPresentation> Presentations = new()
    {
        [ApiErrorCategory.RateLimit] = new(
            "Usage limit reached",
            "You’ve reached your usage limit. Try again after your limit resets.",
            RetryHelps: true, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.ServerRateLimit] = new(
            "Server is temporarily limiting requests",
            "Too many requests right now — try again in a moment.",
            RetryHelps: true, RewindHelps: false, CompactHelps: false,
            MarkerLabel: "Server was temporarily limiting requests"),
        [ApiErrorCategory.ImageDimension] = new(
            "Image is too large",
            "An image in this conversation is over 2000px. Rewind to remove it, or start a new session.",
            RetryHelps: false, RewindHelps: true, CompactHelps: false,
            HintNoRewind: "An image in this conversation is over 2000px. Start a new session to continue."),
        [ApiErrorCategory.Overloaded] = new(
            "Service is busy",
            "Try again in a moment, or switch to a different model.",
            RetryHelps: true, RewindHelps: false, CompactHelps: false,
            MarkerLabel: "Service was busy"),
        [ApiErrorCategory.ServerError] = new(
            "Server error",
            "Something went wrong. Try again in a moment. If it persists, check {statusUrl}.",
            RetryHelps: true, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.StreamIdleTimeout] = new(
            "Connection went idle",
            "The response stream stopped before finishing — try again in a moment.",
            RetryHelps: true, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.Network] = new(
            "Connection problem",
            "Check your internet connection, VPN, or proxy and try again.",
            RetryHelps: true, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.Auth] = new(
            "Authentication failed",
            "Sign in again to continue.",
            RetryHelps: false, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.ContextLength] = new(
            "Your context window is full",
            "This chat is too long to continue. Compact it to summarize earlier messages and keep going, rewind to an earlier point, or start a new chat.",
            RetryHelps: false, RewindHelps: true, CompactHelps: true,
            MarkerLabel: "Context window was full",
            HintNoRewind: "This chat is too long to continue. Start a new chat."),
        [ApiErrorCategory.UsagePolicy] = new(
            "Request was blocked",
            "This request triggered safety guardrails. Rephrase your prompt or rewind to continue.",
            RetryHelps: false, RewindHelps: true, CompactHelps: false,
            HintNoRewind: "This request triggered safety guardrails. Switch models to continue."),
        [ApiErrorCategory.OrgPolicyDenied] = new(
            "Blocked by your organization’s policy",
            "This message was blocked by your organization’s policy. Rephrase your message or rewind to continue.",
            RetryHelps: false, RewindHelps: true, CompactHelps: false,
            HintNoRewind: "This message was blocked by your organization’s policy."),
        [ApiErrorCategory.OrgDisabled] = new(
            "Organization access is disabled",
            "Your organization’s access has been disabled. Contact your admin.",
            RetryHelps: false, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.InvalidRequest] = new(
            "Invalid request",
            "The request couldn’t be completed.",
            RetryHelps: false, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.ImageInvalid] = new(
            "Image couldn’t be processed",
            "An image couldn’t be read. Rewind to remove it and try a different format.",
            RetryHelps: false, RewindHelps: true, CompactHelps: false,
            HintNoRewind: "An image couldn’t be read. Start a new session to continue."),
        [ApiErrorCategory.RequestTooLarge] = new(
            "Request is too large",
            "An attachment is too large. Remove it and try again.",
            RetryHelps: false, RewindHelps: true, CompactHelps: false,
            HintNoRewind: "An attachment is too large. Start a new session to continue."),
        [ApiErrorCategory.ModelNotFound] = new(
            "Model isn’t available",
            "Switch to a different model from the model picker to continue.",
            RetryHelps: false, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.OutputTokenLimit] = new(
            "Response exceeded the output limit",
            "Ask Jarvis to continue, or break the task into smaller steps.",
            RetryHelps: true, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.ExtraUsageRequired] = new(
            "Extra usage is required",
            "Enable extra usage in Settings, or switch to a model with standard context.",
            RetryHelps: false, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.Billing] = new(
            "Billing issue",
            "Add credits or update billing to continue.",
            RetryHelps: false, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.Permission] = new(
            "Access denied",
            "You don’t have access to this resource — check your permissions.",
            RetryHelps: false, RewindHelps: false, CompactHelps: false),
        [ApiErrorCategory.Unknown] = new(
            "An API error occurred",
            "Try sending your message again.",
            RetryHelps: true, RewindHelps: false, CompactHelps: false),
    };

    /// <summary>Anthropic's own status page, which the server-error hint links to.</summary>
    public const string StatusUrl = "https://status.anthropic.com";

    /// <summary>
    /// What the card should say. <paramref name="canRewind"/> picks between the
    /// two hints the reference keeps for the categories that offer a rewind.
    /// </summary>
    public static ApiErrorPresentation Describe(ApiErrorCategory category, bool canRewind = true)
    {
        var presentation = Presentations[category];
        var hint = !canRewind && presentation.HintNoRewind is { Length: > 0 } noRewind
            ? noRewind
            : presentation.Hint.Replace("{statusUrl}", StatusUrl, StringComparison.Ordinal);
        return presentation with
        {
            Hint = hint,
            RewindHelps = presentation.RewindHelps && canRewind,
        };
    }

    /// <summary>The label a collapsed marker uses, which is the headline unless the table names one.</summary>
    public static string MarkerLabelFor(ApiErrorCategory category) =>
        Presentations[category].MarkerLabel ?? Presentations[category].Headline;

    // ---- the session-error card ----

    /// <summary>
    /// The reasons a session itself cannot run, from the reference's <c>Mg</c>
    /// (titles) and <c>Cg</c> (bodies). Only the reasons this app can reach are
    /// carried; the rest are declared in the surface manifest.
    /// </summary>
    public enum SessionErrorKind
    {
        Network,
        ComputerSlept,
        ResumeNotFound,
        FolderNotFound,
        FolderPathTooLong,
        GitRequired,
        GitNotWorking,
        DiskFull,
        UnexpectedOutput,
        NestedSession,
        BypassGateBlocked,
        Interrupted,
        RewindTargetNotFound,
        GitCheckoutFailed,
        WorktreeHookFailed,
        WorktreeRepairNeeded,
        BinaryLocked,
        TrustRequired,
        ImportedNeedsConfirm,
    }

    private static readonly Dictionary<SessionErrorKind, (string Title, string Body)> SessionErrors = new()
    {
        [SessionErrorKind.Network] = (
            "Connection lost",
            "Check your internet connection, VPN, or proxy and try again."),
        [SessionErrorKind.ComputerSlept] = (
            "Computer went to sleep",
            "The response was interrupted when your computer went to sleep and may be incomplete. Try sending your message again."),
        [SessionErrorKind.ResumeNotFound] = (
            "Session history unavailable",
            "This session’s conversation history is no longer on disk. You can keep working here — sending your message will start a fresh session in this folder."),
        [SessionErrorKind.FolderNotFound] = (
            "Folder not found",
            "This session’s working folder no longer exists. Choose a different folder or start a new session."),
        [SessionErrorKind.FolderPathTooLong] = (
            "Folder path is too long",
            "Windows limits a process’s starting folder to 260 characters. Open this project from a shorter path to start a session."),
        [SessionErrorKind.GitRequired] = (
            "Git is required",
            "Install Git (Xcode Command Line Tools on macOS, Git for Windows on Windows, or your distro’s git package on Linux) to continue."),
        [SessionErrorKind.GitNotWorking] = (
            "Git isn’t working",
            "Git is installed but failed to start, usually because of a configuration file it can’t read. The error below explains what to do next."),
        [SessionErrorKind.DiskFull] = (
            "Not enough disk space",
            "There isn’t enough free disk space to set up a worktree for this session. Free up disk space and try again."),
        [SessionErrorKind.UnexpectedOutput] = (
            "Unexpected output from Jarvis Code",
            "A shell hook or tool wrote unexpected output. Check your shell startup files and Jarvis Code hooks for stray stdout writes."),
        [SessionErrorKind.NestedSession] = (
            "Nested session",
            "Jarvis Code can’t run inside another Jarvis Code session. Close the outer session to continue."),
        [SessionErrorKind.BypassGateBlocked] = (
            "Bypass permissions was blocked",
            "Jarvis Code refused to start in bypass permissions mode on this machine. The session has been switched to Accept edits. Send your message again to continue."),
        [SessionErrorKind.Interrupted] = (
            "Session was interrupted",
            "Try sending your message again."),
        [SessionErrorKind.RewindTargetNotFound] = (
            "Couldn’t rewind",
            "That point in the conversation couldn’t be found. Send your message again to continue the session."),
        [SessionErrorKind.GitCheckoutFailed] = (
            "Couldn’t switch branches",
            "Commit or stash changes, or check your repository for issues like a missing branch, locked files, or Git LFS setup."),
        [SessionErrorKind.WorktreeHookFailed] = (
            "Your WorktreeCreate hook failed",
            "Your WorktreeCreate hook didn’t complete successfully. Check the hook output in the details below and fix the hook, then send your message again."),
        [SessionErrorKind.WorktreeRepairNeeded] = (
            "Worktree needs repair",
            "This worktree no longer links back to its repository, usually because the folder was moved or renamed. Run git worktree repair inside the folder, then try again."),
        [SessionErrorKind.BinaryLocked] = (
            "Locked by another program",
            "Another program (e.g. antivirus) has a temporary lock on app files. Wait a moment and try again."),
        [SessionErrorKind.TrustRequired] = (
            "Workspace trust needed",
            "Approve this folder in the trust prompt to continue."),
        [SessionErrorKind.ImportedNeedsConfirm] = (
            "Resume imported session?",
            "This session was imported. Resuming lets Jarvis act on its history — only continue if you trust where it came from."),
    };

    public static (string Title, string Body) Describe(SessionErrorKind kind) => SessionErrors[kind];

    /// <summary>
    /// The reference's disk-full body when it knows the numbers, which its
    /// worktree setup passes through.
    /// </summary>
    public static string DiskFullBody(string worktreeDirectory, int availableGb, int requiredGb) =>
        $"The disk containing {worktreeDirectory} has {availableGb} GB free, but at least {requiredGb} GB is needed " +
        "to set up a worktree for this session. Free up disk space and try again.";

    /// <summary>The crash-loop body, which the reference shows once restarts are parked.</summary>
    public const string CrashLoopParked =
        "Jarvis Code crashed several times in a row right after starting, so automatic restarts are paused for this session. This is usually caused by security or anti-cheat software interfering with Jarvis Code on this machine. Send a message to try again.";
}
