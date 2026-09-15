using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Agent;

/// <summary>Immutable inputs for one agent turn.</summary>
public sealed record AgentTurnContext
{
    /// <summary>Stable identity for a stateful provider; defaults to the tool session identity.</summary>
    public string? ConversationScopeId { get; init; }
    public required ILlmProvider Provider { get; init; }
    public required string ModelId { get; init; }
    public required string SystemPrompt { get; init; }
    /// <summary>Conversation history. The orchestrator appends to this list as the turn progresses.</summary>
    public required IList<ChatMessage> Messages { get; init; }
    public required IToolRegistry Tools { get; init; }
    public required IPermissionGate PermissionGate { get; init; }
    public required ToolExecutionContext ToolContext { get; init; }
    public Hooks.IToolHooks? Hooks { get; init; }
    public bool EnableWebSearch { get; init; }

    /// <summary>
    /// Turn cap, or null for none — which is the default, and what the reference
    /// does: its query generator takes an optional <c>maxTurns</c> and every
    /// check is written <c>if (maxTurns &amp;&amp; …)</c>, so an unset value means the
    /// loop never ends by counting. Its <c>--max-turns</c> flag is documented
    /// "only works with --print", so an interactive session has no cap at all;
    /// a turn ends because the model stopped, a hook stopped it, the user did,
    /// or the provider failed. Only agents whose definition declares one
    /// (fork 200, the autonomous executor 500) carry a cap here too.
    /// </summary>
    public int? MaxIterations { get; init; }

    /// <summary>
    /// True for a print/headless run. The reference gates its truncated-response
    /// recovery on exactly this (its <c>NAt</c> checks <c>isNonInteractiveSession</c>),
    /// because an interactive user can simply ask for the rest.
    /// </summary>
    public bool IsNonInteractiveSession { get; init; }

    /// <summary>
    /// True when this turn is a subagent's. The reference resumes a subagent's
    /// cut-off response unconditionally (only the main thread's is gated on the
    /// session being non-interactive), and words the nudge differently because
    /// only the subagent's final message is ever delivered.
    /// </summary>
    public bool IsSubagent { get; init; }

    /// <summary>
    /// System blocks the provider sends ahead of <see cref="SystemPrompt"/>; empty
    /// by default. See <see cref="LlmRequest.LeadingSystemBlocks"/>.
    /// </summary>
    public IReadOnlyList<SystemPromptBlock> LeadingSystemBlocks { get; init; } = [];

    /// <summary>The prompt-cache TTL to ask for; null keeps the vendor default.</summary>
    public string? CacheTtl { get; init; }

    /// <summary>
    /// Text the harness appends after every batch of tool results, as its own
    /// mid-conversation system turn — the reference's <c>&lt;total_tokens&gt;</c>
    /// block. Called once per batch; a null answer adds nothing. A wire without
    /// a system role folds the turn back onto the results, which is the shape
    /// the reference sends models that lack the role.
    /// </summary>
    public Func<string?>? ToolResultTrailer { get; init; }

    /// <summary>
    /// Reminders the harness places after a batch of tool results, ahead of the
    /// trailer and each as its own block of the same harness turn — the
    /// reference's <c>batching_reminder</c> and <c>silent_turn_reminder</c>
    /// attachments (see <see cref="ToolResultReminders"/>). Called once per batch
    /// with the results just appended; an empty answer adds nothing.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<string>>? ToolResultReminders { get; init; }

    /// <summary>
    /// The reference's oversize-result policy, when the host has a session
    /// directory to keep results in: a result past its tool's threshold is
    /// written to disk and the model gets the <c>&lt;persisted-output&gt;</c>
    /// block instead. Null leaves every result as the tool returned it.
    /// </summary>
    public Tools.ToolResultPersistence? ResultPersistence { get; init; }

    /// <summary>
    /// Some models silently hang after receiving tool results. When the request ends
    /// with tool results and the stream produces nothing for this long, the request
    /// is dropped and re-sent once (claw-code's post-tool stall nudge). Null disables it.
    /// </summary>
    public TimeSpan? PostToolStallTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Reasoning budget forwarded to every model call of the turn.</summary>
    public ThinkingEffort ThinkingEffort { get; init; } = ThinkingEffort.Off;

