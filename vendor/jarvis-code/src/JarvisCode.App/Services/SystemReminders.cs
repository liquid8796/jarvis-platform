using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JarvisCode.Core.Models;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference app's <c>&lt;system-reminder&gt;</c> attachments, rebuilt at the
/// App level: a gitStatus snapshot rides on the first user message of a Code
/// session, and a plan-mode reminder rides on every user message while plan mode
/// is on. Reminders travel as extra text blocks inside the stored user message —
/// exactly how the reference persists them — and the transcript UI hides them
/// via <see cref="VisibleText"/>.
/// </summary>
public static class SystemReminders
{
    private const string OpenTag = "<system-reminder>";
    private const int StatusMaxChars = 2000;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The plan-mode reminder. The reference sends the five-phase workflow while
    /// plan mode is on and a short form once that workflow is already in the
    /// conversation; a subagent gets neither, only the restriction.
    /// </summary>
    public static string PlanMode(
        string planFilePath,
        bool planExists = false,
        bool withAgents = true,
        bool sparse = false,
        string? customInstructions = null) =>
        OpenTag + "\n" +
        (sparse
            ? PlanModePrompts.Sparse(planFilePath, customInstructions is { Length: > 0 })
            : PlanModePrompts.Full(planFilePath, planExists, withAgents, customInstructions)) +
        "\n</system-reminder>";

    /// <summary>The subagent form of the plan-mode reminder.</summary>
    public static string PlanModeForSubagent(string planFilePath, bool planExists) =>
        OpenTag + "\n" + PlanModePrompts.ForSubagent(planFilePath, planExists) + "\n</system-reminder>";

    /// <summary>/goal — the standing goal rides every message so a stop is checked against it.</summary>
    public static string Goal(string goal) =>
        OpenTag + "\n" +
        $"The user's standing goal for this session: {goal}\n" +
        "Before ending your turn, check whether the goal is met. If it is not, continue working toward it, " +
        "or state plainly what remains and why you stopped.\n" +
        "</system-reminder>";

    /// <summary>/brief — brief-only mode, attached to every message while it is on.</summary>
    public const string Brief =
        OpenTag + "\n" +
        "Brief mode is on: answer in the fewest words that fully answer. No preamble, no recap of what you " +
        "did, and no offered next steps unless asked.\n" +
        "</system-reminder>";

    /// <summary>
    /// The reference CLI's "ultrathink" keyword reminder, verbatim — in 2.x the
    /// keyword asks for deeper reasoning in words instead of bumping a budget.
    /// </summary>
    public const string Ultrathink =
        OpenTag + "\n" +
        "The user included the keyword \"ultrathink\", requesting deeper reasoning on this turn. " +
        "Reason as thoroughly as the task warrants.\n" +
        "</system-reminder>";

    /// <summary>
    /// The reference's "ultracode" keyword reminder (turn-scoped opt-in) — the
    /// keyword trigger its `workflowKeywordTriggerEnabled` setting describes:
    /// "including the keyword in a prompt opts that turn into the Workflow tool".
    /// </summary>
    public const string UltracodeKeyword =
        OpenTag + "\n" +
        "The user included the keyword \"ultracode\", opting this turn into multi-agent " +
        "orchestration — author and run a workflow with the workflow tool, or fan the work " +
        "out with Agent workers (run_in_background for anything that should not block), " +
        "to fulfill the request.\n" +
        "</system-reminder>";

    /// <summary>
    /// The reference's ultra_effort_enter reminder (full form). It tells the model
    /// to author a workflow per substantive task; Agent stays named beside it
    /// because a fan-out without a script is still the cheaper shape here.
    /// </summary>
    public const string UltracodeEnter =
        OpenTag + "\n" +
        "Ultracode is on: optimize for the most exhaustive, correct answer — not the fastest " +
        "or cheapest. Author and run a workflow for every substantive task (the workflow tool " +
        "orchestrates subagents deterministically), or fan the work out with Agent workers " +
        "(run_in_background: true so they run off the turn); token cost is not a constraint. " +
        "Adversarially verify findings with independent agents before concluding. Solo only on " +
        "conversational/trivial turns.\n" +
        "</system-reminder>";

