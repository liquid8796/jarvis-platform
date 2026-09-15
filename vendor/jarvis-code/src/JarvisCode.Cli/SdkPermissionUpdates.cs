using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli;

/// <summary>Explicit SDK permission updates, isolated from the invocation's original permission rules.</summary>
internal sealed class SdkPermissionUpdates(CliServices services, CliOptions options, UiPermissionGate gate,
    Session session, Action<bool> setDontAsk)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private sealed record Update(string Type, string Destination, string? Behavior, string? Mode,
        IReadOnlyList<string> Rules, IReadOnlyList<string> Directories);

    public async Task ApplyAsync(JsonArray updates, CancellationToken cancellationToken)
    {
        var parsed = updates.Select(Parse).ToArray(); // validate all updates before changing either memory or files
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            foreach (var update in parsed)
            {
                if (update.Destination != "session") await PersistAsync(update, cancellationToken);
                switch (update.Type)
                {
                    case "addRules": case "replaceRules": case "removeRules":
                        gate.SdkRuleLines = EditRules(gate.SdkRuleLines, update.Type, update.Behavior!, update.Rules);
                        break;
                    case "setMode":
                        var (mode, dontAsk) = (options with { PermissionModeName = update.Mode, DangerouslySkipPermissions = false }).ResolvePermissionMode();
                        if (mode == PermissionMode.Plan) gate.EnterPlanMode();
                        else { if (gate.Mode == PermissionMode.Plan) gate.ExitPlanMode(); gate.Mode = mode; }
                        setDontAsk(dontAsk);
                        break;
                    case "addDirectories": case "removeDirectories":
                        var selected = session.AdditionalDirectories.ToList();
                        foreach (var directory in update.Directories)
                        {
                            selected.RemoveAll(existing => SamePath(existing, directory));
                            if (update.Type == "addDirectories") selected.Add(directory);
                        }
                        session.AdditionalDirectories.Clear();
                        session.AdditionalDirectories.AddRange(selected);
                        gate.AdditionalDirectories = session.AdditionalDirectories;
                        break;
                }
            }
        }
        finally { _mutex.Release(); }
    }

    private Update Parse(JsonNode? node)
    {
        if (node is not JsonObject value) throw new CliError("updatedPermissions entries must be objects.");
        var type = value["type"]?.GetValue<string>() ?? "";
        var destination = value["destination"]?.GetValue<string>() ?? "session";
        if (destination is not ("session" or "userSettings" or "projectSettings" or "localSettings"))
            throw new CliError("Unknown permission-update destination.");
        if ((options.Bare || options.SafeMode || options.Restricted) && destination != "session")
            throw new CliError("This invocation accepts session permission updates only.");
        var behavior = value["behavior"]?.GetValue<string>();
        var mode = value["mode"]?.GetValue<string>();
        var rules = new List<string>(); var directories = new List<string>();
        if (type is "addRules" or "replaceRules" or "removeRules")
        {
            if (behavior is not ("allow" or "deny" or "ask") || value["rules"] is not JsonArray ruleValues)
                throw new CliError("Permission rule updates require rules and allow, deny or ask behavior.");
            foreach (var rule in ruleValues.OfType<JsonObject>())
            {
                var tool = rule["toolName"]?.GetValue<string>();
                var content = rule["ruleContent"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(tool) || tool.Any(char.IsWhiteSpace) || content?.Any(c => c is '\r' or '\n' or '\0') == true)
                    throw new CliError("Permission rules need a tool name and a single-line pattern.");
                rules.AddRange(ToolNames.ToRuleLines([tool + (content is null ? "" : "(" + content + ")")], behavior));
            }
            if (rules.Count != ruleValues.Count) throw new CliError("Permission rules must be objects.");
        }
        else if (type == "setMode")
        {
            if (mode is not ("default" or "manual" or "auto" or "acceptEdits" or "bypassPermissions" or "dontAsk" or "plan"))
                throw new CliError("Unknown permission-update mode.");
            if (options.Restricted && mode == "bypassPermissions") throw new CliError(RestrictedMode.BypassRefused);
        }
        else if (type is "addDirectories" or "removeDirectories")
        {
            if (value["directories"] is not JsonArray entries) throw new CliError("Permission directory updates require a directories array.");
            foreach (var entry in entries)
            {
                var path = entry?.GetValue<string>() ?? "";
                if (path.Length == 0 || Core.Utilities.NetworkPaths.IsNetworkPath(path))
                    throw new CliError("Permission directory updates require local directory paths.");
                var full = Path.GetFullPath(path, session.WorkingDirectory);
                if (type == "addDirectories" && !Directory.Exists(full)) throw new CliError("Permission directory does not exist: " + full);
                directories.Add(full);
            }
        }
        else throw new CliError("Unknown permission update type: " + type);
        return new Update(type, destination, behavior, mode, rules, directories);
    }

    internal static IReadOnlyList<string> EditRules(IReadOnlyList<string> existing, string type,
        string behavior, IReadOnlyList<string> rules)
    {
        var result = existing.ToList();
        if (type == "replaceRules") result.RemoveAll(line => line.StartsWith(behavior + " ", StringComparison.OrdinalIgnoreCase));
        if (type == "removeRules") result.RemoveAll(line => rules.Contains(line, StringComparer.OrdinalIgnoreCase));
        else foreach (var rule in rules) if (!result.Contains(rule, StringComparer.OrdinalIgnoreCase)) result.Add(rule);
        return result;
    }

    private async Task PersistAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.Destination == "userSettings" && update.Type is "addRules" or "replaceRules" or "removeRules" or "setMode")
        {
            services.CliSettings.PersistPermissions(update.Type, update.Mode, update.Behavior, update.Rules);
            return;
        }
        var filename = update.Destination == "localSettings" ? "settings.local.json" : "settings.json";
        var primary = update.Destination == "userSettings" ? Path.Combine(services.App.Paths.Root, "sdk-settings.json")
            : Path.Combine(session.WorkingDirectory, ".jarvis", filename);
        var compatible = Path.Combine(session.WorkingDirectory, ".claude", filename);
        var file = update.Destination == "userSettings" || File.Exists(primary) || !File.Exists(compatible) ? primary : compatible;
        var root = File.Exists(file) ? JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken)) as JsonObject
            ?? throw new CliError("The settings file must contain a JSON object.") : new JsonObject();
        var permissions = root["permissions"] as JsonObject ?? new JsonObject();
        if (permissions.Parent is null) root["permissions"] = permissions;
        if (update.Type == "setMode") permissions["defaultMode"] = update.Mode;
        else if (update.Type is "addDirectories" or "removeDirectories")
        {
            var directories = (permissions["additionalDirectories"] as JsonArray ?? []).Select(entry => entry!.GetValue<string>()).ToList();
            foreach (var directory in update.Directories)
            { directories.RemoveAll(existing => SamePath(existing, directory)); if (update.Type == "addDirectories") directories.Add(directory); }
            permissions["additionalDirectories"] = new JsonArray([.. directories.Select(directory => (JsonNode?)JsonValue.Create(directory))]);
        }
        else
        {
            var old = (permissions[update.Behavior!] as JsonArray ?? []).Select(entry => entry!.GetValue<string>()).ToArray();
            var lines = ToolNames.ToRuleLines(old, update.Behavior!).ToArray();
            var rules = EditRules(lines, update.Type, update.Behavior!, update.Rules).Select(line =>
            {
                var parsed = PermissionRuleEngine.Parse(line)!;
                return parsed.ToolName + (parsed.Pattern is null ? "" : "(" + parsed.Pattern + ")");
            });
            permissions[update.Behavior!] = new JsonArray([.. rules.Select(rule => (JsonNode?)JsonValue.Create(rule))]);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool SamePath(string a, string b) => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
        .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
