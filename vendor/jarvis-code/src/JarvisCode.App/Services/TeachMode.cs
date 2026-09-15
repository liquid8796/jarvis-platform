using System.Text.Json.Nodes;
using System.Windows;
using JarvisCode.App.Views;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>What the user did with a teach tooltip.</summary>
public enum TeachDecision
{
    Next,
    Exit,
}

/// <summary>
/// Teach mode: the app hides itself, a full-screen tooltip overlay takes over, and
/// each step waits for the user to click Next before the model's actions run. The
/// session ends on Exit, when the turn ends, or when the window closes.
/// </summary>
public sealed class TeachController(Func<Window?> mainWindow, Func<UiSettings> settings)
{
    private TeachOverlayWindow? _overlay;
    private TaskCompletionSource<TeachDecision>? _pending;
    private bool _hidMainWindow;

    public bool IsActive => _overlay is not null;

    /// <summary>Hides the app and raises the overlay on the primary display.</summary>
    public bool Begin()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return false;
        }

        return dispatcher.Invoke(() =>
        {
            if (_overlay is not null)
            {
                return true;
            }

            var overlay = new TeachOverlayWindow(settings().ReduceMotion);
            overlay.NextRequested += () => Resolve(TeachDecision.Next);
            overlay.ExitRequested += () => Resolve(TeachDecision.Exit);
            overlay.Closed += (_, _) => Resolve(TeachDecision.Exit);

            var display = ComputerUseService.Displays().FirstOrDefault(d => d.IsPrimary)
                ?? ComputerUseService.Displays().FirstOrDefault();
            overlay.Show();
            if (display is not null)
            {
                overlay.CoverDisplay(display.Bounds);
            }

            if (mainWindow() is { IsVisible: true } window)
            {
                window.Hide();
                _hidMainWindow = true;
            }

            _overlay = overlay;
            return true;
        });
    }

    /// <summary>Shows one tooltip and waits for the user; the anchor is in screen pixels.</summary>
    public async Task<TeachDecision> StepAsync(
        string explanation,
        string? nextPreview,
        System.Drawing.Point? anchor,
        CancellationToken cancellationToken)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher || _overlay is null)
        {
            return TeachDecision.Exit;
        }

        var pending = new TaskCompletionSource<TeachDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        dispatcher.Invoke(() => _overlay?.ShowStep(explanation, nextPreview, anchor));

        using var registration = cancellationToken.Register(() => Resolve(TeachDecision.Exit));
        var decision = await pending.Task.ConfigureAwait(false);
        if (decision == TeachDecision.Next)
        {
            dispatcher.Invoke(() => _overlay?.ShowWorking());
        }

        return decision;
    }

    /// <summary>Puts the overlay back, restores the app window.</summary>
    public void End()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        dispatcher.Invoke(() =>
        {
            Resolve(TeachDecision.Exit);
            if (_overlay is { } overlay)
            {
                _overlay = null;
                overlay.Close();
            }

            if (_hidMainWindow && mainWindow() is { } window)
            {
                window.Show();
                window.Activate();
            }

            _hidMainWindow = false;
        });
    }

    private void Resolve(TeachDecision decision)
    {
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(decision);
    }
}

/// <summary>
/// The reference's teach tools: request_teach_access opens the guided-tour overlay,
/// teach_step shows one tooltip and runs its actions after the user clicks Next,
/// and teach_batch queues several so one round trip covers a whole page.
/// </summary>
public static class TeachTools
{
    public static IReadOnlyList<ITool> Create(
        UiSettingsStore settings,
        TeachController controller,
        ComputerBatchTool batch,
        ComputerUseService service) =>
        [
            new RequestTeachAccessTool(settings, controller),
            new TeachStepTool(controller, batch, service),
            new TeachBatchTool(controller, batch, service),
        ];

    /// <summary>The action list a teach step runs, with a closing screenshot like the reference's.</summary>
    private static JsonObject BatchArguments(JsonArray? actions)
    {
        var list = new JsonArray();
        foreach (var action in actions ?? [])
        {
            list.Add(action?.DeepClone());
        }

        list.Add(new JsonObject { ["action"] = "screenshot" });
        return new JsonObject { ["actions"] = list };
    }

