using System.Text.Json.Nodes;
using JarvisCode.Core.Providers;

namespace JarvisCode.Cli;

/// <summary>Converts neutral provider events into SDK-compatible stream_event envelopes.</summary>
internal sealed class PartialJsonMessage(StreamJson output, string? parentToolUseId = null)
{
    private readonly Dictionary<string, int> _blocks = [];
    private bool _started;
    private string _model = "";

    public void Begin(LlmRequest request)
    {
        _blocks.Clear();
        _started = false;
        _model = request.ModelId;
    }

    private void Start()
    {
        if (_started) return;
        _started = true;
        Emit(new JsonObject
        {
            ["type"] = "message_start", ["message"] = new JsonObject
            {
                ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant",
                ["model"] = _model, ["content"] = new JsonArray(), ["stop_reason"] = null,
                ["stop_sequence"] = null, ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
            },
        });
    }

    private int Block(string key, JsonObject value)
    {
        Start();
        if (_blocks.TryGetValue(key, out var index)) return index;
        index = _blocks.Count;
        _blocks.Add(key, index);
        Emit(new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = value });
        return index;
    }

    private void Delta(int index, JsonObject delta) => Emit(new JsonObject
    { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta });

    public void Observe(ProviderEvent evt)
    {
        switch (evt)
        {
            case TextPreviewEvent preview:
                // Replaceable browser text is explicitly outside SDK content blocks/history.
                output.EmitPreview(preview.Text, parentToolUseId);
                break;
            case TextDeltaEvent text:
                Delta(Block("text:" + (text.Index ?? 0), new JsonObject { ["type"] = "text", ["text"] = "" }),
                    new JsonObject { ["type"] = "text_delta", ["text"] = text.Delta });
                break;
            case ToolCallStartedEvent call:
                Block("call:" + call.Index, new JsonObject
                { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = new JsonObject() });
                break;
            case ToolCallArgumentsDeltaEvent args when _blocks.TryGetValue("call:" + args.Index, out var index):
                Delta(index, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args.Delta });
                break;
            case ThinkingDeltaEvent thinking:
                Delta(Block("thinking:" + thinking.Index, new JsonObject { ["type"] = "thinking", ["thinking"] = "" }),
                    new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking.Delta });
                break;
            case ThinkingCompletedEvent thinking when thinking.Signature is not null:
                Delta(Block("thinking:" + thinking.Index, new JsonObject { ["type"] = "thinking", ["thinking"] = "" }),
                    new JsonObject { ["type"] = "signature_delta", ["signature"] = thinking.Signature });
                break;
            case CitationDeltaEvent citation:
                Delta(Block("text:" + citation.Index, new JsonObject { ["type"] = "text", ["text"] = "" }),
                    new JsonObject { ["type"] = "citations_delta", ["citation"] = JsonNode.Parse(citation.Citation.RawJson) });
                break;
            case ResponseCompletedEvent done:
                Start();
                var usage = new JsonObject { ["output_tokens"] = done.Usage.OutputTokens };
                if (done.Usage.IsEstimated) usage["is_estimated"] = true;
                foreach (var blockIndex in _blocks.Values)
                    Emit(new JsonObject { ["type"] = "content_block_stop", ["index"] = blockIndex });
                Emit(new JsonObject
                {
                    ["type"] = "message_delta", ["delta"] = new JsonObject
                    { ["stop_reason"] = done.StopReason ?? (done.WantsToolUse ? "tool_use" : "end_turn"), ["stop_sequence"] = null },
                    ["usage"] = usage,
                });
                Emit(new JsonObject { ["type"] = "message_stop" });
                _started = false;
                _blocks.Clear();
                break;
        }
    }

    private void Emit(JsonObject evt) => output.EmitPartial(evt, parentToolUseId);
}