    /// <summary>The short recurring form, riding every later message while Ultracode is on.</summary>
    public const string UltracodeStillOn =
        OpenTag + "\n" +
        "Ultracode is still on — fan substantive work out with a workflow or Agent workers " +
        "and verify adversarially.\n" +
        "</system-reminder>";

    /// <summary>The reference's ultra_effort_exit reminder, adapted the same way.</summary>
    public const string UltracodeExit =
        OpenTag + "\n" +
        "Ultracode is off — workflow and Agent delegation return to normal: use them only " +
        "when a task genuinely needs it.\n" +
        "</system-reminder>";

    /// <summary>
    /// The reference skill_listing attachment: the available skills ride a
    /// system-reminder on the user message (full on the first message, only
    /// newly discovered skills afterwards).
    /// </summary>
    public static string SkillListing(string listingBody) =>
        OpenTag + "\n" +
        JarvisCode.Core.Customization.SkillInvocation.ListingHeader + "\n\n" +
        listingBody + "\n" +
        "</system-reminder>";

    /// <summary>
    /// The reference invoked_skills reminder: after a compaction cleared invoked
    /// skills' content from the live context, their instructions ride the next
    /// message for awareness (never for re-execution).
    /// </summary>
    public static string InvokedSkills(string reminderBody) =>
        OpenTag + "\n" +
        reminderBody + "\n" +
        "</system-reminder>";

    private const string PromptHookOpenTag = "<user-prompt-submit-hook>";

    /// <summary>
    /// True for a content block that <em>looks</em> harness-authored. This is the
    /// fallback for messages whose provenance counts are missing or stale; it
    /// cannot tell a reminder the harness wrote from one the user typed, which is
    /// why <see cref="VisibleText"/> prefers the counts.
    /// </summary>
    public static bool IsReminder(ContentBlock block) =>
        block is TextBlock text &&
        (text.Text.StartsWith(OpenTag, StringComparison.Ordinal) ||
         text.Text.StartsWith(PromptHookOpenTag, StringComparison.Ordinal) ||
         text.Text.StartsWith("<session-start-hook>", StringComparison.Ordinal));

    /// <summary>
    /// The reference's <c>hook_success</c> attachment (CLI 2.1.257, at
    /// 190556359): stdout from a SessionStart, UserPromptSubmit or
    /// UserPromptExpansion hook rides the message as
    /// <c>{hookName} hook success: {stdout}</c> inside a reminder — not in a
    /// <c>&lt;session-start-hook&gt;</c> / <c>&lt;user-prompt-submit-hook&gt;</c>
    /// tag pair, which this port used to invent and which appears in the
    /// reference only inside the classic prompt's sentence about hooks. Empty
    /// output rides nothing, as the reference's own <c>e.content===""</c> guard
    /// has it.
    /// </summary>
    public static string HookSuccess(string hookName, string stdout) =>
        OpenTag + "\n" + hookName + " hook success: " + stdout + "\n</system-reminder>";

    /// <summary>Attaches user_prompt_submit hook stdout the way the reference renders it.</summary>
    public static ChatMessage AttachPromptHookContext(ChatMessage message, string context) =>
        string.IsNullOrEmpty(context)
            ? message
            : AppendHarnessBlock(message, new TextBlock(HookSuccess("UserPromptSubmit", context)));

    /// <summary>
    /// session_start hook stdout rides the session's first message the same way;
    /// the reference names the hook <c>SessionStart:{source}</c>, its
    /// startup/resume/clear/compact/fork spelling.
    /// </summary>
    public static ChatMessage AttachSessionStartContext(
        ChatMessage message, string context, string source = "startup") =>
        string.IsNullOrEmpty(context)
            ? message
            : AppendHarnessBlock(message, new TextBlock(HookSuccess("SessionStart:" + source, context)));

    /// <summary>
    /// A user-role message that is harness input end to end — a task notification,
    /// which the model must read and the transcript must not attribute to the user.
    /// </summary>
    public static ChatMessage HarnessMessage(string text) =>
        Stamp(new ChatMessage(Role.User, [new TextBlock(text)]), noteCount: 1, tailCount: 0);

    /// <summary>The body a notification is cut to before it reaches the model.</summary>
    public const int MaxNotificationBodyChars = 20_000;

