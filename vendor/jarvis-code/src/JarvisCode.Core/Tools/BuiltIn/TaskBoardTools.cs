using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Agent;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// The session's structured task board — the reference CLI's TaskCreate /
/// TaskGet / TaskList / TaskUpdate quartet, renamed to this harness's
/// snake_case convention. The docs carry the reference's own text, including
/// the segments it only shows inside an agent team.
/// </summary>
internal static class TaskBoardDocs
{
    public const string CreateSummary = "Create a new task in the task list";
    public const string GetSummary = "Get a task by ID from the task list";
    public const string ListSummary = "List all tasks in the task list";
    public const string UpdateSummary = "Update a task in the task list";

    public static string Create(bool inTeam)
    {
        var assigned = inTeam ? " and potentially assigned to teammates" : "";
        var teamTips = inTeam
            ? "- Include enough detail in the description for another agent to understand and complete the task\n" +
              "- New tasks are created with status 'pending' and no owner - use TaskUpdate with the `owner` parameter to assign them\n"
            : "";
        return $"""
            Use this tool to create a structured task list for your current coding session. This helps you track progress, organize complex tasks, and demonstrate thoroughness to the user.
            It also helps the user understand the progress of the task and overall progress of their requests.

            ## When to Use This Tool

            Use this tool proactively in these scenarios:

            - Complex multi-step tasks - When a task requires 3 or more distinct steps or actions
            - Non-trivial and complex tasks - Tasks that require careful planning or multiple operations{assigned}
            - Plan mode - When using plan mode, create a task list to track the work
            - User explicitly requests todo list - When the user directly asks you to use the todo list
            - User provides multiple tasks - When users provide a list of things to be done (numbered or comma-separated)
            - After receiving new instructions - Immediately capture user requirements as tasks
            - When you start working on a task - Mark it as in_progress BEFORE beginning work
            - After completing a task - Mark it as completed and add any new follow-up tasks discovered during implementation

            ## When NOT to Use This Tool

            Skip using this tool when:
            - There is only a single, straightforward task
            - The task is trivial and tracking it provides no organizational benefit
            - The task can be completed in less than 3 trivial steps
            - The task is purely conversational or informational

            NOTE that you should not use this tool if there is only one trivial task to do. In this case you are better off just doing the task directly.

            ## Task Fields

            - **subject**: A brief, actionable title in imperative form (e.g., "Fix authentication bug in login flow")
            - **description**: What needs to be done
            - **activeForm** (optional): Present continuous form shown in the spinner when the task is in_progress (e.g., "Fixing authentication bug"). If omitted, the spinner shows the subject instead.

            All tasks are created with status `pending`.

            ## Tips

            - Create tasks with clear, specific subjects that describe the outcome
            - After creating tasks, use TaskUpdate to set up dependencies (blocks/blockedBy) if needed
            {teamTips}- Check TaskList first to avoid creating duplicate tasks

            """;
    }

    public const string Get = """
        Use this tool to retrieve a task by its ID from the task list.

        ## When to Use This Tool

        - When you need the full description and context before starting work on a task
        - To understand task dependencies (what it blocks, what blocks it)
        - After being assigned a task, to get complete requirements

        ## Output

        Returns full task details:
        - **subject**: Task title
        - **description**: Detailed requirements and context
        - **status**: 'pending', 'in_progress', or 'completed'
        - **blocks**: Tasks waiting on this one to complete
        - **blockedBy**: Tasks that must complete before this one can start

        ## Tips

        - After fetching a task, verify its blockedBy list is empty before beginning work.
        - Use TaskList to see all tasks in summary form.

        """;

