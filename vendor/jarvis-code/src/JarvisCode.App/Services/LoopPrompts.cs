using System.IO;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference CLI's /loop dynamic-workflow machinery: sentinels, the loop-tasks
/// file, interval parsing, fire-time prompt resolution, and every model-facing text,
/// ported from the installed 2.1.247/2.1.251 bundles. Deltas are deliberate and
/// mechanical only: our tool names (ScheduleWakeup/Agent/TaskStop/ListAgents
/// for ScheduleWakeup/Monitor/TaskStop/TaskList), fixed-interval loops on the
/// session's own timer instead of CronCreate crons, and the reference's
/// push-notification and cloud-schedule suffixes (ka/Ca/_a/Nae) omitted because
/// both features are deliberately absent from this app.
/// </summary>
public static class LoopPrompts
{
    // ---- sentinels (reference lEe / XY / fsn / Fae) ----

    public const string AutonomousSentinel = "<<autonomous-loop>>";
    public const string AutonomousDynamicSentinel = "<<autonomous-loop-dynamic>>";
    public const string LoopFileSentinel = "<<loop.md>>";
    public const string LoopFileDynamicSentinel = "<<loop.md-dynamic>>";

    // ---- runtime constants (reference YRe / Uae / tAe / abo / lbo) ----

    public const int LoopFileMaxChars = 25000;
    public const int MinDelaySeconds = 60;
    public const int MaxDelaySeconds = 3600;
    public const int KeepaliveDelaySeconds = 1200;
    public const int KeepaliveBudget = 1;

    public static bool IsAutonomousSentinel(string prompt) =>
        prompt is AutonomousSentinel or AutonomousDynamicSentinel;

    public static bool IsLoopFileSentinel(string prompt) =>
        prompt is LoopFileSentinel or LoopFileDynamicSentinel;

    public static bool IsSentinel(string prompt) =>
        IsAutonomousSentinel(prompt) || IsLoopFileSentinel(prompt);

    /// <summary>Keepalive is the reference's env/gate opt-in, default off.</summary>
    public static bool KeepaliveEnabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_LOOP_KEEPALIVE"));

    // ---- /loop input parsing (reference da / ua, rules 1-3) ----

    public sealed record ParsedLoop(TimeSpan? Interval, string Prompt, string? IntervalText);

    private static readonly System.Text.RegularExpressions.Regex LeadingToken =
        new(@"^(\d+)([smhd])$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex EveryClause = new(
        @"^every\s+(\d+)\s*(s|sec|secs|second|seconds|m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Rule 1: leading `^\d+[smhd]$` token. Rule 2: trailing "every N unit" clause
    /// (`check every PR` has no interval). Rule 3: the whole input is the prompt and
    /// the loop self-paces. An empty prompt is the autonomous default, not an error.
    /// </summary>
    public static ParsedLoop Parse(string input)
    {
        input = input.Trim();
        if (input.Length == 0)
        {
            return new ParsedLoop(null, "", null);
        }

        int firstBreak = input.IndexOfAny([' ', '\t', '\n']);
        var firstToken = firstBreak < 0 ? input : input[..firstBreak];
        if (LeadingToken.Match(firstToken) is { Success: true } lead)
        {
            var rest = firstBreak < 0 ? "" : input[firstBreak..].Trim();
            return new ParsedLoop(
                ToInterval(int.Parse(lead.Groups[1].Value), lead.Groups[2].Value),
                rest,
                Normalize(lead.Groups[1].Value, lead.Groups[2].Value));
        }

        // Trailing "every" clause: test the tail from the last "every" word.
        int at = input.LastIndexOf("every", StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            bool wordStart = at == 0 || char.IsWhiteSpace(input[at - 1]);
            if (wordStart && EveryClause.Match(input[at..]) is { Success: true } every)
            {
                return new ParsedLoop(
                    ToInterval(int.Parse(every.Groups[1].Value), every.Groups[2].Value),
                    input[..at].Trim(),
                    Normalize(every.Groups[1].Value, every.Groups[2].Value));
            }

            at = at == 0 ? -1 : input.LastIndexOf("every", at - 1, StringComparison.OrdinalIgnoreCase);
        }

        return new ParsedLoop(null, input, null);
    }

    /// <summary>Minimum granularity is 1 minute; seconds round up (reference `Ns` → ceil).</summary>
    private static TimeSpan ToInterval(int amount, string unit) => char.ToLowerInvariant(unit[0]) switch
    {
        's' => TimeSpan.FromMinutes(Math.Max(1, (int)Math.Ceiling(amount / 60.0))),
        'h' => TimeSpan.FromHours(amount),
        'd' => TimeSpan.FromDays(amount),
        _ => TimeSpan.FromMinutes(Math.Max(1, amount)),
    };

    /// <summary>The reference Zu(): `every 5 minutes` reads back as `5m`.</summary>
    private static string Normalize(string amount, string unit) => char.ToLowerInvariant(unit[0]) switch
    {
        's' => $"{amount}s",
        'h' => $"{amount}h",
        'd' => $"{amount}d",
        _ => $"{amount}m",
    };

    // ---- the loop-tasks file (reference ast / msn) ----

    public sealed record LoopFile(string Path, string Content);

    /// <summary>`.claude/loop.md` first, then `loop.md`, both under the session cwd; blank reads as absent.</summary>
    public static LoopFile? ReadLoopFile(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory))
        {
            return null;
        }

