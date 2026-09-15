using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>
/// Hand edits made in the request inspector, held per session for as long as the app
/// runs. Deliberately not persisted: an override that silently outlived a restart
/// would keep rewriting every request with nothing on screen to explain it, and the
/// composer's status dot is the only sign one exists.
/// <para>
/// Static because the two callers sit on opposite sides of the view — the composer
/// footer stores a patch, the view model reads it as a turn begins — and neither owns
/// the other. Every entry is keyed by session id, so split-view sessions stay
/// independent.
/// </para>
/// </summary>
public static class RequestOverrides
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, JsonObject> Patches = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Drafts = new(StringComparer.Ordinal);

    /// <summary>
    /// The merge patch to apply to that session's requests, or null when none is set.
    /// Returns a copy: a stored patch is never handed out for mutation.
    /// </summary>
    public static JsonObject? For(string sessionId)
    {
        lock (Gate)
        {
            return Patches.TryGetValue(sessionId, out var patch) ? (JsonObject)patch.DeepClone() : null;
        }
    }

    public static bool Has(string sessionId)
    {
        lock (Gate)
        {
            return Patches.ContainsKey(sessionId);
        }
    }

    /// <summary>Stores a patch, or clears the session's override when it is null.</summary>
    public static void Set(string sessionId, JsonObject? patch)
    {
        lock (Gate)
        {
            if (patch is null)
            {
                Patches.Remove(sessionId);
            }
            else
            {
                Patches[sessionId] = (JsonObject)patch.DeepClone();
            }
        }
    }

    public static void Clear(string sessionId) => Set(sessionId, null);

    /// <summary>
    /// Editor text the user left behind without applying it, so an accidental click
    /// outside the popup does not throw the edit away.
    /// </summary>
    public static string? Draft(string sessionId)
    {
        lock (Gate)
        {
            return Drafts.GetValueOrDefault(sessionId);
        }
    }

    public static void SetDraft(string sessionId, string? text)
    {
        lock (Gate)
        {
            if (string.IsNullOrEmpty(text))
            {
                Drafts.Remove(sessionId);
            }
            else
            {
                Drafts[sessionId] = text;
            }
        }
    }

    /// <summary>Drops everything for one session — used when a session is deleted.</summary>
    public static void Forget(string sessionId)
    {
        lock (Gate)
        {
            Drafts.Remove(sessionId);
            Patches.Remove(sessionId);
        }
    }
}
