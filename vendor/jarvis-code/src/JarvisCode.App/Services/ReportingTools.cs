using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// ReportFindings (typed code-review findings rendered as a card in the
/// transcript) and SendUserFile (surface a file to the user) — the reference
/// pair, rendered through transcript notices.
/// </summary>
public static class ReportingTools
{
    public static IReadOnlyList<ITool> Create(ChatViewModel viewModel, Action<Action> onUi) =>
        [new ReportFindingsTool(viewModel, onUi), new SendUserFileTool(viewModel, onUi)];

    /// <summary>The headless host receives both rendered text and the original typed findings.</summary>
    public static IReadOnlyList<ITool> CreateHeadless(Action<string, JsonArray> report) =>
        [new ReportFindingsTool(null, action => action(), report)];

    private sealed class ReportFindingsTool(
        ChatViewModel? viewModel, Action<Action> onUi, Action<string, JsonArray>? report = null) : ITool
    {
        public string Name => "ReportFindings";

        public string Description =>
            "Report code-review findings as a typed list so they render as a summary card for the user. Call it " +
            "once with the verified findings ranked most-severe first (empty array if nothing survived " +
            "verification), and do not also print the findings as text.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["findings"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Verified findings, most-severe first; empty if none survived",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["file"] = new JsonObject { ["type"] = "string", ["description"] = "Repo-relative path" },
                            ["line"] = new JsonObject { ["type"] = "integer", ["description"] = "1-indexed anchor line" },
                            ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "One-sentence statement of the defect" },
                            ["failure_scenario"] = new JsonObject { ["type"] = "string", ["description"] = "Concrete inputs/state → wrong output" },
                            ["category"] = new JsonObject { ["type"] = "string", ["description"] = "Short slug: correctness, simplification, efficiency…" },
                            ["verdict"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("CONFIRMED", "PLAUSIBLE") },
                        },
                        ["required"] = new JsonArray("file", "summary"),
                    },
                },
            },
            ["required"] = new JsonArray("findings"),
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) =>
            $"ReportFindings({(arguments["findings"] as JsonArray)?.Count ?? 0})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (arguments["findings"] is not JsonArray findings)
                return Task.FromResult(ToolResult.Error("findings (an array) is required."));

            var text = new StringBuilder();
            if (findings.Count == 0)
            {
                text.Append("Review clean — no verified findings.");
            }
            else
            {
                text.AppendLine($"Review findings ({findings.Count}):");
                int index = 0;
                foreach (var finding in findings.OfType<JsonObject>())
                {
                    index++;
                    var file = JsonArgs.GetString(finding, "file") ?? "?";
                    var line = finding["line"]?.GetValue<double>();
                    var verdict = JsonArgs.GetString(finding, "verdict");
                    var category = JsonArgs.GetString(finding, "category");
                    text.AppendLine(
                        $"  {index}. {file}{(line is > 0 ? $":{line:0}" : "")}" +
                        $"{(category is { Length: > 0 } ? $" [{category}]" : "")}" +
                        $"{(verdict is { Length: > 0 } ? $" {verdict}" : "")}" +
                        $" — {JsonArgs.GetString(finding, "summary") ?? ""}");
                    if (JsonArgs.GetString(finding, "failure_scenario") is { Length: > 0 } scenario)
                        text.AppendLine($"     {scenario}");
                }
            }

            // The findings also reach the diff pane, where the reference's stepper walks
            // them, applies them and re-runs the review.
            var parsed = new List<ReviewFinding>();
            var findingIndex = 0;
            foreach (var finding in findings.OfType<JsonObject>())
            {
                findingIndex++;
                parsed.Add(new ReviewFinding
                {
                    Id = $"finding-{findingIndex}",
                    File = JsonArgs.GetString(finding, "file") ?? "?",
                    Line = finding["line"]?.GetValue<double>() is { } value && value > 0
                        ? (int)value
                        : null,
                    Summary = JsonArgs.GetString(finding, "summary") ?? "",
                    Detail = JsonArgs.GetString(finding, "failure_scenario"),
                });
            }

            var rendered = text.ToString().TrimEnd();
            if (report is not null)
                report(rendered, (JsonArray)findings.DeepClone());
            else if (viewModel is not null) onUi(() =>
            {
                viewModel.Transcript.Add(new NoticeItem { Text = rendered });
                ReviewFindingsStore.Set(viewModel.Session.Id, parsed);
            });
            return Task.FromResult(ToolResult.Success(
                findings.Count == 0
                    ? "Reported: no findings."
                    : report is null
                        ? $"Reported {findings.Count} finding(s) — they render as a card; no need to repeat them in prose."
                        : $"Reported {findings.Count} finding(s) to the user; no need to repeat them in prose."));
        }
    }

    private sealed class SendUserFileTool(ChatViewModel viewModel, Action<Action> onUi) : ITool
    {
        public string Name => "SendUserFile";

        public string Description =>
            "Send a finished file to the user — a generated report, diagram, build artifact — so it is surfaced " +
            "in the conversation, not just mentioned. Do not send routine working files or every incremental save.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["file_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path of the file to surface" },
                ["caption"] = new JsonObject { ["type"] = "string", ["description"] = "Optional one-line context" },
            },
            ["required"] = new JsonArray("file_path"),
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) =>
            $"SendUserFile({JsonArgs.GetString(arguments, "file_path") ?? "?"})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var raw = JsonArgs.GetString(arguments, "file_path");
            if (string.IsNullOrWhiteSpace(raw))
                return Task.FromResult(ToolResult.Error("file_path is required."));
            var path = context.ResolvePath(raw);
            if (!File.Exists(path))
                return Task.FromResult(ToolResult.Error($"File not found: {path}"));

            long bytes = new FileInfo(path).Length;
            var size = bytes switch
            {
                >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
                >= 1024 => $"{bytes / 1024.0:0.#} KB",
                _ => $"{bytes} B",
            };
            var caption = JsonArgs.GetString(arguments, "caption");
            var text = $"📎 {Path.GetFileName(path)} ({size}) — {path}" +
                       (caption is { Length: > 0 } ? $"\n{caption}" : "");
            onUi(() => viewModel.Transcript.Add(new NoticeItem { Text = text }));
            return Task.FromResult(ToolResult.Success($"Sent {path} to the user."));
        }
    }
}