        foreach (var path in new[]
                 {
                     Path.Combine(workingDirectory, ".claude", "loop.md"),
                     Path.Combine(workingDirectory, "loop.md"),
                 })
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var trimmed = text.Trim();
            if (trimmed.Length > 0)
            {
                return new LoopFile(path, TruncateLoopFile(trimmed));
            }
        }

        return null;
    }

    /// <summary>Reference msn(): cut at the last newline under the cap, warn inline.</summary>
    public static string TruncateLoopFile(string content)
    {
        if (content.Length <= LoopFileMaxChars)
        {
            return content;
        }

        int cut = content.LastIndexOf('\n', LoopFileMaxChars);
        return content[..(cut > 0 ? cut : LoopFileMaxChars)] +
               $"\n\n> WARNING: loop.md was truncated to {LoopFileMaxChars} bytes. Keep the task list concise.";
    }

    // ---- delay clamping (reference cbo, minus the cron minute alignment) ----

    public static (int Clamped, bool WasClamped) ClampDelay(double requested)
    {
        int rounded = double.IsNaN(requested) ? MinDelaySeconds
            : requested >= MaxDelaySeconds ? MaxDelaySeconds
            : requested <= MinDelaySeconds ? MinDelaySeconds
            : (int)Math.Round(requested);
        bool wasClamped = !double.IsFinite(requested) || Math.Round(requested) != rounded;
        return (rounded, wasClamped);
    }

    // ---- ScheduleWakeup validation + result texts (reference call() / mapToolResult...) ----

    public const string ErrDelayReason = "`delaySeconds` and `reason` are required when `stop` is not true.";
    public const string ErrPrompt = "`prompt` is required when `stop` is not true.";
    public const string ErrNoop = "`noop` is required when `stop` is not true.";

    private const string StopSuffix =
        "If you armed a background worker for this loop, TaskStop it now; otherwise nothing more to do this turn.";

    public static string StopResult(int cancelledWakeups) => cancelledWakeups == 0
        ? "Loop stopped — any dynamic loop in this session is ended; there was no pending wakeup to cancel. " +
          "If you are running a fixed-interval /loop, it is NOT stopped by this call — the user cancels it " +
          "with /loops stop. " + StopSuffix
        : $"Loop stopped — cancelled {cancelledWakeups} pending wakeup(s); no further dynamic-loop wakeups " +
          "scheduled. " + StopSuffix;

    public static string ScheduledResult(DateTimeOffset firesAt, int clampedSeconds, bool wasClamped)
    {
        var clampNote = wasClamped ? $" (clamped to {clampedSeconds}s from your requested value)" : "";
        return $"Next wakeup scheduled for {firesAt.ToLocalTime():HH:mm:ss} (in {clampedSeconds}s){clampNote}. " +
               "Nothing more to do this turn — the harness re-invokes you when the wakeup fires or a " +
               "task-notification arrives.";
    }

    // ---- the tool description (reference pco + S3t, unknown-TTL variant) ----

    public const string ToolDescription =
        "Schedule when to resume work in /loop dynamic mode — the user invoked /loop without an interval, " +
        "asking you to self-pace iterations of a specific task.\n" +
        "\n" +
        "Do NOT schedule a short-interval wakeup to poll for background work you started — when " +
        "harness-tracked work finishes, you are re-invoked automatically, so polling is wasted. Instead " +
        "schedule a long fallback (1200s+) so the loop survives if the work hangs or never notifies. The " +
        "exception is external work the harness cannot track (a CI run, a deploy, a remote queue) — there, " +
        "pick a delay matched to how fast that state actually changes.\n" +
        "\n" +
        "Pass the same /loop prompt back via `prompt` each turn so the next firing repeats the task. For an " +
        "autonomous /loop (no user prompt), pass the literal sentinel `" + AutonomousDynamicSentinel + "` as " +
        "`prompt` instead — the runtime resolves it back to the autonomous-loop instructions at fire time. " +
        "(There is a similar `" + AutonomousSentinel + "` sentinel for interval-based autonomous loops; do " +
        "not confuse the two — ScheduleWakeup always uses the `-dynamic` variant.) To end the loop, call " +
        "this tool with `stop: true` (omit every other field) — the loop ends immediately and no further " +
        "wakeups fire.\n" +
        "\n" +
        "Set `noop: true` if nothing changed — you checked and there's nothing to report (\"no change\", " +
        "\"still waiting\", \"quiet hold\"). Set `noop: false` if something happened worth keeping — you " +
        "edited a file, posted a message, advanced state, or surfaced a finding. Consecutive `noop: true` " +
        "ticks are collapsed in the user's transcript view and tracked as a streak, so long quiet holds stay " +
        "legible to the user without scrolling. Omit `noop` when stopping (`stop: true`).\n" +
        "\n" +
        "## Picking delaySeconds\n" +
        "\n" +
        "The Anthropic prompt cache decides how expensive a wake-up is: waking inside the cache TTL re-reads " +
        "your conversation context cached (fast, cheap); waking past it re-reads everything uncached. The TTL " +
        "depends on how the session is billed: Claude subscriber sessions get a 1-hour TTL (dropping to 5 " +
        "minutes during usage overage), while API-key, Bedrock, and Vertex sessions default to 5 minutes.\n" +
        "\n" +
        "In either regime: never schedule extra wakeups just to keep the cache warm — they cost more than the " +
        "cache miss they avoid. Match the delay to what you're actually waiting for: when actively polling " +
        "external state the harness can't notify you about (a CI run, a deploy, a remote queue), pick the " +
        "delay from how fast that state actually changes; for idle ticks with no specific signal to watch, " +
        "default to **1200s–1800s** (20–30 min) — the user can always interrupt if they need you sooner.\n" +
        "\n" +
        "On a 5-minute TTL only, two refinements: under 300s (60s–270s) the cache stays warm, so prefer 270s " +
        "over 300s when actively polling (300s is the worst-of-both — you pay the miss without amortizing " +
        "it); and commit to 1200s+ rather than repeated ~300s waits, so one cache miss buys a long wait.\n" +
        "\n" +
        "The runtime clamps to [60, 3600], so you don't need to clamp yourself.\n" +
        "\n" +
        "## The reason field\n" +
        "\n" +
        "One short sentence on what you chose and why. It is shown back to the user. \"watching CI run\" " +
        "beats \"waiting.\" The user reads this to understand what you're doing without having to predict " +
        "your cadence in advance — make it specific.";

    public const string DescDelaySeconds =
        "Seconds from now to wake up. Clamped to [60, 3600] by the runtime. Required unless `stop` is true.";

    public const string DescReason =
        "One short sentence explaining the chosen delay. It is shown to the user. Be specific. Required " +
        "unless `stop` is true.";

    public const string DescPrompt =
        "The /loop input to fire on wake-up. Pass the same /loop input verbatim each turn so the next firing " +
        "re-enters loop mode and continues the loop. For autonomous /loop (no user prompt), pass the literal " +
        "sentinel `" + AutonomousDynamicSentinel + "` instead (the dynamic-pacing variant, not the " +
        "interval-mode `" + AutonomousSentinel + "`). Required unless `stop` is true.";

    public const string DescStop =
        "Set to true to end the dynamic loop immediately instead of scheduling another wakeup. When true, " +
        "all other fields are ignored and no further wakeups fire.";

    public const string DescNoop =
        "true = nothing changed (you checked and there is nothing to report). false = something happened " +
        "worth keeping (edited a file, posted a message, advanced state, surfaced a finding). Consecutive " +
        "noop:true ticks are collapsed in the user's transcript view and tracked as a streak. Required " +
        "unless `stop` is true.";

    // ---- /loop usage (reference oh) ----

    public const string Usage =
        "Usage: /loop [interval] <prompt>\n" +
        "\n" +
        "Run a prompt or slash command on a recurring interval — or with no interval, let the model " +
        "self-pace based on the task.\n" +
        "\n" +
        "Intervals: Ns, Nm, Nh, Nd (e.g. 5m, 30m, 2h, 1d). Minimum granularity is 1 minute.\n" +
        "If no interval is specified, the model picks a delay between iterations based on what it's doing.\n" +
        "\n" +
        "Examples:\n" +
        "  /loop 5m /babysit-prs\n" +
        "  /loop 30m check the deploy\n" +
        "  /loop 1h /standup 1\n" +
        "  /loop check the deploy          (dynamic — model picks delays)\n" +
        "  /loop check the deploy every 20m";

    // ---- the dynamic-mode instruction block (reference ih's `t`) ----

    /// <summary>
    /// The reference's Monitor-tool protocol maps onto this app's wake sources: a
    /// background worker (`Agent` with `run_in_background: true`) or
    /// `subscribe_pr_activity`, whose reports arrive as task-notifications.
    /// </summary>
    public const string DynamicModeInstructions =
        "The user wants you to self-pace. Decide what makes the next iteration worth running — a passage of " +
        "time, or an observable event.\n" +
        "\n" +
        "1. **Run the task in ## Input now.** If it's a slash command, invoke it via the skill tool; " +
        "otherwise act on it directly.\n" +
        "2. **If the next run is gated on an event** (CI finishing, a log line matching, a file changing, a " +
        "PR comment) and no background watcher is already running for it: arm one now (`Agent` with " +
        "`run_in_background: true`, or `subscribe_pr_activity` for PR events). Its report arrives as a " +
        "`<task-notification>` message and wakes this loop immediately — you do not wait for the " +
        "ScheduleWakeup deadline. Arm once; on later iterations call ListAgents first and skip this step " +
        "if a watcher is already running.\n" +
        "3. **Briefly confirm**: that you're self-pacing, whether a watcher is the primary wake signal, that " +
        "you ran the task now, and what fallback delay you're about to pick. Write this as text *before* " +
        "calling ScheduleWakeup — the turn ends as soon as that tool returns.\n" +
        "4. **Then, as the last action of this turn, decide whether the loop continues.** If the task needs " +
        "another iteration, call ScheduleWakeup with:\n" +
        "   - `delaySeconds`: with a watcher armed this is the **fallback heartbeat** — how long to wait if " +
        "no event fires (lean 1200–1800s; idle ticks more frequent than the task needs are pure overhead). " +
        "Without a watcher this is the cadence — pick based on what you observed. Read the tool's own " +
        "description for cache-aware delay guidance.\n" +
        "   - `reason`: one short sentence on why you picked that delay.\n" +
        "   - `prompt`: the full original /loop input verbatim, prefixed with `/loop ` so the next firing " +
        "re-enters loop mode and continues the loop. For example, if the user typed `/loop check the " +
        "deploy`, pass `/loop check the deploy` as the prompt.\n" +
        "   - `noop`: `true` if this tick changed nothing (\"still waiting\", \"quiet hold\"); `false` if it " +
        "did something worth keeping. Consecutive `noop: true` ticks collapse in the transcript.\n" +
        "   If it doesn't need another iteration, stop instead (step 6) — re-arming is a per-turn choice, " +
        "not a default.\n" +
        "5. **If you were woken by a `<task-notification>`** rather than this prompt: handle the event in " +
        "the context of the loop task, then make the same decision. If the loop should continue, call " +
        "ScheduleWakeup again with the same `prompt` and the same 1200–1800s `delaySeconds` from step 4 " +
        "(the watcher remains the wake signal; the new wakeup is only the fallback heartbeat). If the event " +
        "means the work is finished, stop (step 6).\n" +
        "6. **To stop the loop** — the task is complete, further iterations can't make progress, or the user " +
        "asked you to stop — call ScheduleWakeup with `stop: true` (no other fields) and TaskStop any " +
        "watcher you armed (use ListAgents to find its ID if it is no longer in context). Stopping is the " +
        "loop's normal ending — the user can restart it anytime with /loop.";

    /// <summary>The message a dynamic `/loop <prompt>` submits (and every re-entered tick).</summary>
    public static string BuildDynamicExpansion(string prompt) =>
        "# /loop — self-paced prompt (dynamic mode)\n" +
        "\n" +
        DynamicModeInstructions +
        "\n\n## Input\n\n" +
        prompt;

    // ---- the autonomous default (reference ha / Xot) ----

    /// <summary>The reference's autonomous-loop preamble (Xot), verbatim.</summary>
    public const string AutonomousLoopPreamble =
        "# Autonomous loop check\n" +
        "\n" +
        "You're being invoked on a timer while the user is away or occupied. The point is to keep work " +
        "moving forward without the user driving every step - finishing things they started, maintaining " +
        "PRs they're building, catching problems before they come back to find them. You're a steward, not " +
        "an initiator. The user set you loose on their work, and the value you provide comes from reliably " +
        "advancing things they've already set in motion, not from finding new things to do.\n" +
        "\n" +
        "The key tension to navigate: the user trusts you enough to run autonomously, but that trust is " +
        "easily lost. Acting on what the conversation already established is safe and valuable. Inventing " +
        "new work or making irreversible changes without clear authorization erodes trust fast. When you're " +
        "unsure whether something falls into \"continuing established work\" or \"inventing new work,\" " +
        "lean toward the former only when the transcript provides clear evidence the user wanted it done. " +
        "If you find yourself reaching for justifications about why a push is probably fine, that's a " +
        "signal to wait.\n" +
        "\n" +
        "## What to act on\n" +
        "\n" +
        "The current conversation is your highest-signal source - re-read the transcript above, since " +
        "everything there is something the user was actively engaged with. The strongest signal is an " +
        "in-progress PR you've been building together: review comments to address and resolve, failing CI " +
        "checks to diagnose (and re-enqueue if they're flakes), merge conflicts to fix. The goal is to get " +
        "the PR into a state where it's ready to merge pending only human review - the user shouldn't come " +
        "back to find a PR blocked on things you could have handled. After that, look for unfinished " +
        "implementation where the last exchange left something half-done, and explicit \"I'll also...\" or " +
        "\"next I'll...\" commitments the conversation made and didn't honor. Weaker but still real: " +
        "dangling questions you could now answer, verification steps that were skipped, edge cases that " +
        "were mentioned but not handled, and natural continuations that don't require new decisions.\n" +
        "\n" +
        "If you find anything in this category, act on it - actually do the work, don't describe what could " +
        "be done. Run the tests, don't say \"you could run the tests.\" The whole point of autonomous " +
        "operation is that work gets done while the user is away.\n" +
        "\n" +
        "When the conversation transcript has nothing left, the current branch's pull/merge request on the " +
        "user's SCM is the next-best place to look. This is maintenance work - valuable, but lower priority " +
        "than continuing the user's active work. Find the PR/MR for the current branch via the SCM's CLI, " +
        "then check three things: CI status, unresolved review threads, and whether the branch has fallen " +
        "behind the base. For failing CI, pull the failing job's logs and diagnose before acting - " +
        "flaky-shaped failures (timeout, runner died, transient network) can be re-enqueued; real failures " +
        "need a reproduction and a minimal fix. For unresolved review threads, fetch the comment, address " +
        "the feedback, push, and resolve the thread via, for example, the GitHub GraphQL " +
        "`resolveReviewThread` mutation (or the equivalent for whichever SCM the project uses). Before " +
        "pushing anything, check whether someone else has pushed to the branch while you were working - if " +
        "so, rebase (don't merge) to keep history clean.\n" +
        "\n" +
        "When CI is green, threads are clear, and there's idle time, sweeping the branch for issues is a " +
        "good use of that time - bug-hunt or simplification passes catch problems before reviewers do, " +
        "saving everyone a round-trip.\n" +
        "\n" +
        "If everything is genuinely quiet - no conversation work, no PR maintenance - say so in one " +
        "sentence and stop. No summary of what you checked, no list of what you might do later. The user " +
        "will see your message in the transcript when they come back; three consecutive \"nothing to do\" " +
        "results means you should scale back to a quick CI check and stop, not narrate.\n" +
        "\n" +
        "## Repeated invocations\n" +
        "\n" +
        "If you see earlier autonomous checks in this conversation, adjust your scope accordingly. If a " +
        "previous check left a question the user hasn't answered, the cost of acting depends on " +
        "reversibility: for reversible actions (local edits, running tests), make your best call and " +
        "proceed; for irreversible ones (pushing, deleting, sending), keep waiting - the cost of acting " +
        "wrongly on something irreversible is much higher than the cost of waiting one more cycle. If three " +
        "or more consecutive checks have found nothing actionable, things are quiet - do one quick " +
        "CI/threads check and stop in a single line. Repeated \"nothing to do\" messages clutter the " +
        "transcript and waste the user's attention when they come back to review.\n" +
        "\n" +
        "Read and analyze freely - understanding the state of things has no blast radius. Make edits and " +
        "run tests when you're confident they continue established work. Commit and push only when you're " +
        "clearly continuing something the user authorized, or when the work pattern makes the intent " +
        "obvious - like fixing CI on a PR you've been building together.";

    private static string AutonomousStepList(LoopFile? file, string sentinel)
    {
        var task = file is null ? "the autonomous check" : "the loop.md tasks";
        var confirm = file is null
            ? "that this is the autonomous default in dynamic-pacing mode, that you ran the check now"
            : $"that you're running tasks from `{file.Path}` in dynamic-pacing mode, that you ran the first tick now";
        return
            $"1. **Run {task} now**, following the instructions inlined below.\n" +
            "2. **If the next tick is gated on an event** (CI finishing, a PR comment, a log line) and no " +
            "background watcher is already running for it: arm one now (`Agent` with " +
            "`run_in_background: true`, or `subscribe_pr_activity` for PR events). Its report wakes this " +
            "loop immediately — you do not wait for the ScheduleWakeup deadline. Arm once; on later ticks " +
            "call ListAgents first and skip if a watcher is already running.\n" +
            $"3. **Briefly confirm**: {confirm}, whether a watcher is the primary wake signal, and what " +
            "fallback delay you're about to pick. Write this as text *before* calling ScheduleWakeup — " +
            "the turn ends as soon as that tool returns.\n" +
            "4. **Then, as the last action of this turn, decide whether the loop continues.** If the next " +
            "check is worth running, call ScheduleWakeup with:\n" +
            "   - `delaySeconds`: with a watcher armed this is the fallback heartbeat (lean 1200–1800s). " +
            "Without one, pick based on what you observed this turn — quiet branch? wait longer. Lots in " +
            "flight? wait shorter. Read the tool's own description for cache-aware delay guidance.\n" +
            "   - `reason`: one short sentence on why you picked that delay.\n" +
            $"   - `prompt`: the literal string `{sentinel}` — the dynamic-mode sentinel expands at fire " +
            "time to the full instructions (first fire / first fire post-compact / loop.md edited) or a " +
            "dynamic-pacing-specific short reminder (subsequent fires). Do not pass the full instructions; " +
            "that is handled automatically.\n" +
            "   - `noop`: `true` if this tick changed nothing (\"still waiting\", \"quiet hold\"); `false` " +
            "if it did something worth keeping. Consecutive `noop: true` ticks collapse in the transcript.\n" +
            "   If it isn't, stop instead (step 6) — re-arming is a per-turn choice, not a default.\n" +
            "5. **If woken by a `<task-notification>`** rather than this prompt: handle the event, then " +
            $"make the same decision. If the loop should continue, call ScheduleWakeup again with `{sentinel}` " +
            "and the same 1200–1800s `delaySeconds` (the watcher remains the wake signal; the new wakeup is " +
            "only the fallback heartbeat). If the event means the work is finished, stop (step 6).\n" +
            "6. **To stop the loop** — the task is complete, further iterations can't make progress, or the " +
            "user asked you to stop — call ScheduleWakeup with `stop: true` (no other fields) and " +
            "TaskStop any watcher you armed (use ListAgents to find its ID if it is no longer in " +
            "context). Stopping is the loop's normal ending — the user can restart it anytime with /loop.";
    }

    private static string AutonomousBody(LoopFile? file) =>
        (file is null
            ? "## Autonomous-loop instructions (for the immediate execution and every fire)"
            : $"## Loop tasks (from {file.Path})") +
        "\n\n" +
        (file?.Content ?? AutonomousLoopPreamble);

    /// <summary>Bare `/loop`: the autonomous default with dynamic pacing (reference ha, dynamic branch).</summary>
    public static string BuildAutonomousDynamicExpansion(LoopFile? file)
    {
        var sentinel = file is null ? AutonomousDynamicSentinel : LoopFileDynamicSentinel;
        var header = file is null
            ? "# /loop — autonomous default with dynamic pacing\n\nThe user invoked `/loop` with no prompt " +
              "and no interval. Run the autonomous check now, then self-pace the next iteration via " +
              "ScheduleWakeup — no fixed interval."
            : "# /loop — loop.md tasks with dynamic pacing\n\nThe user invoked `/loop` with no prompt and " +
              $"no interval and has a loop-tasks file at `{file.Path}`. Run those tasks now, then self-pace " +
              "the next iteration via ScheduleWakeup — no fixed interval.";
        return $"{header}\n\n## Action\n\n{AutonomousStepList(file, sentinel)}\n\n{AutonomousBody(file)}";
    }

    /// <summary>`/loop 5m` with no prompt: the autonomous default on a recurring tick (reference ha, cron branch).</summary>
    public static string BuildAutonomousFixedExpansion(LoopFile? file, string intervalText)
    {
        var header = file is null
            ? "# /loop — the autonomous default on a recurring interval\n\nThe user invoked `/loop` with no " +
              $"prompt and the interval `{intervalText}`. A recurring loop is scheduled; the " +
              "autonomous-loop instructions below are baked in and ride every tick."
            : "# /loop — loop.md tasks on a recurring interval\n\nThe user invoked `/loop` with no prompt " +
              $"and the interval `{intervalText}`, and has a loop-tasks file at `{file.Path}`. A recurring " +
              "loop is scheduled to run those tasks each tick.";
        var task = file is null ? "the autonomous check" : "the loop.md tasks";
        return $"{header}\n\n## Action\n\nRun {task} now, following the instructions inlined below — don't " +
               "wait for the first tick. The recurring loop fires the next tick automatically — do not call " +
               $"ScheduleWakeup.\n\n{AutonomousBody(file)}";
    }

    // ---- fire-time resolution (reference rbo / ist / _sn and the tick reminders) ----

    /// <summary>
    /// Per-session delivery state (reference lastLoopFileDelivered /
    /// autonomousPreambleDelivered). Reset after compaction so the long
    /// instructions are re-delivered — "first fire post-compact".
    /// </summary>
    public sealed class DeliveryState
    {
        /// <summary>The loop.md content last delivered, or the autonomous marker.</summary>
        public string? LastLoopFileDelivered;

        public bool AutonomousPreambleDelivered;

        public void Reset()
        {
            LastLoopFileDelivered = null;
            AutonomousPreambleDelivered = false;
        }
    }

    private const string AutonomousPreambleMarker = "__autonomous_preamble__";

    /// <summary>
    /// Reference ost, on this app's wake sources: the watcher protocol suffix for
    /// dynamic tick reminders.
    /// </summary>
    private const string WatcherSuffix =
        "\n\nIf a background watcher is armed (check ListAgents), keep `delaySeconds` at 1200–1800s — the " +
        "watcher is the wake signal and this is only the fallback heartbeat. If you were woken by a " +
        "`<task-notification>`, handle the event before deciding whether to re-arm. To stop the loop, call " +
        "ScheduleWakeup with `stop: true` and TaskStop the watcher (use ListAgents to find its ID if no " +
        "longer in context).";

    /// <summary>Reference psn(): the recurring autonomous tick.</summary>
    public const string AutonomousTick =
        "# Autonomous loop tick\n" +
        "\n" +
        "Run the autonomous check using the loop instructions established earlier in this conversation. If " +
        "you cannot find them, treat this as a no-op tick. The recurring loop will fire the next tick " +
        "automatically — do not call ScheduleWakeup from this tick.";

    /// <summary>Reference J_o(): the dynamic autonomous tick.</summary>
    public const string AutonomousDynamicTick =
        "# Autonomous loop tick (dynamic pacing)\n" +
        "\n" +
        "Run the autonomous check using the loop instructions established earlier in this conversation. If " +
        "you cannot find them, treat this as a no-op tick.\n" +
        "\n" +
        "You scheduled this tick via the ScheduleWakeup tool (not a recurring interval). To keep the loop " +
        "alive, call ScheduleWakeup again at the end of this turn with `prompt` set to the literal " +
        "sentinel `" + AutonomousDynamicSentinel + "` and `noop` set to `true` if this tick changed nothing " +
        "(or `false` if it did) — otherwise the loop ends after this tick." + WatcherSuffix;

    /// <summary>Reference Q_o(): the recurring loop.md tick.</summary>
    public const string LoopFileTick =
        "# /loop tick — loop.md tasks\n" +
        "\n" +
        "Work the tasks from the loop.md contents established earlier in this conversation. If you cannot " +
        "find them, treat this as a no-op tick. The recurring loop will fire the next tick automatically — " +
        "do not call ScheduleWakeup from this tick.";

    /// <summary>Reference ebo(): the dynamic loop.md tick.</summary>
    public const string LoopFileDynamicTick =
        "# /loop tick — loop.md tasks (dynamic pacing)\n" +
        "\n" +
        "Work the tasks from the loop.md contents established earlier in this conversation. If you cannot " +
        "find them, treat this as a no-op tick.\n" +
        "\n" +
        "You scheduled this tick via the ScheduleWakeup tool (not a recurring interval). To keep the loop " +
        "alive, call ScheduleWakeup again at the end of this turn with `prompt` set to the literal " +
        "sentinel `" + LoopFileDynamicSentinel + "` and `noop` set to `true` if this tick changed nothing " +
        "(or `false` if it did) — otherwise the loop ends after this tick." + WatcherSuffix;

    /// <summary>Reference tbo(): the dynamic tick when loop.md has gone missing.</summary>
    public const string LoopFileAbsentDynamicTick =
        "# /loop tick — loop.md absent (dynamic pacing)\n" +
        "\n" +
        "loop.md is not currently present. Run the autonomous check using the loop instructions established " +
        "earlier in this conversation.\n" +
        "\n" +
        "To keep the loop alive — and to pick up loop.md if it is recreated — call ScheduleWakeup again at " +
        "the end of this turn with `prompt` set to the literal sentinel `" + LoopFileDynamicSentinel + "` " +
        "and `noop` set to `true` if this tick changed nothing (or `false` if it did) — otherwise the loop " +
        "ends after this tick." + WatcherSuffix;

    /// <summary>
    /// Turns a stored loop prompt into the message a firing tick actually sends:
    /// sentinels expand to their reminders (with the full instructions on first
    /// delivery, and re-delivered when loop.md changed or after a compact), and a
    /// `/loop `-prefixed prompt re-enters dynamic mode with the instruction block.
    /// Anything else fires verbatim.
    /// </summary>
    public static string ResolveFire(string prompt, string workingDirectory, DeliveryState state)
    {
        if (IsAutonomousSentinel(prompt))
        {
            var reminder = prompt == AutonomousDynamicSentinel ? AutonomousDynamicTick : AutonomousTick;
            if (state.AutonomousPreambleDelivered || state.LastLoopFileDelivered is not null)
            {
                return reminder;
            }

            state.AutonomousPreambleDelivered = true;
            return $"{AutonomousLoopPreamble}\n\n---\n\n{reminder}";
        }

        if (IsLoopFileSentinel(prompt))
        {
            bool dynamic = prompt == LoopFileDynamicSentinel;
            if (ReadLoopFile(workingDirectory) is { } file)
            {
                var reminder = dynamic ? LoopFileDynamicTick : LoopFileTick;
                if (state.LastLoopFileDelivered == file.Content)
                {
                    return reminder;
                }

                state.LastLoopFileDelivered = file.Content;
                return "# /loop tick — tasks from " + file.Path + "\n" +
                       "\n" +
                       "The user configured a loop-tasks file. Work through the tasks defined below; these " +
                       "are the instructions for this tick and every subsequent tick (the reminder on later " +
                       "fires refers back to this message).\n" +
                       "\n---\n\n" +
                       file.Content +
                       "\n\n---\n\n" +
                       reminder;
            }

            var absent = dynamic ? LoopFileAbsentDynamicTick : AutonomousTick;
            if (state.LastLoopFileDelivered == AutonomousPreambleMarker || state.AutonomousPreambleDelivered)
            {
                return absent;
            }

            state.LastLoopFileDelivered = AutonomousPreambleMarker;
            state.AutonomousPreambleDelivered = true;
            return $"{AutonomousLoopPreamble}\n\n---\n\n{absent}";
        }

        if (prompt.StartsWith("/loop ", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = Parse(prompt["/loop ".Length..]);
            if (parsed.Interval is null && parsed.Prompt.Length > 0)
            {
                return BuildDynamicExpansion(parsed.Prompt);
            }

            return parsed.Prompt.Length > 0 ? parsed.Prompt : prompt;
        }

        return prompt;
    }
}
