using JarvisCode.Core.Models;

namespace JarvisCode.Core.Agent;

/// <summary>The five shapes the reference's token-budget block takes.</summary>
public enum TotalTokensMode
{
    Off,

    /// <summary>The literal word <c>Infinite</c>.</summary>
    Infinite,

    /// <summary>A constant 5000000.</summary>
    Fixed,

    /// <summary>The model's context window, less the current context in the live block.</summary>
    Countdown,

    /// <summary>A 15000000 budget, less what the task has consumed in the live block. The default.</summary>
    PaddedCountdown,
}

/// <summary>
/// The reference's <c>&lt;total_tokens&gt;N tokens left&lt;/total_tokens&gt;</c>
/// block, per agent: a static form at the end of the system prompt, and a live
/// form after every batch of tool results and after each regular user prompt.
/// </summary>
/// <remarks>
/// Ported from CLI 2.1.257's own class (its <c>YYt</c>), read out of the binary:
/// three per-agent numbers — <c>rolled</c>, <c>anchor</c>, <c>prev</c> — with
/// <c>rollOverContext(agent, tokens)</c> adding the compacted context to
/// <c>rolled</c>, <c>reanchorTaskBudget(agent, tokens)</c> setting
/// <c>anchor = rolled + tokens</c> and <c>prev = 0</c> at each user turn, and
/// <c>cumulativeUsed(agent, current) = prev = max(prev, rolled + current − anchor)</c>.
/// The budget is therefore a <i>task</i> budget: it re-arms on every prompt and
/// counts the context the task has grown by since.
/// <para>
/// The system prompt's copy is static — the budget itself in padded-countdown
/// mode, the model's window in countdown mode — so the cached prefix never
/// changes; only the blocks that follow tool results and user prompts carry the
/// live number. Measured against a three-request capture: 15000000 in the
/// prompt, 14999950 after a first call scripted to report fifty tokens.
/// </para>
/// <para>
/// The mode resolves from <c>CLAUDE_CODE_TOTAL_TOKENS_REMINDER</c>, then the
/// <c>totalTokensReminder</c> setting, then the default <c>padded-countdown</c>;
/// the budget from <c>CLAUDE_CODE_TOTAL_TOKENS_REMINDER_BUDGET</c>, the setting,
/// then 15000000; and whether the block also follows a regular user prompt from
/// <c>CLAUDE_CODE_TOTAL_TOKENS_REMINDER_AFTER_USER_TURN</c>, defaulting to on.
/// </para>
/// </remarks>
public sealed class TotalTokensReminder
{
    public const long DefaultBudget = 15_000_000;
    public const long FixedValue = 5_000_000;

    public const string ModeVariable = "CLAUDE_CODE_TOTAL_TOKENS_REMINDER";
    public const string BudgetVariable = "CLAUDE_CODE_TOTAL_TOKENS_REMINDER_BUDGET";
    public const string AfterUserTurnVariable = "CLAUDE_CODE_TOTAL_TOKENS_REMINDER_AFTER_USER_TURN";

    private long _rolled;
    private long _anchor;
    private long _previous;
    private long _current;

    public TotalTokensReminder(TotalTokensMode mode, long budget = DefaultBudget, bool afterUserTurn = true)
    {
        Mode = mode;
        Budget = budget > 0 ? budget : DefaultBudget;
        AfterUserTurn = afterUserTurn;
    }

    public TotalTokensMode Mode { get; }

    public long Budget { get; }

    /// <summary>Whether the live block also follows each regular user prompt.</summary>
    public bool AfterUserTurn { get; }

    /// <summary>The context size the agent's latest call reported.</summary>
    public long Current => _current;

    /// <summary>True when the block is emitted at all.</summary>
    public bool Enabled => Mode != TotalTokensMode.Off;

