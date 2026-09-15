using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.ToolPrograms;

public sealed record ToolProgramLimits(
    int MaxInstructions = 128,
    int MaxToolCalls = 32,
    int MaxOutputChars = 64_000,
    TimeSpan? MaxElapsed = null)
{
    public TimeSpan ElapsedLimit => MaxElapsed ?? TimeSpan.FromSeconds(120);
}

public delegate Task<ToolReply> GuardedToolInvoker(
    string toolId, JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken);

/// <summary>
/// Deterministic restricted interpreter for composing published tools. It has no
/// eval/reflection/shell/file/network primitive of its own; all effects flow through
/// the injected guarded tool invoker.
/// </summary>
public sealed class ToolProgramEngine
{
    private readonly GuardedToolInvoker _invoke;
    private readonly ToolProgramLimits _limits;

    public ToolProgramEngine(GuardedToolInvoker invoke, ToolProgramLimits? limits = null)
    {
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _limits = limits ?? new ToolProgramLimits();
        if (_limits.MaxInstructions < 1 || _limits.MaxToolCalls < 1 || _limits.MaxOutputChars < 1 || _limits.ElapsedLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async Task<ToolReply> ExecuteAsync(JsonElement program, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        if (program.ValueKind != JsonValueKind.Object || !program.TryGetProperty("instructions", out var instructions) || instructions.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Tool program requires an instructions array.", nameof(program));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limits.ElapsedLimit);
        var state = new State();
        var returned = await ExecuteBlockAsync(instructions, context, state, deadline.Token).ConfigureAwait(false);
        var text = returned ?? state.LastOutput ?? "Program completed.";
        if (text.Length > _limits.MaxOutputChars) throw new InvalidOperationException("Tool program output budget exceeded.");
        return new ToolReply(text);
    }

    private async Task<string?> ExecuteBlockAsync(JsonElement instructions, AgentExecutionContext context, State state, CancellationToken cancellationToken)
    {
        foreach (var instruction in instructions.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++state.Instructions > _limits.MaxInstructions) throw new InvalidOperationException("Tool program instruction budget exceeded.");
            if (instruction.ValueKind != JsonValueKind.Object || !instruction.TryGetProperty("op", out var opNode) || opNode.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Each tool program instruction requires an op.");
            var op = opNode.GetString();
            switch (op)
            {
                case "call":
                {
                    var tool = RequiredString(instruction, "tool");
                    if (StringComparer.Ordinal.Equals(tool, "tool_program.run"))
                        throw new InvalidOperationException("Recursive tool_program.run is not allowed.");
                    if (++state.ToolCalls > _limits.MaxToolCalls) throw new InvalidOperationException("Tool program tool-call budget exceeded.");
                    var args = instruction.TryGetProperty("args", out var argsNode) ? Substitute(argsNode, state.Variables) : WireJson.Element(new { });
                    var reply = await _invoke(tool, args, context, cancellationToken).ConfigureAwait(false);
                    if (reply.IsError) throw new InvalidOperationException("Nested tool failed: " + reply.Text);
                    state.OutputChars = checked(state.OutputChars + reply.Text.Length);
                    if (state.OutputChars > _limits.MaxOutputChars) throw new InvalidOperationException("Tool program output budget exceeded.");
                    state.LastOutput = reply.Text;
                    if (instruction.TryGetProperty("save", out var saveNode) && saveNode.ValueKind == JsonValueKind.String)
                        state.Variables[saveNode.GetString()!] = reply.Text;
                    break;
                }
                case "set":
                {
                    var name = RequiredString(instruction, "variable");
                    if (!instruction.TryGetProperty("value", out var value)) throw new ArgumentException("set requires value.");
                    state.Variables[name] = ResolveScalar(value, state.Variables);
                    break;
                }
                case "if":
                {
                    var variable = RequiredString(instruction, "variable");
                    if (!state.Variables.TryGetValue(variable, out var value)) throw new ArgumentException("Unknown tool program variable: " + variable);
                    var expected = RequiredString(instruction, "equals");
                    if (StringComparer.Ordinal.Equals(value, expected) && instruction.TryGetProperty("then", out var thenBlock) && thenBlock.ValueKind == JsonValueKind.Array)
                    {
                        var result = await ExecuteBlockAsync(thenBlock, context, state, cancellationToken).ConfigureAwait(false);
                        if (result is not null) return result;
                    }
                    break;
                }
                case "forEach":
                {
                    var itemName = RequiredString(instruction, "item");
                    if (!instruction.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                        !instruction.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException("forEach requires items and body arrays.");
                    if (items.GetArrayLength() > 64) throw new InvalidOperationException("Tool program loop item budget exceeded.");
                    foreach (var item in items.EnumerateArray())
                    {
                        state.Variables[itemName] = ResolveScalar(item, state.Variables);
                        var result = await ExecuteBlockAsync(body, context, state, cancellationToken).ConfigureAwait(false);
                        if (result is not null) return result;
                    }
                    break;
                }
                case "assert":
                {
                    var variable = RequiredString(instruction, "variable");
                    if (!state.Variables.TryGetValue(variable, out var value)) throw new InvalidOperationException("Tool program assertion variable is missing: " + variable);
                    var expected = RequiredString(instruction, "equals");
                    if (!StringComparer.Ordinal.Equals(value, expected)) throw new InvalidOperationException("Tool program assertion failed.");
                    break;
                }
                case "return":
                {
                    var variable = RequiredString(instruction, "variable");
                    if (!state.Variables.TryGetValue(variable, out var value)) throw new ArgumentException("Unknown tool program variable: " + variable);
                    return value;
                }
                default:
                    throw new ArgumentException("Unsupported tool program op: " + op);
            }
        }
        return null;
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(node.GetString())
            ? node.GetString()! : throw new ArgumentException($"Tool program instruction requires {name}.");

    private static string ResolveScalar(JsonElement value, IReadOnlyDictionary<string, string> variables)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? "";
            if (text.StartsWith('$'))
            {
                var name = text[1..];
                if (!variables.TryGetValue(name, out var resolved)) throw new ArgumentException("Unknown tool program variable: " + name);
                return resolved;
            }
            return text;
        }
        return value.ToString();
    }

    private static JsonElement Substitute(JsonElement value, IReadOnlyDictionary<string, string> variables)
    {
        var node = JsonNode.Parse(value.GetRawText()) ?? new JsonObject();
        Replace(node, variables);
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString(), WireJson.Options);
    }

    private static void Replace(JsonNode node, IReadOnlyDictionary<string, string> variables)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                var child = obj[key];
                if (child is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.StartsWith('$'))
                {
                    var name = text[1..];
                    if (!variables.TryGetValue(name, out var resolved)) throw new ArgumentException("Unknown tool program variable: " + name);
                    obj[key] = resolved;
                }
                else if (child is not null) Replace(child, variables);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var child = array[i];
                if (child is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.StartsWith('$'))
                {
                    var name = text[1..];
                    if (!variables.TryGetValue(name, out var resolved)) throw new ArgumentException("Unknown tool program variable: " + name);
                    array[i] = resolved;
                }
                else if (child is not null) Replace(child, variables);
            }
        }
    }

    private sealed class State
    {
        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);
        public int Instructions;
        public int ToolCalls;
        public int OutputChars;
        public string? LastOutput;
    }
}

public sealed class ToolProgramTool(ToolProgramEngine engine) : ICompositeAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "tool_program.run", "tool_program__run", "workflow",
        "Run a bounded JSON tool program with call/set/if/forEach/assert/return instructions. The interpreter has no direct OS API; every nested tool call re-enters Jarvis local schema, Arm/Pause, permission and approval checks. Recursive tool_program.run is rejected.",
        WireJson.Element(new
        {
            type = "object",
            properties = new { instructions = new { type = "array", minItems = 1, maxItems = 128 } },
            required = new[] { "instructions" },
            additionalProperties = false
        }),
        ReadOnly: false,
        Sensitive: true);

    public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(arguments, context, cancellationToken);
}
