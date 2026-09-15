using JarvisCode.Core.Mcp;

namespace JarvisCode.App.Services;

/// <summary>
/// What the user is asked before a connector's <c>headersHelper</c> command
/// runs.
/// </summary>
/// <remarks>
/// The reference does not ask: its gate is workspace trust, and a server
/// declared in a repository's own settings simply does not get its helper run
/// until that workspace has been trusted (its <c>isRepoResidentConfig</c> path
/// in <c>F_t</c>). This app's MCP config has no such tiers — a project's
/// <c>.jarvis/mcp.json</c> and the user-level file are read the same way — so
/// the decision is asked instead, once per server per session and again
/// whenever the command text changes. It is the same decision a hook command
/// is, and it is declared as this build's own addition in
/// <c>Deltas/reference-surface-deltas.tsv</c>.
/// </remarks>
public static class McpHeadersHelperConsent
{
    public const string Title = "Run this connector's headers command?";

    public const string ConfirmLabel = "Run command";

    public static string Body(McpServerConfig server) =>
        $"The MCP server '{server.Name}' is configured with a headersHelper — a command Jarvis Code runs to " +
        "produce the HTTP headers it sends to that server, usually a short-lived credential. It runs with " +
        "your account's permissions.";

    public static string Footnote(McpServerConfig server) =>
        server.Url is { Length: > 0 } url
            ? $"Headers from this command are sent to {url}."
            : "Headers from this command are sent to the configured server.";
}