    /// <summary>
    /// The mode the reference resolves: environment, then setting, then the
    /// default. An unrecognised spelling reads as the default rather than as
    /// off, which is the reference's own fallback.
    /// </summary>
    public static TotalTokensMode ResolveMode(string? environmentValue, string? settingValue) =>
        ParseMode(environmentValue) ?? ParseMode(settingValue) ?? TotalTokensMode.PaddedCountdown;

    public static TotalTokensMode? ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => TotalTokensMode.Off,
        "infinite" => TotalTokensMode.Infinite,
        "fixed" => TotalTokensMode.Fixed,
        "countdown" => TotalTokensMode.Countdown,
        "padded-countdown" => TotalTokensMode.PaddedCountdown,
        _ => null,
    };

    /// <summary>The budget the reference resolves: environment, then setting, then 15000000.</summary>
    public static long ResolveBudget(string? environmentValue, long? settingValue)
    {
        if (long.TryParse(environmentValue, out var fromEnvironment) && fromEnvironment > 0)
        {
            return fromEnvironment;
        }

        return settingValue is > 0 ? settingValue.Value : DefaultBudget;
    }

    /// <summary>Whether the block follows user prompts: environment, then setting, then true.</summary>
    public static bool ResolveAfterUserTurn(string? environmentValue, bool? settingValue)
    {
        if (environmentValue is { Length: > 0 })
        {
            return environmentValue.Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off");
        }

        return settingValue ?? true;
    }

    /// <summary>Records the context size a completed call reported: everything in, plus what came out.</summary>
    public void Observe(Usage usage) =>
        _current = usage.TotalInputTokens + usage.OutputTokens;

    /// <summary>Records a context size directly (a host that already tracks it).</summary>
    public void Observe(long contextTokens) => _current = Math.Max(0, contextTokens);

    /// <summary>
    /// A compaction: the reference banks the context that was compacted away so
    /// the count keeps rising across the summary rather than resetting with the
    /// shrunken context.
    /// </summary>
    public void RollOverContext(long compactedContextTokens) =>
        _rolled += Math.Max(0, compactedContextTokens);

    /// <summary>
    /// A new user turn: the task budget re-arms. The anchor moves to the context
    /// the turn starts on and the running maximum resets, exactly as the
    /// reference's <c>reanchorTaskBudget</c> does.
    /// </summary>
    public void ReanchorTaskBudget(long contextTokens)
    {
        _anchor = _rolled + Math.Max(0, contextTokens);
        _previous = 0;
    }

    /// <summary>The reference's <c>cumulativeUsed</c>: monotonic within a task.</summary>
    public long CumulativeUsed(long current)
    {
        var used = _rolled + current - _anchor;
        _previous = Math.Max(_previous, used);
        return _previous;
    }

    /// <summary>
    /// The block as it ends the system prompt: the static budget (or window),
    /// so the cached prefix is stable. Null when the mode is off.
    /// </summary>
    public string? RenderStatic(long contextWindow = 0) => Mode switch
    {
        TotalTokensMode.Off => null,
        TotalTokensMode.Infinite => Block("Infinite"),
        TotalTokensMode.Fixed => Block(FixedValue.ToString()),
        TotalTokensMode.Countdown => Block(Math.Max(0, contextWindow).ToString()),
        _ => Block(Budget.ToString()),
    };

    /// <summary>
    /// The block as it follows tool results and user prompts: the live number,
    /// computed from the latest observed context. Null when the mode is off.
    /// </summary>
    public string? RenderLive(long contextWindow = 0) => Mode switch
    {
        TotalTokensMode.Off => null,
        TotalTokensMode.Infinite => Block("Infinite"),
        TotalTokensMode.Fixed => Block(FixedValue.ToString()),
        TotalTokensMode.Countdown => Block(Math.Max(0, contextWindow - _current).ToString()),
        _ => Block(Math.Max(0, Budget - CumulativeUsed(_current)).ToString()),
    };

    private static string Block(string value) => $"<total_tokens>{value} tokens left</total_tokens>";
}
