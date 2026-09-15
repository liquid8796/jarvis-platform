using JarvisCode.Core.Models;

namespace JarvisCode.App.Services;

/// <summary>
/// How the model menu divides its rows. Nothing ships built in, so the list is whatever
/// the user added on the provider cards, in the order they added it — which is not
/// grouped by anything. The menu groups it and names each group after the provider that
/// serves it, so a run of models says which key answers for it.
/// </summary>
public static class ModelMenuPresentation
{
    /// <summary>One provider's run of models, under the label the menu draws on its rule.</summary>
    public sealed record ModelGroup(string ProviderId, string Label, IReadOnlyList<ModelInfo> Models);

    /// <summary>
    /// Groups the models by provider, keeping each provider where its first model appeared
    /// and each model where it sat within its provider. <paramref name="providerName"/> gives
    /// the registered provider's display name; a provider this build no longer carries has
    /// none, and its leftover entries are labelled with the raw id rather than going nameless.
    /// </summary>
    public static IReadOnlyList<ModelGroup> Group(
        IEnumerable<ModelInfo> models,
        Func<string, string?> providerName) =>
        [.. models
            .GroupBy(static m => m.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelGroup(
                group.Key,
                providerName(group.Key) is { Length: > 0 } name ? name : group.Key,
                [.. group]))];
}
