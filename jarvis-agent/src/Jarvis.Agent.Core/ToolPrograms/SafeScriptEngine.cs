using System.Runtime.ExceptionServices;
using System.Text.Json;
using Jint;
using Jint.Runtime;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.ToolPrograms;

public sealed record SafeScriptLimits(
    int MaxScriptChars = 50_000,
    int MaxToolCalls = 32,
    int MaxOutputChars = 64_000,
    int MaxStatements = 10_000,
    long MaxMemoryBytes = 8 * 1024 * 1024,
    TimeSpan? MaxElapsed = null)
{
    public TimeSpan ElapsedLimit => MaxElapsed ?? TimeSpan.FromSeconds(30);
}

/// <summary>
/// Fresh-engine JavaScript sandbox for control-flow-heavy tool composition. The script receives
/// exactly one host capability: invokeTool(toolId, argsJson). It never enables CLR interop, Node
/// globals, filesystem, process or network primitives. Every nested effect re-enters the guarded
/// Jarvis tool invoker supplied by AgentConnection.
/// </summary>
public sealed class SafeScriptEngine
{
    private const int MaxArgumentsJsonChars = 128_000;
    private readonly GuardedToolInvoker _invoke;
    private readonly SafeScriptLimits _limits;

    public SafeScriptEngine(GuardedToolInvoker invoke, SafeScriptLimits? limits = null)
    {
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _limits = limits ?? new SafeScriptLimits();
        if (_limits.MaxScriptChars < 1 || _limits.MaxToolCalls < 1 || _limits.MaxOutputChars < 1 ||
            _limits.MaxStatements < 1 || _limits.MaxMemoryBytes < 1 || _limits.ElapsedLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async Task<ToolReply> ExecuteAsync(string script, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(script) || script.Length > _limits.MaxScriptChars)
            throw new ArgumentException($"Script must contain 1..{_limits.MaxScriptChars} characters.", nameof(script));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limits.ElapsedLimit);
        var toolCalls = 0;
        var outputChars = 0;
        Exception? hostFailure = null;

        var engine = new Engine(options =>
        {
            options.LimitMemory(_limits.MaxMemoryBytes);
            options.MaxStatements(_limits.MaxStatements);
            options.TimeoutInterval(_limits.ElapsedLimit);
            options.CancellationToken(deadline.Token);
        });

        async Task<string> InvokeToolAsync(string toolId, string argumentsJson)
        {
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(toolId) || toolId.Length > 100)
                    throw new ArgumentException("Nested tool ID is invalid.");
                if (StringComparer.Ordinal.Equals(toolId, "tool_script.run") || StringComparer.Ordinal.Equals(toolId, "tool_program.run"))
                    throw new InvalidOperationException("Recursive composite tool execution is not allowed.");
                if (Interlocked.Increment(ref toolCalls) > _limits.MaxToolCalls)
                    throw new InvalidOperationException("Safe script tool-call budget exceeded.");
                if (argumentsJson is null || argumentsJson.Length > MaxArgumentsJsonChars)
                    throw new ArgumentException("Nested tool arguments JSON exceeds the bounded size.");

                JsonElement arguments;
                try
                {
                    using var document = JsonDocument.Parse(argumentsJson);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Nested tool arguments JSON must be an object.");
                    arguments = document.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException("Nested tool arguments JSON is invalid.", ex);
                }

                var reply = await _invoke(toolId, arguments, context, deadline.Token).ConfigureAwait(false);
                if (reply.IsError) throw new InvalidOperationException("Nested tool failed: " + reply.Text);
                var total = Interlocked.Add(ref outputChars, reply.Text.Length);
                if (total > _limits.MaxOutputChars) throw new InvalidOperationException("Safe script output budget exceeded.");
                return reply.Text;
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref hostFailure, ex, null);
                throw;
            }
        }

        // Jint converts Task-returning delegates to Promises. No other host object is exposed.
        engine.SetValue("invokeTool", new Func<string, string, Task<string>>(InvokeToolAsync));
        var wrapped = "(async () => {\n" + script + "\n})().then(value => JSON.stringify(value));";
        Jint.Native.JsValue result;
        try
        {
            result = await engine.EvaluateAsync(wrapped, cancellationToken: deadline.Token).ConfigureAwait(false);
        }
        catch (PromiseRejectedException) when (hostFailure is not null)
        {
            ExceptionDispatchInfo.Capture(hostFailure).Throw();
            throw;
        }
        // A script cannot swallow a failed/denied nested tool call and report success.
        // This preserves the same fail-closed semantics as the deterministic tool program.
        if (hostFailure is not null)
        {
            ExceptionDispatchInfo.Capture(hostFailure).Throw();
        }
        var value = result.ToObject();
        var text = value?.ToString() ?? "null";
        if (text.Length > _limits.MaxOutputChars) throw new InvalidOperationException("Safe script output budget exceeded.");
        return new ToolReply(text);
    }
}

public sealed class SafeScriptTool(SafeScriptEngine engine) : ICompositeAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "tool_script.run", "tool_script__run", "workflow",
        "Run bounded in-process JavaScript with JSON/control flow and a single invokeTool(toolId,argsJson) host capability. No Node/CLR/file/process/network globals are enabled; every nested tool call re-enters Jarvis schema, Arm/Pause, permission and approval checks. Composite recursion is rejected.",
        WireJson.Element(new
        {
            type = "object",
            properties = new
            {
                script = new { type = "string", minLength = 1, maxLength = 50_000 }
            },
            required = new[] { "script" },
            additionalProperties = false
        }),
        ReadOnly: false,
        Sensitive: true);

    public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("script", out var script) || script.ValueKind != JsonValueKind.String)
            throw new ArgumentException("tool_script.run requires a script string.");
        return engine.ExecuteAsync(script.GetString() ?? "", context, cancellationToken);
    }
}