    public static string List(bool inTeam)
    {
        var assignLine = inTeam ? "- Before assigning tasks to teammates, to see what's available\n" : "";
        var teammateWorkflow = inTeam
            ? """

              ## Teammate Workflow

              When working as a teammate:
              1. After completing your current task, call TaskList to find available work
              2. Look for tasks with status 'pending', no owner, and empty blockedBy
              3. **Prefer tasks in ID order** (lowest ID first) when multiple tasks are available, as earlier tasks often set up context for later ones
              4. Claim an available task using TaskUpdate (set `owner` to your name), or wait for leader assignment
              5. If blocked, focus on unblocking tasks or notify the team lead

              """
            : "";
        return $"""
            Use this tool to list all tasks in the task list.

            ## When to Use This Tool

            - To see what tasks are available to work on (status: 'pending', no owner, not blocked)
            - To check overall progress on the project
            - To find tasks that are blocked and need dependencies resolved
            {assignLine}- After completing a task, to check for newly unblocked work or claim the next available task
            - **Prefer working on tasks in ID order** (lowest ID first) when multiple tasks are available, as earlier tasks often set up context for later ones

            ## Output

            Returns a summary of each task:
            - **id**: Task identifier (use with TaskGet, TaskUpdate)
            - **subject**: Brief description of the task
            - **status**: 'pending', 'in_progress', or 'completed'
            - **owner**: Agent ID if assigned, empty if available
            - **blockedBy**: List of open task IDs that must be resolved first (tasks with blockedBy cannot be claimed until dependencies resolve)

            Use TaskGet with a specific task ID to view full details including description and comments.
            {teammateWorkflow}
            """;
    }

    public const string Update = """
        Use this tool to update a task in the task list.

        ## When to Use This Tool

        **Mark tasks as resolved:**
        - When you have completed the work described in a task
        - When a task is no longer needed or has been superseded
        - IMPORTANT: Always mark your assigned tasks as resolved when you finish them
        - After resolving, call TaskList to find your next task

        - ONLY mark a task as completed when you have FULLY accomplished it
        - If you encounter errors, blockers, or cannot finish, keep the task as in_progress
        - When blocked, create a new task describing what needs to be resolved
        - Never mark a task as completed if:
          - Tests are failing
          - Implementation is partial
          - You encountered unresolved errors
          - You couldn't find necessary files or dependencies

        **Delete tasks:**
        - When a task is no longer relevant or was created in error
        - Setting status to `deleted` permanently removes the task

        **Update task details:**
        - When requirements change or become clearer
        - When establishing dependencies between tasks

        ## Fields You Can Update

        - **status**: The task status (see Status Workflow below)
        - **subject**: Change the task title (imperative form, e.g., "Run tests")
        - **description**: Change the task description
        - **activeForm**: Present continuous form shown in spinner when in_progress (e.g., "Running tests")
        - **owner**: Change the task owner (agent name)
        - **metadata**: Merge metadata keys into the task (set a key to null to delete it)
        - **addBlocks**: Mark tasks that cannot start until this one completes
        - **addBlockedBy**: Mark tasks that must complete before this one can start

        ## Status Workflow

        Status progresses: `pending` → `in_progress` → `completed`

        Use `deleted` to permanently remove the task.

        ## Staleness

        Make sure to read a task's latest state using `TaskGet` before updating it.

        ## Examples

        Mark task as in progress when starting work:
        ```json
        {"taskId": "1", "status": "in_progress"}
        ```

        Mark task as completed after finishing work:
        ```json
        {"taskId": "1", "status": "completed"}
        ```

        Delete a task:
        ```json
        {"taskId": "1", "status": "deleted"}
        ```

        Claim a task by setting owner:
        ```json
        {"taskId": "1", "owner": "my-name"}
        ```

        Set up task dependencies:
        ```json
        {"taskId": "2", "addBlockedBy": ["1"]}
        ```

        """;
}

/// <summary>Creates one task on the session board (always <c>pending</c>).</summary>
public sealed class TaskCreateTool(bool inTeam = false) : ITool
{
    public string Name => "TaskCreate";

