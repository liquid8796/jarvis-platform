using JarvisCode.App.Composition;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Services;

/// <summary>
/// Projects ChatGPT's live composer catalogue into the app's persisted model catalogue. The
/// browser's option keys are opaque and therefore namespaced; entries the user added by hand are
/// deliberately left alone, even when they happen to carry the same visible label.
/// </summary>
internal static class ChatGptComposerDiscovery
{
    internal readonly record struct MergeResult(int Offered, int Added, int Updated)
    {
        public bool Changed => Added > 0 || Updated > 0;
    }

    public static ChatGptWebProvider? Provider(AppServices services)
    {
        try
        {
            return ProviderDecorators.Unwrap(
                services.Providers.Get(ChatGptWebProvider.ProviderId)) as ChatGptWebProvider;
        }
        catch (ProviderException)
        {
            return null;
        }
    }

    public static MergeResult MergeModels(AppServices services, ChatGptComposerControls controls)
    {
        var settings = services.Settings.Current;
        var added = 0;
        var updated = 0;
        foreach (var option in controls.Models.Where(static option =>
                     !string.IsNullOrWhiteSpace(option.Key) && !string.IsNullOrWhiteSpace(option.Label)))
        {
            var rawKey = option.Key.Trim();
            var id = rawKey.StartsWith(ChatGptWebProvider.DynamicModelPrefix, StringComparison.OrdinalIgnoreCase)
                ? rawKey
                : ChatGptWebProvider.DynamicModelPrefix + rawKey;
            var displayName = DisplayName(option, controls.CurrentModelLabel);
            var index = settings.CustomModels.FindIndex(model =>
                model.ModelId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                // A provider-specific prefix should make this impossible, but model ids are global.
                // Do not steal an entry if a hand-written provider deliberately used the same id.
                if (services.Settings.Models.Any(model =>
                        model.ModelId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                settings.CustomModels.Add(new ModelInfo(
                    ChatGptWebProvider.ProviderId, id, displayName, 128_000));
                added++;
                continue;
            }

            var existing = settings.CustomModels[index];
            if (!existing.ProviderId.Equals(ChatGptWebProvider.ProviderId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal))
            {
                continue;
            }

            settings.CustomModels[index] = existing with { DisplayName = displayName };
            updated++;
        }

        return new MergeResult(controls.Models.Count, added, updated);
    }

    /// <summary>
    /// A selected alias can have a concrete model name on the trigger that differs from its picker
    /// label. Showing both keeps the app useful without freezing that changing name into the
    /// option's stable id.
    /// </summary>
    private static string DisplayName(ChatGptPickerOption option, string currentModelLabel)
    {
        var label = option.Label.Trim();
        var current = currentModelLabel?.Trim() ?? "";
        return option.Selected
               && current.Length > 0
               && !current.Equals(label, StringComparison.OrdinalIgnoreCase)
            ? $"{label} · {current}"
            : label;
    }
}
