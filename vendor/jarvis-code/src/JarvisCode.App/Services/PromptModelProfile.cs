using System.Text.RegularExpressions;
using JarvisCode.Core.Agent;
using JarvisCode.Providers.Anthropic;

namespace JarvisCode.App.Services;

/// <summary>
/// Which of the reference's prompt sections a model receives.
/// </summary>
/// <remarks>
/// <para>
/// Measured on CLI 2.1.257 by capturing one request per model at a local
/// listener — thirteen of them, which is every model the reference's catalog
/// names and will still route. The prompt form and the bundled sections follow
/// the model's <i>family and version</i>: opus-4-8, opus-5 and the whole
/// fable/mythos family take the lean <c># Harness</c> document, and every
/// sonnet, every haiku, the Claude 3 generation and opus 4.0 through 4.7 take
/// the classic <c># System</c> one.
/// </para>
/// <para>
/// The reference's catalog does carry a <c>lean_prompt</c> capability, and it
/// is <b>not</b> what the client reads — <c>claude-mythos-5</c> declares an
/// empty capability array and is still sent the lean prompt <i>and</i>
/// <c># Communicating with the user</c>, exactly like <c>claude-fable-5</c>
/// beside it. <see cref="ReferenceModelCatalog"/> is therefore consulted for
/// the knowledge cutoff alone, which every capture agrees with; the prompt
/// gate is the reference's own name test.
/// </para>
/// </remarks>
public sealed record PromptModelProfile(
    /// <summary>The lean <c># Harness</c> prompt rather than the classic <c># System</c> one.</summary>
    bool Lean,
    /// <summary>Any fable or mythos model — the reference's <c>fable_5_mitigations</c> bundle.</summary>
    bool Fable,
    /// <summary>Exactly fable-5-1 or mythos-5-1 — the <c>fable_5_1_prompt_bundle</c>.</summary>
    bool Fable51,
    /// <summary>opus-5 — the <c>opus_5_prompt_bundle</c>.</summary>
    bool Opus5,
    /// <summary>opus-4-8: lean, but with none of the opus-5 bundle's sections.</summary>
    bool Opus48,
    /// <summary>The canonical <c>claude-{family}-{version}</c> the tables key on.</summary>
    string Canonical)
{
    /// <summary>
    /// The catalog row this model resolved to; empty when the reference has
    /// none for it. Internal because the catalog is reference data rather than
    /// app surface, and it answers the knowledge cutoff alone.
    /// </summary>
    internal ReferenceModelCatalog.Entry Catalog { get; init; }

    public static PromptModelProfile For(string modelId)
    {
        var canonical = ContextWindows.CanonicalModelName(modelId);
        var fable = canonical.StartsWith("claude-fable-", StringComparison.Ordinal) ||
                    canonical.StartsWith("claude-mythos-", StringComparison.Ordinal);
        return new PromptModelProfile(
            Lean: ReferencePromptBuilder.UsesLeanPrompt(modelId),
            Fable: fable,
            Fable51: canonical is "claude-fable-5-1" or "claude-mythos-5-1",
            Opus5: canonical == "claude-opus-5",
            Opus48: canonical == "claude-opus-4-8",
            Canonical: canonical)
        {
            Catalog = Lookup(modelId),
        };
    }

    /// <summary>
    /// The catalog row for a model id, or an empty entry when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ids arrive in every shape the providers use — a bare
    /// <c>claude-opus-5</c>, a date stamp (<c>claude-haiku-4-5-20251001</c>), a
    /// Bedrock inference profile (<c>us.anthropic.claude-opus-5-v1:0</c>), a
    /// Vertex revision (<c>claude-opus-5@20260115</c>) and a relay prefix
    /// (<c>llmapi/claude-opus-5</c>) — so the family and version are read out of
    /// the id the way the wire classifier reads them, and a trailing number
    /// above 20 is a date stamp rather than a minor version.
    /// </para>
    /// <para>
    /// A model outside the catalog — every model reached through another
    /// provider, and any Claude id newer than the measured build — resolves to
    /// the empty entry, and its prompt carries no cutoff line, which is what
    /// the reference does for an id it has no row for.
    /// </para>
    /// </remarks>
    internal static ReferenceModelCatalog.Entry Lookup(string? modelId)
    {
        foreach (var key in CatalogKeys(modelId ?? ""))
        {
            if (ReferenceModelCatalog.Models.TryGetValue(key, out var entry))
            {
                return entry;
            }
        }

        return default;
    }

    /// <summary>The catalog ids a model id could name, most specific first.</summary>
    private static IEnumerable<string> CatalogKeys(string modelId)
    {
        // The Claude 3 generation puts the version before the family
        // ("claude-3-5-sonnet"), which the modern pattern cannot read.
        var legacy = LegacyVersion.Match(modelId);
        if (legacy.Success)
        {
            yield return legacy.Groups[1].Success
                ? $"claude-3-{legacy.Groups[1].Value}-{legacy.Groups[2].Value}"
                : $"claude-3-{legacy.Groups[2].Value}";
            yield break;
        }

        var match = Version.Match(modelId);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var major) || major > 20)
        {
            yield break;
        }

        var family = match.Groups[1].Value;
        if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var minor) && minor <= 20)
        {
            yield return $"claude-{family}-{major}-{minor}";
            yield break;
        }

        // A major-only id is how the catalog spells the 5 generation
        // ("claude-opus-5"), and how a date-stamped 4.0 id reads once the stamp
        // is dropped ("claude-sonnet-4-20250514") — which the catalog files
        // under an explicit -0.
        yield return $"claude-{family}-{major}";
        yield return $"claude-{family}-{major}-0";
    }

    // Family + version out of ids like "claude-opus-4-6", "claude-opus-5",
    // "claude-sonnet-4-20250514", "us.anthropic.claude-opus-5-v1:0" and
    // "claude-opus-5@20260115". A second number above 20 is a date stamp, not a
    // minor version.
    private static readonly Regex Version = new(
        @"(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "claude-3-7-sonnet-20250219", "claude-3-5-sonnet-20241022", "claude-3-opus".
    private static readonly Regex LegacyVersion = new(
        @"claude-3(?:-(\d))?-(opus|sonnet|haiku)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The paragraph that follows <c># Harness</c>: fable-5-1 gets the one-line
    /// turn-updates rule, other fables the full <c># Communicating with the user</c>
    /// section, and every other lean model the single code-style line.
    /// </summary>
    public CommunicationSection Communication =>
        Fable51 ? CommunicationSection.TurnUpdates
        : Fable ? CommunicationSection.CommunicatingWithUser
        : CommunicationSection.CodeStyle;

    /// <summary>
    /// The third <c># Harness</c> bullet: opus-5 and the fables are told about
    /// mid-conversation system turns; opus-4-8 gets the plain
    /// <c>&lt;system-reminder&gt;</c> sentence.
    /// </summary>
    public bool HarnessMentionsSystemTurns => Opus5 || Fable;

    /// <summary>
    /// The "If what you find contradicts how it was described…" clause of the
    /// action-caution paragraph. Omitted for opus-5 alone.
    /// </summary>
    public bool ActionCautionCarriesContradictsClause => !Opus5;

    /// <summary>
    /// The model-identity paragraph: fable-5-1 and fable-5 each have their own;
    /// nothing else has one.
    /// </summary>
    public bool HasFableIdentity => Fable;

    /// <summary><c># Delivering work</c>: fable-5-1 and opus-5, not fable-5 or opus-4-8.</summary>
    public bool HasDeliveringWork => Fable51 || Opus5;

    /// <summary><c># Corrections</c>: opus-5 only.</summary>
    public bool HasCorrections => Opus5;

    /// <summary>The reduced-delegation sentence: opus-5 only.</summary>
    public bool HasReducedDelegation => Opus5;

    /// <summary><c># Writing for the user</c>: fable-5-1 only.</summary>
    public bool HasWritingForTheUser => Fable51;

    /// <summary>The "You are operating autonomously" append: every fable.</summary>
    public bool HasAutonomyAppend => Fable;

    /// <summary>
    /// The <c># Reporting outcomes</c> block the reference sends as its own system
    /// entry ahead of the prompt: fable/mythos 5.1 only, and only while the
    /// attribution header is on.
    /// </summary>
    public bool EligibleForReportingOutcomes => Fable51;

    /// <summary>
    /// Whether the plan-mode workflow tells the model to fan out Explore and
    /// Plan agents. opus-5 gets the reference's "no_nudges" workflow — read the
    /// files directly, design the plan itself — while every other model gets the
    /// agent phases (measured in plan mode on opus-5, fable-5-1 and opus-4-5).
    /// </summary>
    public bool PlanWorkflowUsesAgents => !Opus5;

    /// <summary>
    /// The bash-first bypass notice is forced on for fable-5-1 (the reference's
    /// <c>uKt()</c>); every other model's copy sits behind a cohort flag that
    /// ships off, and a clean opus-5 bypass run sends none.
    /// </summary>
    public bool BypassNoticeForced => Fable51;

    /// <summary>
    /// The task-board tools (TaskCreate/TaskGet/TaskList/TaskUpdate) and the
    /// <c>Use TaskCreate</c> line: every model without the mid-conversation
    /// system role gets them; opus-5, opus-4-8, sonnet-5 and the fable/mythos
    /// family do not. Measured across thirteen captures as 26 tools against 30,
    /// and the wire classifier answers all thirteen — including mythos-5, whose
    /// catalog row declares no <c>mid_conv_system</c> and which is nonetheless
    /// sent the beta and no board.
    /// </summary>
    public bool TakesTaskBoard => !AnthropicEffort.SupportsHarnessSystemTurn(Canonical.Length > 0 ? Canonical : "");

    /// <summary>
    /// The knowledge cutoff the reference prints for this model, from its own
    /// catalog; null for a model it has none for (the Claude 3 generation and
    /// unknown ids), whose prompt carries no such line.
    /// </summary>
    public string? KnowledgeCutoff => Catalog.KnowledgeCutoff;
}

/// <summary>The three shapes the communication section takes.</summary>
public enum CommunicationSection
{
    /// <summary>"Write code that reads like the surrounding code…" alone.</summary>
    CodeStyle,

    /// <summary>The one-paragraph "Before you start, say in a line…" rule (fable-5-1).</summary>
    TurnUpdates,

    /// <summary>The six-paragraph <c># Communicating with the user</c> section (fable-5).</summary>
    CommunicatingWithUser,
}
