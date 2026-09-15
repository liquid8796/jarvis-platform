using System.Text.Json.Nodes;
using JarvisCode.Core.Settings;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference Config tool, narrowed to a safe allowlist: the model can read
/// and change a few behavioral settings when the user asks for it. Anything
/// security-relevant (permission mode, rules, keys) is deliberately absent.
/// </summary>
public sealed class ConfigTool(
    Func<AppSettings> current,
    Action save,
    Action<string, string>? onChanged = null,
    Func<UiSettings>? uiCurrent = null,
    Action? uiSave = null) : ITool
{
    public string Name => "config";

    public string Description =>
        "Reads or changes a small set of app settings when the user asks for it. Keys: " +
        "effort (Low|Medium|High — reasoning effort for new turns), " +
        "autocompact_window (100000-1000000 tokens, or \"auto\" for the model's own window), " +
        "precompute_compaction (on|off), precompute_sidecar (on|off), " +
        "autocompact (on|off), web_search (on|off), shell_timeout_seconds (5-600), " +
        "dynamic_workflows (on|off — offers the workflow tool), " +
        "teammate_mode (auto|in-process|tmux|iterm2 — how a named agent runs), " +
        "workflow_size (default|unrestricted|small|medium|large — advisory scale for workflows). " +
        "action get without a key lists all values. Never change a setting the user did not ask to change.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("get", "set"),
                ["description"] = "get reads (all keys when key is omitted); set writes one key",
            },
            ["key"] = new JsonObject { ["type"] = "string", ["description"] = "One of the listed keys" },
            ["value"] = new JsonObject { ["type"] = "string", ["description"] = "The new value (set only)" },
        },
        ["required"] = new JsonArray("action"),
    };

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
    {
        var action = JsonArgs.GetString(arguments, "action") ?? "?";
        var key = JsonArgs.GetString(arguments, "key");
        var value = JsonArgs.GetString(arguments, "value");
        return action == "set" ? $"Config(set {key} = {value})" : $"Config(get {key ?? "all"})";
    }

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var settings = current();
        var action = JsonArgs.GetString(arguments, "action");
        var key = JsonArgs.GetString(arguments, "key")?.Trim().ToLowerInvariant();

        var ui = uiCurrent?.Invoke();
        if (action == "get")
        {
            return Task.FromResult(key is null or ""
                ? ToolResult.Success(
                    $"effort = {settings.ThinkingEffortName}\n" +
                    $"autocompact = {(settings.AutoCompactEnabled ? "on" : "off")}\n" +
                    $"autocompact_window = {WindowSetting(settings)}\n" +
                    $"precompute_compaction = {(settings.PrecomputeCompactionEnabled ? "on" : "off")}\n" +
                    $"precompute_sidecar = {(settings.PrecomputeSidecarEnabled ? "on" : "off")}\n" +
                    $"web_search = {(settings.EnableWebSearch ? "on" : "off")}\n" +
                    $"shell_timeout_seconds = {settings.ShellTimeoutSeconds}" +
                    (ui is null
                        ? ""
                        : $"\ndynamic_workflows = {(ui.DynamicWorkflowsEnabled ? "on" : "off")}\n" +
                          $"teammate_mode = {ui.TeammateMode}\n" +
                          $"workflow_size = {(string.IsNullOrEmpty(ui.WorkflowSize) ? "default (medium)" : ui.WorkflowSize)}"))
                : Read(settings, key, ui));
        }

        if (action != "set")
            return Task.FromResult(ToolResult.Error("action must be get or set."));
        var value = JsonArgs.GetString(arguments, "value")?.Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return Task.FromResult(ToolResult.Error("set needs both key and value."));

        var result = Write(settings, key, value, ui);
        if (!result.IsError)
        {
            // The two orchestration switches live in ui-settings, so they save
            // through their own store rather than the engine's.
            if (key is "dynamic_workflows" or "teammate_mode" or "workflow_size")
                uiSave?.Invoke();
            else
                save();
            onChanged?.Invoke(key!, value!);
        }

        return Task.FromResult(result);
    }

    private static ToolResult Read(AppSettings settings, string key, UiSettings? ui) => key switch
    {
        "dynamic_workflows" when ui is not null =>
            ToolResult.Success(ui.DynamicWorkflowsEnabled ? "on" : "off"),
        "teammate_mode" when ui is not null => ToolResult.Success(ui.TeammateMode),
        "workflow_size" when ui is not null => ToolResult.Success(
            string.IsNullOrEmpty(ui.WorkflowSize) ? "default (medium)" : ui.WorkflowSize),
        "effort" => ToolResult.Success(settings.ThinkingEffortName),
        "autocompact" => ToolResult.Success(settings.AutoCompactEnabled ? "on" : "off"),
        "autocompact_window" => ToolResult.Success(WindowSetting(settings)),
        "precompute_compaction" => ToolResult.Success(
            settings.PrecomputeCompactionEnabled ? "on" : "off"),
        "precompute_sidecar" => ToolResult.Success(
            settings.PrecomputeSidecarEnabled ? "on" : "off"),
        "WebSearch" => ToolResult.Success(settings.EnableWebSearch ? "on" : "off"),
        "shell_timeout_seconds" => ToolResult.Success(settings.ShellTimeoutSeconds.ToString()),
        _ => ToolResult.Error($"Unknown key '{key}'."),
    };

    private static ToolResult Write(AppSettings settings, string key, string value, UiSettings? ui)
    {
        switch (key)
        {
            case "dynamic_workflows" when ui is not null:
                if (!TryParseSwitch(value, out bool workflowsOn))
                    return ToolResult.Error("dynamic_workflows must be on or off.");
                ui.DynamicWorkflowsEnabled = workflowsOn;
                return ToolResult.Success(
                    $"dynamic_workflows = {(workflowsOn ? "on" : "off")} (applies from the next turn).");

            case "workflow_size" when ui is not null:
                var size = value.ToLowerInvariant();
                if (size is "default")
                    size = "";
                if (size is not ("" or "unrestricted" or "small" or "medium" or "large"))
                    return ToolResult.Error("workflow_size must be default, unrestricted, small, medium or large.");
                ui.WorkflowSize = size;
                return ToolResult.Success(
                    $"workflow_size = {(size.Length == 0 ? "default (medium)" : size)} " +
                    "(applies from the next turn).");

            case "teammate_mode" when ui is not null:
                var mode = value.ToLowerInvariant();
                if (mode is not ("auto" or "in-process" or "tmux" or "iterm2"))
                    return ToolResult.Error("teammate_mode must be auto, in-process, tmux or iterm2.");
                ui.TeammateMode = mode;
                return ToolResult.Success($"teammate_mode = {mode} (applies from the next spawn).");

            case "effort":
                if (!EffortLevels.TryResolve(value, out var effortName))
                    return ToolResult.Error("effort must be Low, Medium, High, Extra high (xhigh), or Max.");
                // The reference never saves ultracode as a default — it is
                // session-scoped and lives on the composer's effort selector.
                if (EffortLevels.IsUltracode(effortName))
                    return ToolResult.Error(
                        "ultracode is session-only and can't be saved as the default; pick it in the effort selector.");
                settings.ThinkingEffortName = effortName;
                return ToolResult.Success($"effort = {settings.ThinkingEffortName} (applies from the next turn).");

            case "autocompact":
                if (!TryParseSwitch(value, out bool compactOn))
                    return ToolResult.Error("autocompact must be on or off.");
                settings.AutoCompactEnabled = compactOn;
                return ToolResult.Success($"autocompact = {(compactOn ? "on" : "off")}.");

            case "precompute_sidecar":
                if (!TryParseSwitch(value, out bool sidecarOn))
                    return ToolResult.Error("precompute_sidecar must be on or off.");
                settings.PrecomputeSidecarEnabled = sidecarOn;
                return ToolResult.Success($"precompute_sidecar = {(sidecarOn ? "on" : "off")}.");

            case "precompute_compaction":
                if (!TryParseSwitch(value, out bool precomputeOn))
                    return ToolResult.Error("precompute_compaction must be on or off.");
                settings.PrecomputeCompactionEnabled = precomputeOn;
                return ToolResult.Success(
                    $"precompute_compaction = {(precomputeOn ? "on" : "off")}.");

            case "autocompact_window":
                // The reference's own range, and its own word for "the model decides".
                if (!ContextWindows.TryParseWindow(value, out int? autoCompactWindow))
                    return ToolResult.Error(
                        "autocompact_window must be 100000-1000000 tokens (500k and 200 are accepted too), " +
                        "or \"auto\" for the model's own window.");
                settings.AutoCompactWindow = autoCompactWindow;
                return ToolResult.Success($"autocompact_window = {WindowSetting(settings)}.");

            // A /config key, not a tool name.
            case "web_search":
                if (!TryParseSwitch(value, out bool searchOn))
                    return ToolResult.Error("web_search must be on or off.");
                settings.EnableWebSearch = searchOn;
                return ToolResult.Success($"web_search = {(searchOn ? "on" : "off")} (applies from the next turn).");

            case "shell_timeout_seconds":
                if (!int.TryParse(value, out int seconds) || seconds is < 5 or > 600)
                    return ToolResult.Error("shell_timeout_seconds must be 5-600.");
                settings.ShellTimeoutSeconds = seconds;
                return ToolResult.Success($"shell_timeout_seconds = {seconds} (applies from the next turn).");

            default:
                return ToolResult.Error($"Unknown key '{key}'.");
        }
    }

    private static bool TryParseSwitch(string value, out bool on)
    {
        on = value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase);
        return on ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The window as the reference's setting stores it: a token count, or the
    /// word it uses when the model's own window decides.
    /// </summary>
    private static string WindowSetting(JarvisCode.Core.Settings.AppSettings settings) =>
        settings.AutoCompactWindow?.ToString() ?? "auto";

}
