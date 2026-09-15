using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Promise;
using Jint.Runtime;

namespace JarvisCode.Core.Agent;

/// <summary>One <c>agent()</c> call a workflow script makes.</summary>
public sealed record WorkflowAgentRequest(
    string Prompt,
    string? Label = null,
    string? Phase = null,
    JsonNode? Schema = null,
    string? AgentType = null,
    string? Model = null,
    /// <summary>Per-call reasoning effort ('low'…'max'); null inherits the session's.</summary>
    string? Effort = null,
    /// <summary>"worktree" runs this agent in a fresh checkout of its own.</summary>
    string? Isolation = null);

/// <summary>What one workflow agent returned.</summary>
public sealed record WorkflowAgentResult(
    bool Success,
    string Text,
    JsonNode? Structured = null,
    long OutputTokens = 0,
    string? Error = null);

/// <summary>Runs the subagent behind a script's <c>agent()</c> call.</summary>
public interface IWorkflowAgentRunner
{
    Task<WorkflowAgentResult> RunAgentAsync(WorkflowAgentRequest request, CancellationToken cancellationToken);
}

/// <summary>The turn's token target, shared across the main loop and every workflow.</summary>
public sealed class WorkflowBudget(long? total, Func<long>? spentSoFar = null)
{
    private long _spentHere;

    /// <summary>Null when the user set no target; then remaining() is Infinity.</summary>
    public long? Total { get; } = total;

    public long Spent() => (spentSoFar?.Invoke() ?? 0) + Interlocked.Read(ref _spentHere);

    public double Remaining() => Total is null ? double.PositiveInfinity : Math.Max(0, Total.Value - Spent());

    public void Charge(long outputTokens) => Interlocked.Add(ref _spentHere, outputTokens);

    /// <summary>True once the hard ceiling is reached; further agent() calls throw.</summary>
    public bool Exhausted => Total is not null && Spent() >= Total.Value;
}

public sealed record WorkflowRunOptions
{
    public required string Script { get; init; }

    /// <summary>Passed to the script as the <c>args</c> global, verbatim.</summary>
    public JsonNode? Args { get; init; }

    /// <summary>Name of a saved workflow, when this run came from one.</summary>
    public string? Name { get; init; }

    /// <summary>Where journals live; null disables journalling (and resume).</summary>
    public string? JournalDirectory { get; init; }

    /// <summary>Replay agent results from this earlier run where the calls match.</summary>
    public string? ResumeFromRunId { get; init; }

    public WorkflowBudget? Budget { get; init; }

    /// <summary>Runs a nested workflow (one level only); null forbids nesting.</summary>
    public Func<string, JsonNode?, CancellationToken, Task<WorkflowOutcome>>? RunNested { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The script's declared <c>meta.phases</c>. Like the reference, every one
    /// is announced before the body runs, so the groups exist even for phases
    /// no agent ever reaches.
    /// </summary>
    public IReadOnlyList<WorkflowPhaseDeclaration>? Phases { get; init; }

    /// <summary>Live progress sink — phase, agent and log entries as they happen.</summary>
    public Action<WorkflowProgressEntry>? Progress { get; init; }
}

public sealed record WorkflowOutcome(
    bool Success,
    string RunId,
    JsonNode? Result,
    string? Error,
    IReadOnlyList<string> Log,
    int AgentCount,
    int CachedAgentCount,
    long OutputTokens,
    TimeSpan Duration);

/// <summary>
/// Append-only record of a run's agent calls, so a later run can replay the
/// results of calls it makes again. One JSON object per line, as the reference's
/// <c>journal.jsonl</c>.
/// </summary>
public sealed class WorkflowJournal
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<JsonNode?>> _replay = new(StringComparer.Ordinal);

    private WorkflowJournal(string? path) => _path = path;

    public static WorkflowJournal ForRun(string? directory, string runId)
    {
        if (string.IsNullOrEmpty(directory))
            return new WorkflowJournal(null);
        var runDirectory = Path.Combine(directory, runId);
        Directory.CreateDirectory(runDirectory);
        return new WorkflowJournal(Path.Combine(runDirectory, "journal.jsonl"));
    }