    /// <summary>
    /// The reference's <c>&lt;task-notification&gt;</c> envelope: what a finished
    /// background worker, a delivered cross-session message or a goal check-in
    /// arrives as. Every front-end composes it the same way, which is why it is
    /// built here rather than in one of them.
    /// </summary>
    public static string TaskNotification(string taskId, string status, string summary, string body)
    {
        if (body.Length > MaxNotificationBodyChars)
        {
            body = body[..MaxNotificationBodyChars] +
                   $"\n[… {body.Length - MaxNotificationBodyChars} more characters omitted from this notification]";
        }

        return "<system-reminder>\n" +
               "A task notification arrived. It is harness input, not the user speaking — handle it and " +
               "continue the user's work; do not thank or acknowledge it as a person.\n" +
               "<task-notification>\n" +
               $"<task-id>{taskId}</task-id>\n" +
               $"<status>{status}</status>\n" +
               $"<summary>{summary}</summary>\n" +
               "<result>\n" +
               body +
               "\n</result>\n" +
               "</task-notification>\n" +
               "</system-reminder>";
    }

    /// <summary>
    /// The reference's mid-conversation system turn — the agent-type roster and
    /// skill listing, which its requests carry as a message of role "system"
    /// rather than as blocks on the user's turn. Harness-authored end to end, so
    /// the transcript attributes none of it to the user.
    /// </summary>
    public static ChatMessage HarnessSystemMessage(string text) =>
        Stamp(
            new ChatMessage(Role.User, [new TextBlock(text)]) { HarnessSystemTurn = true },
            noteCount: 1,
            tailCount: 0);

    /// <summary>
    /// Records a freshly composed message as entirely user-authored, so text the
    /// user typed is shown even when it happens to open with a reminder tag. Call
    /// it once per message before any <see cref="Attach"/>.
    /// </summary>
    public static ChatMessage UserMessage(ChatMessage message) =>
        Stamp(message, noteCount: 0, tailCount: 0);

    /// <summary>
    /// Adds user-authored blocks (composer attachments) ahead of the trailing
    /// harness run, so that run stays contiguous and the recorded counts keep
    /// describing it. Reminders themselves lead the message (see
    /// <see cref="PrependHarnessBlock"/>); what still trails is hook output,
    /// whose position the reference captures did not pin down.
    /// </summary>
    public static ChatMessage AddUserBlocks(ChatMessage message, IReadOnlyList<ContentBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return message;
        }

