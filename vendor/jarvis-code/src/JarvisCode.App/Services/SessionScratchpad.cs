using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// The per-session temporary directory the harness prompt points the model at.
/// </summary>
/// <remarks>
/// The reference lays this out as
/// <c>%TEMP%\claude\{project-slug}\{session-id}\scratchpad</c> and tells the
/// model to prefer it over <c>/tmp</c>; this app uses the same shape under its
/// own name. The directory is created eagerly because the prompt promises it
/// already exists — a block that names a directory the model then has to create
/// would be worse than no block.
/// </remarks>
internal static class SessionScratchpad
{
    /// <summary>The root every session's scratchpad lives under.</summary>
    internal static string Root { get; } = Path.Combine(Path.GetTempPath(), "jarvis");

    /// <summary>
    /// The project component of the path, matching
    /// <see cref="JarvisCode.Core.Memory.ProjectMemory.DirectoryFor"/> so a
    /// project is recognisable in both trees.
    /// </summary>
    internal static string ProjectSlug(string workingDirectory)
    {
        var normalized = Path.GetFullPath(workingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8]
            .ToLowerInvariant();
        var tail = Path.GetFileName(normalized);
        var name = new string((tail.Length > 0 ? tail : "root")
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray());
        return $"{name}-{hash}";
    }

    /// <summary>
    /// Where a background command's output is mirrored — the reference's
    /// project-scoped <c>tasks/</c> directory (its <c>s$e()</c>), whose files it
    /// names <c>{taskId}.output</c> and points the model at with Read.
    /// </summary>
    internal static string TasksDirectory(string workingDirectory) =>
        Path.Combine(Root, ProjectSlug(workingDirectory), "tasks");

    /// <summary>
    /// Where an oversize tool result is persisted — the reference's
    /// <c>{project}/{sessionId}/tool-results</c> (its <c>Rb</c>).
    /// </summary>
    internal static string ToolResultsDirectory(string workingDirectory, string sessionId) =>
        Path.Combine(Root, ProjectSlug(workingDirectory), Sanitize(sessionId), "tool-results");

    /// <summary>Where a background agent's JSONL transcript is written.</summary>
    internal static string AgentTranscriptsDirectory(string workingDirectory, string sessionId) =>
        Path.Combine(Root, ProjectSlug(workingDirectory), Sanitize(sessionId), "agents");

    /// <summary>The directory for one session, without touching the disk.</summary>
    internal static string PathFor(string workingDirectory, string sessionId) =>
        Path.Combine(Root, ProjectSlug(workingDirectory), Sanitize(sessionId), "scratchpad");

    /// <summary>
    /// The directory for one session, created if missing. Returns null when it
    /// cannot be created, so the prompt simply omits the section rather than
    /// naming a directory that is not there.
    /// </summary>
    internal static string? Ensure(string workingDirectory, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        // The reference's own scratchpad section stands down in a background job
        // (its TNe returns null for CLAUDE_CODE_SESSION_KIND === "bg"), because
        // the # Background Session block names $CLAUDE_JOB_DIR/tmp instead.
        if (string.Equals(
                Environment.GetEnvironmentVariable(GatedPromptSections.SessionKindVariable),
                "bg",
                StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var path = PathFor(workingDirectory, sessionId);
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Session ids are ours rather than the user's, but one arriving from a
    /// stored file could still carry a separator; a path component is built
    /// from it, so it is sanitised rather than trusted.
    /// </summary>
    private static string Sanitize(string sessionId)
    {
        var cleaned = new string(sessionId
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray());
        return cleaned.Length == 0 ? "session" : cleaned;
    }
}
