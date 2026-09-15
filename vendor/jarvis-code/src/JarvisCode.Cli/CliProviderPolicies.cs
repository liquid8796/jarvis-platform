using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Validation;

namespace JarvisCode.Cli;

internal sealed record CliModelUsage(ModelInfo Model, Usage Usage, decimal? CostUsd,
    int Calls, int WebSearchRequests);

internal sealed record CliUsageSnapshot(Usage Usage, decimal? CostUsd, int Calls,
    long DurationApiMs, IReadOnlyList<CliModelUsage> Models, int UnreportedCalls = 0);

/// <summary>
/// One ledger for the whole print session, including retries, subagents,
/// compaction and model-backed hooks. Costs use measured tokens and configured
/// list prices; an unknown price stays null, never a fabricated zero.
/// </summary>
internal sealed class CliUsageLedger(Func<string, string, ModelInfo?> resolveModel, decimal? maximumUsd)
{
    private readonly object _sync = new();
    private readonly Dictionary<(string, string), CliModelUsage> _models = [];
    private readonly SemaphoreSlim _budgetCalls = new(1, 1);
    private long _durationApiMs;
    private int _startedCalls;
    private int _unreportedCalls;
    public bool BudgetReached { get; private set; }
    public string? BudgetDetail { get; private set; }

    internal static decimal? Price(ModelInfo model, Usage usage, string? cacheTtl)
    {
        if (usage.IsEstimated || !double.IsFinite(model.InputPricePerMTok) || !double.IsFinite(model.OutputPricePerMTok) ||
            model.InputPricePerMTok <= 0 || model.OutputPricePerMTok <= 0 || usage.InputTokens < 0 ||
            usage.OutputTokens < 0 || usage.CacheReadInputTokens < 0 || usage.CacheCreationInputTokens < 0)
            return null;
        try
        {
        var input = (decimal)model.InputPricePerMTok / 1_000_000m;
        var output = (decimal)model.OutputPricePerMTok / 1_000_000m;
        // Cache buckets are separate on the Anthropic wire, including its
        // Bedrock/Vertex routes. Other adapters leave them zero when their
        // provider's input usage already includes cached tokens.
        var writeMultiplier = cacheTtl == "1h" ? 2m : 1.25m;
        return usage.InputTokens * input + usage.OutputTokens * output +
               usage.CacheReadInputTokens * input * .1m +
               usage.CacheCreationInputTokens * input * writeMultiplier;
        }
        catch (OverflowException) { return null; }
    }

    public void RequirePrice(string providerId, string modelId)
    {
        if (maximumUsd is null)
            return;
        var model = resolveModel(providerId, modelId);
        if (model is null || Price(model, Usage.Zero, null) is null)
            throw new CliError($"Cannot enforce --max-budget-usd: configure input and output prices for " +
                $"{providerId}/{modelId} in Settings before starting this run.");
    }

    private void CheckBudget()
    {
        lock (_sync)
        {
            if (maximumUsd is not { } maximum)
                return;
            if (_unreportedCalls > 0 || _models.Values.Any(row => row.CostUsd is null))
            {
                BudgetReached = true;
                BudgetDetail = "API budget cannot be verified after a provider request ended without reporting usage. " +
                    "No further model calls will be started; unreported charges are not treated as zero.";
                throw new CliError(BudgetDetail);
            }
            if (_models.Values.Sum(x => x.CostUsd ?? 0) < maximum) return;
            BudgetReached = true;
            BudgetDetail = $"Maximum API budget reached (${maximum:0.########} USD). " +
                "No further model calls will be started. Cost is calculated from reported tokens and configured list prices.";
            throw new CliError(BudgetDetail);
        }
    }

    public CliUsageSnapshot Snapshot()
    {
        lock (_sync)
        {
            var rows = _models.Values.ToArray();
            return new CliUsageSnapshot(rows.Aggregate(Usage.Zero, (sum, row) => sum.Add(row.Usage)),
                _unreportedCalls > 0 || rows.Any(row => row.CostUsd is null) ? null : rows.Sum(row => row.CostUsd ?? 0),
                _startedCalls, _durationApiMs, rows, _unreportedCalls);
        }
    }

