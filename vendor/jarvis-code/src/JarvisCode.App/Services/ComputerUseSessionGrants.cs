using System;
using System.Collections.Generic;
using System.Linq;

namespace JarvisCode.App.Services;

/// <summary>
/// What one session is allowed to drive, which is where the reference keeps it.
///
/// Measured in desktop 1.44121.2.0: a grant lands on the session object as
/// <c>cuAllowedApps</c> / <c>cuGrantFlags</c> and is written with
/// <c>saveSession</c> (<c>index.chunk-ArHpFRaV.js</c>, its
/// <c>onCuPermissionUpdated</c>), the request handler reads it back through
/// <c>getAllowedApps()</c>, a dispatch child inherits its parent's copy at
/// <c>startSession</c>, and <c>revokeComputerUseGrant(sessionId, bundleId)</c>
/// takes one app back. Nothing writes it to a global list: approving Notepad in
/// one conversation is not approval in the next one.
///
/// This port used to keep a single global allowlist in ui-settings, which asked
/// once and then never again for the life of the installation — the tool's own
/// description says "for this session", so the description was the accurate half
/// and the storage was the wrong one.
/// </summary>
public sealed class ComputerUseGrantSet
{
    /// <summary>Process names this session may drive.</summary>
    public List<string> Apps { get; set; } = [];

    /// <summary>Per-app tier ("read" | "click" | "full"); a name missing from here is full.</summary>
    public Dictionary<string, string> Tiers { get; set; } = [];

    public bool ClipboardRead { get; set; }

    public bool ClipboardWrite { get; set; }

    public bool SystemKeyCombos { get; set; }

    /// <summary>Whether this session has been warned about a restricted category yet.</summary>
    public List<string> WarnedCategories { get; set; } = [];

    /// <summary>
    /// The order this set was opened in, which is what the cap evicts by. A
    /// clock will not do: a hundred sets opened in one loop share a timestamp on
    /// Windows, and a dictionary stops keeping insertion order the moment
    /// anything is removed from it.
    /// </summary>
    public long Sequence { get; set; }
}

/// <summary>
/// The per-session grant sets, kept in ui-settings under the session's own id.
/// </summary>
internal static class ComputerUseSessionGrants
{
    /// <summary>
    /// How many sessions keep a grant set. The reference has no cap — its sets
    /// live on the sessions themselves and go when a session does — so this is
    /// the same bound ui-settings already puts on its other per-session maps.
    /// </summary>
    internal const int MaxSessions = 100;

    /// <summary>
    /// The set for a session. A call with no session identity — a headless run,
    /// a tool reached outside a turn — gets one shared unnamed set rather than a
    /// new one each time, which is the same treatment
    /// <see cref="DesktopLock"/> gives an anonymous caller.
    /// </summary>
    internal static ComputerUseGrantSet For(UiSettings settings, string? sessionId)
    {
        var key = Key(sessionId);
        if (settings.ComputerUseGrantsBySession.TryGetValue(key, out var found))
        {
            return found;
        }

        var next = settings.ComputerUseGrantsBySession.Count == 0
            ? 1
            : settings.ComputerUseGrantsBySession.Values.Max(static existing => existing.Sequence) + 1;
        var set = new ComputerUseGrantSet { Sequence = next };
        settings.ComputerUseGrantsBySession[key] = set;
        Trim(settings);
        return set;
    }

    /// <summary>The set for a session, or null when it has none — a read that stores nothing.</summary>
    internal static ComputerUseGrantSet? Peek(UiSettings settings, string? sessionId) =>
        settings.ComputerUseGrantsBySession.TryGetValue(Key(sessionId), out var found) ? found : null;

    /// <summary>
    /// Takes one app back from a session, the reference's
    /// <c>revokeComputerUseGrant</c>. Answers whether anything was held.
    /// </summary>
    internal static bool Revoke(UiSettings settings, string? sessionId, string app)
    {
        if (Peek(settings, sessionId) is not { } set)
        {
            return false;
        }

        var name = ComputerUseGrants.Normalize(app);
        var removed = set.Apps.RemoveAll(a =>
            ComputerUseGrants.Normalize(a).Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
        foreach (var key in set.Tiers.Keys
            .Where(k => ComputerUseGrants.Normalize(k).Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            set.Tiers.Remove(key);
        }

        return removed;
    }

    /// <summary>Drops a session's grants — for a deleted session, and for tests.</summary>
    internal static void Forget(UiSettings settings, string? sessionId) =>
        settings.ComputerUseGrantsBySession.Remove(Key(sessionId));

    private static string Key(string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? "" : sessionId;

    /// <summary>Oldest set out first, by the order it was opened in.</summary>
    private static void Trim(UiSettings settings)
    {
        while (settings.ComputerUseGrantsBySession.Count > MaxSessions)
        {
            var oldest = settings.ComputerUseGrantsBySession
                .OrderBy(static entry => entry.Value.Sequence)
                .Select(static entry => entry.Key)
                .FirstOrDefault();
            if (oldest is null)
            {
                return;
            }

            settings.ComputerUseGrantsBySession.Remove(oldest);
        }
    }
}