    /// <summary>Opaque provider-defined options forwarded to every model call of the turn.</summary>
    public IReadOnlyDictionary<string, string> ProviderOptions { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Enables mid-turn auto-compaction when set; null keeps the loop as it was.</summary>
    public AutoCompactOptions? AutoCompact { get; init; }

    /// <summary>
    /// Consulted before an auto-compaction runs, with the reference's trigger
    /// name ("auto"): a non-empty answer is a PreCompact hook's refusal and the
    /// compaction is skipped, as it is in the reference. Null never blocks.
    /// </summary>
    public Func<string, Task<string?>>? CompactionBlockedAsync { get; init; }

    /// <summary>
    /// Hand edits from the request inspector, forwarded to every model call of the
    /// turn. Null — the default — sends what the adapter built.
    /// </summary>
    public JsonObject? BodyOverride { get; init; }

    /// <summary>Extra request headers forwarded to every model call of the turn.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ExtraHeaders { get; init; } = [];

    /// <summary>
    /// Messages addressed to this agent while it runs (a teammate's inbox).
    /// Drained before every model call and appended as user turns, so a
    /// teammate hears its lead mid-run instead of after the report.
    /// </summary>
    public Func<IReadOnlyList<string>>? DrainInbox { get; init; }

    /// <summary>
    /// Messages the user queued while this turn was running, folded into the
    /// turn between model calls instead of waiting for it to end — the
    /// reference's mid-turn queue fold. Drained at the same point as
    /// <see cref="DrainInbox"/>; null keeps the queue for the next turn.
    /// </summary>
    public Func<IReadOnlyList<string>>? FoldQueuedMessages { get; init; }

    /// <summary>
    /// The provider request this context produces for one model call. Every call of
    /// the loop is built through it, so the request inspector can preview the real
    /// thing instead of a reconstruction. Pass <paramref name="tools"/> when the set
    /// has already been resolved for this iteration; otherwise the registry is read.
    /// </summary>
    public LlmRequest ToRequest(IReadOnlyList<ToolDefinition>? tools = null) => new()
    {
        ConversationScopeId = ConversationScopeId ?? ToolContext.SessionId,
        ModelId = ModelId,
        SystemPrompt = SystemPrompt,
        // Repair orphaned results / dangling calls (e.g. from an interrupted
        // prior turn in a resumed session) — providers 400 on both shapes.
        Messages = [.. Utilities.MessagePairing.Sanitize([.. Messages])],
        Tools = tools ?? [.. Tools.All.Select(tool => tool.ToDefinition())],
        EnableWebSearch = EnableWebSearch && ProviderCapabilities.For(Provider).SupportsWebSearchControl,
        ThinkingEffort = ProviderCapabilities.For(Provider).SupportsThinkingEffort ? ThinkingEffort : ThinkingEffort.Off,
        ProviderOptions = ProviderOptions,
        BodyOverride = BodyOverride,
        ExtraHeaders = ExtraHeaders,
        LeadingSystemBlocks = LeadingSystemBlocks,
        CacheTtl = CacheTtl,
    };
}

/// <summary>
/// Auto-compaction settings for a turn. The reference compacts when the context
/// reaches the model's window minus a fixed reserve, preserving the most recent
/// conversation group verbatim (more only when the summarizer itself overflows).
/// </summary>
public sealed record AutoCompactOptions(
    int MaxContextTokens,
    /// <summary>Context size (input+output tokens) measured at the end of the previous turn.</summary>
    long InitialContextTokens = 0)
{
    /// <summary>Tokens held back below the effective window before compaction fires (reference value).</summary>
    public const int ReserveTokens = ContextWindows.CompactReserveTokens;

    /// <summary>
    /// The model's maximum output, whose lesser part (capped at 20k) the
    /// reference holds back from the window before the 13k reserve.
    /// </summary>
    public int MaxOutputTokens { get; init; } = ContextWindows.OutputReserveCap;

    /// <summary>The user's auto-compact window (/autocompact); null keeps the model's own.</summary>
    public int? ConfiguredWindow { get; init; }

    /// <summary>CLAUDE_CODE_AUTO_COMPACT_WINDOW, which outranks the setting.</summary>
    public string? EnvironmentWindow { get; init; }

    /// <summary>
    /// The model the turn runs on: the reference keys its per-model window
    /// defaults on the canonical name, not on the window's size.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Whether auto-compaction may run (the reference's <c>Xp</c>). Off still
    /// leaves the window and the model here, because the blocking gate has to
    /// measure the context whether or not anything is allowed to shrink it.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The reference's CLAUDE_AUTOCOMPACT_PCT_OVERRIDE: fire at this percent of
    /// the effective window when that is earlier than the 13k reserve. 0 keeps
    /// the reserve alone; the reference honours anything in (0, 100].
    /// </summary>
    public double ThresholdPercent { get; init; }

    /// <summary>
    /// Clear old tool results before summarizing (the reference microcompact).
    /// The reference reaches this only from its context-hint reject path, which
    /// rides a server-side flag that is off by default — so this is off here too.
    /// </summary>
    public bool MicrocompactEnabled { get; init; }

    /// <summary>Failure and thrash bookkeeping carried across the session's turns.</summary>
    public CompactionState? State { get; init; }

    /// <summary>
    /// The session's precomputed-compact store. Null leaves the background
    /// summary off, which is where the reference leaves it: its own gate hangs
    /// off a server flag that ships false.
    /// </summary>
    public PrecomputeStore? Precompute { get; init; }

    /// <summary>
    /// Where a precomputed summary is stored between processes. Null keeps it
    /// in memory only, which is where the reference keeps it unless its own
    /// sidecar flag is on.
    /// </summary>
    public PrecompactSidecar? Sidecar { get; init; }

    /// <summary>The window in force and what decided it (the reference's GA).</summary>
    public AutoCompactWindow Window =>
        ContextWindows.ResolveWindow(MaxContextTokens, ConfiguredWindow, EnvironmentWindow, ModelId);

    /// <summary>The window less the model's output reserve (nF).</summary>
    public long EffectiveWindow => ContextWindows.EffectiveWindow(Window.Window, MaxOutputTokens);

    private double? PercentOverride => ThresholdPercent is > 0 and <= 100 ? ThresholdPercent : null;

    /// <summary>The context size that triggers compaction; 0 or less disables it.</summary>
    public long Threshold => ContextWindows.CompactThreshold(EffectiveWindow, PercentOverride);

    /// <summary>Where a background compact is armed (nhe).</summary>
    public long PrecomputeArmThreshold =>
        ContextWindows.PrecomputeArmThreshold(
            EffectiveWindow, ContextWindows.PrecomputeBufferFraction, PercentOverride);

    /// <summary>
    /// The model's own window less the output reserve (the reference's
    /// <c>ZYe</c>). The blocked rung measures against this and not against a
    /// narrowed setting: a smaller window is a compaction policy, while the
    /// model's window is the wall.
    /// </summary>
    public long RawEffectiveWindow =>
        ContextWindows.EffectiveWindow(MaxContextTokens, MaxOutputTokens);

    /// <summary>
    /// The reference's <c>v4e</c> guard: a window deliberately narrowed below
    /// the model default never arms a background summary, because what it would
    /// save does not pay for the call.
    /// </summary>
    public bool ArmsPrecomputation =>
        Window.Source == AutoCompactWindowSource.Auto ||
        Window.Window >= ContextWindows.ModelDefaultWindow;

    /// <summary>Where a context of this size sits on the reference's ladder.</summary>
    public (ContextLevel Level, int PercentLeft) Classify(long usedTokens) =>
        ContextWindows.Classify(
            usedTokens, EffectiveWindow, RawEffectiveWindow, Enabled, PercentOverride);

    /// <summary>
    /// The reference's <c>Hd</c>: the blocking gate stands down while
    /// auto-compaction is on and the model's own window is the one in force,
    /// because compaction is then expected to keep the turn under the wall.
    /// </summary>
    public bool DefersToAutoCompaction =>
        Enabled && Window.Source == AutoCompactWindowSource.Auto;
}

/// <summary>What the turn's auto-compaction attempt did, if it ran at all.</summary>
public enum CompactionOutcome
{
    /// <summary>The threshold was not crossed, or an attempt already ran this turn.</summary>
    NotAttempted,

    Compacted,

    /// <summary>The summarizer failed; <see cref="AgentOrchestrator"/> keeps the detail.</summary>
    Failed,

    /// <summary>A PreCompact hook refused it. The reference reports no detail for this.</summary>
    HookBlocked,
}

/// <summary>
/// The reference's recompaction bookkeeping (its <c>recompactionInfo</c>): how
/// many turns have passed since the last compact, how many attempts failed in a
/// row, and whether either breaker has tripped for the rest of the session.
/// </summary>
public sealed class CompactionState
{
    /// <summary>Consecutive failures that trip the circuit breaker (the reference's qRt).</summary>
    public const int FailureBreakerLimit = 3;

    /// <summary>Turns within which a refill counts as rapid (P4e), and the trip count.</summary>
    public const int RapidRefillTurns = 3;