    public async IAsyncEnumerable<ProviderEvent> RunAsync(ILlmProvider inner, LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Serial admission with an active dollar ceiling prevents parallel
        // agents from all passing the same remaining-balance check. A provider
        // reports the charge only after a response, so the final call can cross
        // the ceiling; it is then the last call, including on fallback paths.
        if (maximumUsd is not null)
            await _budgetCalls.WaitAsync(cancellationToken);
        var clock = Stopwatch.StartNew();
        bool initiated = false;
        bool accounted = false;
        try
        {
            if (maximumUsd is not null && !ProviderCapabilities.For(inner).ReportsExactUsage)
                throw new CliError("Cannot enforce --max-budget-usd: this browser provider reports estimated tokens, not billable API usage.");
            RequirePrice(inner.Id, request.ModelId);
            CheckBudget();
            int searches = 0;
            lock (_sync) _startedCalls++;
            initiated = true;
            await foreach (var evt in inner.StreamChatAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (evt is ServerToolNoticeEvent { ToolName: "web_search" })
                    searches++;
                if (evt is ResponseCompletedEvent completed && !accounted)
                {
                    accounted = true;
                    var model = resolveModel(inner.Id, request.ModelId) ??
                        new ModelInfo(inner.Id, request.ModelId, request.ModelId, 0);
                    var cost = Price(model, completed.Usage, request.CacheTtl);
                    lock (_sync)
                    {
                        var key = (inner.Id, request.ModelId);
                        if (_models.TryGetValue(key, out var previous))
                            _models[key] = previous with
                            {
                                Usage = previous.Usage.Add(completed.Usage),
                                CostUsd = previous.CostUsd is { } before && cost is { } now ? before + now : null,
                                Calls = previous.Calls + 1,
                                WebSearchRequests = previous.WebSearchRequests + searches,
                            };
                        else
                            _models[key] = new CliModelUsage(model, completed.Usage, cost, 1, searches);
                    }
                    // Do not execute a tool response after its call spent the
                    // budget. All already reported tokens remain in the ledger.
                    CheckBudget();
                }
                yield return evt;
            }
        }
        finally
        {
            lock (_sync)
            {
                _durationApiMs += clock.ElapsedMilliseconds;
                if (initiated && !accounted) _unreportedCalls++;
            }
            if (maximumUsd is not null)
                _budgetCalls.Release();
        }
    }
}

internal sealed class CliMeteredProvider(ILlmProvider inner, CliUsageLedger ledger) : ILlmProvider, IDecoratedProvider
{
    public ILlmProvider InnerProvider => inner;
    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public bool RequiresApiKey => inner.RequiresApiKey;
    public IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, CancellationToken cancellationToken) =>
        ledger.RunAsync(inner, request, cancellationToken);
}

/// <summary>Resolve through the live registry so custom-provider reloads survive decoration.</summary>
internal sealed class CliMeteredRegistry(IProviderRegistry inner, CliUsageLedger ledger) : IProviderRegistry
{
    public IReadOnlyList<ILlmProvider> All => [.. inner.All.Select(provider => new CliMeteredProvider(provider, ledger))];
    public ILlmProvider Get(string providerId) => new CliMeteredProvider(inner.Get(providerId), ledger);
}

internal sealed class CliStructuredOutput
{
    public const int MaximumAttempts = 3;
    public JsonNode Schema { get; }
    public string? FailureDetail { get; private set; }
    public JsonNode? Value { get; private set; }
    public bool HasValue { get; private set; }

    public CliStructuredOutput(string json)
    {
        try
        {
            Schema = JsonNode.Parse(json) ?? throw new CliError("--json-schema cannot be null.");
        }
        catch (JsonException ex)
        {
            throw new CliError($"Invalid --json-schema JSON: {ex.Message}");
        }
        var verdict = JsonSchemaValidation.ValidateSchema(Schema);
        if (!verdict.IsValid)
            throw new CliError($"Invalid --json-schema: {verdict.Error}");
    }

    public void BeginTurn()
    {
        FailureDetail = null;
        Value = null;
        HasValue = false;
    }

    public ILlmProvider Wrap(ILlmProvider inner, Action<string>? notice = null,
        Func<LlmRequest, bool>? applies = null) => new Provider(inner, this, notice, applies);

