using JarvisCode.Core.Models;

namespace JarvisCode.Core.Agent;

/// <summary>
/// The two reminders the reference harness adds after a batch of tool results
/// (CLI 2.1.257): the <c>batching_reminder</c> its <c>vBn</c> (189099019)
/// inserts after a tool-result message, and the <c>silent_turn_reminder</c> its
/// <c>vQo</c> sends once the model has gone a number of assistant turns without
/// saying anything to the user. Both ride the same trailing harness turn as the
/// <c>&lt;total_tokens&gt;</c> block, ahead of it, and each is its own block.
///
/// Both ride one trailing harness turn whose <c>content</c> is a single string,
/// the sections joined by a blank line — measured on CLI 2.1.257, where a turn
/// carrying both arrives as <c>{"role":"system","content":"{batching}\n\n
/// {secondary}"}</c> appended as the last element of <c>messages</c>. There is
/// no <c>&lt;system-reminder&gt;</c> wrapper and no per-section block; the names
/// <c>batching_reminder</c> and <c>secondary_reminder</c> are internal to the
/// reference and never reach the wire.
///
/// Both are model-gated, and the gate is absolute: <c>TBn</c> checks
/// <c>fTe</c> — the mid-conversation system role — <em>before</em> it resolves
/// any text, so a model without that role receives neither reminder however the
/// text was configured. Measured: with <c>CLAUDE_CODE_TOASTY_THIMBLE</c> set,
/// claude-opus-5 and claude-fable-5-1 carry the turn while claude-haiku-4-5 and
/// claude-opus-4-5 carry no system role at all. What the environment override
/// buys is text for a model that owns none of its own — the batching text's
/// <c>modelOwn</c> is <c>KU</c> (<c>!MAt() &amp;&amp; fable_5_1_prompt_bundle</c>),
/// so only a fable/mythos 5.1 model owns it — not an exemption from the gate.
/// </summary>
public static class ToolResultReminders
{
    /// <summary>The reference's <c>WHo</c>, verbatim.</summary>
    public const string BatchingText =
        "First privately list what you need next; then request every item that doesn't depend on another's " +
        "result in this one response.";

    /// <summary>The reference's <c>Etr</c>, the silent-turn text a build ships with.</summary>
    public const string SilentTurnDefaultText =
        "The user hasn't heard from you in a while. As you continue, keep them updated when there's " +
        "something to tell — a finding, a change of plan.";

    public const string BatchingTextVariable = "CLAUDE_CODE_TOASTY_THIMBLE";
    public const string SecondaryTextVariable = "CLAUDE_CODE_GENTLE_PARASOL";
    public const string SilentTurnVariable = "CLAUDE_CODE_SILENT_TURN_REMINDER";
    public const string SilentTurnTextVariable = "CLAUDE_CODE_SILENT_TURN_REMINDER_TEXT";
    public const string SilentTurnTurnsVariable = "CLAUDE_CODE_SILENT_TURN_REMINDER_TURNS";

    /// <summary>The reference's <c>Str</c>: assistant turns without a word to the user before the reminder.</summary>
    public const int DefaultSilentTurns = 5;

    /// <summary>The reference's <c>Otr</c>: reminders per stretch between two genuine user messages.</summary>
    public const int MaxSilentRemindersPerStretch = 3;

