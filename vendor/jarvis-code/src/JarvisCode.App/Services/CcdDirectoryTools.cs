using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// What a folder request resolved to. <see cref="Ok"/> false with no
/// <see cref="Error"/> is the user closing the picker, which the reference
/// reports as an ordinary result rather than a failure.
/// </summary>
public sealed record FolderPickResult(bool Ok, string? Resolved, string? Error = null)
{
    public static FolderPickResult Cancelled() => new(false, null);

    public static FolderPickResult Failed(string error) => new(false, null, error);

    public static FolderPickResult Granted(string resolved) => new(true, resolved);
}

/// <summary>The host side of ccd_directory: the picker, the grant, and the pending cwd move.</summary>
public interface ICcdDirectoryHost
{
    /// <summary>
    /// Resolves a folder to grant. With a path, it is validated and shown for
    /// approval; without one, the native picker opens.
    /// </summary>
    Task<FolderPickResult> PickFolderAsync(string? providedPath, CancellationToken cancellationToken);

    /// <summary>Grants the session access to the folders. False when the grant could not be made.</summary>
    Task<bool> AddDirectoriesAsync(IReadOnlyList<string> paths);

    /// <summary>
    /// Queues the working-directory move for the end of the turn. False when
    /// this session's directory cannot move, which the reference reports rather
    /// than treating as a failure.
    /// </summary>
    bool RequestPendingCwd(string path);
}

/// <summary>
/// The reference desktop's <c>ccd_directory</c> in-process MCP server:
/// request_directory grants a folder outside the working directory,
/// change_directory grants one and moves the session into it at turn end. Docs,
/// schemas, the ordering of the checks and every result sentence are the
/// reference's own (desktop 1.40609.0.0, <c>index2.chunk-CEBgETf7.js</c>).
/// </summary>
public static class CcdDirectoryTools
{
    public const string NotSupported = "Directory access is not supported in this session.";

    public const string PathRequired =
        "change_directory requires `path`: the absolute path of the folder to move the session to. To let the " +
        "user choose a folder, call request_directory without a path instead.";

    public const string Cancelled = "Directory selection was cancelled by the user.";

    public const string ChangedWhilePending =
        "The directory changed while approval was pending; request it again.";

    public const string GrantFailed =
        "Failed to grant folder access — it may be outside your administrator's allowed folders, or the session " +
        "is no longer available.";

    /// <summary>User-facing picker chrome, rebranded per this app's convention.</summary>
    public const string DialogTitle = "Select a folder to share with Jarvis";

    public const string DialogMessage = "Jarvis is requesting access to a folder on your computer.";

    public static string Granted(string path) =>
        $"Folder access granted: {path}\n\nUse this exact path with Read/Write/Edit/Grep/Glob.";

    public static string GrantedAndMoving(string path) =>
        $"Folder access granted: {path}\n\nThe session's working directory will move to this folder when the " +
        "current turn ends. Until then, use this exact absolute path with Read/Write/Edit/Grep/Glob and in Bash " +
        "commands.";

    public static string GrantedButImmovable(string path) =>
        $"Folder access granted: {path}\n\nThis session's working directory can't be moved, so it stays where it " +
        "is. Use this exact absolute path with Read/Write/Edit/Grep/Glob and in Bash commands.";

    public static InternalMcpServerDefinition Server(ICcdDirectoryHost host) =>
        new(InternalMcpServerNames.CcdDirectory, Create(host))
        {
            // The reference: ccd and not SSH. Granting a folder opens a picker
            // on the machine the user is at, which a remote host has none of.
            IsEnabled = static context =>
                context.SessionType == InternalMcpSessionContext.CodeSessionType && !context.IsSsh,
        };

    public static IReadOnlyList<ITool> Create(ICcdDirectoryHost host) =>
    [
        McpToolBuilder.Tool(
            "request_directory",
            "Request access to a directory on the user's computer that is outside your current working " +
            "directory. If you know the path, pass it — the user sees and approves it. If you omit `path`, a " +
            "native folder picker opens. Use this whenever the user asks you to work with files you don't " +
            "currently have access to.",
            McpToolBuilder.Schema(new JsonObject
            {
                ["path"] = McpToolBuilder.Prop(
                    "string",
                    "Absolute host path to grant (e.g. ~/Downloads). Omit to open the native folder picker."),
            }),
            isReadOnly: false,
            (args, _, cancellationToken) => RunAsync(host, args, moveSession: false, cancellationToken),
            static args => $"request_directory({JsonArgs.GetString(args, "path") ?? "pick a folder"})"),

        McpToolBuilder.Tool(
            "change_directory",
            "Move this session to a different project directory on the user's computer. Access is granted " +
            "immediately; the session's working directory (for Bash, relative paths, and project settings) moves " +
            "there when the current turn ends, so use absolute paths until then. `path` is required — the user " +
            "sees and approves that exact folder. Use this when the user's task is about an existing project and " +
            "the session isn't in it yet; to let the user pick a folder themselves, use request_directory without " +
            "a path.",
            McpToolBuilder.Schema(
                new JsonObject
                {
                    ["path"] = McpToolBuilder.Prop(
                        "string",
                        "Absolute host path of the folder to move the session to (e.g. ~/code/my-project)."),
                },
                "path"),
            isReadOnly: false,
            (args, _, cancellationToken) => RunAsync(host, args, moveSession: true, cancellationToken),
            static args => $"change_directory({JsonArgs.GetString(args, "path")})"),
    ];

    /// <summary>
    /// Both tools share one body, as they do in the reference; only the missing
    /// -path refusal and the closing sentence differ.
    /// </summary>
    private static async Task<ToolResult> RunAsync(
        ICcdDirectoryHost host, JsonObject args, bool moveSession, CancellationToken cancellationToken)
    {
        var requested = JsonArgs.GetString(args, "path") is { } raw && raw.Trim().Length > 0
            ? raw.Trim()
            : null;

        if (moveSession && requested is null)
        {
            return ToolResult.Error(PathRequired);
        }

        var picked = await host.PickFolderAsync(requested, cancellationToken);
        if (!picked.Ok)
        {
            // No error text means the user closed the picker, which is not a failure.
            return picked.Error is { } error ? ToolResult.Error(error) : ToolResult.Success(Cancelled);
        }

        var resolved = picked.Resolved ?? "";

        // The approval was for the path the user saw. If canonicalising moved it,
        // the grant would not be the one that was approved.
        if (requested is not null && !string.Equals(resolved, requested, StringComparison.Ordinal))
        {
            return ToolResult.Error(ChangedWhilePending);
        }

        if (!await host.AddDirectoriesAsync([resolved]))
        {
            return ToolResult.Error(GrantFailed);
        }

        if (!moveSession)
        {
            return ToolResult.Success(Granted(resolved));
        }

        return ToolResult.Success(host.RequestPendingCwd(resolved)
            ? GrantedAndMoving(resolved)
            : GrantedButImmovable(resolved));
    }
}
