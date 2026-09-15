using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Customization;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Lets the model run the user's custom slash commands (the reference
/// SlashCommand tool): the command's markdown template expands and comes back
/// as instructions to follow. Skills stay on the skill tool; built-in commands
/// are UI actions and are not available here.
/// </summary>
public sealed class SlashCommandTool(IReadOnlyList<CustomCommandDefinition> commands) : ITool
{
    public string Name => "slash_command";

    public string Description
    {
        get
        {
            var text = new StringBuilder(
                "Runs one of the user's custom slash commands and returns its expanded instructions for you to " +
                "follow. Use it when the user names a command, or when a listed command matches the task at hand. " +
                "Available commands:\n");
            foreach (var command in commands)
                text.AppendLine($"- /{command.Name}" +
                    (string.IsNullOrWhiteSpace(command.Description) ? "" : $" — {command.Description}"));
            return text.ToString().TrimEnd();
        }
    }

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("command", SchemaBuilder.String("The command with its arguments, e.g. \"/fix-issue 123\"")),
        ],
        "command");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"SlashCommand({JsonArgs.GetString(arguments, "command") ?? "?"})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var raw = JsonArgs.GetString(arguments, "command")?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return Task.FromResult(ToolResult.Error("command is required, e.g. \"/fix-issue 123\"."));

        var parts = raw.TrimStart('/').Split(' ', 2);
        var name = parts[0];
        var args = parts.Length > 1 ? parts[1].Trim() : "";
        var command = commands.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            return Task.FromResult(ToolResult.Error(
                $"Unknown command '/{name}'. Available: {string.Join(", ", commands.Select(c => "/" + c.Name))}."));
        }

        var expanded = command.BuildPrompt(args);
        return Task.FromResult(ToolResult.Success(context.Truncate(
            $"The user's /{command.Name} command expands to the following instructions — follow them as if " +
            $"the user had sent them:\n\n{expanded}", "command contents")));
    }
}
