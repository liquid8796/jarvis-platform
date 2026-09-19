using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;
using Json.Schema;
namespace Jarvis.Agent.Windows;
public interface IUserQuestions
{
    Task<UserQuestionAnswers?> AskAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken);
}
/// <summary>Adapt the baseline tool without reimplementing its schema or execution logic.</summary>
public sealed class LegacyToolAdapter : IAgentTool
{
    private readonly ITool _tool;
    private readonly IUserQuestions _questions;
    private readonly IArtifactSink _artifacts;
    private readonly JsonSchema _schema;
    private readonly string _privateRoot;
    private readonly SessionFileObservations? _fileObservations;
    public ToolDescriptor Descriptor { get; }
    public LegacyToolAdapter(ITool tool, string category, IUserQuestions questions, IArtifactSink artifacts, string? privateRoot = null, SessionFileObservations? fileObservations = null)
    {
        _tool = tool; _questions = questions; _artifacts = artifacts; _fileObservations = fileObservations;
        _privateRoot = privateRoot ?? AgentProfile.Root;
        Descriptor = CreateDescriptor(tool, category);
        _schema = SchemaGuard.Compile(Descriptor.InputSchema);
    }
    internal static ToolDescriptor CreateDescriptor(ITool tool, string category)
    {
        var inputSchema = tool.InputSchema.DeepClone().AsObject();
        if (category == "browser" && tool.Name == "qa")
            ((JsonObject)inputSchema["properties"]!)["spec"] = JsonSerializer.SerializeToNode(FrontendQaSchemas.Spec(), WireJson.Options);
        if (category is "filesystem" or "git" or "shell" or "browser")
        {
            var properties = inputSchema["properties"] as JsonObject ?? new JsonObject();
            if (inputSchema["properties"] is null) inputSchema["properties"] = properties;
            properties["workingDirectory"] = new JsonObject
            {
                ["type"] = "string", ["minLength"] = 1,
                ["description"] = "Optional starting directory, including paths outside selected project folders. Relative paths use this directory."
            };
        }
        if (category == "browser")
        {
            var properties = inputSchema["properties"] as JsonObject ?? new JsonObject();
            if (inputSchema["properties"] is null) inputSchema["properties"] = properties;
            properties["browserFamily"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("auto", "dev", "chrome", "edge", "extension"),
                ["description"] = "Optional browser routing. auto sends localhost/127.0.0.1 targets to the isolated dev browser and other targets to the external extension browser."
            };
        }
        var schema = JsonSerializer.Deserialize<JsonElement>(inputSchema.ToJsonString());
        return new(category + "." + tool.Name, category + "__" + tool.Name, category,
            Describe(tool, category) + (category == "visualize" ? " Jarvis Agent renders locally; ChatGPT inline embedding and sendPrompt are not provided by this host." : ""),
            schema, tool.IsReadOnly,
            category is "computer" or "browser" or "git" || (category == "visualize" && tool.Name == "show_widget"));
    }