    /// <summary>
    /// The tools whose use counts as addressing the user (the reference's
    /// <c>TQo</c>: AskUserQuestion, SendUserMessage and its legacy Brief name,
    /// SendUserFile).
    /// </summary>
    public static readonly IReadOnlySet<string> UserFacingTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "AskUserQuestion", "SendUserMessage", "Brief", "SendUserFile",
    };

    /// <summary>
    /// Result text that says the call was refused or cut short, which suppresses
    /// the batching reminder.
    ///
    /// The reference's <c>e1o()</c> is eight of its own constants
    /// (<c>xw, NR, FP, CV, Jb, uue, iu, Ov</c> in CLI 2.1.257, read at bytes
    /// 190460716 and 185995752): the two "The user doesn't want to proceed with
    /// this tool use…" wordings, the two "Permission for this tool use was
    /// denied…" wordings, "The user doesn't want to take this action right
    /// now…", and the three bracketed markers below. A list of prefixes is
    /// enough there because those eight are the whole of its refusal
    /// vocabulary.
    ///
    /// This build's vocabulary is its own, and one member of it —
    /// <c>"{tool} may not touch a path outside the working directory…"</c> —
    /// opens with a tool name, which no fixed prefix can match. So the prefixes
    /// cover the fixed wordings and <see cref="ToolResultBlock.Refused"/>
    /// carries the rest: the orchestrator sets it on every gate denial, whatever
    /// reason the gate supplied.
    /// </summary>
    public static readonly IReadOnlyList<string> InterruptedPrefixes =
    [
        "The user denied this tool call.",
        // The reference's iu and K_; this build emits the first verbatim.
        "[Request interrupted by user",
        // The reference's Ov and uue, emitted when a turn ends before a call ran.
        "[Tool call did not complete",
        "[Tool call skipped",
    ];

    /// <summary>
    /// The batching text for this turn: the environment's override for any
    /// model, else the reference's own text when the model owns it, else nothing.
    /// </summary>
    public static string? ResolveBatchingText(string? environmentOverride, bool modelOwnsText)
    {
        var trimmed = environmentOverride?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            // The reference reads a falsy value as "no reminder" (its GHo).
            return IsFalsy(trimmed) ? null : trimmed;
        }

        return modelOwnsText ? BatchingText : null;
    }

    /// <summary>The silent-turn text: <c>CLAUDE_CODE_SILENT_TURN_REMINDER_TEXT</c> or the default.</summary>
    public static string ResolveSilentTurnText(string? environmentOverride) =>
        string.IsNullOrEmpty(environmentOverride) ? SilentTurnDefaultText : environmentOverride;

    /// <summary>The turn count: <c>CLAUDE_CODE_SILENT_TURN_REMINDER_TURNS</c> when it is a number ≥ 1, else 5.</summary>
    public static int ResolveSilentTurns(string? environmentOverride)
    {
        if (double.TryParse(environmentOverride, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed >= 1)
        {
            return (int)Math.Floor(parsed);
        }

        return DefaultSilentTurns;
    }

    /// <summary>
    /// Whether the silent-turn reminder is on: <c>CLAUDE_CODE_SILENT_TURN_REMINDER</c>
    /// decides when set, else the model's own ownership (the reference's <c>KU</c>).
    /// </summary>
    public static bool ResolveSilentTurnEnabled(string? environmentOverride, bool modelOwnsReminder)
    {
        if (!string.IsNullOrEmpty(environmentOverride))
        {
            return !IsFalsy(environmentOverride);
        }

        return modelOwnsReminder;
    }

    /// <summary>
    /// The reference's <c>wBn</c> gate, which both reminders share: the message
    /// this turn follows is a user message carrying tool results.
    /// </summary>
    public static bool FollowsToolResults(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role != Role.User)
            {
                return false;
            }

            return message.Content.Any(static b => b is ToolResultBlock);
        }

        return false;
    }

    /// <summary>
    /// Whether the run of tool-result messages behind this turn holds a call the
    /// gate refused or the user cut short — the reference's inner loop, which
    /// clears its <c>d</c> flag.
    /// </summary>
    public static bool RunHasRefusal(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role != Role.User)
            {
                break;
            }

            var results = message.Content.OfType<ToolResultBlock>().ToList();
            if (results.Count == 0)
            {
                break;
            }

            foreach (var result in results)
            {
                if (result.Refused ||
                    InterruptedPrefixes.Any(prefix =>
                        result.Content.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the batching reminder itself applies: the shared gate, and
    /// nothing in the run that stands it down.
    /// </summary>
    public static bool BatchingApplies(IReadOnlyList<ChatMessage> messages) =>
        FollowsToolResults(messages) && !RunHasRefusal(messages);

    /// <summary>
    /// The reference's <c>bbr</c>: walking back from the end, how many assistant
    /// messages have passed since the last silent-turn reminder without the
    /// model addressing the user (text, or one of <see cref="UserFacingTools"/>),
    /// and how many reminders the stretch since the last genuine user message
    /// already holds. A message that addressed the user ends the count.
    /// </summary>
    public static (int TurnsSinceLastReminder, int RemindersInStretch) CountSilentTurns(
        IReadOnlyList<ChatMessage> messages, string reminderText)
    {
        var turns = 0;
        var reminders = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role == Role.Assistant)
            {
                if (AddressesUser(message))
                {
                    return (turns, reminders);
                }

                if (reminders == 0)
                {
                    turns++;
                }

                continue;
            }

            if (message.Role != Role.User)
            {
                continue;
            }

            if (IsReminder(message, reminderText))
            {
                reminders++;
                continue;
            }

            if (IsGenuineUserMessage(message))
            {
                break;
            }
        }

        return (turns, reminders);
    }

    /// <summary>
    /// The reference's <c>vQo</c>: the reminder fires when the stretch has fewer
    /// than three and the silent run reached the configured length.
    /// </summary>
    public static bool SilentTurnDue(IReadOnlyList<ChatMessage> messages, string reminderText, int turns)
    {
        var (since, inStretch) = CountSilentTurns(messages, reminderText);
        return inStretch < MaxSilentRemindersPerStretch && since >= turns;
    }

    /// <summary>A user turn the person typed: not harness-authored, not tool results, not the system turn.</summary>
    public static bool IsGenuineUserMessage(ChatMessage message)
    {
        // The reference's own predicate is `type === "user" && !isMeta`: a turn
        // the harness submitted — a loop tick, a skill body, a continuation —
        // never ends the silent-turn run, because the user still has not heard
        // anything.
        if (message.Role != Role.User || message.HarnessSystemTurn || message.IsMeta)
        {
            return false;
        }

        if (message.Content.Any(static b => b is ToolResultBlock))
        {
            return false;
        }

        var harnessBlocks = message.HarnessNoteCount + message.HarnessTailCount;
        return harnessBlocks < message.Content.Count;
    }

    /// <summary>
    /// What this build sends a model, resolved once per session. Both reminders
    /// need the mid-conversation system role, which is where the reference's
    /// <c>fTe</c> gate sits.
    /// </summary>
    /// <param name="SystemTurnModel">
    /// Whether the model takes the harness's trailing system turn; false sends
    /// neither reminder, as the reference's <c>TBn</c> and its silent-turn
    /// sibling both refuse without it.
    /// </param>
    /// <param name="ModelOwnsText">
    /// Whether the model carries the reference's <c>fable_5_1_prompt_bundle</c>,
    /// which is the only bundle that owns either text.
    /// </param>
    public sealed record Options(bool SystemTurnModel, bool ModelOwnsText)
    {
        /// <summary>The batching text this session sends, or null for none.</summary>
        public string? BatchingText { get; init; }

        /// <summary>The reference's <c>secondary_reminder</c>: environment-only, no text of its own.</summary>
        public string? SecondaryText { get; init; }

        /// <summary>Whether the silent-turn reminder is on for this session.</summary>
        public bool SilentTurnEnabled { get; init; }

        /// <summary>The silent-turn text.</summary>
        public string SilentTurnText { get; init; } = SilentTurnDefaultText;

        /// <summary>Silent assistant turns before the reminder fires.</summary>
        public int SilentTurns { get; init; } = DefaultSilentTurns;

        /// <summary>Reads the environment the reference reads, for a model with these two capabilities.</summary>
        public static Options FromEnvironment(bool systemTurnModel, bool modelOwnsText)
        {
            var silentEnabled = ResolveSilentTurnEnabled(
                Environment.GetEnvironmentVariable(SilentTurnVariable), modelOwnsText);
            return new Options(systemTurnModel, modelOwnsText)
            {
                BatchingText = ResolveBatchingText(
                    Environment.GetEnvironmentVariable(BatchingTextVariable), modelOwnsText),
                SecondaryText = ResolveBatchingText(
                    Environment.GetEnvironmentVariable(SecondaryTextVariable), modelOwnsText: false),
                SilentTurnEnabled = silentEnabled,
                SilentTurnText = ResolveSilentTurnText(
                    Environment.GetEnvironmentVariable(SilentTurnTextVariable)),
                SilentTurns = ResolveSilentTurns(
                    Environment.GetEnvironmentVariable(SilentTurnTurnsVariable)),
            };
        }

        /// <summary>
        /// These options with both reminder texts run through
        /// <paramref name="latch"/>, so a conversation keeps the text it first
        /// resolved for this model. The silent-turn reminder is not latched:
        /// the reference latches only the two <c>TBn</c> slots.
        /// </summary>
        public Options Latched(TextLatch latch, string conversationId, string modelId) => this with
        {
            BatchingText = latch.Latch(TextLatch.BatchingSlot, conversationId, modelId, BatchingText),
            SecondaryText = latch.Latch(TextLatch.SecondarySlot, conversationId, modelId, SecondaryText),
        };
    }

    /// <summary>The star pattern, which the reference's <c>dDe</c> treats as the last resort.</summary>
    public const string StarPattern = "*";

    /// <summary>
    /// Whether a pattern matches a model id, the reference's <c>UCn</c>: the
    /// pattern is split on <c>*</c>, its empty parts dropped, and each remaining
    /// part must occur in the value in order. It anchors at neither end, so
    /// <c>claude-opus</c> matches anywhere in the id.
    /// </summary>
    public static bool PatternMatches(string pattern, string value)
    {
        var at = 0;
        foreach (var part in pattern.Split(StarPattern))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var found = value.IndexOf(part, at, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            at = found + part.Length;
        }

        return true;
    }

    /// <summary>
    /// The entry a model-pattern map holds for a model — the reference's
    /// <c>_ce</c>, which is how it turns a <c>client_data</c> map into one text.
    ///
    /// Its precedence, in order: an exact match on the model id wins outright; a
    /// glob whose literal characters are the longest comes next, but only after
    /// an exact match on the model's other spelling; and bare <c>*</c> is the
    /// last resort. A pattern made only of stars matches nothing.
    /// </summary>
    /// <param name="baseModelId">
    /// The model's other spelling, which the reference matches beside the id
    /// (its <c>hr(Ve(n))</c>). Null when a caller has only the one.
    /// </param>
    public static string? MatchModelPattern(
        IReadOnlyDictionary<string, string?> map, string modelId, string? baseModelId = null)
    {
        var id = modelId.ToLowerInvariant();
        var other = (baseModelId ?? modelId).ToLowerInvariant();

        string? star = null;
        string? exactOther = null;
        string? bestGlob = null;
        var bestLiteral = -1;

        foreach (var (rawPattern, text) in map)
        {
            var pattern = rawPattern.Trim().ToLowerInvariant();
            if (pattern == StarPattern)
            {
                star ??= text;
                continue;
            }

            if (pattern == id)
            {
                return text;
            }

            if (pattern == other)
            {
                exactOther ??= text;
                continue;
            }

            var literal = pattern.Replace(StarPattern, string.Empty);
            if (literal.Length == 0)
            {
                continue;
            }

            if ((PatternMatches(pattern, id) || PatternMatches(pattern, other)) &&
                literal.Length > bestLiteral)
            {
                bestGlob = text;
                bestLiteral = literal.Length;
            }
        }

        return exactOther ?? bestGlob ?? star;
    }

    /// <summary>
    /// The reference's per-reminder latch — its <c>VHo</c> and <c>KHo</c> read
    /// by <c>TBn</c>. Once a conversation has resolved a reminder's text for a
    /// model, that is the text the conversation keeps for that model: later
    /// turns return the latched value instead of resolving again, so changing
    /// the environment mid-conversation moves nothing. A different model gets
    /// its own entry (the reference keys a <c>byModel</c> map by the lower-cased
    /// model id), and a different conversation starts over.
    /// </summary>
    public sealed class TextLatch
    {
        /// <summary>The batching reminder's slot.</summary>
        public const string BatchingSlot = "batching";

        /// <summary>The secondary reminder's slot.</summary>
        public const string SecondarySlot = "secondary";

        private readonly Dictionary<(string Slot, string Model), string?> _byModel = [];
        private readonly Lock _lock = new();
        private string? _conversationId;

        /// <summary>
        /// The text this conversation already holds for <paramref name="slot"/>
        /// and <paramref name="modelId"/>, latching <paramref name="resolved"/>
        /// when it holds none yet.
        /// </summary>
        public string? Latch(string slot, string conversationId, string modelId, string? resolved)
        {
            lock (_lock)
            {
                if (!string.Equals(_conversationId, conversationId, StringComparison.Ordinal))
                {
                    _conversationId = conversationId;
                    _byModel.Clear();
                }

                var key = (slot, modelId.ToLowerInvariant());
                if (_byModel.TryGetValue(key, out var latched))
                {
                    return latched;
                }

                _byModel[key] = resolved;
                return resolved;
            }
        }
    }

    /// <summary>
    /// The blocks that ride the harness turn after this batch of tool results,
    /// in the reference's order: the batching reminder, its secondary sibling,
    /// then the silent-turn reminder.
    /// </summary>
    /// <param name="deliveryPending">
    /// Whether a harness delivery is waiting to join the conversation — a
    /// message the user queued, or one another session sent. The reference
    /// stands the batching reminder down when such an attachment follows the
    /// tool results (its <c>ZHo</c>: <c>queued_command</c>,
    /// <c>teammate_mailbox</c>, <c>poll_events</c>). This build folds those in
    /// at the top of the next iteration rather than appending them here, so the
    /// equivalent question at this point is whether one is pending.
    /// </param>
    public static IReadOnlyList<string> Compose(
        IReadOnlyList<ChatMessage> messages, Options options, bool deliveryPending = false)
    {
        if (!options.SystemTurnModel)
        {
            return [];
        }

        var blocks = new List<string>();
        if (FollowsToolResults(messages))
        {
            // The reference's `d`. It gates the batching reminder alone: its
            // `k = d ? QHo(...) : null` sits beside an ungated
            // `T = JHo(...)`, so a refused call or a pending delivery silences
            // the nudge to batch without silencing its secondary sibling.
            var suppressed = deliveryPending || RunHasRefusal(messages);
            if (!suppressed && !string.IsNullOrEmpty(options.BatchingText))
            {
                blocks.Add(options.BatchingText);
            }

            if (!string.IsNullOrEmpty(options.SecondaryText))
            {
                blocks.Add(options.SecondaryText);
            }
        }

        if (options.SilentTurnEnabled &&
            SilentTurnDue(messages, options.SilentTurnText, options.SilentTurns))
        {
            blocks.Add(options.SilentTurnText);
        }

        return blocks;
    }

    private static bool IsFalsy(string value) =>
        value.Trim().ToLowerInvariant() is "0" or "false" or "no" or "off";

    private static bool IsReminder(ChatMessage message, string reminderText) =>
        message.HarnessSystemTurn &&
        message.Content.Any(b => b is TextBlock text && text.Text.Contains(reminderText, StringComparison.Ordinal));

    private static bool AddressesUser(ChatMessage message)
    {
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Trim().Length > 0:
                    return true;
                case ToolCallBlock call when UserFacingTools.Contains(call.Name):
                    return true;
            }
        }

        return false;
    }
}
