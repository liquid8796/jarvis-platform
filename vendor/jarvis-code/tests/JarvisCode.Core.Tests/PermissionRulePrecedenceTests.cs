using System.Text.Json.Nodes;
using JarvisCode.Core.Permissions;

namespace JarvisCode.Core.Tests;

/// <summary>Primary reference: code.claude.com/docs/en/permissions, Manage permissions and Settings precedence.</summary>
public sealed class PermissionRulePrecedenceTests
{
    [Theory]
    [InlineData("allow", "ask", "deny")]
    [InlineData("allow", "deny", "ask")]
    [InlineData("ask", "allow", "deny")]
    [InlineData("ask", "deny", "allow")]
    [InlineData("deny", "allow", "ask")]
    [InlineData("deny", "ask", "allow")]
    public void Deny_wins_in_every_source_order(string first, string second, string third)
    {
        var rules = PermissionRuleEngine.ParseAll(new[] { first, second, third }.Select(action => action + " Read *.txt"));
        Assert.Equal(RuleAction.Deny, PermissionRuleEngine.Evaluate(rules, "Read", new JsonObject { ["file_path"] = "notes.txt" })!.Action);
    }

    [Fact]
    public void Broad_ask_outranks_specific_allow_and_unmatched_deny_has_no_effect()
    {
        var rules = PermissionRuleEngine.ParseAll(["allow Read notes.txt", "deny Read another.json", "ask Read *.txt"]);
        Assert.Equal(RuleAction.Ask, PermissionRuleEngine.Evaluate(rules, "Read", new JsonObject { ["file_path"] = "notes.txt" })!.Action);
    }

    [Theory]
    [InlineData("PowerShell")]
    [InlineData("Bash")]
    public void Both_shell_names_match_their_command_subject(string shell)
    {
        var rules = PermissionRuleEngine.ParseAll([$"allow {shell} git status", $"deny {shell} git *"]);
        Assert.Equal(RuleAction.Deny, PermissionRuleEngine.Evaluate(rules, shell, new JsonObject { ["command"] = "git status" })!.Action);
    }
}