    private sealed class Provider(ILlmProvider inner, CliStructuredOutput policy, Action<string>? notice,
        Func<LlmRequest, bool>? applies) : ILlmProvider, IDecoratedProvider
    {
        public ILlmProvider InnerProvider => inner;
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public bool RequiresApiKey => inner.RequiresApiKey;

        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (applies?.Invoke(request) == false)
            {
                await foreach (var evt in inner.StreamChatAsync(request, cancellationToken).WithCancellation(cancellationToken))
                    yield return evt;
                yield break;
            }
            var next = request with
            {
                SystemPrompt = request.SystemPrompt + "\n\n# Required final response format\n" +
                    "You may use tools while working. When finished, return only a JSON value satisfying this " +
                    "JSON Schema, without markdown fences or extra prose:\n" + policy.Schema.ToJsonString(),
            };
            for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                var buffered = new List<ProviderEvent>();
                var text = new StringBuilder();
                ResponseCompletedEvent? completion = null;
                await foreach (var evt in inner.StreamChatAsync(next, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    buffered.Add(evt);
                    if (evt is TextDeltaEvent delta) text.Append(delta.Delta);
                    if (evt is ResponseCompletedEvent done) completion = done;
                }

                // Tool turns and provider refusals retain the ordinary recovery
                // and tool-execution path. Only a completed final answer is
                // subject to the caller's output schema.
                if (completion is null || completion.WantsToolUse ||
                    completion.StopReason is "refusal" or "content_filter" or "max_tokens" or "length")
                {
                    foreach (var evt in buffered) yield return evt;
                    yield break;
                }

                string? error;
                JsonNode? value = null;
                try
                {
                    value = JsonNode.Parse(text.ToString());
                    var verdict = JsonSchemaValidation.ValidateInstance(policy.Schema, value);
                    error = verdict.IsValid ? null : verdict.Error ?? "The response does not satisfy the schema.";
                }
                catch (JsonException ex) { error = ex.Message; }

                if (error is null)
                {
                    policy.Value = value;
                    policy.HasValue = true;
                    foreach (var evt in buffered) yield return evt;
                    yield break;
                }
                // A repair has no tools and must not become a separate autonomous
                // browser turn. Validate the requested turn, but leave correction
                // to the caller when tool-free inference cannot be enforced.
                if (!ProviderCapabilities.For(inner).SupportsToolFreeInference)
                {
                    policy.FailureDetail = "Structured output did not satisfy --json-schema: " +
                        error[..Math.Min(error.Length, 1000)] +
                        " No correction request was sent because this provider cannot guarantee tool-free inference.";
                    throw new CliError(policy.FailureDetail);
                }
                if (attempt == MaximumAttempts)
                {
                    policy.FailureDetail = $"Structured output did not satisfy --json-schema after {MaximumAttempts} attempts: " +
                        error[..Math.Min(error.Length, 1000)];
                    throw new CliError(policy.FailureDetail);
                }
                notice?.Invoke($"Structured output validation failed; retrying ({attempt + 1}/{MaximumAttempts}).");
                next = next with
                {
                    Messages = [.. next.Messages,
                        new ChatMessage(Role.Assistant, [new TextBlock(text.ToString())]),
                        ChatMessage.FromUserText("Your final response failed JSON Schema validation: " +
                            error[..Math.Min(error.Length, 1000)] +
                            "\nReturn a corrected JSON value only. Do not repeat completed tool actions.") with { IsMeta = true }],
                    Tools = [],
                };
            }
        }
    }
}

/// <summary>Forwards provider deltas to the print stream without changing the engine event model.</summary>
internal sealed class CliObservedProvider(ILlmProvider inner, Action<LlmRequest> started,
    Action<ProviderEvent> observed, Func<LlmRequest, bool>? applies = null) : ILlmProvider, IDecoratedProvider
{
    public ILlmProvider InnerProvider => inner;
    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public bool RequiresApiKey => inner.RequiresApiKey;
    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var active = applies?.Invoke(request) != false;
        if (active) started(request);
        await foreach (var evt in inner.StreamChatAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            if (active) observed(evt);
            yield return evt;
        }
    }
}