    /// <summary>Loads an earlier run's entries as the replay cache for this run.</summary>
    public static WorkflowJournal Replaying(string? directory, string runId, string resumeFromRunId)
    {
        var journal = ForRun(directory, runId);
        if (string.IsNullOrEmpty(directory))
            return journal;
        var source = Path.Combine(directory, resumeFromRunId, "journal.jsonl");
        if (!File.Exists(source))
            return journal;
        foreach (var line in File.ReadLines(source))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonNode.Parse(line) is not JsonObject entry)
                    continue;
                var key = entry["key"]?.GetValue<string>();
                if (key is null)
                    continue;
                if (!journal._replay.TryGetValue(key, out var queue))
                    journal._replay[key] = queue = new Queue<JsonNode?>();
                queue.Enqueue(entry["result"]?.DeepClone());
            }
            catch (JsonException)
            {
                // A truncated journal line just means less replay, never a failed run.
            }
        }

        return journal;
    }

    /// <summary>Takes a cached result for this call key, if the resumed run had one.</summary>
    public bool TryReplay(string key, out JsonNode? result)
    {
        lock (_gate)
        {
            if (_replay.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                result = queue.Dequeue();
                return true;
            }
        }

        result = null;
        return false;
    }

    public void Record(string key, WorkflowAgentRequest request, JsonNode? result)
    {
        if (_path is null)
            return;
        var entry = new JsonObject
        {
            ["key"] = key,
            ["label"] = request.Label,
            ["phase"] = request.Phase,
            ["prompt"] = request.Prompt.Length > 2000 ? request.Prompt[..2000] : request.Prompt,
            ["result"] = result?.DeepClone(),
        };
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, entry.ToJsonString() + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Journalling is best-effort; losing it only costs resume.
            }
        }
    }
}

/// <summary>
/// Runs a dynamic workflow: a deterministic JavaScript script that orchestrates
/// subagents. Ported against the reference CLI's Workflow tool — same script
/// API (<c>agent</c>, <c>parallel</c>, <c>pipeline</c>, <c>log</c>, <c>phase</c>,
/// <c>args</c>, <c>budget</c>, <c>workflow</c>), the same caps, and the same
/// determinism guards, whose messages are the reference's own.
/// </summary>
public sealed class WorkflowEngine
{
    /// <summary>Concurrent agent() calls per workflow, as the reference computes it.</summary>
    public static int ConcurrencyLimit => Math.Max(1, Math.Min(16, Environment.ProcessorCount - 2));

    /// <summary>Runaway-loop backstop on the total agents one run may spawn.</summary>
    public const int MaxAgentsPerRun = 1000;

    /// <summary>Largest list a single parallel()/pipeline() call accepts.</summary>
    public const int MaxItemsPerCall = 4096;

    public const string DateGuardMessage =
        "Date.now() / new Date() are unavailable in workflow scripts (breaks resume). " +
        "Stamp results after the workflow returns, or pass timestamps via args.";

    public const string RandomGuardMessage =
        "Math.random() is unavailable in workflow scripts (breaks resume). " +
        "For N independent samples, include the index in the agent label or prompt.";

    /// <summary>
    /// The realm prelude: the reference's Date/Math.random shims, then
    /// parallel() and pipeline() built on the host's agent().
    /// </summary>
    private const string Prelude = """
        (() => {
          const NOW_ERR = __NOW_ERR__;
          const RANDOM_ERR = __RANDOM_ERR__;
          Math.random = function random() { throw new Error(RANDOM_ERR) };
          const RealDate = Date;
          RealDate.now = function now() { throw new Error(NOW_ERR) };
          function ShimDate(...a) {
            if (!new.target) throw new Error(NOW_ERR);
            if (a.length === 0) throw new Error(NOW_ERR);
            return Reflect.construct(RealDate, a, new.target);
          }
          ShimDate.now = RealDate.now;
          ShimDate.parse = RealDate.parse;
          ShimDate.UTC = RealDate.UTC;
          ShimDate.prototype = RealDate.prototype;
          RealDate.prototype.constructor = ShimDate;
          globalThis.Date = ShimDate;

          const MAX_ITEMS = __MAX_ITEMS__;
          globalThis.parallel = function parallel(thunks) {
            if (!Array.isArray(thunks))
              throw new Error("parallel() expects an array of functions");
            if (thunks.length > MAX_ITEMS)
              throw new Error("parallel() accepts at most " + MAX_ITEMS + " items, got " + thunks.length);
            return Promise.all(thunks.map(thunk => {
              try {
                return Promise.resolve(thunk()).then(v => v, () => null);
              } catch (e) {
                return null;
              }
            }));
          };
          globalThis.pipeline = function pipeline(items, ...stages) {
            if (!Array.isArray(items))
              throw new Error("pipeline() expects an array of items");
            if (items.length > MAX_ITEMS)
              throw new Error("pipeline() accepts at most " + MAX_ITEMS + " items, got " + items.length);
            return Promise.all(items.map(async (item, index) => {
              let value = item;
              for (const stage of stages) {
                try {
                  value = await stage(value, item, index);
                } catch (e) {
                  return null;
                }
              }
              return value;
            }));
          };
        })()
        """;

