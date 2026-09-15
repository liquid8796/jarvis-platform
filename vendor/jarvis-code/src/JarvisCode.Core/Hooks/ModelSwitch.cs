using System.Text.Json.Nodes;
using JarvisCode.Core.Models;

namespace JarvisCode.Core.Hooks;

/// <summary>
/// What moved a session from one model to another. The names are the reference's
/// own (its kQ map), because they ride the hook payload's "source" field and a
/// hook written against the reference must read the same word here.
/// </summary>
public enum ModelSwitchSource
{
    /// <summary>A typed command asked for the model by name.</summary>
    Command,

    /// <summary>An interactive model picker.</summary>
    Picker,

    /// <summary>A headless set_model, from an SDK or a remote client.</summary>
    Sdk,

    /// <summary>An automatic fallback or other programmatic change.</summary>
    Auto,

    /// <summary>The model was restored while resuming a session.</summary>
    Resume,
}

/// <summary>
/// One model switch, described the way the reference's PreModelSwitch and
/// PostModelSwitch hooks describe it.
///
/// The payload exists so a hook can answer a question that is expensive to get
/// wrong: switching model forfeits the prompt cache, so the hook is told how
/// much context the next request would have to re-send and whether that cache
/// is likely still warm. Two of the reference's nine fields are not measurable
/// here and say so rather than guess — see <see cref="EstimatedCacheWriteUsd"/>.
/// </summary>
public sealed record ModelSwitch(
    string FromModel,
    string ToModel,
    string? RequestedModel,
    ModelSwitchSource Source,
    long ContextTokens,
    bool PromptCacheWarm)
{
    /// <summary>
    /// The cache lifetime this app asks for. Its Anthropic adapter sends the
    /// plain ephemeral cache_control and never the extended one, so the answer
    /// is the API's default five minutes rather than a setting to read.
    /// </summary>
    public const string CacheTtl = "5m";

    /// <summary>
    /// The same number as <see cref="CacheTtl"/>, as a span: the payload wants
    /// the word and the warmth test wants the duration, and they must not drift.
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Estimated cost of a five-minute Anthropic cache write using the selected
    /// model's configured input list price. Unknown prices and other provider
    /// cache tariffs remain null; this is never a reported charge or invoice.
    /// </summary>
    public decimal? EstimatedCacheWriteUsd { get; init; }

    /// <summary>States whether the estimate has a configured, supported price source.</summary>
    public string Pricing => EstimatedCacheWriteUsd is null ? "unavailable" : "configured-list-price";

    /// <summary>
    /// Whether this switch runs the blocking half. The reference gives
    /// PreModelSwitch three sources and PostModelSwitch five: a switch that
    /// nobody chose — an automatic fallback, or a model restored with a session
    /// — is reported after the fact and is not offered for refusal.
    /// </summary>
    public bool RunsPreHooks =>
        Source is ModelSwitchSource.Command or ModelSwitchSource.Picker or ModelSwitchSource.Sdk;

    /// <summary>
    /// The switch a session is about to make, or null when what is about to
    /// happen is not one: picking the model already running, or naming the first
    /// model a session ever had. Neither is something a hook could meaningfully
    /// refuse — the first would be refusing to stay put, and the second would
    /// arrive with an empty from_model the reference's schema never produces.
    /// </summary>
    public static ModelSwitch? From(
        string? currentModelId,
        string toModelId,
        string? requestedModel,
        ModelSwitchSource source,
        long contextTokens,
        DateTimeOffset? lastResponseAt,
        DateTimeOffset now,
        ModelInfo? targetModel = null)
    {
        if (string.IsNullOrEmpty(currentModelId) ||
            string.Equals(currentModelId, toModelId, StringComparison.Ordinal))
        {
            return null;
        }

        return new ModelSwitch(
            currentModelId,
            toModelId,
            requestedModel,
            source,
            contextTokens,
            PromptCacheWarm: lastResponseAt is { } at && now - at < CacheLifetime)
        { EstimatedCacheWriteUsd = EstimateCacheWrite(contextTokens, targetModel) };
    }

    private static decimal? EstimateCacheWrite(long contextTokens, ModelInfo? model)
    {
        if (contextTokens < 0 || model is null || model.ProviderId != "anthropic" ||
            !double.IsFinite(model.InputPricePerMTok) || model.InputPricePerMTok <= 0) return null;
        try { return contextTokens * (decimal)model.InputPricePerMTok * 1.25m / 1_000_000m; }
        catch (OverflowException) { return null; }
    }

    /// <summary>The wire spelling of a source.</summary>
    public static string SourceName(ModelSwitchSource source) => source switch
    {
        ModelSwitchSource.Command => "command",
        ModelSwitchSource.Picker => "picker",
        ModelSwitchSource.Sdk => "sdk",
        ModelSwitchSource.Auto => "auto",
        ModelSwitchSource.Resume => "resume",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "unknown model-switch source"),
    };

    /// <summary>The hook's stdin payload, minus the "event" field the runner adds.</summary>
    public JsonObject ToPayload() => new()
    {
        ["from_model"] = FromModel,
        ["to_model"] = ToModel,
        ["requested_model"] = RequestedModel,
        ["source"] = SourceName(Source),
        ["context_tokens"] = ContextTokens,
        ["prompt_cache_warm"] = PromptCacheWarm,
        ["cache_ttl"] = CacheTtl,
        ["estimated_cache_write_usd"] = EstimatedCacheWriteUsd,
        ["pricing"] = Pricing,
    };
}
