using System.Text.Json.Nodes;
using JarvisCode.Core.Security;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Permissions;

/// <summary>
/// Session-wide permission stance, Claude-Code style. Auto is the default gate
/// behavior; the others tighten or loosen it.
/// </summary>
public enum PermissionMode
{
    /// <summary>Policy decides: benign calls run, everything else prompts.</summary>
    Auto,
    /// <summary>Every change prompts — allow rules and session approvals are ignored.</summary>
    Manual,
    /// <summary>Like Auto, but in-workspace file edits run without prompting.</summary>
    AcceptEdits,
    /// <summary>Read-only research; the agent proposes a plan instead of changing anything.</summary>
    Plan,
    /// <summary>Every call runs unprompted. Use deliberately.</summary>
    Bypass,
}

/// <summary>How a specific tool call should be treated by the permission gate.</summary>
public enum CallRisk
{
    /// <summary>Provably benign (read-only, inside the workspace) — run without asking.</summary>
    AutoAllowable,
    /// <summary>Normal mutating call — rules and session approvals apply as usual.</summary>
    Standard,
    /// <summary>Destructive or out-of-workspace — always prompt, with the warning shown; allow rules and session-wide approvals do not apply.</summary>
    Escalated,
}

/// <summary>Per-call verdict: the risk bucket plus a human-readable warning for the prompt.</summary>
public sealed record ToolCallAssessment(CallRisk Risk, string? Warning = null)
{
    public static readonly ToolCallAssessment Standard = new(CallRisk.Standard);
    public static readonly ToolCallAssessment AutoAllowable = new(CallRisk.AutoAllowable);
}

/// <summary>
/// Dynamic per-invocation permission classification (ported from claw-code's
/// permission enforcer): instead of a static per-tool level, each call is judged by
/// what it actually touches. Read-only shell commands inside the workspace can run
/// unprompted; destructive commands and paths escaping the workspace escalate past
/// allow rules and session-wide approvals so a human always sees them.
/// </summary>
public static class ToolCallPolicy
{
    public static ToolCallAssessment Assess(
        string toolName,
        bool isReadOnly,
        JsonObject arguments,
        WorkspacePathScope scope,
        string workingDirectory)
    {
        switch (toolName)
        {
            case "PowerShell" or "Bash":
            {
                var command = JsonArgs.GetString(arguments, "command") ?? "";
                var warnings = new List<string>();
                if (ShellCommandInspector.DescribeDestructive(command) is { } destructive)
                    warnings.Add(destructive);
                var scopeDecision = scope.ValidatePayload(command, workingDirectory);
                if (!scopeDecision.Allowed)
                    warnings.Add(OutOfScopeWarning(scopeDecision));
                if (warnings.Count > 0)
                    return new ToolCallAssessment(CallRisk.Escalated, string.Join("\n", warnings));
                return ShellCommandInspector.IsReadOnly(command)
                    ? ToolCallAssessment.AutoAllowable
                    : ToolCallAssessment.Standard;
            }

            case "Write" or "Edit":
                return AssessPathArgument(arguments, "file_path", scope, workingDirectory, ToolCallAssessment.Standard);

            case "Read":
                return AssessPathArgument(arguments, "file_path", scope, workingDirectory, ToolCallAssessment.AutoAllowable);

            case "LSP":
                return AssessPathArgument(arguments, "filePath", scope, workingDirectory, ToolCallAssessment.AutoAllowable);

            case "list_directory" or "Glob" or "Grep":
                return AssessPathArgument(arguments, "path", scope, workingDirectory, ToolCallAssessment.AutoAllowable);

            default:
                return isReadOnly ? ToolCallAssessment.AutoAllowable : ToolCallAssessment.Standard;
        }
    }

    /// <summary>
    /// True when the mode auto-approves this call beyond the base policy —
    /// AcceptEdits waves in-workspace file edits through. Escalated calls always prompt.
    /// </summary>
    public static bool ModeAutoAllows(PermissionMode mode, string toolName, ToolCallAssessment assessment) =>
        mode == PermissionMode.AcceptEdits &&
        assessment.Risk != CallRisk.Escalated &&
        toolName is "Write" or "Edit";

    /// <summary>
    /// True when the mode demands a prompt even where allow rules or session
    /// approvals would normally apply — Manual asks before every change.
    /// Provably read-only calls stay silent: they change nothing.
    /// </summary>
    public static bool ModeForcesPrompt(PermissionMode mode, ToolCallAssessment assessment) =>
        mode == PermissionMode.Manual && assessment.Risk != CallRisk.AutoAllowable;

    private static ToolCallAssessment AssessPathArgument(
        JsonObject arguments,
        string argumentName,
        WorkspacePathScope scope,
        string workingDirectory,
        ToolCallAssessment inScopeAssessment)
    {
        var path = JsonArgs.GetString(arguments, argumentName);
        // A missing/blank path targets the working directory; a malformed one fails
        // in the tool itself with a clear error — neither is worth a prompt.
        if (string.IsNullOrWhiteSpace(path))
            return inScopeAssessment;
        var decision = scope.ValidatePath(path, workingDirectory);
        return decision.Allowed
            ? inScopeAssessment
            : new ToolCallAssessment(CallRisk.Escalated, OutOfScopeWarning(decision));
    }

    private static string OutOfScopeWarning(PathScopeDecision decision)
    {
        var target = decision.Resolved ?? decision.Candidate;
        return target is null
            ? "Touches paths outside the workspace."
            : $"Touches a path outside the workspace: {target}";
    }
}