    /// <summary>Rejects scripts the reference's compiler rejects, with its wording.</summary>
    public static string? Reject(string script)
    {
        if (script.Contains("import(", StringComparison.Ordinal))
            return "SyntaxError: import() is not available in workflow scripts.";
        // `with (x)` — the keyword followed by an opening parenthesis.
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(
                     script, @"(^|[^\w$.])with\s*\(").Cast<System.Text.RegularExpressions.Match>())
        {
            _ = match;
            return "SyntaxError: 'with' statements are not supported in workflow scripts.";
        }

        return null;
    }

    public async Task<WorkflowOutcome> RunAsync(
        WorkflowRunOptions options,
        IWorkflowAgentRunner runner,
        CancellationToken cancellationToken)
    {
        var runId = NewRunId();
        var started = DateTimeOffset.Now;
        var log = new List<string>();

        if (Reject(options.Script) is { } rejection)
            return new WorkflowOutcome(false, runId, null, rejection, log, 0, 0, 0, TimeSpan.Zero);

        var journal = options.ResumeFromRunId is { Length: > 0 } resumeFrom
            ? WorkflowJournal.Replaying(options.JournalDirectory, runId, resumeFrom)
            : WorkflowJournal.ForRun(options.JournalDirectory, runId);

        var engine = new Engine(engineOptions => engineOptions
            .Strict(false)
            .LimitRecursion(256)
            .CancellationToken(cancellationToken));

        var pending = new ConcurrentDictionary<int, (ManualPromise Promise, Task<JsonNode?> Work)>();
        var nextPromiseId = 0;
        var agentCount = 0;
        var cachedCount = 0;
        long outputTokens = 0;
        var currentPhase = "";
        var phaseGate = new object();
        var phaseIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextPhaseIndex = 0;
        var keyCounts = new ConcurrentDictionary<string, int>();

        // The reference's phase registry: a title is announced once, keeps the
        // index it was given, and every later mention reuses it.
        int AnnouncePhase(string title, string? detail)
        {
            lock (phaseGate)
            {
                if (phaseIndexes.TryGetValue(title, out var existing))
                    return existing;
                existing = ++nextPhaseIndex;
                phaseIndexes[title] = existing;
                options.Progress?.Invoke(new WorkflowProgressEntry.Phase(existing, title, detail));
                return existing;
            }
        }

        // Declared phases exist before anything runs, so the groups are all
        // there from the start — including ones no agent ever reaches.
        foreach (var declared in options.Phases ?? [])
        {
            if (!string.IsNullOrWhiteSpace(declared.Title))
                AnnouncePhase(declared.Title, declared.Detail);
        }

        var gate = new SemaphoreSlim(ConcurrencyLimit, ConcurrencyLimit);
        var budget = options.Budget;
        var serializer = new Jint.Native.Json.JsonSerializer(engine);

        JsValue Fail(string message) => throw new JavaScriptException(
            engine.Intrinsics.Error.Construct(message));

        JsValue StartHostWork(Func<CancellationToken, Task<JsonNode?>> work)
        {
            var promise = engine.Advanced.RegisterPromise();
            var id = Interlocked.Increment(ref nextPromiseId);
            var task = Task.Run(() => work(cancellationToken), cancellationToken);
            pending[id] = (promise, task);
            return promise.Promise;
        }

        engine.SetValue("log", new Action<JsValue>(message =>
        {
            var text = message.IsString() ? message.AsString() : message.ToString();
            log.Add(text);
            options.Progress?.Invoke(new WorkflowProgressEntry.Log(text));
        }));

        engine.SetValue("phase", new Action<JsValue>(title =>
        {
            currentPhase = PhaseTitle(title);
            AnnouncePhase(currentPhase, null);
            log.Add($"── {currentPhase} ──");
        }));

        engine.SetValue("__agent", new Func<JsValue, JsValue, JsValue>((promptValue, optionsValue) =>
        {
            if (!promptValue.IsString() || string.IsNullOrWhiteSpace(promptValue.AsString()))
                return Fail("agent() needs a non-empty prompt string");

            var ordinal = Interlocked.Increment(ref agentCount);
            if (ordinal > MaxAgentsPerRun)
                return Fail($"This workflow exceeded the {MaxAgentsPerRun}-agent cap for a single run.");

            if (budget is { Exhausted: true })
                return Fail($"The turn's token budget ({budget.Total}) is spent; agent() cannot run.");

            var request = ReadRequest(promptValue.AsString(), optionsValue, currentPhase, serializer);
            var key = CallKey(request, keyCounts);
            var index = ordinal;
            var label = request.Label is { Length: > 0 } declared ? declared : $"agent {index}";
            int? phaseIndex = request.Phase is { Length: > 0 } phase ? AnnouncePhase(phase, null) : null;
            var preview = PromptPreview(request.Prompt);

            var startedAt = DateTimeOffset.Now;

            void Report(WorkflowAgentState state, long tokens = 0, TimeSpan? duration = null) =>
                options.Progress?.Invoke(new WorkflowProgressEntry.Agent(
                    index, label, state, phaseIndex, request.Phase, preview, tokens,
                    request.Model, duration, DateTimeOffset.Now));

            if (journal.TryReplay(key, out var cached))
            {
                Interlocked.Increment(ref cachedCount);
                Report(WorkflowAgentState.Done, duration: TimeSpan.Zero);
                return StartHostWork(_ => Task.FromResult(cached));
            }

            // "start" is the reference's launched-but-queued state; the agent
            // only counts as running once it is past the concurrency gate.
            Report(WorkflowAgentState.Start);
            return StartHostWork(async token =>
            {
                await gate.WaitAsync(token);
                startedAt = DateTimeOffset.Now;
                Report(WorkflowAgentState.Progress);
                try
                {
                    var result = await runner.RunAgentAsync(request, token);
                    Interlocked.Add(ref outputTokens, result.OutputTokens);
                    budget?.Charge(result.OutputTokens);
                    JsonNode? value = result switch
                    {
                        { Success: false } => null,
                        { Structured: { } structured } => structured,
                        _ => JsonValue.Create(result.Text),
                    };
                    journal.Record(key, request, value);
                    Report(
                        result.Success ? WorkflowAgentState.Done : WorkflowAgentState.Error,
                        result.OutputTokens,
                        DateTimeOffset.Now - startedAt);
                    return value;
                }
                catch
                {
                    Report(WorkflowAgentState.Error, duration: DateTimeOffset.Now - startedAt);
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            });
        }));

        engine.SetValue("__workflow", new Func<JsValue, JsValue, JsValue>((nameValue, argsValue) =>
        {
            if (options.RunNested is not { } runNested)
                return Fail("workflow() inside a child workflow is not allowed — nesting is one level only.");
            var name = nameValue.IsString()
                ? nameValue.AsString()
                : nameValue.AsObject().Get("scriptPath").ToString();
            var childArgs = ToJson(argsValue, serializer);
            return StartHostWork(async token =>
            {
                var outcome = await runNested(name, childArgs, token);
                Interlocked.Add(ref outputTokens, outcome.OutputTokens);
                if (!outcome.Success)
                    throw new InvalidOperationException(outcome.Error ?? $"workflow('{name}') failed.");
                return outcome.Result;
            });
        }));

        // budget is a live object: spent() and remaining() are read per call.
        engine.SetValue("__budgetTotal", budget?.Total is { } total ? total : JsValue.Null);
        engine.SetValue("__budgetSpent", new Func<double>(() => budget?.Spent() ?? 0));
        engine.SetValue("__budgetRemaining", new Func<double>(() =>
            budget is null ? double.PositiveInfinity : budget.Remaining()));

        engine.SetValue("__args", options.Args is null
            ? JsValue.Undefined
            : new Jint.Native.Json.JsonParser(engine).Parse(options.Args.ToJsonString()));

        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SetValue("__resolve", new Action<JsValue>(value =>
            completion.TrySetResult(ToJson(value, serializer))));
        engine.SetValue("__reject", new Action<string>(message =>
            completion.TrySetException(new InvalidOperationException(message))));

        try
        {
            engine.Execute(Prelude
                .Replace("__NOW_ERR__", JsonSerializerText(DateGuardMessage))
                .Replace("__RANDOM_ERR__", JsonSerializerText(RandomGuardMessage))
                .Replace("__MAX_ITEMS__", MaxItemsPerCall.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            engine.Execute("""
                globalThis.args = __args;
                globalThis.agent = function agent(prompt, opts) { return __agent(prompt, opts || {}); };
                globalThis.workflow = function workflow(nameOrRef, args) { return __workflow(nameOrRef, args); };
                globalThis.budget = {
                  total: __budgetTotal,
                  spent() { return __budgetSpent(); },
                  remaining() { return __budgetRemaining(); },
                };
                """);

            engine.Execute("(async () => {\n" + options.Script + "\n})().then(" +
                "v => __resolve(v), " +
                "e => __reject(e && e.message ? (e.name + ': ' + e.message + " +
                "(e.stack ? '\\n' + e.stack : '')) : String(e)))");
        }
        catch (JavaScriptException ex)
        {
            var name = ex.Error.IsObject() ? ex.Error.AsObject().Get("name").ToString() : "";
            var message = name == "SyntaxError"
                ? $"SyntaxError: {ex.Message}"
                : $"Workflow script error: {ex.Message}";
            return new WorkflowOutcome(false, runId, null, message,
                log, agentCount, cachedCount, outputTokens, DateTimeOffset.Now - started);
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Parse", StringComparison.Ordinal))
        {
            // Jint reports script syntax errors through its parser's own
            // exception type, which is not part of its public surface.
            return new WorkflowOutcome(false, runId, null, $"SyntaxError: {ex.Message}",
                log, agentCount, cachedCount, outputTokens, DateTimeOffset.Now - started);
        }

        // The pump: drain microtasks, settle finished host work, repeat. Only
        // this loop ever touches the engine, so the realm stays single-threaded.
        var deadline = DateTime.UtcNow + options.Timeout;
        while (!completion.Task.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            engine.Advanced.ProcessTasks();

            foreach (var (id, entry) in pending.ToArray())
            {
                if (!entry.Work.IsCompleted)
                    continue;
                pending.TryRemove(id, out _);
                if (entry.Work.IsFaulted)
                {
                    var error = entry.Work.Exception?.GetBaseException().Message ?? "agent failed";
                    entry.Promise.Reject(engine.Intrinsics.Error.Construct(error));
                }
                else if (entry.Work.IsCanceled)
                {
                    entry.Promise.Reject(engine.Intrinsics.Error.Construct("The workflow was stopped."));
                }
                else
                {
                    var value = entry.Work.Result;
                    entry.Promise.Resolve(value is null
                        ? JsValue.Null
                        : new Jint.Native.Json.JsonParser(engine).Parse(value.ToJsonString()));
                }
            }

            engine.Advanced.ProcessTasks();

            if (completion.Task.IsCompleted)
                break;

            if (DateTime.UtcNow > deadline)
            {
                return new WorkflowOutcome(false, runId, null,
                    $"The workflow exceeded its {options.Timeout.TotalMinutes:0} minute limit.",
                    log, agentCount, cachedCount, outputTokens, DateTimeOffset.Now - started);
            }

            var unfinished = pending.Values.Where(p => !p.Work.IsCompleted).Select(p => (Task)p.Work).ToList();
            if (unfinished.Count > 0)
                await Task.WhenAny(Task.WhenAny(unfinished), Task.Delay(50, cancellationToken));
            else
                await Task.Delay(5, cancellationToken);
        }

        try
        {
            var result = await completion.Task;
            return new WorkflowOutcome(true, runId, result, null, log, agentCount, cachedCount,
                outputTokens, DateTimeOffset.Now - started);
        }
        catch (Exception ex)
        {
            return new WorkflowOutcome(false, runId, null, ex.Message, log, agentCount, cachedCount,
                outputTokens, DateTimeOffset.Now - started);
        }
    }

    private static string JsonSerializerText(string value) => JsonValue.Create(value)!.ToJsonString();

    private static WorkflowAgentRequest ReadRequest(
        string prompt, JsValue optionsValue, string currentPhase, Jint.Native.Json.JsonSerializer serializer)
    {
        string? label = null, phase = currentPhase.Length == 0 ? null : currentPhase, agentType = null, model = null;
        string? effort = null, isolation = null;
        JsonNode? schema = null;
        if (optionsValue.IsObject())
        {
            var options = optionsValue.AsObject();
            if (options.Get("label") is { } labelValue && labelValue.IsString())
                label = labelValue.AsString();
            if (options.Get("phase") is { } phaseValue && phaseValue.IsString())
                phase = phaseValue.AsString();
            if (options.Get("agentType") is { } typeValue && typeValue.IsString())
                agentType = typeValue.AsString();
            if (options.Get("model") is { } modelValue && modelValue.IsString())
                model = modelValue.AsString();
            if (options.Get("schema") is { } schemaValue && schemaValue.IsObject())
                schema = ToJson(schemaValue, serializer);
            if (options.Get("effort") is { } effortValue && effortValue.IsString())
                effort = effortValue.AsString();
            if (options.Get("isolation") is { } isolationValue && isolationValue.IsString())
                isolation = isolationValue.AsString();
        }

        return new WorkflowAgentRequest(prompt, label, phase, schema, agentType, model, effort, isolation);
    }

    private static JsonNode? ToJson(JsValue value, Jint.Native.Json.JsonSerializer serializer)
    {
        if (value.IsUndefined() || value.IsNull())
            return null;
        try
        {
            var text = serializer.Serialize(value)?.ToString();
            return string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or JavaScriptException)
        {
            return JsonValue.Create(value.ToString());
        }
    }

    /// <summary>
    /// Identifies one agent() call for the journal. Keyed by content rather than
    /// by position: concurrent calls have no stable order, and an unchanged
    /// script re-issuing the same call must hit the cache.
    /// </summary>
    private static string CallKey(WorkflowAgentRequest request, ConcurrentDictionary<string, int> counts)
    {
        var material = string.Join(' ',
            request.Prompt, request.Label ?? "", request.Phase ?? "",
            request.Schema?.ToJsonString() ?? "", request.AgentType ?? "", request.Model ?? "",
            request.Effort ?? "", request.Isolation ?? "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
        var occurrence = counts.AddOrUpdate(hash, 1, (_, current) => current + 1);
        return $"{hash}:{occurrence}";
    }

    /// <summary>
    /// The reference's title coercion for <c>phase()</c>: anything but an object
    /// or a function stringifies, and those two read as their type.
    /// </summary>
    private static string PhaseTitle(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined())
            return value.ToString();
        if (value.IsObject())
            return value is Jint.Native.Function.Function ? "[function]" : "[object]";
        return value.ToString();
    }

    /// <summary>A one-line opening of the prompt, for the agent's progress row.</summary>
    private static string PromptPreview(string prompt)
    {
        var line = prompt.AsSpan().Trim();
        var breakAt = line.IndexOfAny('\r', '\n');
        if (breakAt >= 0)
            line = line[..breakAt];
        return line.Length > 120 ? string.Concat(line[..120], "…") : line.ToString();
    }

    private static string NewRunId() =>
        "run-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}

/// <summary>
/// Saved workflows: <c>.jarvis/workflows/&lt;name&gt;.js</c> in the project,
/// with the reference's <c>.claude/workflows</c> read too, then the user's.
/// </summary>
public sealed class WorkflowStore(string projectDirectory, string? userDirectory = null,
    IReadOnlyDictionary<string, string>? explicitWorkflows = null, bool discover = true)
{
    public IReadOnlyList<string> Directories()
    {
        if (!discover) return [];
        var directories = new List<string>
        {
            Path.Combine(projectDirectory, ".jarvis", "workflows"),
            Path.Combine(projectDirectory, ".claude", "workflows"),
        };
        if (!string.IsNullOrEmpty(userDirectory))
            directories.Add(userDirectory);
        return directories;
    }

    /// <summary>Names of every saved workflow, first definition winning.</summary>
    public IReadOnlyList<string> Names()
    {
        var names = new List<string>(explicitWorkflows?.Where(pair => File.Exists(pair.Value)).Select(pair => pair.Key) ?? []);
        var seen = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directories())
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.js").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (seen.Add(name))
                    names.Add(name);
            }
        }

        return names;
    }

    public string? Find(string name)
    {
        if (explicitWorkflows?.TryGetValue(name, out var explicitPath) == true)
            return File.Exists(explicitPath) ? explicitPath : null;
        if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || Path.IsPathRooted(name)) return null;
        foreach (var directory in Directories())
        {
            var path = Path.Combine(directory, name + ".js");
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
