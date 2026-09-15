using System.Text.Json.Nodes;
using JarvisCode.App.Composition;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Models;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// Judges an LLM prompt hook's condition, the way the reference does: a side
/// query with thinking off, the transcript in front of it for a stop condition,
/// and one tool the model must call exactly once to answer {ok, reason}. A goal
/// riding a Stop hook is what makes a turn keep working until the goal is met,
/// so this evaluation is the loop's actual verdict — never a guess from text.
/// </summary>
public static class PromptHookEvaluation
{
    /// <summary>A gate for the side query, which has one tool and needs no approval.</summary>
    private sealed class PromptlessGate : IPermissionGate
    {
        public ValueTask<PermissionDecision> RequestAsync(
            PermissionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PermissionDecision.Allow);
    }

    /// <summary>The tool the evaluating model answers through.</summary>
    private sealed class VerdictTool : ITool
    {
        public PromptHookVerdict? Verdict { get; private set; }

        public string Name => PromptHooks.VerdictToolName;

        public string Description => PromptHooks.VerdictToolDescription;

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["ok"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Whether the condition was met",
                },
                ["reason"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Reason, if the condition was not met",
                },
                ["impossible"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "True when the condition can never be satisfied in this session",
                },
            },
            ["required"] = new JsonArray("ok"),
            ["additionalProperties"] = false,
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) => "Hook verdict";

        public Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Verdict = new PromptHookVerdict(
                JsonArgs.GetBool(arguments, "ok"),
                JsonArgs.GetString(arguments, "reason"),
                JsonArgs.GetBool(arguments, "impossible"));
            return Task.FromResult(ToolResult.Success("Recorded."));
        }
    }

    /// <summary>
    /// Builds the evaluator for one session. <paramref name="transcript"/> is read
    /// at evaluation time, so a stop condition is judged against the conversation
    /// as it stands when the turn wants to end.
    /// </summary>
    public static PromptHookEvaluator Create(
        AppServices services,
        ILlmProvider provider,
        ModelInfo model,
        Func<IReadOnlyList<ChatMessage>> transcript,
        string workingDirectory) =>
        async (request, cancellationToken) =>
        {
            var callModel = request.Model is { Length: > 0 } requested &&
                ModelCatalog.Find(services.Settings.Models, requested) is { } found
                ? found
                : model;
            var callProvider = callModel.ProviderId == model.ProviderId
                ? provider
                : services.Providers.Get(callModel.ProviderId);
            if (callProvider is null)
            {
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProviderCapabilities.For(callProvider).SupportsToolFreeInference)
            {
                // A verdict-only local registry does not disable native browser tools.
                // Do not treat an unevaluated authorization/stop condition as success.
                return new PromptHookVerdict(false,
                    "The configured browser model cannot perform isolated hook review. Configure an API hook model; " +
                    "no evaluation prompt was sent.");
            }

            var question = request.IsStopCondition
                ? PromptHooks.StopConditionQuestion(request.Condition)
                : PromptHooks.Render(request.Condition, request.PayloadJson);

            // A stop condition is judged on transcript evidence, so the
            // conversation goes in front of the question; anything else is
            // answered from the payload alone.
            var messages = new List<ChatMessage>();
            if (request.IsStopCondition)
            {
                messages.AddRange(transcript());
            }

            messages.Add(ChatMessage.FromUserText(question));

            var verdictTool = new VerdictTool();
            var turn = new AgentTurnContext
            {
                Provider = callProvider,
                ModelId = callModel.ModelId,
                SystemPrompt = request.IsStopCondition
                    ? PromptHooks.StopConditionSystemPrompt
                    : PromptHooks.GenericSystemPrompt,
                Messages = messages,
                Tools = new ToolRegistry([verdictTool]),
                PermissionGate = new PromptlessGate(),
                ToolContext = new ToolExecutionContext { WorkingDirectory = workingDirectory },
                ThinkingEffort = ThinkingEffort.Off,
                MaxIterations = 1,
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout <= TimeSpan.Zero ? PromptHooks.DefaultTimeout : request.Timeout);
            try
            {
                await foreach (var evt in services.Orchestrator.RunTurnAsync(turn, timeout.Token))
                {
                    if (evt is TurnCompleted { Reason: TurnEndReason.Error })
                    {
                        return null;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A hook that timed out has no opinion; the turn ends as it would
                // have without it rather than being held by an unanswered question.
                return null;
            }

            return verdictTool.Verdict;
        };
}
