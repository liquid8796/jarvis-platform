using System.Collections.Generic;
using System.Linq;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// Builds the harness's <c>deferred_tools_delta</c> block for one turn from what
/// this session has already announced — the reference derives the same delta by
/// reading its earlier attachments back out of the conversation, which is why
/// the first request lists every deferred tool and a later one only what
/// appeared since.
/// </summary>
internal static class DeferredToolNotice
{
    /// <summary>
    /// The block, or null when there is nothing new to say. Marks what it
    /// announced, so calling it twice for one turn is not the same as calling it
    /// for two turns — call it once, where the harness sections are composed.
    /// </summary>
    /// <param name="nonInteractive">
    /// Whether this session can raise an OAuth flow. The needs-authentication
    /// paragraph states in its own words that the session is non-interactive and
    /// tells the model to hand the job to the user, so it rides only where that
    /// sentence is true; a desktop session signs the server in from the
    /// Connectors page instead.
    /// </param>
    public static string? Build(
        SkillSessionState state,
        DeferredToolRegistry? deferred,
        McpManager? mcp,
        bool nonInteractive)
    {
        var added = deferred is null
            ? []
            : deferred.DeferredNames.Where(name => !state.AnnouncedDeferredTools.Contains(name)).ToList();

        var needsAuth = new List<string>();
        var failed = new List<DeferredToolAnnouncements.FailedServer>();
        var dropped = new List<string>();
        if (mcp is not null)
        {
            // The reference announces excluded tools as a delta: a server that
            // drops the same tool on every refresh is named once per session.
            dropped.AddRange(mcp.DroppedToolEntries
                .Where(entry => !state.AnnouncedDroppedTools.Contains(entry)));

            if (nonInteractive)
            {
                needsAuth.AddRange(mcp.NeedsAuthServers
                    .Where(name => !state.AnnouncedNeedsAuthServers.Contains(name)));
            }

            failed.AddRange(mcp.FailedServers
                .Select(server => new DeferredToolAnnouncements.FailedServer(server.Name, null, server.Error)));
        }

        var block = DeferredToolAnnouncements.Build(
            added, needsAuth, failed.Count == 0 ? null : failed);
        var droppedBlock = DeferredToolAnnouncements.DroppedTools(dropped);
        if (block is null && droppedBlock is null)
        {
            return null;
        }

        foreach (var entry in dropped)
        {
            state.AnnouncedDroppedTools.Add(entry);
        }

        foreach (var name in added)
        {
            state.AnnouncedDeferredTools.Add(name);
        }

        foreach (var name in needsAuth)
        {
            state.AnnouncedNeedsAuthServers.Add(name);
        }

        return block is null
            ? droppedBlock
            : droppedBlock is null ? block : block + "\n\n" + droppedBlock;
    }
}
