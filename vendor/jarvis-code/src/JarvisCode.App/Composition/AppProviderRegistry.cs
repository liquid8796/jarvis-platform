using JarvisCode.Core.Providers;
using JarvisCode.Core.Settings;

namespace JarvisCode.App.Composition;

/// <summary>
/// The provider registry for a running app: the ones this build ships, plus the ones the
/// user wrote by hand. <see cref="ProviderRegistry"/> is fixed at construction, which is
/// right for a build's own providers and wrong for a list that is edited on the Providers
/// page — adding an endpoint would otherwise mean restarting before it could be used.
/// <para>
/// The lookup is a whole dictionary replaced at once, so a turn that resolved its
/// provider a moment ago keeps the instance it holds (which reads its endpoint live) and
/// never sees a half-written map.
/// </para>
/// </summary>
internal sealed class AppProviderRegistry : IProviderRegistry
{
    private readonly IReadOnlyList<ILlmProvider> _builtIn;
    private readonly Func<IReadOnlyList<CustomProviderSpec>> _specs;
    private readonly Func<CustomProviderSpec, ILlmProvider> _create;
    private volatile Snapshot _snapshot;

    public AppProviderRegistry(
        IEnumerable<ILlmProvider> builtIn,
        Func<IReadOnlyList<CustomProviderSpec>> specs,
        Func<CustomProviderSpec, ILlmProvider> create)
    {
        _builtIn = [.. builtIn];
        _specs = specs;
        _create = create;
        _snapshot = Build();
    }

    public IReadOnlyList<ILlmProvider> All => _snapshot.Ordered;

    public ILlmProvider Get(string providerId) =>
        _snapshot.ById.TryGetValue(providerId, out var provider)
            ? provider
            : throw new ProviderException($"No LLM provider registered with id '{providerId}'.");

    /// <summary>
    /// Re-reads the custom providers. Called when settings are saved, which is the only
    /// moment the list can change; a build's own providers are never rebuilt.
    /// </summary>
    public void Refresh() => _snapshot = Build();

    private Snapshot Build()
    {
        var ordered = new List<ILlmProvider>(_builtIn);
        var byId = new Dictionary<string, ILlmProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _builtIn)
        {
            byId[provider.Id] = provider;
        }

        // A custom entry may not take a built-in id: shadowing "anthropic" would send
        // stored sessions somewhere else without anything on screen having changed.
        foreach (var spec in CustomProviders.Usable(_specs(), byId.Keys))
        {
            var provider = _create(spec);
            byId[provider.Id] = provider;
            ordered.Add(provider);
        }

        return new Snapshot(byId, ordered);
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, ILlmProvider> ById, IReadOnlyList<ILlmProvider> Ordered);
}