    private static string Describe(ITool tool, string category)
    {
        var description = tool.Description;
        if (category == "browser" && tool.Name == "file_upload")
            description = description.Replace(
                "Only files the user has shared with this session (attachments, the session's outputs/uploads " +
                "folders, or folders the user has connected) can be uploaded; other paths will be rejected.",
                "Files outside selected project folders are supported; uploads follow local tool permissions.");
        if (category == "computer")
            description += " Jarvis Agent: when THIS tool has local Full permission, app/tier/clipboard " +
                "grant prompts are not needed. Other tools retain their own settings. Windows permissions and denied apps still apply.";
        if (category == "filesystem" && tool.Name is "Write" or "Edit" or "NotebookEdit")
            description += " Read existing files in this chat before changing them. FILE_CHANGED requires a fresh read and reconciliation; other sessions' reads do not count.";
        return description;
    }
    public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext execution, CancellationToken cancellationToken)
    {
        var args = JsonNode.Parse(arguments.GetRawText()) as JsonObject ?? throw new ArgumentException("Object arguments required.");
        if (!SchemaGuard.Matches(_schema, arguments)) throw new ArgumentException("Arguments do not match the local tool schema.");
        cancellationToken.ThrowIfCancellationRequested();
        using var consent = ToolConsentScope.Enter(execution.FullPermission, cancellationToken);
        var workingDirectory = execution.Workspace;
        if (Descriptor.Category is "filesystem" or "git" or "shell" or "browser" &&
            args.Remove("workingDirectory", out var directory) && directory is not null)
            workingDirectory = WorkspaceDirectories.Normalize(WorkspaceDirectories.ResolvePath(directory.GetValue<string>(), execution.Workspace));
        if (Descriptor.Category is "filesystem" or "git" or "browser") NormalizePaths(args, workingDirectory);
        if (Descriptor.Category is "shell" or "git" ||
            (Descriptor.Category == "filesystem" && string.IsNullOrWhiteSpace(workingDirectory) &&
             !args.Any(pair => SinglePaths.Contains(pair.Key) || ManyPaths.Contains(pair.Key))))
            workingDirectory = WorkspaceDirectories.ResolvePath(null, workingDirectory);
        foreach (var field in new[] { "target", "revision", "ref" })
            if (Descriptor.Category == "git" && args[field]?.GetValue<string>() is { } revision && revision.StartsWith('-'))
                throw new ArgumentException("Git revisions must not be command-line options.");
        if (Descriptor.Category == "shell" && args["run_in_background"]?.GetValue<bool>() == true)
            return ToolReply.Error("Use process__start/read/cancel for managed background jobs. Baseline detached shell jobs are disabled in this host.");
        if (Descriptor.Category == "visualize" && _tool.Name == "show_widget")
        {
            var code = args["widget_code"]?.GetValue<string>() ?? "";
            if (code.Length > 240000) throw new ArgumentException("Widget is too large.");
            var widget = new WidgetArtifact(args["title"]?.GetValue<string>() ?? "Jarvis visual", code);
            await _artifacts.ShowAsync(widget, cancellationToken);
            return new ToolReply("Visual artifact delivered to the local Jarvis Agent host. CLI hosts save the artifact rather than displaying an inline chat widget.", Widget: widget);
        }
        var context = new ToolExecutionContext { WorkingDirectory = workingDirectory, CallId = execution.CallId,
            AdditionalDirectories = execution.AdditionalDirectories,
            EnforceWorkspaceFileScope = false,
            SessionId = execution.SessionId, ShellTimeout = TimeSpan.FromSeconds(110), MaxOutputChars = 60000,
            AskUserAsync = _questions.AskAsync, SessionLifetime = cancellationToken };
        var filePath = Descriptor.Category == "filesystem" ? (args["file_path"] ?? args["notebook_path"])?.GetValue<string>() : null;
        var trackFile = _fileObservations is not null && filePath is not null;
        var observationScope = trackFile ? execution.IsolationScopeId : null;
        SessionFileObservations.FileObservation? before = null;
        if (trackFile)
        {
            if (!_tool.IsReadOnly) await _fileObservations!.ValidateWriteAsync(observationScope!, filePath!, cancellationToken);
            before = await SessionFileObservations.CaptureAsync(filePath!, cancellationToken);
        }
        ToolResult result;
        try { result = await _tool.ExecuteAsync(args, context, cancellationToken); }
        catch
        {
            if (trackFile && !_tool.IsReadOnly) _fileObservations!.Forget(observationScope!, filePath!);
            throw;
        }
        if (trackFile)
        {
            if (!result.IsError)
            {
                var after = await SessionFileObservations.CaptureAsync(filePath!, cancellationToken);
                if (_tool.IsReadOnly && before != after)
                {
                    _fileObservations!.Forget(observationScope!, filePath!);
                    throw new AgentRequestException("FILE_CHANGED", "File changed while being read. Read it again before editing.");
                }
                _fileObservations!.Remember(observationScope!, filePath!, after);
            }
            else if (!_tool.IsReadOnly) _fileObservations!.Forget(observationScope!, filePath!);
        }
        return new ToolReply(result.Content + (result.FollowUpText is null ? "" : "\n" + result.FollowUpText), result.IsError,
            result.Images?.Select(i => new WireImage(i.MediaType, i.Base64Data)).ToArray());
    }
    private static readonly HashSet<string> SinglePaths = ["file_path", "notebook_path", "path", "filePath", "directory"];
    private static readonly HashSet<string> ManyPaths = ["paths", "filePaths", "file_paths"];
    private void NormalizePaths(JsonObject args, string workingDirectory)
    {
        foreach (var pair in args.ToArray())
        {
            if (SinglePaths.Contains(pair.Key) && pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
            { var full = WorkspaceDirectories.ResolvePath(text, workingDirectory); RejectProfile(full); args[pair.Key] = full; }
            else if (ManyPaths.Contains(pair.Key) && pair.Value is JsonArray paths)
            {
                for (var i = 0; i < paths.Count; i++)
                    if (paths[i]?.GetValue<string>() is { } path) { var full = WorkspaceDirectories.ResolvePath(path, workingDirectory); RejectProfile(full); paths[i] = full; }
            }
            else if (pair.Value is JsonObject nested) NormalizePaths(nested, workingDirectory);
            else if (pair.Value is JsonArray array) foreach (var nestedObject in array.OfType<JsonObject>()) NormalizePaths(nestedObject, workingDirectory);
        }
    }
    private void RejectProfile(string path)
    {
        var root = ToolExecutionResources.CanonicalPath(_privateRoot);
        var target = ToolExecutionResources.CanonicalPath(path);
        if (target.Equals(root, StringComparison.OrdinalIgnoreCase) || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The agent's private profile cannot be accessed by file tools.");
    }
}
