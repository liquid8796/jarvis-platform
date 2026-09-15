using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

public enum TodoStatus
{
    Pending,
    InProgress,
    Completed,
}

public sealed record TodoItem(string Content, TodoStatus Status);

/// <summary>
/// Lets the model maintain a visible task list for multi-step work. Each call
/// replaces the whole list; the host renders it via the context's TodoSink.
/// </summary>
public sealed class TodoTool : ITool
{
    public string Name => "todo_write";

    public string Description =>
        "Replaces your visible task list for the current session. Use it for multi-step work so the user can " +
        "follow progress: add items up front, mark exactly one as in_progress while you work on it, and mark " +
        "items completed immediately when done. Statuses: pending, in_progress, completed.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["todos"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "The full task list (replaces the previous one)",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Short imperative task description" },
                        ["status"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("pending", "in_progress", "completed"),
                        },
                    },
                    ["required"] = new JsonArray("content", "status"),
                },
            },
        },
        ["required"] = new JsonArray("todos"),
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments)
    {
        int count = (arguments["todos"] as JsonArray)?.Count ?? 0;
        return $"Todo({count} item{(count == 1 ? "" : "s")})";
    }

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (arguments["todos"] is not JsonArray todosJson)
            return Task.FromResult(ToolResult.Error("todos (array) is required."));

        var items = new List<TodoItem>();
        foreach (var entry in todosJson)
        {
            if (entry is not JsonObject todo)
                return Task.FromResult(ToolResult.Error("Each todo must be an object with content and status."));
            var content = JsonArgs.GetString(todo, "content");
            if (string.IsNullOrWhiteSpace(content))
                return Task.FromResult(ToolResult.Error("Each todo needs a non-empty content."));
            var statusText = JsonArgs.GetString(todo, "status") ?? "";
            TodoStatus? status = statusText switch
            {
                "pending" => TodoStatus.Pending,
                "in_progress" => TodoStatus.InProgress,
                "completed" => TodoStatus.Completed,
                _ => null,
            };
            if (status is null)
                return Task.FromResult(ToolResult.Error(
                    $"Unknown status '{statusText}'. Use pending, in_progress or completed."));
            items.Add(new TodoItem(content, status.Value));
        }

        if (context.TodoSink is null)
            return Task.FromResult(ToolResult.Error("The todo list is not available in this context."));
        context.TodoSink(items);

        int done = items.Count(i => i.Status == TodoStatus.Completed);
        var response = $"Todo list updated: {items.Count} item(s), {done} completed.";

        // Claw-code verification nudge: a substantial list that just became "all done"
        // without any verification step is the classic unverified-completion smell.
        bool allDone = items.Count >= 3 && done == items.Count;
        bool mentionsVerification = items.Any(i =>
            i.Content.Contains("verif", StringComparison.OrdinalIgnoreCase) ||
            i.Content.Contains("test", StringComparison.OrdinalIgnoreCase));
        if (allDone && !mentionsVerification)
        {
            response += " All items are complete, but none of them was a verification step — " +
                "confirm the work actually runs (build, tests, or a manual check) before reporting done.";
        }

        return Task.FromResult(ToolResult.Success(response));
    }
}
