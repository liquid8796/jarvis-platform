using System.Text.Json;
using JarvisCode.Core.Utilities;

namespace JarvisCode.Core.Settings;

public sealed class JsonSettingsStore(string filePath, ISecretProtector protector) : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(filePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), SerializerOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt settings file falls back to defaults instead of crashing the app.
            DiagnosticLog.Write($"settings: load failed ({ex.GetType().Name}: {ex.Message}); using defaults");
            settings = new AppSettings();
        }

        // Metadata only, as everything in this log is: which file was read, whether it was
        // there, and how much came back — never a value, and never a key.
        DiagnosticLog.Write(
            $"settings: loaded from {filePath} (existed={File.Exists(filePath)}, "
            + $"projectName={settings.ChatGptProjectName.Length} chars, "
            + $"keySets={settings.ApiKeySets.Count}, customModels={settings.CustomModels.Count})");

        var decryptedSets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (providerId, storedKeys) in settings.ApiKeySets)
        {
            var plain = storedKeys
                .Select(protector.Unprotect)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k!)
                .ToList();
            if (plain.Count > 0)
                decryptedSets[providerId] = plain;
        }

        // Settings written before key lists existed carry one key per provider.
        foreach (var (providerId, stored) in settings.ApiKeys)
        {
            if (decryptedSets.ContainsKey(providerId))
                continue;
            var plain = protector.Unprotect(stored);
            if (!string.IsNullOrWhiteSpace(plain))
                decryptedSets[providerId] = [plain];
        }

        settings.ApiKeySets = decryptedSets;
        // Deserialization hands back a case-sensitive dictionary; model ids are matched
        // case-insensitively everywhere else, so the comparer is restored here.
        settings.ModelContextOverrides = new Dictionary<string, int>(
            settings.ModelContextOverrides, StringComparer.OrdinalIgnoreCase);
        settings.ProviderOptionsByModel = settings.ProviderOptionsByModel.ToDictionary(
            static entry => entry.Key,
            static entry => new Dictionary<string, string>(entry.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        settings.ApiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        var copy = new AppSettings
        {
            AnthropicBaseUrl = settings.AnthropicBaseUrl,
            ApiKeySets = settings.ApiKeySets
                .Where(kv => kv.Value.Any(k => !string.IsNullOrWhiteSpace(k)))
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Select(protector.Protect)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase),
            OllamaBaseUrl = settings.OllamaBaseUrl,
            NvidiaBaseUrl = settings.NvidiaBaseUrl,
            OpenRouterBaseUrl = settings.OpenRouterBaseUrl,
            TokenRouterBaseUrl = settings.TokenRouterBaseUrl,
            DeepSeekBaseUrl = settings.DeepSeekBaseUrl,
            ZhipuBaseUrl = settings.ZhipuBaseUrl,
            MiniMaxBaseUrl = settings.MiniMaxBaseUrl,
            LlmApiBaseUrl = settings.LlmApiBaseUrl,
            LlmApiProtocolName = settings.LlmApiProtocolName,
            BedrockRegion = settings.BedrockRegion,
            BedrockAccessKeyId = settings.BedrockAccessKeyId,
            VertexProjectId = settings.VertexProjectId,
            VertexRegion = settings.VertexRegion,
            ChatGptProjectName = settings.ChatGptProjectName,
            ChatGptProjectId = settings.ChatGptProjectId,
            ChatGptRotateAfterMessages = settings.ChatGptRotateAfterMessages,
            ChatGptChats = [.. settings.ChatGptChats],
            DefaultModelId = settings.DefaultModelId,
            LastWorkingDirectory = settings.LastWorkingDirectory,
            ShellTimeoutSeconds = settings.ShellTimeoutSeconds,
            OtherProviderPromptForm = settings.OtherProviderPromptForm,
            ReminderOverridesEnabled = settings.ReminderOverridesEnabled,
            ReminderOverrideSource = settings.ReminderOverrideSource,
            AutoCompactEnabled = settings.AutoCompactEnabled,
            AutoCompactWindow = settings.AutoCompactWindow,
            PrecomputeCompactionEnabled = settings.PrecomputeCompactionEnabled,
            PrecomputeSidecarEnabled = settings.PrecomputeSidecarEnabled,
            EnableWebSearch = settings.EnableWebSearch,
            SearxngBaseUrl = settings.SearxngBaseUrl,
            AllowedHttpHookUrls = settings.AllowedHttpHookUrls is null ? null : [.. settings.AllowedHttpHookUrls],
            HttpHookAllowedEnvVars = settings.HttpHookAllowedEnvVars is null ? null : [.. settings.HttpHookAllowedEnvVars],
            InstructionFileExcludes = [.. settings.InstructionFileExcludes],
            PermissionRuleLines = [.. settings.PermissionRuleLines],
            PermissionModeName = settings.PermissionModeName,
            ThinkingEffortName = settings.ThinkingEffortName,
            TotalTokensReminder = settings.TotalTokensReminder,
            TotalTokensReminderBudget = settings.TotalTokensReminderBudget,
            TotalTokensReminderAfterUserTurn = settings.TotalTokensReminderAfterUserTurn,
            BlockReadsOutsideWorkingDirectories = settings.BlockReadsOutsideWorkingDirectories,
            TimeFormat = settings.TimeFormat,
            TimeZone = settings.TimeZone,
            Language = settings.Language,
            EffortByModel = new Dictionary<string, string>(settings.EffortByModel, StringComparer.Ordinal),
            ProviderOptionsByModel = settings.ProviderOptionsByModel.ToDictionary(
                static entry => entry.Key,
                static entry => new Dictionary<string, string>(entry.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            CustomProviders = [.. settings.CustomProviders],
            CustomModels = [.. settings.CustomModels],
            ModelContextOverrides = new Dictionary<string, int>(
                settings.ModelContextOverrides, StringComparer.OrdinalIgnoreCase),
        };
        string json;
        try
        {
            json = JsonSerializer.Serialize(copy, SerializerOptions);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"settings: serialize failed ({ex.GetType().Name}: {ex.Message})");
            throw;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, json);
            DiagnosticLog.Write(
                $"settings: wrote {json.Length} chars to {filePath} "
                + $"(now {new FileInfo(filePath).Length} bytes on disk)");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"settings: write to {filePath} failed ({ex.GetType().Name}: {ex.Message})");
            throw;
        }
    }
}
