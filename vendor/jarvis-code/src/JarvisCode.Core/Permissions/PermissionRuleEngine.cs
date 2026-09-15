using System.IO.Enumeration;
using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Permissions;

public enum RuleAction
{
    Allow,
    Deny,
    Ask,
}

/// <summary>One parsed rule: action, tool, and an optional glob over the call's subject.</summary>
public sealed record PermissionRule(RuleAction Action, string ToolName, string? Pattern)
{
    public override string ToString() =>
        $"{Action.ToString().ToLowerInvariant()} {ToolName}{(Pattern is null ? "" : " " + Pattern)}";
}

/// <summary>
/// Persisted permission rules, written one per line as
/// "allow|deny &lt;tool&gt; [pattern]". The pattern is a glob matched against the
/// call's subject — the command for PowerShell, the url for WebFetch, the file path
/// for file tools — so "allow shell git *" auto-approves git commands while
/// everything else still prompts. Matching deny rules outrank ask rules, which outrank allows,
/// across all settings scopes (reference: code.claude.com/docs/en/permissions).
/// </summary>
public static class PermissionRuleEngine
{
    public static PermissionRule? Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;
        var parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;
        RuleAction? action = parts[0].ToLowerInvariant() switch
        {
            "allow" => RuleAction.Allow,
            "deny" => RuleAction.Deny,
            "ask" => RuleAction.Ask,
            _ => null,
        };
        if (action is null)
            return null;
        return new PermissionRule(action.Value, parts[1], parts.Length == 3 ? parts[2].Trim() : null);
    }

    public static IReadOnlyList<PermissionRule> ParseAll(IEnumerable<string> lines) =>
        [.. lines.Select(Parse).Where(rule => rule is not null).Select(rule => rule!)];

    /// <summary>Deny, then ask, then allow; source order only selects between rules with the same action.</summary>
    public static PermissionRule? Evaluate(IReadOnlyList<PermissionRule> rules, string toolName, JsonObject arguments)
    {
        if (rules.Count == 0)
            return null;
        var subject = ExtractSubject(toolName, arguments);
        PermissionRule? ask = null;
        PermissionRule? allow = null;
        foreach (var rule in rules)
        {
            if (!rule.ToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (rule.Pattern is not null && !Matches(rule.Pattern, subject)) continue;
            if (rule.Action == RuleAction.Deny) return rule;
            if (rule.Action == RuleAction.Ask) ask ??= rule;
            else if (rule.Action == RuleAction.Allow) allow ??= rule;
        }
        return ask ?? allow;
    }

    private static bool Matches(string pattern, string subject)
    {
        if (subject.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            return true;
        // MatchesSimpleExpression treats '\' as an escape character, which would
        // break Windows paths in rules; normalize separators on both sides.
        return FileSystemName.MatchesSimpleExpression(
            pattern.Replace('\\', '/'), subject.Replace('\\', '/'));
    }

    /// <summary>The argument a human would reason about when writing a rule for the tool.</summary>
    internal static string ExtractSubject(string toolName, JsonObject arguments) => toolName switch
    {
        "PowerShell" or "Bash" => JsonArgs.GetString(arguments, "command") ?? "",
        "WebFetch" => JsonArgs.GetString(arguments, "url") ?? "",
        "Write" or "Edit" or "Read" => JsonArgs.GetString(arguments, "file_path") ?? "",
        "LSP" => JsonArgs.GetString(arguments, "filePath") ?? "",
        "TaskStop" => JsonArgs.GetString(arguments, "task_id") ?? "",
        "Skill" => JsonArgs.GetString(arguments, "Skill") ?? JsonArgs.GetString(arguments, "name") ?? "",
        _ => arguments.ToJsonString(),
    };
}
