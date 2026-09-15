namespace JarvisCode.Core.Providers;

/// <summary>Resolves the provider strategy that serves a given provider id.</summary>
public interface IProviderRegistry
{
    IReadOnlyList<ILlmProvider> All { get; }

    /// <summary>Returns the provider with the given id, or throws <see cref="ProviderException"/> if unknown.</summary>
    ILlmProvider Get(string providerId);
}

public sealed class ProviderRegistry(IEnumerable<ILlmProvider> providers) : IProviderRegistry
{
    private readonly Dictionary<string, ILlmProvider> _byId =
        providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ILlmProvider> All => [.. _byId.Values];

    public ILlmProvider Get(string providerId) =>
        _byId.TryGetValue(providerId, out var provider)
            ? provider
            : throw new ProviderException($"No LLM provider registered with id '{providerId}'.");
}
