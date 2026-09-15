using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>One structured question posed to the user.</summary>
public sealed record UserQuestion(
    string Question, string Header, IReadOnlyList<UserQuestionOption> Options, bool MultiSelect);

public sealed record UserQuestionOption(string Label, string Description, string? Preview = null);

/// <summary>
/// The user's replies: one answer per question text (multi-select answers are
/// comma-separated), plus optional freeform text typed instead of an option.
/// </summary>
public sealed record UserQuestionAnswers(
    IReadOnlyDictionary<string, string> Answers, string? FreeText = null);

/// <summary>
/// The reference app's AskUserQuestion tool: pauses the turn on a structured
/// multiple-choice card the host renders, and returns the picks. The host
/// supplies the UI through <see cref="ToolExecutionContext.AskUserAsync"/>;
/// without it the tool reports itself unavailable.
/// </summary>
public sealed class AskUserQuestionTool : ITool
{
    public const int MaxQuestions = 4;
    public const int MinOptions = 2;
    public const int MaxOptions = 4;

    public string Name => "AskUserQuestion";

    public string Description => """
        Use this tool only when you are blocked on a decision that is genuinely the user's to make: one you cannot resolve from the request, the code, or sensible defaults.

        Usage notes:
        - Users will always be able to select "Other" to provide custom text input
        - Use multiSelect: true to allow multiple answers to be selected for a question
        - If you recommend a specific option, make that the first option in the list and add "(Recommended)" at the end of the label

        Plan mode note: In plan mode, use this tool to clarify requirements or choose between approaches BEFORE finalizing your plan. Do NOT use this tool to ask "Is my plan ready?", "Should I proceed?", or otherwise reference "the plan" in questions — the user cannot see the plan until you call ExitPlanMode for approval.

        Preview feature:
        Use the optional `preview` field on options when presenting concrete artifacts that users need to visually compare:
        - HTML mockups of UI layouts or components
        - Formatted code snippets showing different implementations
        - Visual comparisons or diagrams

        Preview content must be a self-contained HTML fragment (no <html>/<body> wrapper, no <script> or <style> tags — use inline style attributes instead). Do not use previews for simple preference questions where labels and descriptions suffice. Note: previews are only supported for single-select questions (not multiSelect).

        Reserve this for decisions where the user's answer changes what you do next — not for choices with a conventional default or facts you can verify in the codebase yourself. In those cases pick the obvious option, mention it in your response, and proceed.
        """;

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["questions"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Questions to ask the user (1-4 questions)",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["question"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "The complete question to ask the user. Should be clear, specific, and end with a " +
                                "question mark. If multiSelect is true, phrase it accordingly.",
                        },
                        ["header"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "Very short label displayed as a chip/tag (max 12 chars). " +
                                "Examples: \"Auth method\", \"Library\", \"Approach\".",
                        },
                        ["options"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] =
                                "The available choices for this question. Must have 2-4 options. Each option should " +
                                "be a distinct, mutually exclusive choice (unless multiSelect is enabled). There " +
                                "should be no 'Other' option, that will be provided automatically.",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["label"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "The display text for this option that the user will see and select. Should be concise (1-5 words) and clearly describe the choice.",
                                    },
                                    ["description"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Explanation of what this option means or what will happen if chosen. Useful for providing context about trade-offs or implications.",
                                    },
                                    ["preview"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Optional preview content rendered when this option is focused. Use for mockups, code snippets, or visual comparisons that help users compare options. See the tool description for the expected content format.",
                                    },
                                },
                                ["required"] = new JsonArray("label", "description"),
                            },
                        },
                        ["multiSelect"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Set to true to allow the user to select multiple options instead of just one. Use when choices are not mutually exclusive.",
                        },
                    },
                    ["required"] = new JsonArray("question", "header", "options"),
                },
            },
        },
        ["required"] = new JsonArray("questions"),
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments)
    {
        var first = (arguments["questions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        return $"AskUserQuestion({JsonArgs.GetString(first ?? [], "question") ?? "?"})";
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.AskUserAsync is null)
            return ToolResult.Error("AskUserQuestion is not available in this context.");

        if (arguments["questions"] is not JsonArray questionsJson || questionsJson.Count is 0 or > MaxQuestions)
            return ToolResult.Error($"questions must be an array of 1-{MaxQuestions} questions.");

        var questions = new List<UserQuestion>();
        foreach (var entry in questionsJson)
        {
            if (entry is not JsonObject question)
                return ToolResult.Error("Each question must be an object.");
            var text = JsonArgs.GetString(question, "question");
            var header = JsonArgs.GetString(question, "header");
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(header))
                return ToolResult.Error("Each question needs a non-empty question and header.");
            if (question["options"] is not JsonArray optionsJson ||
                optionsJson.Count is < MinOptions or > MaxOptions)
                return ToolResult.Error($"Each question needs {MinOptions}-{MaxOptions} options.");

            var multiSelect = JsonArgs.GetBool(question, "multiSelect");
            var options = new List<UserQuestionOption>();
            foreach (var optionEntry in optionsJson)
            {
                if (optionEntry is not JsonObject option ||
                    JsonArgs.GetString(option, "label") is not { Length: > 0 } label)
                    return ToolResult.Error("Each option needs a non-empty label.");
                // Previews are single-select only, per the reference.
                var preview = multiSelect ? null : JsonArgs.GetString(option, "preview");
                options.Add(new UserQuestionOption(
                    label, JsonArgs.GetString(option, "description") ?? "",
                    string.IsNullOrWhiteSpace(preview) ? null : preview));
            }

            questions.Add(new UserQuestion(text, header, options, multiSelect));
        }

        var answers = await context.AskUserAsync(questions, cancellationToken);
        if (answers is null)
            return ToolResult.Error(
                "The user dismissed the question without answering. Ask how they would like to proceed.");

        var result = new StringBuilder("The user answered:");
        foreach (var question in questions)
        {
            result.AppendLine();
            result.Append(question.Question);
            result.Append(" → ");
            result.Append(answers.Answers.TryGetValue(question.Question, out var answer) ? answer : "(no answer)");
        }

        if (!string.IsNullOrWhiteSpace(answers.FreeText))
        {
            result.AppendLine();
            result.Append("Additional note from the user: ");
            result.Append(answers.FreeText);
        }

        return ToolResult.Success(result.ToString());
    }
}