    /// <summary>True once this session has compacted at least once.</summary>
    public bool Compacted { get; set; }

    /// <summary>Turns run since that compact.</summary>
    public int TurnCounter { get; set; }

    public int ConsecutiveFailures { get; set; }

    public int ConsecutiveRapidRefills { get; set; }

    /// <summary>Set once the circuit breaker tripped; no further attempts this session.</summary>
    public bool CircuitTripped { get; set; }

    /// <summary>Set once the rapid-refill breaker tripped.</summary>
    public bool RapidRefillTripped { get; set; }

    /// <summary>The reference's thrashing notice, shown when the refill breaker trips.</summary>
    public const string ThrashingNotice =
        "Autocompact is thrashing: the context refilled to the limit within 3 turns of the " +
        "previous compact, 3 times in a row. A file being read or a tool output is likely too " +
        "large for the context window. Try reading in smaller chunks, or use /clear to start fresh.";

    /// <summary>A turn started: the reference advances the counter behind its compact flag.</summary>
    public void BeginTurn()
    {
        if (Compacted)
            TurnCounter++;
    }

    /// <summary>
    /// The reference's <c>C9</c>/<c>FZt</c>: a compact requested within
    /// <see cref="RapidRefillTurns"/> turns of the previous one counts as a rapid
    /// refill, and three in a row trip the breaker.
    /// </summary>
    public bool RegisterAttemptAndCheckThrashing()
    {
        ConsecutiveRapidRefills = Compacted && TurnCounter < RapidRefillTurns
            ? ConsecutiveRapidRefills + 1
            : 0;
        if (ConsecutiveRapidRefills < RapidRefillTurns)
            return false;
        RapidRefillTripped = true;
        return true;
    }

    /// <summary>The reference's <c>GRt</c>: count a failure and trip after three.</summary>
    public bool RegisterFailure()
    {
        ConsecutiveFailures++;
        if (ConsecutiveFailures < FailureBreakerLimit)
            return false;
        CircuitTripped = true;
        return true;
    }

    /// <summary>A compact landed: the chain restarts from this turn.</summary>
    public void RegisterSuccess()
    {
        Compacted = true;
        TurnCounter = 0;
        ConsecutiveFailures = 0;
    }
}

/// <summary>
/// Runs the agentic loop: model → tool calls → tool results → model, until the
/// model answers without requesting tools. Emits <see cref="AgentEvent"/>s for the caller.
/// </summary>
public sealed class AgentOrchestrator
{
    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        AgentTurnContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var toolDefinitions = context.Tools.All.Select(t => t.ToDefinition()).ToList();
        int advertisedToolCount = toolDefinitions.Count;
        var totalUsage = Usage.Zero;
        long knownContextTokens = context.AutoCompact?.InitialContextTokens ?? 0;
        bool compactionAttempted = false;
        bool microcompactAttempted = false;
        var compactOutcome = CompactionOutcome.NotAttempted;
        string? compactFailureDetail = null;
        // The reference counts turns since the last compact behind its own flag;
        // three refills inside three turns trip the rapid-refill breaker.
        context.AutoCompact?.State?.BeginTurn();

        Utilities.DiagnosticLog.Write(
            $"turn: start provider={context.Provider.Id} model={context.ModelId} " +
            $"messages={context.Messages.Count} tools={toolDefinitions.Count}");