    /// <summary>The team-only doc segments appear once the session has a team.</summary>
    public string Description => TaskBoardDocs.Create(inTeam);

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["subject"] = new JsonObject { ["type"] = "string", ["description"] = "A brief title for the task" },
            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "What needs to be done" },
            ["activeForm"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Present continuous form shown in spinner when in_progress (e.g., \"Running tests\")",
            },
            ["metadata"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Arbitrary metadata to attach to the task",
            },
        },
        ["required"] = new JsonArray("subject", "description"),
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"TaskCreate({JsonArgs.GetString(arguments, "subject")})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Tasks is not { } board)
            return Task.FromResult(ToolResult.Error("The task list is not available in this context."));

        var subject = JsonArgs.GetString(arguments, "subject");
        if (string.IsNullOrWhiteSpace(subject))
            return Task.FromResult(ToolResult.Error("subject is required."));
        var description = JsonArgs.GetString(arguments, "description") ?? "";

        var metadata = ReadMetadata(arguments["metadata"] as JsonObject);
        var task = board.Create(subject, description, JsonArgs.GetString(arguments, "activeForm"), metadata);

        context.FireHook?.Invoke(Hooks.HookEvent.TaskCreated, new JsonObject
        {
            ["task_id"] = task.Id,
            ["subject"] = task.Subject,
            ["description"] = task.Description,
            ["agent_name"] = context.Team?.AgentName,
        });

        return Task.FromResult(ToolResult.Success($"Task #{task.Id} created successfully: {task.Subject}"));
    }

    internal static Dictionary<string, JsonNode?>? ReadMetadata(JsonObject? source)
    {
        if (source is null)
            return null;
        var metadata = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
            metadata[key] = value?.DeepClone();
        return metadata;
    }
}

/// <summary>Reads one task in full, including both dependency directions.</summary>
public sealed class TaskGetTool : ITool
{
    public string Name => "TaskGet";

    public string Description => TaskBoardDocs.Get;

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["taskId"] = new JsonObject { ["type"] = "string", ["description"] = "The ID of the task to retrieve" },
        },
        ["required"] = new JsonArray("taskId"),
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) => $"TaskGet(#{JsonArgs.GetString(arguments, "taskId")})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Tasks is not { } board)
            return Task.FromResult(ToolResult.Error("The task list is not available in this context."));

        var id = JsonArgs.GetString(arguments, "taskId");
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ToolResult.Error("taskId is required."));

        var task = board.Get(id);
        if (task is null)
            return Task.FromResult(ToolResult.Success("Task not found"));

        var lines = new List<string>
        {
            $"Task #{task.Id}: {task.Subject}",
            $"Status: {TaskBoard.StatusName(task.Status)}",
            $"Description: {task.Description}",
        };
        if (task.BlockedBy.Count > 0)
            lines.Add($"Blocked by: {string.Join(", ", task.BlockedBy.Select(x => $"#{x}"))}");
        if (task.Blocks.Count > 0)
            lines.Add($"Blocks: {string.Join(", ", task.Blocks.Select(x => $"#{x}"))}");

        return Task.FromResult(ToolResult.Success(string.Join("\n", lines)));
    }
}

/// <summary>Lists the board in summary form; completed blockers are dropped.</summary>
public sealed class TaskListTool(bool inTeam = false) : ITool
{
    public string Name => "TaskList";

    public string Description => TaskBoardDocs.List(inTeam);

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) => "TaskList()";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Tasks is not { } board)
            return Task.FromResult(ToolResult.Error("The task list is not available in this context."));

        var tasks = board.Visible();
        if (tasks.Count == 0)
            return Task.FromResult(ToolResult.Success("No tasks found"));

        var rendered = new StringBuilder();
        foreach (var task in tasks)
        {
            if (rendered.Length > 0)
                rendered.Append('\n');
            var owner = string.IsNullOrEmpty(task.Owner) ? "" : $" ({task.Owner})";
            var blockers = board.OpenBlockers(task);
            var blocked = blockers.Count > 0
                ? $" [blocked by {string.Join(", ", blockers.Select(x => $"#{x}"))}]"
                : "";
            rendered.Append($"#{task.Id} [{TaskBoard.StatusName(task.Status)}] {task.Subject}{owner}{blocked}");
        }

        return Task.FromResult(ToolResult.Success(rendered.ToString()));
    }
}

/// <summary>Updates one task; <c>deleted</c> removes it permanently.</summary>
public sealed class TaskUpdateTool : ITool
{
    public string Name => "TaskUpdate";

