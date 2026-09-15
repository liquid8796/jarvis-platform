using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Validation;

namespace JarvisCode.Cli;

internal sealed record CliInputMessage(ChatMessage Message, string Uuid)
{
    public static CliInputMessage Parse(JsonObject root)
    {
        if (root["type"]?.GetValue<string>() != "user" || root["message"] is not JsonObject message ||
            message["role"]?.GetValue<string>() != "user")
            throw new CliError("stream-json input requires a user message with message.role=\"user\".");
        var blocks = new List<ContentBlock>();
        if (message["content"] is JsonValue textValue && textValue.TryGetValue<string>(out var text))
            blocks.Add(new TextBlock(text));
        else if (message["content"] is JsonArray content)
        {
            foreach (var block in content)
            {
                switch (block?["type"]?.GetValue<string>())
                {
                    case "text":
                        blocks.Add(new TextBlock(block["text"]?.GetValue<string>() ?? ""));
                        break;
                    case "image":
                        if (block?["source"] is not JsonObject source || source["type"]?.GetValue<string>() != "base64")
                            throw new CliError("stream-json images require a base64 source.");
                        var media = source["media_type"]?.GetValue<string>() ?? "";
                        var data = source["data"]?.GetValue<string>() ?? "";
                        if (media is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
                            throw new CliError("Unsupported stream-json image media_type.");
                        try { _ = Convert.FromBase64String(data); }
                        catch (FormatException) { throw new CliError("stream-json image data is not valid base64."); }
                        blocks.Add(new ImageBlock(media, data));
                        break;
                    default:
                        throw new CliError("Unsupported stream-json user content block; use text or a base64 image.");
                }
            }
        }
        else throw new CliError("stream-json user message.content must be a string or an array of content blocks.");
        if (blocks.Count == 0 || blocks.All(x => x is TextBlock { Text.Length: 0 }))
            throw new CliError("stream-json user message must contain text or an image.");
        return new CliInputMessage(new ChatMessage(Role.User, blocks),
            root["uuid"]?.GetValue<string>() is { Length: > 0 } uuid ? uuid : Guid.NewGuid().ToString());
    }
}

/// <summary>
/// Reads stdin while a turn runs, so permission replies and interrupts are
/// handled immediately rather than deadlocking behind the model's tool call.
/// User messages remain ordered and are consumed once; EOF closes outstanding
/// prompts with a denial instead of leaving a print process parked forever.
/// </summary>
internal sealed class StreamJsonInput(TextReader input, StreamJson output,
    Func<JsonObject, CancellationToken, Task<JsonObject>> handleControl, bool replayUsers = false) : IDisposable
{
    internal const int MaximumLineCharacters = 32 * 1024 * 1024;
    private readonly Channel<CliInputMessage> _messages = Channel.CreateUnbounded<CliInputMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject?>> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeControls = new();
    private readonly ConcurrentDictionary<string, Task> _controlTasks = new();
    private volatile bool _closed;
    public ChannelReader<CliInputMessage> Messages => _messages.Reader;

    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (await input.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > MaximumLineCharacters)
                    throw new CliError("stream-json input line exceeds the 32 MiB limit.");
                JsonObject root;
                try { root = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("Expected an object."); }
                catch (JsonException ex) { throw new CliError("Invalid stream-json input: " + ex.Message); }
                switch (root["type"]?.GetValue<string>())
                {
                    case "user":
                        var message = CliInputMessage.Parse(root);
                        if (replayUsers) output.EmitUser(message);
                        if (_seen.Add(message.Uuid)) await _messages.Writer.WriteAsync(message, cancellationToken);
                        break;
                    case "control_response":
                        if (root["response"] is JsonObject response &&
                            response["request_id"]?.GetValue<string>() is { } responseId &&
                            _pending.TryRemove(responseId, out var completion))
                            completion.TrySetResult(response);
                        break;
                    case "control_request":
                        var id = root["request_id"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(id) || root["request"] is not JsonObject request)
                            throw new CliError("Malformed stream-json control_request.");
                        var controlCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        if (!_activeControls.TryAdd(id, controlCancellation))
                        { controlCancellation.Dispose(); Reply(id, null, "Duplicate active control request_id."); break; }
                        var controlTask = RunControlAsync(id, request, controlCancellation);
                        _controlTasks[id] = controlTask;
                        _ = controlTask.ContinueWith(completed => _controlTasks.TryRemove(id, out var removed),
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        break;
                    case "control_cancel_request":
                        if (root["request_id"]?.GetValue<string>() is { } cancelledId &&
                            _activeControls.TryGetValue(cancelledId, out var control)) control.Cancel();
                        break;
                    case "keep_alive":
                        break;
                    default:
                        throw new CliError("Unsupported stream-json input type; expected user, control_request, or control_response.");
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Close();
            await Task.WhenAll(_controlTasks.Values);
            _messages.Writer.TryComplete(failure);
        }
    }

    public async Task<JsonObject?> RequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        if (_closed) return null;
        var id = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        if (_closed)
        {
            _pending.TryRemove(id, out _);
            return null;
        }
        output.WriteLine(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
        try
        {
            var envelope = await completion.Task.WaitAsync(cancellationToken);
            return envelope?["subtype"]?.GetValue<string>() == "success"
                ? envelope["response"] as JsonObject : null;
        }
        catch (OperationCanceledException)
        {
            output.WriteLine(new JsonObject { ["type"] = "control_cancel_request", ["request_id"] = id });
            throw;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task RunControlAsync(string id, JsonObject request, CancellationTokenSource cancellation)
    {
        try { Reply(id, await handleControl(request, cancellation.Token), null); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Reply(id, null, ex.Message); }
        finally { _activeControls.TryRemove(id, out _); cancellation.Dispose(); }
    }

    private void Reply(string id, JsonObject? value, string? error)
    {
        var response = new JsonObject { ["subtype"] = error is null ? "success" : "error", ["request_id"] = id };
        if (error is null) response["response"] = value;
        else response["error"] = error;
        output.WriteLine(new JsonObject { ["type"] = "control_response", ["response"] = response });
    }

    private void Close()
    {
        _closed = true;
        foreach (var cancellation in _activeControls.Values)
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var (id, completion) in _pending)
            if (_pending.TryRemove(id, out _)) completion.TrySetResult(null);
    }

    public void Dispose() => Close();
}

/// <summary>
/// The ordinary permission policy still runs first. The SDK host answers only
/// calls that would otherwise prompt, and an updatedInput is schema-checked
/// and re-assessed by the local policy before the tool's normal hooks run.
/// </summary>
internal sealed class SdkPermissionBridge(UiPermissionGate inner,
    Func<JsonObject, CancellationToken, Task<JsonObject?>> requestPermission) : IPermissionGate, IDenialReasonSource
{
    public SdkPermissionBridge(UiPermissionGate inner, StreamJsonInput transport) : this(inner, transport.RequestAsync) { }
    public Action? Interrupt { get; set; }
    public Func<JsonArray, CancellationToken, Task>? UpdatePermissions { get; set; }
    private sealed class CallState(PermissionRequest request)
    {
        public PermissionRequest Request { get; } = request;
        public JsonObject? UpdatedInput { get; set; }
        public bool ConfirmingUpdate { get; set; }
        public bool PermissionsUpdated { get; set; }
    }
    private readonly AsyncLocal<CallState?> _current = new();
    private readonly ConcurrentDictionary<string, string> _denials = new();

    public async ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        var previous = _current.Value;
        var state = new CallState(request);
        _current.Value = state;
        try
        {
            var decision = await inner.RequestAsync(request, cancellationToken);
            if (decision == PermissionDecision.Deny || state.UpdatedInput is null && !state.PermissionsUpdated)
                return decision;
            var updated = state.UpdatedInput ?? (JsonObject)request.Arguments.DeepClone();
            var verdict = JsonSchemaValidation.ValidateInstance(request.Tool.InputSchema, updated);
            if (!verdict.IsValid)
            {
                Denial(request.CallId, "The SDK host supplied invalid updatedInput: " + verdict.Error);
                return PermissionDecision.Deny;
            }
            request.Arguments.Clear();
            foreach (var (key, value) in updated) request.Arguments[key] = value?.DeepClone();
            state.ConfirmingUpdate = true;
            // Deny rules/workspace restrictions are evaluated again for the
            // new arguments. Only another ask reuses the host's explicit answer.
            return await inner.RequestAsync(request with
            { CallDescription = request.Tool.DescribeCall(request.Arguments) }, cancellationToken);
        }
        finally { _current.Value = previous; }
    }

    public async Task<PermissionDecision> PromptAsync(PermissionPrompt prompt, CancellationToken cancellationToken)
    {
        var state = _current.Value;
        if (state?.ConfirmingUpdate == true) return PermissionDecision.Allow;
        var original = prompt.ArgumentsJson is { } json ? JsonNode.Parse(json) as JsonObject ?? [] : new JsonObject();
        var answer = await requestPermission(new JsonObject
        {
            ["subtype"] = "can_use_tool", ["tool_name"] = prompt.ToolName,
            ["input"] = original.DeepClone(), ["tool_use_id"] = prompt.CallId,
            ["permission_suggestions"] = new JsonArray(),
        }, cancellationToken);
        if (answer?["behavior"]?.GetValue<string>() != "allow")
        {
            Denial(prompt.CallId, answer?["message"]?.GetValue<string>() ??
                "The SDK host denied this tool call or disconnected before answering.");
            if (answer?["interrupt"]?.GetValue<bool>() == true) Interrupt?.Invoke();
            return PermissionDecision.Deny;
        }
        if (answer.ContainsKey("updatedInput") && answer["updatedInput"] is not JsonObject)
        {
            Denial(prompt.CallId, "The SDK host supplied updatedInput that is not an object.");
            return PermissionDecision.Deny;
        }
        if (answer["updatedInput"] is JsonObject updated && !JsonNode.DeepEquals(original, updated))
        {
            if (state is null)
            {
                Denial(prompt.CallId, "This permission request cannot accept rewritten tool input.");
                return PermissionDecision.Deny;
            }
            var validation = JsonSchemaValidation.ValidateInstance(state.Request.Tool.InputSchema, updated);
            if (!validation.IsValid)
            { Denial(prompt.CallId, "The SDK host supplied invalid updatedInput: " + validation.Error); return PermissionDecision.Deny; }
            state.UpdatedInput = (JsonObject)updated.DeepClone();
        }
        if (answer["updatedPermissions"] is JsonArray updates && updates.Count > 0)
        {
            if (UpdatePermissions is null)
            { Denial(prompt.CallId, "This permission handler does not support permission updates."); return PermissionDecision.Deny; }
            try { await UpdatePermissions(updates, cancellationToken); }
            catch (CliError ex) { Denial(prompt.CallId, ex.Message); return PermissionDecision.Deny; }
            if (state is not null) state.PermissionsUpdated = true;
        }
        return PermissionDecision.Allow;
    }

    private void Denial(string? callId, string message)
    { if (callId is not null) _denials[callId] = message; }

    public string? TakeDenialReason(string? callId)
    {
        var innerReason = inner.TakeDenialReason(callId);
        return callId is not null && _denials.TryRemove(callId, out var reason) ? reason : innerReason;
    }

    public bool TakeDenialWasUserRefusal(string? callId) => inner.TakeDenialWasUserRefusal(callId);
}
