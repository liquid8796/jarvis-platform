using System.Text.Json.Nodes;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// ScheduleWakeup — the reference's ScheduleWakeup: the /loop dynamic-pacing tool.
/// Schema, validation and result texts follow the installed CLI; the description,
/// sentinels and clamp live in <see cref="LoopPrompts"/>.
/// </summary>
public sealed class ScheduleWakeupTool(Action<TimeSpan, string, bool> schedule, Func<int> stop) : ITool
{
    public ScheduleWakeupTool(ChatViewModel viewModel)
        : this((delay, prompt, noop) => viewModel.ScheduleWakeup(delay, prompt, noop), viewModel.StopDynamicLoops) { }
    public string Name => "ScheduleWakeup";

    public string Description => LoopPrompts.ToolDescription;

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["delaySeconds"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = LoopPrompts.DescDelaySeconds,
            },
            ["reason"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = LoopPrompts.DescReason,
            },
            ["prompt"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = LoopPrompts.DescPrompt,
            },
            ["stop"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = LoopPrompts.DescStop,
            },
            ["noop"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = LoopPrompts.DescNoop,
            },
        },
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        JsonArgs.GetBool(arguments, "stop")
            ? "ScheduleWakeup(stop)"
            : $"ScheduleWakeup({JsonArgs.GetDouble(arguments, "delaySeconds") ?? 0:0}s)";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (JsonArgs.GetBool(arguments, "stop"))
        {
            int cancelled = stop();
            return Task.FromResult(ToolResult.Success(LoopPrompts.StopResult(cancelled)));
        }

        double? delaySeconds = JsonArgs.GetDouble(arguments, "delaySeconds");
        var reason = JsonArgs.GetString(arguments, "reason");
        if (delaySeconds is null || string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(ToolResult.Error(LoopPrompts.ErrDelayReason));
        }

        var prompt = JsonArgs.GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult(ToolResult.Error(LoopPrompts.ErrPrompt));
        }

        bool? noop = arguments["noop"] is null ? null : JsonArgs.GetBool(arguments, "noop");
        if (noop is null)
        {
            return Task.FromResult(ToolResult.Error(LoopPrompts.ErrNoop));
        }

        var (clamped, wasClamped) = LoopPrompts.ClampDelay(delaySeconds.Value);
        schedule(TimeSpan.FromSeconds(clamped), prompt, noop.Value);
        return Task.FromResult(ToolResult.Success(LoopPrompts.ScheduledResult(
            DateTimeOffset.Now.AddSeconds(clamped), clamped, wasClamped)));
    }
}
