using System.Text.Json;

namespace Jarvis.Agent.Core;

public enum ToolCapabilityScope
{
    Session,
    Turn
}

/// <summary>
/// Time-bounded local authority for one exact tool. Constraints are evaluated at invocation time;
/// a lease never installs a tool, arms remote control, or bypasses schema validation.
/// </summary>
public sealed record ToolCapabilityLease(
    string Id,
    string ToolId,
    ToolCapabilityScope Scope,
    string SessionId,
    string? TurnId,
    DateTimeOffset ExpiresUtc,
    IReadOnlyList<string>? WorkspaceRoots = null,
    IReadOnlyList<string>? CommandPrefixes = null,
    bool AllowNetwork = false)
{
    public ToolCapabilityLease Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || Id.Any(char.IsWhiteSpace))
            throw new ArgumentException("Capability lease ID is invalid.", nameof(Id));
        if (string.IsNullOrWhiteSpace(ToolId) || ToolId.Length > 256 || ToolId.Contains('*') || ToolId.Any(char.IsWhiteSpace))
            throw new ArgumentException("Capability lease requires an exact tool ID.", nameof(ToolId));
        if (string.IsNullOrWhiteSpace(SessionId) || SessionId.Length > 256)
            throw new ArgumentException("Capability lease session is invalid.", nameof(SessionId));
        if (Scope == ToolCapabilityScope.Turn && string.IsNullOrWhiteSpace(TurnId))
            throw new ArgumentException("Turn-scoped capability lease requires a turn ID.", nameof(TurnId));
        if (ExpiresUtc <= DateTimeOffset.UtcNow)
            throw new ArgumentException("Capability lease must expire in the future.", nameof(ExpiresUtc));
        if (ExpiresUtc - DateTimeOffset.UtcNow > TimeSpan.FromHours(24))
            throw new ArgumentException("Capability lease cannot exceed 24 hours.", nameof(ExpiresUtc));
        if ((WorkspaceRoots?.Count ?? 0) > 32 || (CommandPrefixes?.Count ?? 0) > 64)
            throw new ArgumentException("Capability lease exceeds bounded constraint limits.");
        return this;
    }

    internal bool Matches(string toolId, JsonElement arguments, AgentExecutionContext context, DateTimeOffset now)
    {
        if (now >= ExpiresUtc || !StringComparer.Ordinal.Equals(ToolId, toolId) ||
            !StringComparer.Ordinal.Equals(SessionId, context.SessionId)) return false;
        if (Scope == ToolCapabilityScope.Turn && !StringComparer.Ordinal.Equals(TurnId, context.TurnId)) return false;

        if (WorkspaceRoots is { Count: > 0 })
        {
            var requested = context.Workspace;
            if (arguments.ValueKind == JsonValueKind.Object && (arguments.TryGetProperty("workingDirectory", out var cwd) || arguments.TryGetProperty("workdir", out cwd)) && cwd.ValueKind == JsonValueKind.String)
            {
                var text = cwd.GetString();
                if (string.IsNullOrWhiteSpace(text)) return false;
                requested = Path.GetFullPath(text, context.Workspace);
            }
            requested = Path.GetFullPath(requested);
            if (!WorkspaceRoots.Any(root => IsWithin(requested, root))) return false;
        }

        if (CommandPrefixes is { Count: > 0 })
        {
            if (arguments.ValueKind != JsonValueKind.Object) return false;
            if ((arguments.TryGetProperty("command", out var commandElement) || arguments.TryGetProperty("cmd", out commandElement)) && commandElement.ValueKind == JsonValueKind.String)
            {
                var command = commandElement.GetString() ?? "";
                if (!CommandPrefixes.Any(prefix => CommandHasPrefix(command, prefix))) return false;
            }
            else if (arguments.TryGetProperty("argv", out var argvElement) && argvElement.ValueKind == JsonValueKind.Array)
            {
                var argv = argvElement.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "").ToArray();
                if (!CommandPrefixes.Any(prefix => ArgvHasPrefix(argv, prefix))) return false;
            }
            else return false;
        }

        if (!AllowNetwork && arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("allowNetwork", out var network) &&
            network.ValueKind == JsonValueKind.True) return false;
        return true;
    }

    private static bool CommandHasPrefix(string command, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!command.StartsWith(prefix, comparison)) return false;
        return command.Length == prefix.Length || char.IsWhiteSpace(command[prefix.Length]);
    }

    private static bool ArgvHasPrefix(IReadOnlyList<string> argv, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || argv.Count == 0) return false;
        var expected = prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (expected.Length == 0 || expected.Length > argv.Count) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < expected.Length; i++)
            if (!string.Equals(argv[i], expected[i], comparison)) return false;
        return true;
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.Equals(fullRoot, comparison) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison) ||
            fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
