using JarvisCode.Core.Models;

namespace JarvisCode.Core.Settings;

/// <summary>
/// The models offered in the model picker. Nothing ships built in: a key is entered
/// and its model ids are added on the provider card, so the picker only ever offers
/// models this installation can actually reach.
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// The catalog as the app sees it: the user's own models, with any context window
    /// they corrected applied on top. Two entries sharing an id would send a turn to
    /// whichever provider is listed first, so the first one carrying an id wins. An
    /// override for an id no entry carries is ignored rather than conjuring a model,
    /// and a non-positive one is treated as absent.
    /// </summary>
    public static IReadOnlyList<ModelInfo> WithCustom(
        IEnumerable<ModelInfo> customModels,
        IReadOnlyDictionary<string, int>? contextOverrides = null)
    {
        var all = new List<ModelInfo>();
        foreach (var custom in customModels)
        {
            if (!all.Any(m => m.ModelId.Equals(custom.ModelId, StringComparison.OrdinalIgnoreCase)))
                all.Add(custom);
        }

        if (contextOverrides is null or { Count: 0 })
            return all;

        for (var i = 0; i < all.Count; i++)
        {
            if (contextOverrides.TryGetValue(all[i].ModelId, out var tokens) && tokens > 0)
                all[i] = all[i] with { MaxContextTokens = tokens };
        }
        return all;
    }

    public static ModelInfo? Find(IEnumerable<ModelInfo> models, string? modelId) =>
        modelId is null
            ? null
            : models.FirstOrDefault(m => m.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The reference's family aliases — <c>sonnet</c>, <c>opus</c>, <c>haiku</c>,
    /// <c>fable</c> — as an agent definition's <c>model:</c> may spell them:
    /// the newest model of that family the user has added, by version. A name
    /// that is not an alias resolves as an id; null when neither matches.
    /// </summary>
    public static ModelInfo? Resolve(IEnumerable<ModelInfo> models, string? nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias))
            return null;
        var list = models as IReadOnlyList<ModelInfo> ?? [.. models];
        if (Find(list, nameOrAlias) is { } exact)
            return exact;

        var alias = nameOrAlias.Trim().ToLowerInvariant();
        if (alias is not ("sonnet" or "opus" or "haiku" or "fable" or "mythos"))
            return null;

        return list
            .Select(m => (Model: m, Canonical: Agent.ContextWindows.CanonicalModelName(m.ModelId)))
            .Where(x => x.Canonical.StartsWith($"claude-{alias}-", StringComparison.Ordinal))
            .OrderByDescending(x => VersionOf(x.Canonical))
            .Select(x => x.Model)
            .FirstOrDefault();
    }

    /// <summary>"claude-sonnet-4-5" → 4.05, so a newer minor sorts above an older one.</summary>
    private static double VersionOf(string canonical)
    {
        var parts = canonical.Split('-');
        double version = 0;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var major))
            version = major;
        if (parts.Length >= 4 && int.TryParse(parts[3], out var minor))
            version += minor / 100.0;
        return version;
    }
}