        // The reference counts a turn per tool round-trip, not per model call: its
        // recovery paths (a truncated answer resumed, a malformed tool call retried,
        // a thinking-only response nudged) re-enter the loop with the turn count
        // unchanged, so a recovery can never eat the caller's budget.
        int turnCount = 0;
        int outputRecoveryCount = 0;
        bool thinkingOnlyNudged = false;
        bool malformedToolUseRetried = false;
        while (context.MaxIterations is not int maxTurns || turnCount < maxTurns)
        {
            int iteration = turnCount;
            // A teammate reads its inbox between rounds: anything the lead
            // sent since the last call joins the conversation as a user turn.
            if (context.DrainInbox is { } drainInbox)
            {
                foreach (var delivered in drainInbox())
                    context.Messages.Add(Models.ChatMessage.FromUserText(delivered));
            }

            // Anything the user typed while this turn ran joins it here rather
            // than waiting for the turn to end.
            if (context.FoldQueuedMessages is { } foldQueued)
            {
                foreach (var queued in foldQueued())
                    context.Messages.Add(Models.ChatMessage.FromUserText(queued));
            }

            // Microcompact first: clearing old tool results is cheap, keeps the
            // rest of the history verbatim, and usually frees enough. One
            // attempt per turn — a second pass would find nothing new to clear.
            if (context.AutoCompact is { MicrocompactEnabled: true } micro &&
                !microcompactAttempted &&
                micro.Threshold > 0 &&
                knownContextTokens >= micro.Threshold)
            {
                microcompactAttempted = true;
                // The reference refuses the pass below its 20k minimum saving and
                // keeps the last five clearable results; Run answers null for both.
                if (Microcompactor.Run(context.Messages) is { } microResult)
                {
                    knownContextTokens -= microResult.TokensSaved;
                    Utilities.DiagnosticLog.Write(
                        $"[KEEP-RECENT MC] cleared {microResult.ClearedIds.Count} tool results " +
                        $"(~{microResult.TokensSaved} tokens), kept last {Microcompactor.KeepRecent}");
                    yield return new ConversationMicrocompacted(
                        microResult.ClearedIds.Count, microResult.TokensSaved);
                }
            }

            // A fifth of the window before the threshold, the reference starts
            // writing the summary in the background so that crossing it costs a
            // swap rather than a turn spent waiting on the summarizer.
            if (context.AutoCompact is { Enabled: true, Precompute: { } precompute } armLimits &&
                ProviderCapabilities.For(context.Provider).SupportsToolFreeInference &&
                armLimits.ArmsPrecomputation &&
                armLimits.PrecomputeArmThreshold > 0 &&
                knownContextTokens >= armLimits.PrecomputeArmThreshold &&
                new ConversationCompactor().CanCompact([.. context.Messages]) &&
                Armable(precompute, armLimits, [.. context.Messages]))
            {
                Utilities.DiagnosticLog.Write(
                    $"precomputed compact: started ({context.Messages.Count} msgs, " +
                    $"~{knownContextTokens} tok, attempt {precompute.Attempts + 1})");
                precompute.Arm(
                    context.Provider, context.ModelId, [.. context.Messages],
                    CompactionStripper.EnabledByEnvironment, armLimits.Sidecar);
            }

            // Auto-compaction: when the last known context size crosses the
            // window-minus-reserve threshold, summarize before the next call.
            // One attempt per turn — a failed summarization must not loop.
            if (context.AutoCompact is { Enabled: true } autoCompact &&
                !compactionAttempted &&
                autoCompact.Threshold > 0 &&
                knownContextTokens >= autoCompact.Threshold &&
                autoCompact.State is not { CircuitTripped: true } and not { RapidRefillTripped: true })
            {
                compactionAttempted = true;
                if (autoCompact.State?.RegisterAttemptAndCheckThrashing() == true)
                {
                    // The reference ends the turn here rather than running it on
                    // a context that compaction has already failed to hold.
                    Utilities.DiagnosticLog.Write(
                        "autocompact: rapid-refill breaker tripped — " +
                        $"{autoCompact.State.ConsecutiveRapidRefills} consecutive refills within " +
                        $"<{CompactionState.RapidRefillTurns} turns each");
                    yield return new TurnCompleted(
                        TurnEndReason.RapidRefillBreaker, CompactionState.ThrashingNotice);
                    yield break;
                }

                if (context.CompactionBlockedAsync is { } askHook &&
                    await askHook("auto") is { Length: > 0 } hookReason)
                {
                    // A hook refusing an automatic compaction is not announced —
                    // the reference suppresses the notification and lets the turn
                    // run on, which the blocking gate below then judges.
                    compactOutcome = CompactionOutcome.HookBlocked;
                    Utilities.DiagnosticLog.Write($"compaction blocked by PreCompact hook: {Trim(hookReason)}");
                }
                else
                {
                    var compactor = new ConversationCompactor();
                    if (compactor.CanCompact([.. context.Messages]))
                    {
                        yield return new ConversationCompacting();
                        CompactionResult? compaction = null;
                        string? failure = null;
                        TurnCompleted? terminalFailure = null;

                        // A summary written ahead of time is swapped in whole;
                        // everything added since it was written is kept verbatim.
                        if (ProviderCapabilities.For(context.Provider).SupportsToolFreeInference &&
                            autoCompact.Precompute?.TakeReady(
                                [.. context.Messages], autoCompact.Sidecar) is { } ready)
                        {
                            compaction = PrecomputeStore.Apply(ready, [.. context.Messages]);
                            Utilities.DiagnosticLog.Write(
                                $"precomputed compact: swapped in ({ready.PrefixLength} messages summarized)");
                        }

                        try
                        {
                            compaction ??= await compactor.CompactAsync(
                                context.Provider, context.ModelId, [.. context.Messages], cancellationToken,
                                stripNonEssential: CompactionStripper.EnabledByEnvironment);
                        }
                        catch (Exception ex) when (ex is ProviderException or InvalidOperationException)
                        {
                            failure = ex.Message;
                            if (ex is ProviderException { CanRetry: false })
                                terminalFailure = new TurnCompleted(TurnEndReason.Error, ex.Message) { CanRetry = false };
                        }

                        if (terminalFailure is not null)
                        {
                            yield return terminalFailure;
                            yield break;
                        }

                        if (compaction is not null)
                        {
                            context.Messages.Clear();
                            foreach (var message in compaction.Messages)
                                context.Messages.Add(message);
                            knownContextTokens = 0;
                            compactOutcome = CompactionOutcome.Compacted;
                            autoCompact.Precompute?.Invalidate(autoCompact.Sidecar);
                            autoCompact.State?.RegisterSuccess();
                            Utilities.DiagnosticLog.Write(
                                $"turn: auto-compacted to {context.Messages.Count} messages");
                            yield return new ConversationCompacted(compaction);
                        }
                        else
                        {
                            // The turn continues on the uncompacted context; the gate
                            // below decides whether it can be run at all.
                            compactOutcome = CompactionOutcome.Failed;
                            compactFailureDetail = failure;
                            if (autoCompact.State?.RegisterFailure() == true)
                            {
                                Utilities.DiagnosticLog.Write(
                                    "autocompact: circuit breaker tripped after " +
                                    $"{autoCompact.State.ConsecutiveFailures} consecutive failures " +
                                    "— skipping future attempts this session");
                            }
                            Utilities.DiagnosticLog.Write($"turn: auto-compaction failed — {Trim(failure)}");
                        }
                    }
                }
            }

            // The context wall: past it the request cannot be sent at all, so the
            // reference refuses the turn instead of letting the provider reject
            // it. It stands down while auto-compaction is on and the model's own
            // window is in force, because compaction is then expected to cope.
            if (context.AutoCompact is { } limits &&
                !limits.DefersToAutoCompaction &&
                compactOutcome != CompactionOutcome.Compacted &&
                limits.Classify(knownContextTokens).Level == ContextLevel.Blocked)
            {
                var blocked = BlockingLimitMessage(
                    compactOutcome == CompactionOutcome.Failed ? compactFailureDetail : null);
                Utilities.DiagnosticLog.Write(
                    $"turn: end BlockingLimit at {knownContextTokens} tokens");
                yield return new TurnCompleted(TurnEndReason.BlockingLimit, blocked);
                yield break;
            }

            // The registry may be live (a plan-mode approval swaps in the full
            // tool set mid-turn), so re-read it before every model call.
            var currentTools = context.Tools.All;
            if (currentTools.Count != advertisedToolCount)
            {
                toolDefinitions = currentTools.Select(t => t.ToDefinition()).ToList();
                advertisedToolCount = toolDefinitions.Count;
                Utilities.DiagnosticLog.Write($"turn: tool set changed mid-turn, now {advertisedToolCount} tools");
            }

            var request = context.ToRequest(toolDefinitions);

            // The stall nudge applies when this model call follows tool results — the
            // situation where some models hang without ever sending a first event.
            bool nudgeEligible = context.PostToolStallTimeout is not null &&
                ProviderCapabilities.For(context.Provider).SupportsPostToolStallRetry &&
                context.Messages.Count > 0 &&
                context.Messages[^1].Content.Any(b => b is ToolResultBlock);

            Utilities.DiagnosticLog.Write(
                $"turn: model call {iteration + 1}/{context.MaxIterations?.ToString() ?? "unbounded"}, " +
                $"messages={request.Messages.Count} nudge={nudgeEligible}");

            var accumulator = new ResponseAccumulator();
            for (int streamAttempt = 0; ; streamAttempt++)
            {
            accumulator = new ResponseAccumulator();
            bool stalled = false;
            bool awaitingFirstEvent = true;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IAsyncEnumerator<ProviderEvent>? stream = null;
            try
            {
                stream = context.Provider.StreamChatAsync(request, attemptCts.Token).GetAsyncEnumerator(attemptCts.Token);
                while (true)
                {
                    ProviderEvent? providerEvent = null;
                    TurnCompleted? failure = null;
                    try
                    {
                        bool hasNext;
                        if (awaitingFirstEvent && nudgeEligible && streamAttempt == 0)
                        {
                            var moveTask = stream.MoveNextAsync().AsTask();
                            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var delayTask = Task.Delay(context.PostToolStallTimeout!.Value, delayCts.Token);
                            var winner = await Task.WhenAny(moveTask, delayTask);
                            if (winner == moveTask)
                            {
                                delayCts.Cancel();
                                hasNext = await moveTask;
                            }
                            else if (cancellationToken.IsCancellationRequested)
                            {
                                throw new OperationCanceledException(cancellationToken);
                            }
                            else
                            {
                                // Stalled: abandon this attempt and silently re-send once.
                                Utilities.DiagnosticLog.Write(
                                    $"turn: no first event within {context.PostToolStallTimeout!.Value.TotalSeconds:0}s " +
                                    "after tool results — re-sending once");
                                stalled = true;
                                attemptCts.Cancel();
                                try
                                {
                                    await moveTask;
                                }
                                catch (ProviderException ex) when (!ex.CanRetry)
                                {
                                    // Even a request abandoned for a stall can reveal a
                                    // terminal boundary. Do not replay it on the nudge path.
                                    throw;
                                }
                                catch (Exception)
                                {
                                    // The abandoned attempt's outcome is irrelevant.
                                }
                                break;
                            }
                        }
                        else
                        {
                            hasNext = await stream.MoveNextAsync();
                        }
                        if (!hasNext)
                            break;
                        providerEvent = stream.Current;
                        awaitingFirstEvent = false;
                    }
                    catch (OperationCanceledException)
                    {
                        failure = new TurnCompleted(TurnEndReason.Cancelled);
                    }
                    catch (Exception ex)
                    {
                        // The reference separates a request that no longer fits from
                        // any other API failure: prompt_too_long, not api_error.
                        failure = ex is ProviderException { CanRetry: false }
                            ? new TurnCompleted(TurnEndReason.Error, ex.Message) { CanRetry = false }
                            : Utilities.ContextOverflowDetector.IsContextOverflow(ex.Message)
                            ? new TurnCompleted(TurnEndReason.PromptTooLong, ex.Message)
                            : new TurnCompleted(TurnEndReason.Error, ex.Message);
                    }
                    if (failure is not null)
                    {
                        Utilities.DiagnosticLog.Write($"turn: end {failure.Reason} — {Trim(failure.Detail)}");
                        yield return failure;
                        yield break;
                    }

                    switch (providerEvent)
                    {
                        case TextPreviewEvent preview:
                            yield return new AssistantTextPreviewed(preview.Text);
                            break;

                        case TextDeltaEvent text:
                            accumulator.AppendText(text.Delta, text.Index);
                            yield return new AssistantTextDelta(text.Delta);
                            break;
                        case CitationDeltaEvent citation:
                            accumulator.AddCitation(citation.Index, citation.Citation);
                            yield return new AssistantCitationDelta(citation.Citation);
                            break;
                        case ToolCallStartedEvent start:
                            accumulator.StartToolCall(start.Index, start.Id, start.Name);
                            yield return new ToolInputStarted(start.Id, start.Name);
                            break;
                        case ToolCallArgumentsDeltaEvent args:
                            if (accumulator.AppendToolArguments(args.Index, args.Delta) is { } inputDelta)
                                yield return inputDelta;
                            break;
                        case ThinkingDeltaEvent thinking:
                            yield return new AssistantThinkingDelta(thinking.Delta);
                            break;
                        case ThinkingCompletedEvent thinkingDone:
                            accumulator.AddThinking(thinkingDone.Index, thinkingDone.Thinking, thinkingDone.Signature);
                            break;
                        case RawBlockEvent raw:
                            accumulator.AddRawBlock(raw.Index, context.Provider.Id, raw.RawJson);
                            break;
                        case ServerToolNoticeEvent serverTool:
                            yield return new ServerToolActivity(serverTool.ToolName);
                            break;
                        case ResponseCompletedEvent completed:
                            accumulator.Complete(completed.WantsToolUse, completed.Usage, completed.StopReason);
                            totalUsage = totalUsage.Add(completed.Usage);
                            break;
                    }
                }
            }
            finally
            {
                if (stream is not null)
                    await stream.DisposeAsync();
            }
            if (!stalled)
                break;
            }

            var assistantMessage = accumulator.ToMessage();
            context.Messages.Add(assistantMessage);
            yield return new AssistantMessageCompleted(assistantMessage);
            yield return new UsageReported(totalUsage, accumulator.LastCallUsage);
            knownContextTokens = accumulator.LastCallUsage.TotalInputTokens + accumulator.LastCallUsage.OutputTokens;
            compactionAttempted = false;
            microcompactAttempted = false;

            var toolCalls = assistantMessage.ToolCalls.ToList();

            // Two stops the model itself chose, and neither is recoverable by
            // asking again. The reference reads a refusal as an error carried on
            // the message rather than as an answer; re-prompting one would be
            // arguing with a decision, and letting it end the turn quietly would
            // leave the user reading an empty transcript.
            if (accumulator.StopReason == StopReasons.Refusal)
            {
                Utilities.DiagnosticLog.Write("turn: end ModelError — the model refused");
                yield return new TurnCompleted(TurnEndReason.ModelError, RefusedMessage);
                yield break;
            }

            if (accumulator.StopReason == StopReasons.ModelContextWindowExceeded)
            {
                Utilities.DiagnosticLog.Write("turn: end PromptTooLong — context window exceeded");
                yield return new TurnCompleted(TurnEndReason.PromptTooLong, PromptTooLongMessage);
                yield break;
            }

            // The recovery ladder, in the reference's order: a cut-off answer is
            // resumed, a tool call the provider never delivered is retried once,
            // and a response with nothing user-visible is nudged once. None of
            // these advance the turn count.
            if (accumulator.StopReason == StopReasons.MaxTokens &&
                outputRecoveryCount < TurnRecovery.MaxResumeAttempts)
            {
                outputRecoveryCount++;
                Utilities.DiagnosticLog.Write(
                    $"turn: output token limit hit — resuming (attempt {outputRecoveryCount})");
                context.Messages.Add(ChatMessage.FromUserText(TurnRecovery.OutputLimitResume));
                continue;
            }

            // A stream that ended without the provider's completion event was cut
            // off in flight. The reference resumes it for a subagent always — a
            // subagent's partial answer reaches nobody — and for the main thread
            // only in a print run, where nobody is watching who could just ask
            // for the rest (CLI 2.1.257; 2.1.251 gated both on the print run).
            if (!accumulator.SawCompletion && (context.IsSubagent || context.IsNonInteractiveSession) &&
                outputRecoveryCount < TurnRecovery.MaxResumeAttempts)
            {
                outputRecoveryCount++;
                Utilities.DiagnosticLog.Write(
                    $"turn: response truncated mid-stream — resuming (attempt {outputRecoveryCount})");
                context.Messages.Add(ChatMessage.FromUserText(
                    context.IsSubagent ? TurnRecovery.TruncatedRewriteForSubagent : TurnRecovery.TruncatedResume));
                continue;
            }

            if (accumulator.WantsToolUse && toolCalls.Count == 0)
            {
                // The message claims a tool call that never arrived. Keeping it
                // would leave the model reading its own broken turn, so it is
                // taken back out and the call is asked for again — once. Removed
                // by position rather than by value: this is the message just
                // appended, and nothing earlier may be taken instead of it.
                if (context.Messages.Count > 0 &&
                    ReferenceEquals(context.Messages[^1], assistantMessage))
                {
                    context.Messages.RemoveAt(context.Messages.Count - 1);
                }
                yield return new AssistantMessageRetracted(assistantMessage);
                if (malformedToolUseRetried)
                {
                    Utilities.DiagnosticLog.Write("turn: end MalformedToolUseExhausted");
                    yield return new TurnCompleted(
                        TurnEndReason.MalformedToolUseExhausted, TurnRecovery.MalformedToolUseExhausted);
                    yield break;
                }

                malformedToolUseRetried = true;
                Utilities.DiagnosticLog.Write("turn: malformed tool use — retrying once");
                context.Messages.Add(ChatMessage.FromUserText(TurnRecovery.MalformedToolUseRetry));
                continue;
            }

            // Nothing readable came back. The stop reason is deliberately not
            // part of this condition any more: the two it must not fire on end
            // the turn above, and gating it on a list of the four names this
            // engine knows let a vendor's fifth one — a relay's own word, a new
            // API state — fall past every rung and finish the turn as a success
            // with an empty message behind it.
            if (toolCalls.Count == 0 && !accumulator.HasVisibleText && !thinkingOnlyNudged)
            {
                thinkingOnlyNudged = true;
                Utilities.DiagnosticLog.Write("turn: thinking-only response — nudging once");
                context.Messages.Add(ChatMessage.FromUserText(TurnRecovery.ThinkingOnlyNudge));
                continue;
            }

            // The nudge was already spent and the answer is still unreadable, so
            // the turn ends on what actually happened instead of on "completed".
            if (toolCalls.Count == 0 && !accumulator.HasVisibleText)
            {
                Utilities.DiagnosticLog.Write(
                    $"turn: end ModelError — no visible output (stop={accumulator.StopReason ?? "none"})");
                yield return new TurnCompleted(
                    TurnEndReason.ModelError, EmptyResponseDetail(accumulator.StopReason));
                yield break;
            }

            if (!accumulator.WantsToolUse || toolCalls.Count == 0)
            {
                Utilities.DiagnosticLog.Write(
                    $"turn: end Completed after {iteration + 1} model call(s), " +
                    $"in={totalUsage.InputTokens} out={totalUsage.OutputTokens}");
                yield return new TurnCompleted(TurnEndReason.Completed);
                yield break;
            }

            var results = new ToolResultBlock[toolCalls.Count];
            var eventChannel = Channel.CreateUnbounded<AgentEvent>();
            var executionTask = ExecuteToolCallsAsync(context, toolCalls, results, eventChannel.Writer, cancellationToken);
            await foreach (var toolEvent in eventChannel.Reader.ReadAllAsync(CancellationToken.None))
                yield return toolEvent;

            // An interrupt that lands while tools are running is its own terminal
            // reason in the reference (aborted_tools, not aborted_streaming).
            TurnCompleted? toolFailure = null;
            try
            {
                await executionTask;
            }
            catch (OperationCanceledException)
            {
                toolFailure = new TurnCompleted(TurnEndReason.AbortedTools);
            }
            catch (Hooks.HookStopRequestedException ex)
            {
                toolFailure = new TurnCompleted(TurnEndReason.HookStopped, ex.Message);
            }
            if (toolFailure is not null)
            {
                Utilities.DiagnosticLog.Write("turn: end " + toolFailure.Reason);
                yield return toolFailure;
                yield break;
            }

            context.Messages.Add(ChatMessage.FromToolResults(results));
            // The reference's <total_tokens> block follows every batch of tool
            // results as a mid-conversation system turn (measured: a lean model
            // gets it as a trailing role-system message; a classic one gets the
            // same text folded onto the results as a <system-reminder>, which the
            // Anthropic adapter does when the model lacks the role). Its
            // batching and silent-turn reminders ride the same turn ahead of it,
            // each as its own block.
            var trailerBlocks = new List<ContentBlock>();
            if (context.ToolResultReminders?.Invoke([.. context.Messages]) is { Count: > 0 } reminders)
            {
                foreach (var reminder in reminders)
                {
                    if (!string.IsNullOrEmpty(reminder))
                        trailerBlocks.Add(new TextBlock(reminder));
                }
            }

            if (context.ToolResultTrailer?.Invoke() is { Length: > 0 } trailer)
            {
                trailerBlocks.Add(new TextBlock(trailer));
            }

            if (trailerBlocks.Count > 0)
            {
                context.Messages.Add(new ChatMessage(Role.User, trailerBlocks)
                {
                    HarnessSystemTurn = true,
                });
            }

            turnCount++;
        }

