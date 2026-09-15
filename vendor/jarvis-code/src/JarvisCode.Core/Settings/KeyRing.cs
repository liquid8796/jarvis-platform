namespace JarvisCode.Core.Settings;

/// <summary>
/// Sticky key rotation per provider: the current key is reused until a caller
/// reports it rate limited, then the ring advances to the next key. Thread-safe —
/// parallel subagents report failures concurrently, and only the first report for
/// the current key advances the ring.
/// </summary>
public sealed class KeyRing(Func<string, IReadOnlyList<string>> keysForProvider)
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public string? Current(string providerId)
    {
        lock (_gate)
        {
            var keys = keysForProvider(providerId);
            if (keys.Count == 0)
                return null;
            return keys[NormalizedIndex(providerId, keys.Count)];
        }
    }

    /// <summary>
    /// Reports the key as rate limited. Returns true when a different key is now
    /// active (a retry makes sense), false when there is nothing to rotate to.
    /// </summary>
    public bool RotateIfCurrent(string providerId, string rateLimitedKey)
    {
        lock (_gate)
        {
            var keys = keysForProvider(providerId);
            if (keys.Count < 2)
                return false;
            int index = NormalizedIndex(providerId, keys.Count);
            if (keys[index] == rateLimitedKey)
            {
                index = (index + 1) % keys.Count;
                _indices[providerId] = index;
            }
            // Someone else may have already advanced past the failed key.
            return keys[index] != rateLimitedKey;
        }
    }

    /// <summary>Forgets rotation state (e.g. after the key lists were edited).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _indices.Clear();
        }
    }

    private int NormalizedIndex(string providerId, int count)
    {
        int index = _indices.GetValueOrDefault(providerId) % count;
        _indices[providerId] = index;
        return index;
    }
}