    public string Description => TaskBoardDocs.Update;

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["taskId"] = new JsonObject { ["type"] = "string", ["description"] = "The ID of the task to update" },
            ["subject"] = new JsonObject { ["type"] = "string", ["description"] = "New subject for the task" },
            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "New description for the task" },
            ["activeForm"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Present continuous form shown in spinner when in_progress (e.g., \"Running tests\")",
            },
            ["status"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("pending", "in_progress", "completed", "deleted"),
                ["description"] = "New status for the task",
            },
            ["addBlocks"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Task IDs that this task blocks",
            },
            ["addBlockedBy"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Task IDs that block this task",
            },
            ["owner"] = new JsonObject { ["type"] = "string", ["description"] = "New owner for the task" },
            ["metadata"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Metadata keys to merge into the task. Set a key to null to delete it.",
            },
        },
        ["required"] = new JsonArray("taskId"),
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments)
    {
        var status = JsonArgs.GetString(arguments, "status");
        var id = JsonArgs.GetString(arguments, "taskId");
        return status is null ? $"TaskUpdate(#{id})" : $"TaskUpdate(#{id} → {status})";
    }

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Tasks is not { } board)
            return Task.FromResult(ToolResult.Error("The task list is not available in this context."));

        var id = JsonArgs.GetString(arguments, "taskId");
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ToolResult.Error("taskId is required."));

        var statusText = JsonArgs.GetString(arguments, "status");
        if (statusText is not null && statusText != "deleted" && TaskBoard.ParseStatus(statusText) is null)
            return Task.FromResult(ToolResult.Error(
                $"Unknown status '{statusText}'. Use pending, in_progress, completed or deleted."));

        var existing = board.Get(id);
        if (existing is null)
            return Task.FromResult(ToolResult.Success($"Task #{id} not found"));

        if (statusText == "deleted")
        {
            var deleted = board.Delete(id);
            return Task.FromResult(ToolResult.Success(deleted
                ? $"Updated task #{id} deleted"
                : "Failed to delete task"));
        }

        // The reference fires TaskCompleted before it writes, and a blocking
        // hook keeps the task open with the hook's reason as the result.
        if (TaskBoard.ParseStatus(statusText) == TaskState.Completed && existing.Status != TaskState.Completed)
        {
            context.FireHook?.Invoke(Hooks.HookEvent.TaskCompleted, new JsonObject
            {
                ["task_id"] = existing.Id,
                ["subject"] = existing.Subject,
                ["description"] = existing.Description,
                ["agent_name"] = context.Team?.AgentName,
            });
        }

        var request = new TaskUpdateRequest
        {
            Subject = JsonArgs.GetString(arguments, "subject"),
            Description = JsonArgs.GetString(arguments, "description"),
            ActiveForm = JsonArgs.GetString(arguments, "activeForm"),
            Owner = JsonArgs.GetString(arguments, "owner"),
            Status = TaskBoard.ParseStatus(statusText),
            Metadata = TaskCreateTool.ReadMetadata(arguments["metadata"] as JsonObject),
            AddBlocks = ReadIds(arguments["addBlocks"] as JsonArray),
            AddBlockedBy = ReadIds(arguments["addBlockedBy"] as JsonArray),
        };

        var outcome = board.Update(id, request, context.Team?.AgentName);
        if (!outcome.Success)
            return Task.FromResult(ToolResult.Success($"Task #{id} not found"));

        var message = $"Updated task #{id} {string.Join(", ", outcome.UpdatedFields)}";
        if (outcome.StatusTo == "completed" && context.Team is not null)
        {
            message += "\n\nTask completed. Call TaskList now to find your next available task " +
                "or see if your work unblocked others.";
        }

        return Task.FromResult(ToolResult.Success(message));
    }

    private static List<string>? ReadIds(JsonArray? array)
    {
        if (array is null)
            return null;
        var ids = new List<string>();
        foreach (var entry in array)
        {
            var id = entry?.GetValue<object>()?.ToString();
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }

        return ids;
    }
}