        Utilities.DiagnosticLog.Write($"turn: end MaxIterationsReached at {context.MaxIterations} turns");
        yield return new TurnCompleted(
            TurnEndReason.MaxIterationsReached,
            $"Stopped after {context.MaxIterations} turns.");
    }

    /// <summary>The line separator used inside message text, LF on every platform.</summary>
    private const string NL = "\n";

    /// <summary>
    /// Caps a vendor error before it reaches the log: the message is the useful part,
    /// and a long one can echo back parts of the request.
    /// </summary>
    /// <summary>The reference's <c>fH</c>: what a turn refused at the wall says.</summary>
    public const string PromptTooLongMessage = "Prompt is too long";

    /// <summary>How much of a compaction failure rides that sentence (the reference's 300).</summary>
    private const int CompactFailureDetailChars = 300;

    /// <summary>
    /// What a turn says when the model declined to answer. This build's own
    /// sentence: the reference reads the refusal's category and explanation off
    /// the vendor's <c>stop_details</c>, which only its own account API returns.
    /// </summary>
    public const string RefusedMessage = "The model declined to answer this request.";

    /// <summary>
    /// What a turn says when a call came back with nothing readable — no text and
    /// no tool call — and the nudge did not change that. Also this build's own.
    /// </summary>
    public const string EmptyResponseMessage = "The model returned no readable response.";

