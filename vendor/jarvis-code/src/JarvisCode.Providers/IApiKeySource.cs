namespace JarvisCode.Providers;

/// <summary>Supplies the API key for a provider at call time (backed by settings in the app).</summary>
public interface IApiKeySource
{
    string? GetKey(string providerId);

    /// <summary>
    /// Reports the key as rate limited. Returns true when a different key is now
    /// active and the request is worth retrying; the default (single-key sources)
    /// has nothing to rotate to.
    /// </summary>
    bool TryRotate(string providerId, string rateLimitedKey) => false;
}