    /// <summary>
    /// Anchors ride the same coordinate frame as clicks — the last screenshot's
    /// region, which is not always the primary display.
    /// </summary>
    private static System.Drawing.Point? ReadAnchor(JsonObject arguments, ComputerUseService service)
    {
        if (arguments["anchor"] is not JsonArray anchor || anchor.Count != 2)
        {
            return null;
        }

        if (anchor[0] is not JsonValue x || anchor[1] is not JsonValue y)
        {
            return null;
        }

        var frame = service.Frame;
        return new System.Drawing.Point(
            frame.X + (int)Math.Round(ReadNumber(x)),
            frame.Y + (int)Math.Round(ReadNumber(y)));
    }

    private static double ReadNumber(JsonValue value)
    {
        if (value.TryGetValue<double>(out var number))
            return number;
        return value.TryGetValue<int>(out var whole) ? whole : 0;
    }

    /// <summary>The per-step schema, shared by teach_step and teach_batch.</summary>
    private static JsonObject StepProperties() => new()
    {
        ["explanation"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] =
                "Tooltip body text. Explain what the user is looking at and why it matters. This is the " +
                "ONLY place the user sees your words — be complete but concise.",
        },
        ["next_preview"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] =
                "One line describing exactly what will happen when the user clicks Next. Example: " +
                "\"Next: I'll click Create Bucket and type the name.\" Shown below the explanation in a " +
                "smaller font.",
        },
        ["anchor"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "number" },
            ["minItems"] = 2,
            ["maxItems"] = 2,

            // The reference interpolates its coordinate-mode sentence in the
            // middle of this one; that sentence is the adaptive-resolution
            // description, which this port does not offer.
            ["description"] =
                "(x, y) — where the tooltip arrow points. Omit to center the tooltip with no arrow (for " +
                "general-context steps).",
        },
        ["actions"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "object" },
            ["description"] =
                "Actions to execute when the user clicks Next. Same item schema as computer_batch.actions. " +
                "Empty array is valid for purely explanatory steps. Actions run sequentially and stop on " +
                "first error.",
        },
    };

    private sealed class RequestTeachAccessTool(UiSettingsStore settings, TeachController controller) : ITool
    {
        public string Name => "request_teach_access";

        public string Description =>
            "Request permission to guide the user through a task step-by-step with on-screen tooltips. Use " +
            "this INSTEAD OF request_access when the user wants to LEARN how to do something (phrases like " +
            "\"teach me\", \"walk me through\", \"show me how\", \"help me learn\"). On approval the main " +
            "Jarvis window hides and a fullscreen tooltip overlay appears. You then call teach_step " +
            "repeatedly; each call shows one tooltip and waits for the user to click Next. Same app-allowlist " +
            "semantics as request_access, but no clipboard/system-key flags. Teach mode ends automatically " +
            "when your turn ends.";

        // Same apps argument as request_access, installed list and all: the
        // reference builds both descriptions from the one win32 text.
        public JsonObject InputSchema => ComputerUseExtras.WithInstalledApps("request_teach_access");

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) =>
            $"RequestTeachAccess({JsonArgs.GetString(arguments, "reason") ?? "a guided tour"})";

        public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var apps = (arguments["apps"] as JsonArray ?? [])
                .OfType<JsonValue>()
                .Select(static v => v.TryGetValue<string>(out var s) ? ComputerUseGrants.Normalize(s) : "")
                .Where(static s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (apps.Count == 0)
                return ToolResult.Error("apps (a non-empty array of application names) is required.");
            if (context.AskUserAsync is null)
                return ToolResult.Error("There is no UI to run teach mode in this context.");

            var reason = JsonArgs.GetString(arguments, "reason")?.Trim();
            var question = new JarvisCode.Core.Tools.BuiltIn.UserQuestion(
                reason is { Length: > 0 }
                    ? $"Jarvis wants to guide you through {reason}"
                    : "Jarvis wants to guide you through a task",
                "Teach",
                [
                    new JarvisCode.Core.Tools.BuiltIn.UserQuestionOption(
                        "Start the walkthrough",
                        $"Tooltips take over the screen; Jarvis may act in {string.Join(", ", apps)}"),
                    new JarvisCode.Core.Tools.BuiltIn.UserQuestionOption("Not now", "Stay in the chat"),
                ],
                MultiSelect: false);

            var answers = await context.AskUserAsync([question], cancellationToken);
            if (answers is null
                || !answers.Answers.TryGetValue(question.Question, out var picked)
                || !picked.StartsWith("Start", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Error("The user declined the walkthrough.");
            }

            // Onto the session, where the reference keeps a grant.
            var granted = ComputerUseSessionGrants.For(settings.Current, context.SessionId);
            foreach (var app in apps.Where(a =>
                ComputerUseGrants.GrantedTier(settings.Current, context.SessionId, a) is null))
            {
                granted.Apps.Add(app);
                granted.Tiers[app] = ComputerUseGrants.TierName(ComputerUseGrants.ProposedTier(app));
            }

            settings.Save();
            if (!controller.Begin())
                return ToolResult.Error("Teach mode could not open its overlay window.");

            return ToolResult.Success(
                "Teach mode is on: the main window is hidden and the tooltip overlay is up. Take a " +
                "screenshot to see the screen, then call teach_step for the first tooltip. The user only " +
                "sees what you put in `explanation`.");
        }
    }

    private sealed class TeachStepTool(
        TeachController controller,
        ComputerBatchTool batch,
        ComputerUseService service) : ITool
    {
        public string Name => "teach_step";

        public string Description =>
            "Show one guided-tour tooltip and wait for the user to click Next. On Next, execute the actions, " +
            "take a fresh screenshot, and return both — you do NOT need a separate screenshot call between " +
            "steps. The returned image shows the state after your actions ran; anchor the next teach_step " +
            "against it. IMPORTANT — the user only sees the tooltip during teach mode. Put ALL narration in " +
            "`explanation`. Text you emit outside teach_step calls is NOT visible until teach mode ends. Pack " +
            "as many actions as possible into each step's `actions` array — the user waits through the whole " +
            "round trip between clicks, so one step that fills a form beats five steps that fill one field " +
            "each. Returns {exited:true} if the user clicks Exit — do not call teach_step again after that. " +
            "Take an initial screenshot before your FIRST teach_step to anchor it.";

        public JsonObject InputSchema =>
            CapturedMcpSchemas.Schema(InternalMcpServerNames.ComputerUse, "teach_step");

        public bool IsReadOnly => false;

        public string DescribeCall(JsonObject arguments)
        {
            var explanation = JsonArgs.GetString(arguments, "explanation") ?? "";
            return $"TeachStep({(explanation.Length > 48 ? explanation[..48] + "…" : explanation)})";
        }

        public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (!controller.IsActive)
                return ToolResult.Error("Teach mode is not active. Call request_teach_access first.");

            var explanation = JsonArgs.GetString(arguments, "explanation");
            if (string.IsNullOrWhiteSpace(explanation))
                return ToolResult.Error("explanation is required — it is the only thing the user sees.");
            if (arguments["actions"] is not JsonArray)
                return ToolResult.Error("actions must be an array (empty is fine for an explanatory step).");

            var decision = await controller.StepAsync(
                explanation,
                JsonArgs.GetString(arguments, "next_preview"),
                ReadAnchor(arguments, service),
                cancellationToken);
            if (decision == TeachDecision.Exit)
            {
                controller.End();
                return ToolResult.Success("{\"exited\": true}");
            }

            var result = await batch.ExecuteAsync(
                BatchArguments(arguments["actions"] as JsonArray), context, cancellationToken);
            return new ToolResult(
                (result.IsError ? "{\"stepFailed\": true}\n" : "{\"stepsCompleted\": 1}\n") + result.Content,
                result.IsError,
                result.Images);
        }
    }

    private sealed class TeachBatchTool(
        TeachController controller,
        ComputerBatchTool batch,
        ComputerUseService service) : ITool
    {
        public string Name => "teach_batch";

        public string Description =>
            "Queue multiple teach steps in one tool call. Parallels computer_batch: N steps → one model↔API " +
            "round trip instead of N. Each step still shows a tooltip and waits for the user's Next click, " +
            "but YOU aren't waiting for a round trip between steps. You can call teach_batch multiple times " +
            "in one tour — treat each batch as one predictable SEGMENT (typically: all the steps on one " +
            "page). The returned screenshot shows the state after the batch's final actions; anchor the NEXT " +
            "teach_batch against it. WITHIN a batch, all anchors and click coordinates refer to the " +
            "PRE-BATCH screenshot (same invariant as computer_batch) — for steps 2+ in a batch, either omit " +
            "anchor (centered tooltip) or target elements you know won't have moved. Good pattern: batch 5 " +
            "tooltips on page A (last step navigates) → read returned screenshot → batch 3 tooltips on page " +
            "B → done. Returns {exited:true, stepsCompleted:N} if the user clicks Exit — do NOT call again " +
            "after that; {stepsCompleted, stepFailed, ...} if an action errors mid-batch; otherwise " +
            "{stepsCompleted, results:[...]} plus a final screenshot. Fall back to individual teach_step " +
            "calls when you need to react to each intermediate screenshot.";

        public JsonObject InputSchema =>
            CapturedMcpSchemas.Schema(InternalMcpServerNames.ComputerUse, "teach_batch");

        public bool IsReadOnly => false;

        public string DescribeCall(JsonObject arguments)
        {
            var count = (arguments["steps"] as JsonArray)?.Count ?? 0;
            return $"TeachBatch({count} step{(count == 1 ? "" : "s")})";
        }

        public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (!controller.IsActive)
                return ToolResult.Error("Teach mode is not active. Call request_teach_access first.");
            if (arguments["steps"] is not JsonArray steps || steps.Count == 0)
                return ToolResult.Error("\"steps\" must be a non-empty array.");

            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i] is not JsonObject step)
                    return ToolResult.Error($"steps[{i}] must be an object");
                if (string.IsNullOrWhiteSpace(JsonArgs.GetString(step, "explanation")))
                    return ToolResult.Error($"steps[{i}].explanation is required — it is the only thing the user sees.");
                if (step["actions"] is not JsonArray)
                    return ToolResult.Error($"steps[{i}].actions must be an array (empty is fine).");
            }

            var completed = 0;
            var lines = new List<string>();
            var images = new List<JarvisCode.Core.Models.ImageBlock>();
            for (var i = 0; i < steps.Count; i++)
            {
                var step = (JsonObject)steps[i]!;
                var decision = await controller.StepAsync(
                    JsonArgs.GetString(step, "explanation")!,
                    JsonArgs.GetString(step, "next_preview"),
                    ReadAnchor(step, service),
                    cancellationToken);
                if (decision == TeachDecision.Exit)
                {
                    controller.End();
                    lines.Insert(0, $"{{\"exited\": true, \"stepsCompleted\": {completed}}}");
                    return new ToolResult(string.Join("\n", lines), IsError: false);
                }

                var result = await batch.ExecuteAsync(
                    BatchArguments(step["actions"] as JsonArray), context, cancellationToken);
                lines.Add($"[step {i + 1}/{steps.Count}] {result.Content}");
                if (result.IsError)
                {
                    lines.Insert(0, $"{{\"stepsCompleted\": {completed}, \"stepFailed\": {i + 1}}}");
                    return new ToolResult(string.Join("\n", lines), IsError: true);
                }

                completed++;

                // Only the final screenshot matters; the ones in between would cost
                // tokens for states the user already walked past.
                images.Clear();
                if (result.Images is { Count: > 0 })
                {
                    images.AddRange(result.Images);
                }
            }

            lines.Insert(0, $"{{\"stepsCompleted\": {completed}}}");
            return new ToolResult(string.Join("\n", lines), IsError: false, images.Count > 0 ? images : null);
        }
    }
}