    /// <summary>How much of a vendor stop reason rides that sentence.</summary>
    private const int StopReasonDetailChars = 60;

    /// <summary>
    /// The sentence above, told which stop reason came with it. Providers may
    /// report a word this engine has never heard of — naming it is the whole
    /// diagnostic, so it is shown, capped, rather than swallowed.
    /// </summary>
    internal static string EmptyResponseDetail(string? stopReason)
    {
        if (stopReason is not { Length: > 0 } reason)
            return EmptyResponseMessage;

        var shortened = reason.Length <= StopReasonDetailChars
            ? reason
            : reason[..StopReasonDetailChars] + "…";
        return $"{EmptyResponseMessage} (stop reason: {shortened})";
    }

    /// <summary>
    /// The reference's <c>Eve</c>: the same sentence, told which compaction
    /// failed to make room, when one did.
    /// </summary>
    internal static string BlockingLimitMessage(string? compactFailureDetail)
    {
        if (compactFailureDetail is not { Length: > 0 } detail)
            return PromptTooLongMessage;

        var shortened = detail.Length <= CompactFailureDetailChars
            ? detail
            : detail[..CompactFailureDetailChars] + "…";
        return $"{PromptTooLongMessage} · automatic compaction failed: {shortened}";
    }

    /// <summary>
    /// A stored summary is read back before the first arm, so a process that
    /// starts on a long conversation can compact without paying for the
    /// summarization its predecessor already paid for.
    /// </summary>
    private static bool Armable(
        PrecomputeStore precompute, AutoCompactOptions limits, IReadOnlyList<ChatMessage> messages)
    {
        precompute.RehydrateOnce(limits.Sidecar, messages);
        return precompute.CanArm;
    }

