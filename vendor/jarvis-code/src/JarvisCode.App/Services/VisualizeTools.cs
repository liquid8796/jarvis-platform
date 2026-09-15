using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference desktop's <c>visualize</c> in-process MCP server — the two
/// tools its <c>getImagineServerDef</c> declares (desktop 1.40609.1.0,
/// <c>.vite/build/index2.chunk-CBBJKSmo.js</c>), plus the one MCP resource it
/// serves, which no other server in this shell has.
///
/// <c>read_me</c> hands the model the corpus <see cref="VisualizeCorpus"/>
/// composes; <c>show_widget</c> renders nothing itself — its result says so —
/// because the widget is drawn from the tool call's own arguments by the
/// transcript row, which loads the runtime from the <c>ui://</c> resource below.
///
/// Both tools carry the reference's <c>readOnlyHint</c>, so neither asks the
/// user for permission, and both ride the server's <c>alwaysLoad</c>, so neither
/// is ever deferred behind ToolSearch.
/// </summary>
public static class VisualizeTools
{
    /// <summary>The uri of the page the widget runtime is served from.</summary>
    public const string RuntimeUri = "ui://imagine/show-widget.html";

    /// <summary>The reference's mime type for an MCP App resource.</summary>
    public const string RuntimeMimeType = "text/html;profile=mcp-app";

    /// <summary>The resource's name, as resources/list reports it.</summary>
    public const string RuntimeName = "visualize widget";

    /// <summary>The resource's description, as resources/list reports it.</summary>
    public const string RuntimeDescription = "Renders SVG or HTML content progressively as it streams for the show_widget tool";

    /// <summary>
    /// What show_widget answers with. The widget is not in the tool result: the
    /// host renders it from the call's arguments, and the model is told to stop
    /// there rather than repeat it in prose.
    /// </summary>
    public const string ShowWidgetResult = "Content rendered and shown to the user. Please do not duplicate the shown content in text because it's already visually represented.";

    /// <summary>
    /// The CSP domains and the sandbox permission the resource's <c>_meta.ui</c>
    /// declares. The host applies them to the document it loads the runtime into.
    /// </summary>
    public static readonly VisualizeRuntimeSecurity RuntimeSecurity = new(
        ConnectDomains:
        [
            "https://esm.sh",
            "https://cdnjs.cloudflare.com",
            "https://cdn.jsdelivr.net",
            "https://unpkg.com"
        ],
        ResourceDomains:
        [
            "https://esm.sh",
            "https://cdnjs.cloudflare.com",
            "https://cdn.jsdelivr.net",
            "https://unpkg.com",
            "https://fonts.googleapis.com",
            "https://fonts.gstatic.com"
        ],
        ClipboardWrite: true);

    /// <summary>
    /// The reference gates this server on two remote flags —
    /// <c>xC("3444158716") &amp;&amp; (sessionType === "cowork" || (sessionType
    /// === "ccd" &amp;&amp; xC("3516166472")))</c>. There is no flag channel
    /// here, so what is carried is the half that is a fact about the session:
    /// the Code surface, which is its <c>ccd</c>. The Chat surface runs toolless
    /// and never reaches this.
    /// </summary>
    public static InternalMcpServerDefinition Server() =>
        new(InternalMcpServerNames.Visualize, Create())
        {
            // Both tools set alwaysLoad in the reference's declaration, so the
            // server carries it: the model is meant to be able to call read_me
            // without first searching for it.
            AlwaysLoad = true,
            IsEnabled = static context =>
                context.SessionType == InternalMcpSessionContext.CodeSessionType,
            Resources =
            [
                new McpResourceDescriptor(RuntimeUri, RuntimeName, RuntimeDescription, RuntimeMimeType),
            ],
            ReadResource = static uri => string.Equals(uri, RuntimeUri, StringComparison.Ordinal)
                ? new InternalMcpResourceContents(
                    RuntimeUri, RuntimeMimeType, VisualizeCorpus.WidgetRuntimeHtml, RuntimeMeta())
                : null,
        };

    public static IReadOnlyList<ITool> Create() =>
    [
        McpToolBuilder.Tool(
            "show_widget",
            """
            Show visual content — SVG graphics, diagrams, charts, or interactive HTML widgets — that renders inline alongside your text response.
            Use for flowcharts, architecture diagrams, dashboards, forms, calculators, data tables, games, illustrations, or any visual content.
            The code is auto-detected: starts with <svg = SVG mode, otherwise HTML mode.
            A global sendPrompt(text) function is available — it sends a message to chat as if the user typed it.
            IMPORTANT: Call read_me before your first show_widget call. Do NOT narrate or mention the read_me call to the user — call it silently, then respond as if you went straight to building the visualization.
            """,
            CapturedMcpSchemas.Schema(InternalMcpServerNames.Visualize, "show_widget"),
            isReadOnly: true,
            static (_, _) => ToolResult.Success(ShowWidgetResult),
            static args => $"show_widget({JsonArgs.GetString(args, "title")})"),

        McpToolBuilder.Tool(
            "read_me",
            """
            Returns required context for show_widget (CSS variables, colors, typography, layout rules, examples). Call before your first show_widget call. Call again later if you need a different module. Do NOT mention or narrate this call to the user — it is an internal setup step. Call it silently and proceed directly to the visualization in your response.
            """,
            CapturedMcpSchemas.Schema(InternalMcpServerNames.Visualize, "read_me"),
            isReadOnly: true,
            static (args, _) => ToolResult.Success(VisualizeCorpus.ReadMe(
                Modules(args),
                JsonArgs.GetString(args, "platform"))),
            static _ => "read_me()"),
    ];

    /// <summary>
    /// The module names the call asked for, in the order it listed them. A
    /// non-string entry is dropped rather than refused: the reference indexes
    /// its table with whatever arrived and a name it has no module for
    /// contributes nothing.
    /// </summary>
    private static IReadOnlyList<string> Modules(JsonObject args) =>
        args["modules"] is JsonArray modules
            ? [.. modules.OfType<JsonValue>()
                .Select(static value => value.TryGetValue<string>(out var name) ? name : null)
                .OfType<string>()]
            : [];

    /// <summary>
    /// The <c>_meta</c> the reference returns beside the runtime: the CSP the
    /// host must apply to the document and the sandbox permissions it grants.
    /// </summary>
    private static JsonObject RuntimeMeta() => new()
    {
        ["ui"] = new JsonObject
        {
            ["permissions"] = new JsonObject { ["clipboardWrite"] = new JsonObject() },
            ["csp"] = new JsonObject
            {
                ["connectDomains"] = new JsonArray(
                    [.. RuntimeSecurity.ConnectDomains.Select(static d => (JsonNode)d)]),
                ["resourceDomains"] = new JsonArray(
                    [.. RuntimeSecurity.ResourceDomains.Select(static d => (JsonNode)d)]),
            },
        },
    };
}

/// <summary>
/// What the widget document is allowed to reach, as the resource's
/// <c>_meta.ui</c> declares it. connectDomains map to CSP <c>connect-src</c> and
/// resourceDomains to <c>img-src</c>, <c>script-src</c>, <c>style-src</c>,
/// <c>font-src</c> and <c>media-src</c> — the mapping the reference's own MCP
/// Apps schema states in its field descriptions
/// (<c>ion-dist/assets/v1/c131c9a32-CQgiruhm.js</c>).
/// </summary>
public sealed record VisualizeRuntimeSecurity(
    IReadOnlyList<string> ConnectDomains,
    IReadOnlyList<string> ResourceDomains,
    bool ClipboardWrite);