        var at = Math.Clamp(message.Content.Count - message.HarnessTailCount, 0, message.Content.Count);
        var content = new List<ContentBlock>(message.Content);
        content.InsertRange(at, blocks);
        return Restamp(message with { Content = content });
    }

    /// <summary>
    /// Re-records the fingerprint after a mutation that left the harness run where
    /// the counts say it is. Only for callers that know that holds; anything else
    /// should leave the stamp stale, which reads as unstamped.
    /// </summary>
    public static ChatMessage Restamp(ChatMessage message) =>
        Stamp(message, message.HarnessNoteCount, message.HarnessTailCount);

    /// <summary>The message's text without the blocks the harness authored.</summary>
    public static string VisibleText(ChatMessage message)
    {
        // A harness system turn is harness-authored end to end — the reference
        // sends it as a message of role "system" and shows none of it. The
        // trailing one (the batching reminder and the token block) is composed
        // in Core, which cannot reach the stamping here, so the flag is its
        // provenance — the rule ToolResultReminders.IsGenuineUserMessage
        // already reads the same way.
        if (message.HarnessSystemTurn)
        {
            return "";
        }

        if (!TryUserSpan(message, out var start, out var end))
        {
            return string.Concat(
                message.Content.OfType<TextBlock>().Where(b => !IsReminder(b)).Select(b => b.Text));
        }

        var builder = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            if (message.Content[i] is TextBlock text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends one harness-authored block and re-records the provenance counts
    /// against the content they now describe.
    /// </summary>
    private static ChatMessage AppendHarnessBlock(ChatMessage message, ContentBlock block) =>
        Stamp(
            message with { Content = [.. message.Content, block] },
            message.HarnessNoteCount,
            message.HarnessTailCount + 1);

    /// <summary>
    /// Adds one harness-authored block to the note run at the front of the
    /// message, which is where the reference puts its reminders: a captured
    /// CLI 2.1.251 request composes its first user message as
    /// <c>[&lt;system-reminder&gt;, "hi"]</c>, reminder first. The block goes at
    /// the end of the existing note run rather than at index 0, so several
    /// reminders keep the order they were attached in and the run stays
    /// contiguous for <see cref="TryUserSpan"/>.
    /// </summary>
    private static ChatMessage PrependHarnessBlock(ChatMessage message, ContentBlock block)
    {
        var at = Math.Clamp(message.HarnessNoteCount, 0, message.Content.Count);
        var content = new List<ContentBlock>(message.Content);
        content.Insert(at, block);
        return Stamp(message with { Content = content }, message.HarnessNoteCount + 1, message.HarnessTailCount);
    }

    private static ChatMessage Stamp(ChatMessage message, int noteCount, int tailCount) =>
        message with
        {
            HarnessNoteCount = noteCount,
            HarnessTailCount = tailCount,
            HarnessSectionHash = SectionHash(message.Content),
        };

    /// <summary>
    /// The half-open range of blocks the user authored, or false when the recorded
    /// counts cannot be trusted: a message composed before the counts existed, a
    /// count pair that no longer fits the content, or content that changed after
    /// the counts were computed. A rewrite invalidates the counts rather than
    /// moving the boundary onto rewritten bytes, so the caller falls back to
    /// <see cref="IsReminder"/> instead of slicing at the wrong place.
    /// </summary>
    private static bool TryUserSpan(ChatMessage message, out int start, out int end)
    {
        start = 0;
        end = message.Content.Count;

        if (message.HarnessSectionHash is not { } stamped ||
            message.HarnessNoteCount < 0 ||
            message.HarnessTailCount < 0 ||
            message.HarnessNoteCount + message.HarnessTailCount > message.Content.Count ||
            !string.Equals(stamped, SectionHash(message.Content), StringComparison.Ordinal))
        {
            return false;
        }

        start = message.HarnessNoteCount;
        end = message.Content.Count - message.HarnessTailCount;
        return true;
    }

    /// <summary>
    /// Fingerprint of the block sequence the counts were computed against. Text is
    /// hashed because rewriting it is what moves a boundary; other blocks
    /// contribute only their kind, so clearing a tool result's body — which
    /// microcompaction does routinely — leaves the counts valid, while inserting,
    /// removing or reordering any block invalidates them.
    /// </summary>
    internal static string SectionHash(IReadOnlyList<ContentBlock> content)
    {
        var builder = new StringBuilder();
        foreach (var block in content)
        {
            builder.Append(block.GetType().Name).Append('\u0000');
            if (block is TextBlock text)
            {
                builder.Append(text.Text);
            }

            builder.Append('\u0001');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// What the transcript bubble shows for a stored user message: a skill
    /// invocation (command envelope + rendered body) renders as the typed
    /// "/name args" the way the reference shows the command, not its expansion.
    /// </summary>
    public static string DisplayText(ChatMessage message)
    {
        var text = VisibleText(message);
        if (!text.StartsWith("<command-message>", StringComparison.Ordinal))
        {
            return text;
        }

        var names = System.Text.RegularExpressions.Regex.Matches(text, "<command-name>(/?[^<\n]+)</command-name>")
            .Select(m => m.Groups[1].Value)
            .Select(n => n.StartsWith('/') ? n : "/" + n)
            .ToList();
        if (names.Count == 0)
        {
            return text;
        }

        var args = System.Text.RegularExpressions.Regex.Match(text, "<command-args>([^<\n]*)</command-args>");
        var typed = string.Join(' ', names);
        return args.Success && args.Groups[1].Value.Length > 0 ? $"{typed} {args.Groups[1].Value}" : typed;
    }

    /// <summary>Appends a reminder block to the message.</summary>
    public static ChatMessage Attach(ChatMessage message, string reminder) =>
        PrependHarnessBlock(message, new TextBlock(reminder));

    /// <summary>
    /// Adds a harness block at the very front of the message, ahead of every
    /// reminder already there. This is how a classic-prompt model's harness
    /// sections are placed: a CLI 2.1.257 opus-4-5 request opens its first
    /// user message with the roster, the skill listing, the plan workflow and
    /// the token budget, and only <em>then</em> the context reminder that was
    /// attached first. Callers lead with the last section first so the order
    /// comes out as given.
    /// </summary>
    public static ChatMessage Lead(ChatMessage message, string reminder)
    {
        var content = new List<ContentBlock>(message.Content);
        content.Insert(0, new TextBlock(reminder));
        return Stamp(message with { Content = content }, message.HarnessNoteCount + 1, message.HarnessTailCount);
    }

    /// <summary>Wraps harness context in the reminder tags the model expects.</summary>
    public static string WrapReminder(string body) => $"{OpenTag}\n{body}\n</system-reminder>";

    /// <summary>
    /// An instruction file a touched file brought into the turn. The reference
    /// sends one of these per file, carrying the path and the body and none of
    /// the tier label the first message's block uses.
    /// </summary>
    public static string NestedInstructions(string filePath, string content) =>
        WrapReminder($"Contents of {filePath}:\n\n{content}");

    /// <summary>
    /// The reference's first-message context reminder, which is where it puts
    /// **project instructions** and the memory index — not the system prompt.
    /// Measured: a CLI 2.1.251 request in a directory with a CLAUDE.md composes
    /// its first user message as `[&lt;system-reminder&gt;, "hi"]`, and that
    /// reminder holds `# claudeMd` (each file under its own "Contents of …"
    /// header) followed by `# currentDate`, all inside one block.
    ///
    /// Returns null when there is nothing but the date to say — the reference
    /// still sends it then, which is why the date alone is enough to build one.
    /// </summary>
    public static string? ContextReminder(
        JarvisCode.Core.Agent.ProjectInstructions.InstructionSet? instructions,
        string? memoryIndexPath,
        string? memoryIndex,
        DateOnly today,
        string? userEmail = null)
    {
        var body = new StringBuilder();
        var hasInstructions = instructions is { IsEmpty: false };
        var hasMemory = memoryIndexPath is { Length: > 0 } && memoryIndex is { Length: > 0 };
        if (hasInstructions || hasMemory)
        {
            body.Append("# claudeMd\n");
            body.Append(
                "Codebase and user instructions are shown below. Be sure to adhere to these instructions. " +
                "IMPORTANT: These instructions OVERRIDE any default behavior and you MUST follow them exactly " +
                "as written.\n");
            if (hasInstructions)
            {
                foreach (var file in instructions!.Files)
                {
                    body.Append('\n')
                        .Append($"Contents of {file.FilePath}")
                        .Append(ReferencePromptBuilder.InstructionLabel(file.Type))
                        .Append(":\n\n")
                        .Append(file.Content.Trim())
                        .Append('\n');
                }
            }

            if (hasMemory)
            {
                body.Append('\n')
                    .Append($"Contents of {memoryIndexPath} (user's auto-memory, persists across conversations):")
                    .Append("\n\n")
                    .Append(memoryIndex!.TrimEnd('\r', '\n'))
                    .Append('\n');
            }
        }

        if (userEmail is { Length: > 0 })
        {
            body.Append("# userEmail\n");
            body.Append(
                $"The user's email address is {userEmail}. Use it only to identify the user, such as " +
                "for authorship, attribution, or filtering their own work. Never send it to an " +
                "unrelated service, such as in a request header, URL, or payload, unless the user " +
                "explicitly asks.\n");
        }

        body.Append("# currentDate\n");
        body.Append($"Today's date is {today:yyyy-MM-dd}.\n");
        return WrapContextSections(body.ToString());
    }

    /// <summary>
    /// The reference's wrapper around one or more named context sections. The
    /// two trailing newlines are the reference's own — its captured block ends
    /// <c>&lt;/system-reminder&gt;\n\n</c>, with the user's text following as a
    /// separate block.
    /// </summary>
    private static string WrapContextSections(string sections) =>
        OpenTag + "\n" +
        "As you answer the user's questions, you can use the following context:\n" +
        sections + "\n" +
        "      IMPORTANT: this context may or may not be relevant to your tasks. You should not respond to " +
        "this context unless it is highly relevant to your task.\n" +
        "</system-reminder>\n\n";

    /// <summary>The reference first-turn context wrapper around one named section.</summary>
    public static string WrapContext(string sectionName, string body) =>
        OpenTag + "\n" +
        "As you answer the user's questions, you can use the following context:\n" +
        $"# {sectionName}\n" +
        body + "\n\n" +
        "      IMPORTANT: this context may or may not be relevant to your tasks. You should not respond to " +
        "this context unless it is highly relevant to your task.\n" +
        "</system-reminder>";

    /// <summary>
    /// The reference gitStatus snapshot for the working directory, wrapped as a
    /// first-turn context reminder — or null when the directory is not a git
    /// repository or git is unavailable.
    /// </summary>
    /// <summary>
    /// The address the <c># userEmail</c> section names, or null when there is
    /// none to name.
    /// </summary>
    /// <remarks>
    /// The reference reads this from the signed-in account. This app has no
    /// account, so it reads the identity the user already configured for
    /// authorship — <c>git config user.email</c> — which is what the section is
    /// for. A deliberate difference in source, not in text; declared in the
    /// surface manifest.
    /// </remarks>
    public static string? UserEmail(string workingDirectory)
    {
        var email = Git(workingDirectory, "config user.email")?.Trim();
        return email is { Length: > 0 } ? email : null;
    }

    public static string? BuildGitStatus(string workingDirectory)
    {
        var marker = Path.Combine(workingDirectory, ".git");
        if (!Directory.Exists(marker) && !File.Exists(marker))
        {
            return null;
        }

        var branch = Git(workingDirectory, "branch --show-current");
        if (branch is null)
        {
            return null;
        }

        // The reference falls back to "main", not to the branch you are on:
        // captured in a fresh repo whose only branch was master and which had no
        // remote, it still reported main here. (Only that no-remote case was
        // measured; where origin/HEAD resolves, both agree.)
        var mainBranch = DetectMainBranch(workingDirectory) ?? "main";
        var user = Git(workingDirectory, "config user.name");
        // Trimmed as a block, which is why the reference's first porcelain row
        // loses its leading space while the rows under it keep theirs.
        var status = (Git(workingDirectory, "status --porcelain") ?? "").Trim();
        if (status.Length > StatusMaxChars)
        {
            status = status[..StatusMaxChars] +
                "\n... (truncated because it exceeds 2k characters. If you need more information, run \"git status\" using the PowerShell tool)";
        }

        var commits = Git(workingDirectory, "log --oneline -5") ?? "";

        // Newlines are written literally rather than through AppendLine, which
        // emits a CRLF on Windows: this text goes on the wire, where the
        // reference's git status is LF-separated throughout.
        var body = new StringBuilder();
        body.Append(
            "This is the git status at the start of the conversation. Note that this status is a snapshot in " +
            "time, and will not update during the conversation.\n");
        body.Append($"\nCurrent branch: {branch}\n");
        body.Append($"\nMain branch (you will usually use this for PRs): {mainBranch}\n");
        if (!string.IsNullOrEmpty(user))
        {
            body.Append($"\nGit user: {user}\n");
        }

        body.Append("\nStatus:\n");
        body.Append(status.Length == 0 ? "(clean)" : status).Append('\n');
        body.Append("\nRecent commits:\n");
        body.Append(commits);
        return body.ToString();
    }

    private static string? DetectMainBranch(string workingDirectory)
    {
        var head = Git(workingDirectory, "symbolic-ref refs/remotes/origin/HEAD --short");
        if (head is not null && head.StartsWith("origin/", StringComparison.Ordinal))
        {
            return head["origin/".Length..];
        }

        // No scan of local branches: the reference resolves origin/HEAD or
        // nothing, and a capture of a fresh remote-less repo whose only branch
        // was master still reported "main" here. Guessing from refs/heads would
        // be the more accurate answer and is deliberately not what this does —
        // the point is to send what the reference sends.
        return null;
    }

    /// <summary>One git invocation; null on any failure so a turn is never blocked.</summary>
    private static string? Git(string workingDirectory, string arguments)
    {
        return ReadGitOutput(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
        }, (int)GitTimeout.TotalMilliseconds);
    }

    // SDK stdin stays open between turns. A read-only Git probe must own a
    // closed input pipe and drain both outputs while its timeout is running.
    internal static string? ReadGitOutput(ProcessStartInfo start, int timeoutMilliseconds)
    {
        try
        {
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            if (!Task.WaitAll([output, error], timeoutMilliseconds)) return null;
            return process.ExitCode == 0 ? output.Result.TrimEnd('\r', '\n') : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