    private static string Trim(string? detail) => detail is null
        ? "(no detail)"
        : detail.Length <= 200 ? detail : detail[..200] + "…";

    /// <summary>A tool call resolved against the registry with parsed arguments.</summary>
    private sealed record PreparedCall(int Index, ToolCallBlock Call, ITool Tool, JsonObject Arguments);

    /// <summary>
    /// Executes one batch of tool calls, streaming events through the channel.
    /// Read-only calls (including subagents) run concurrently; mutating calls run
    /// afterwards one at a time, so permission prompts appear in order and never
    /// race the reads. Every call fills its slot in <paramref name="results"/>.
    /// </summary>
    private static async Task ExecuteToolCallsAsync(
        AgentTurnContext context,
        IReadOnlyList<ToolCallBlock> calls,
        ToolResultBlock[] results,
        ChannelWriter<AgentEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            void Reject(int index, ToolCallBlock call, string message)
            {
                results[index] = new ToolResultBlock(call.Id, call.Name, message, IsError: true);
                events.TryWrite(new ToolExecutionCompleted(call.Id, call.Name, message, IsError: true));
            }

            var executable = new List<PreparedCall>();
            for (int i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                var tool = context.Tools.Find(call.Name);
                if (tool is null)
                {
                    Reject(i, call, $"Unknown tool: {call.Name}");
                    continue;
                }
                JsonObject? arguments;
                try
                {
                    arguments = string.IsNullOrWhiteSpace(call.ArgumentsJson)
                        ? []
                        : JsonNode.Parse(call.ArgumentsJson) as JsonObject ?? [];
                }
                // ArgumentException is the transcode refusal for arguments holding
                // half of a surrogate pair, which a stream cut mid-character leaves
                // behind: malformed arguments, and rejected as such.
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    Reject(i, call, $"Tool arguments were not valid JSON: {ex.Message}");
                    continue;
                }
                executable.Add(new PreparedCall(i, call, tool, arguments));
            }

            await Task.WhenAll(executable
                .Where(p => p.Tool.IsReadOnly)
                .Select(p => ExecuteOneAsync(context, p, results, events, cancellationToken)));

