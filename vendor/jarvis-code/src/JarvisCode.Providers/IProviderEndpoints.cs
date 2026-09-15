namespace JarvisCode.Providers;

/// <summary>Supplies configurable endpoints (today: the Ollama base URL) at call time.</summary>
public interface IProviderEndpoints
{
    string? GetBaseUrl(string providerId);
}
