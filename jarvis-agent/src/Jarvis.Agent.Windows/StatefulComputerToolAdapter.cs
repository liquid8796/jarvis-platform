using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows;

public interface IComputerObservationProvider
{
    ComputerObservation Capture();
}

/// <summary>
/// Adds snapshot-bound desktop semantics around the reused baseline computer tools.
/// The baseline receives only its original arguments; Jarvis-owned state metadata is
/// consumed before dispatch.
/// </summary>
public sealed class StatefulComputerToolAdapter : IAgentTool
{
    private readonly IAgentTool _inner;
    private readonly ComputerStateTracker _states;
    private readonly IComputerObservationProvider _observer;
    private readonly bool _requiresState;
    private readonly bool _capturesState;

    public ToolDescriptor Descriptor { get; }

    public StatefulComputerToolAdapter(IAgentTool inner, ComputerStateTracker states, IComputerObservationProvider observer)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        if (!StringComparer.Ordinal.Equals(inner.Descriptor.Category, "computer"))
            throw new ArgumentException("Stateful computer adapter requires a computer tool.", nameof(inner));

        _requiresState = StringComparer.Ordinal.Equals(inner.Descriptor.Id, "computer.computer_batch");
        _capturesState = StringComparer.Ordinal.Equals(inner.Descriptor.Id, "computer.screenshot");
        Descriptor = _requiresState ? WithStateSchema(inner.Descriptor) : inner.Descriptor;
    }

    public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_requiresState)
        {
            var args = JsonNode.Parse(arguments.GetRawText()) as JsonObject ?? throw new ArgumentException("Object arguments required.");
            var stateId = args["stateId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(stateId))
                throw new InvalidOperationException("A current stateId from computer.screenshot or computer.get_state is required before computer_batch.");
            _states.Validate(context.IsolationScopeId, stateId);
            args.Remove("stateId");
            try
            {
                var cleaned = JsonSerializer.Deserialize<JsonElement>(args.ToJsonString(), WireJson.Options);
                return await _inner.ExecuteAsync(cleaned, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Once dispatch begins the observed screen may have changed even when
                // the inner tool later reports an error. Force re-observation.
                _states.InvalidateAll();
            }
        }

        ToolReply result;
        try { result = await _inner.ExecuteAsync(arguments, context, cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (Descriptor.Id is not ("computer.screenshot" or "computer.list_granted_applications" or "computer.read_clipboard"))
                _states.InvalidateAll();
        }
        if (!_capturesState || result.IsError) return result;

        var state = _states.Capture(context.IsolationScopeId, _observer.Capture());
        return result with
        {
            Text = result.Text + $"\nJarvis computer state: stateId={state.StateId}; generation={state.Generation}; observedAt={state.ObservedAt:O}. Use this stateId for the next computer_batch only."
        };
    }

    private static ToolDescriptor WithStateSchema(ToolDescriptor descriptor)
    {
        var schema = JsonNode.Parse(descriptor.InputSchema.GetRawText()) as JsonObject
            ?? throw new ArgumentException("Computer tool schema must be an object.");
        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        schema["properties"] = properties;
        properties["stateId"] = new JsonObject
        {
            ["type"] = "string",
            ["minLength"] = 16,
            ["description"] = "Opaque Jarvis computer observation state. Obtain a fresh value from computer.screenshot or computer.get_state before input."
        };
        var required = schema["required"] as JsonArray ?? new JsonArray();
        schema["required"] = required;
        if (!required.Any(node => node?.GetValue<string>() == "stateId")) required.Add("stateId");
        var element = JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString(), WireJson.Options);
        return descriptor with
        {
            InputSchema = element,
            Description = descriptor.Description + " Requires the current Jarvis stateId and invalidates it after dispatch; re-observe before another input batch."
        };
    }
}