            foreach (var prepared in executable.Where(p => !p.Tool.IsReadOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteOneAsync(context, prepared, results, events, cancellationToken);
            }
        }
        finally
        {
            events.Complete();
        }
    }

    private static async Task ExecuteOneAsync(
        AgentTurnContext context,
        PreparedCall prepared,
        ToolResultBlock[] results,
        ChannelWriter<AgentEvent> events,
        CancellationToken cancellationToken)
    {
        var (index, call, tool, arguments) = prepared;
        using var hookCall = Hooks.HookRunner.EnterToolCall(call.Id);
        string? hookPermission = null;
        if (context.Hooks is not null)
        {
            var decision = await context.Hooks.BeforeToolAsync(tool, arguments, cancellationToken);
            if (!decision.Allowed)
            {
                var reason = decision.BlockReason ?? "Blocked by a pre_tool_use hook.";
                results[index] = new ToolResultBlock(call.Id, call.Name, reason, IsError: true);
                events.TryWrite(new ToolExecutionCompleted(call.Id, call.Name, reason, IsError: true));
                return;
            }
            hookPermission = decision.PermissionBehavior;
        }
        var description = SafeDescribe(tool, arguments);
        // Read-only calls consult the gate too: deny rules and out-of-workspace
        // escalation apply to reads, and the gate is what auto-approves benign ones.
        var gateDecision = await context.PermissionGate.RequestAsync(
            new PermissionRequest(tool, arguments, description, call.Id, hookPermission), cancellationToken);
        if (gateDecision == PermissionDecision.Deny)
        {
            var denialSource = context.PermissionGate as IDenialReasonSource;
            var denied = denialSource?.TakeDenialReason(call.Id)
                ?? "The user denied this tool call. Ask how they would like to proceed instead of retrying.";
            results[index] = new ToolResultBlock(call.Id, call.Name, denied, IsError: true)
            {
                // Marked rather than matched, because the gate may answer with
                // any reason and one of this build's opens with the tool's name.
                // Only a refusal the user themselves gave counts: a rule, a
                // setting or a run with nobody to ask denies too, and the
                // reference leaves the batching reminder riding those.
                Refused = denialSource?.TakeDenialWasUserRefusal(call.Id) ?? false,
            };
            events.TryWrite(new ToolExecutionDenied(call.Id, call.Name));
            return;
        }

        events.TryWrite(new ToolExecutionStarted(call.Id, call.Name, description, arguments.ToJsonString()));

        var callContext = context.ToolContext with
        {
            CallId = call.Id,
            // A fork inherits the parent's conversation, and the assistant
            // message carrying this call is already its last entry — which is
            // exactly where the reference forks from.
            ConversationSnapshot = () => [.. context.Messages],
        };
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(arguments, callContext, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Error($"{tool.Name} failed: {ex.Message}");
        }

        // A PostToolUse hook may answer with context for the model; the reference
        // delivers it with the result rather than as a message of its own.
        if (context.Hooks is not null &&
            await context.Hooks.AfterToolAsync(tool, arguments, result, cancellationToken) is { Length: > 0 } added)
        {
            result = result with { Content = result.Content + NL + NL + added };
        }

        // The reference persists an oversize result to disk before it reaches the
        // model and names the file in its place; the transcript still shows what
        // the tool returned.
        var modelContent = context.ResultPersistence is { } persistence && !result.IsError
            ? persistence.Apply(
                call.Name, call.Id, result.Content, result.Images is { Count: > 0 },
                (context.Tools.Find(call.Name) as Tools.IResultSizeTool)?.MaxResultSizeChars)
            : result.Content;
        results[index] = new ToolResultBlock(call.Id, call.Name, modelContent, result.IsError, result.Images)
        {
            FollowUpText = result.FollowUpText,
        };
        events.TryWrite(new ToolExecutionCompleted(call.Id, call.Name, result.Content, result.IsError, result.Images));
    }

    private static string SafeDescribe(ITool tool, JsonObject arguments)
    {
        try
        {
            return tool.DescribeCall(arguments);
        }
        catch (Exception)
        {
            return $"{tool.Name}(…)";
        }
    }

    /// <summary>
    /// Folds streaming provider events into one assistant message, preserving
    /// block order by stream index — replaying thinking to Anthropic requires it
    /// to precede the text and tool-use blocks it produced.
    /// </summary>
    private sealed class ResponseAccumulator
    {
        private abstract record Slot;
        private sealed record TextSlot(StringBuilder Text) : Slot
        {
            public List<TextCitation> Citations { get; } = [];
        }
        private sealed record ToolSlot(string Id, string Name, StringBuilder Args) : Slot;
        private sealed record ThinkingSlot(string Thinking, string? Signature) : Slot;
        private sealed record RawSlot(string ProviderId, string RawJson) : Slot;

        private readonly SortedDictionary<int, Slot> _slots = [];
        private readonly Dictionary<int, int> _providerIndexToSlot = [];
        private int _lastTextIndex = -1;

        public bool WantsToolUse { get; private set; }

        /// <summary>The vendor's stop reason for this call, when it reported one.</summary>
        public string? StopReason { get; private set; }

        /// <summary>False when the stream ended without the provider's completion event.</summary>
        public bool SawCompletion { get; private set; }

        /// <summary>True when the message carries no text block with anything in it.</summary>
        public bool HasVisibleText =>
            _slots.Values.OfType<TextSlot>().Any(slot => slot.Text.ToString().Trim().Length > 0);

        /// <summary>Usage of this single model call; input+output ≈ current context size.</summary>
        public Usage LastCallUsage { get; private set; } = Usage.Zero;

        public void AppendText(string delta, int? providerIndex = null)
        {
            if (providerIndex is { } index)
            {
                _lastTextIndex = Reserve(index);
                if (!_slots.TryGetValue(_lastTextIndex, out var existing) || existing is not TextSlot)
                    _slots[_lastTextIndex] = new TextSlot(new StringBuilder());
            }
            // Providers without block indexes stream plain text; merge consecutive
            // deltas into the current text slot.
            if (_lastTextIndex < 0 || _slots[_lastTextIndex] is not TextSlot slot)
            {
                _lastTextIndex = NextIndex();
                slot = new TextSlot(new StringBuilder());
                _slots[_lastTextIndex] = slot;
            }
            slot.Text.Append(delta);
        }

        public void AddCitation(int index, TextCitation citation)
        {
            var key = Reserve(index);
            if (!_slots.TryGetValue(key, out var existing) || existing is not TextSlot slot)
                _slots[key] = slot = new TextSlot(new StringBuilder());
            slot.Citations.Add(citation);
        }

        public void StartToolCall(int index, string id, string name) =>
            _slots[Reserve(index)] = new ToolSlot(id, name, new StringBuilder());

        public ToolInputDelta? AppendToolArguments(int index, string delta)
        {
            if (_providerIndexToSlot.TryGetValue(index, out int slotKey) && _slots[slotKey] is ToolSlot tool)
            {
                tool.Args.Append(delta);
                return new ToolInputDelta(tool.Id, tool.Name, delta);
            }
            return null;
        }

        public void AddThinking(int index, string thinking, string? signature) =>
            _slots[Reserve(index)] = new ThinkingSlot(thinking, signature);

        public void AddRawBlock(int index, string providerId, string rawJson) =>
            _slots[Reserve(index)] = new RawSlot(providerId, rawJson);

        public void Complete(bool wantsToolUse, Usage usage, string? stopReason = null)
        {
            WantsToolUse = wantsToolUse;
            LastCallUsage = usage;
            StopReason = stopReason;
            SawCompletion = true;
        }

        /// <summary>Maps the provider's block index onto a free slot key, keeping stream order.</summary>
        private int Reserve(int providerIndex)
        {
            if (_providerIndexToSlot.TryGetValue(providerIndex, out int existing))
                return existing;
            int slot = NextIndex();
            _providerIndexToSlot[providerIndex] = slot;
            return slot;
        }

        private int NextIndex() => _slots.Count == 0 ? 0 : _slots.Keys.Max() + 1;

        public ChatMessage ToMessage()
        {
            var blocks = new List<ContentBlock>();
            foreach (var (_, slot) in _slots)
            {
                switch (slot)
                {
                    case TextSlot text when text.Text.Length > 0:
                        blocks.Add(new TextBlock(text.Text.ToString())
                        {
                            Citations = text.Citations.Count == 0 ? null : text.Citations.ToArray(),
                        });
                        break;
                    case ToolSlot tool:
                        blocks.Add(new ToolCallBlock(tool.Id, tool.Name, tool.Args.ToString()));
                        break;
                    case ThinkingSlot thinking:
                        blocks.Add(new ThinkingBlock(thinking.Thinking, thinking.Signature));
                        break;
                    case RawSlot raw:
                        blocks.Add(new RawProviderBlock(raw.ProviderId, raw.RawJson));
                        break;
                }
            }
            if (blocks.Count == 0)
                blocks.Add(new TextBlock(""));
            return new ChatMessage(Role.Assistant, blocks);
        }
    }
}
