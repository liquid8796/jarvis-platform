using System.Reflection;
using System.Text.Json;
using JarvisCode.Core.Models;
using JarvisCode.Core.Settings;

namespace JarvisCode.Core.Tests.Persistence;

/// <summary>
/// The settings store writes a field-by-field copy rather than the object it is handed, so
/// a setting that is added to <see cref="AppSettings"/> and not to that copy is accepted by
/// every editor, saved to disk without it, and silently back at its default on the next
/// launch. Nothing else in the app would notice, which is why the provider settings are
/// pinned here rather than trusted to the serializer.
/// </summary>
public sealed class ProviderSettingsRoundTripTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AppSettings SaveAndLoad(Action<AppSettings> edit)
    {
        var path = Path.Combine(_temp.Path, "settings.json");
        var store = new JsonSettingsStore(path, new PassThroughSecretProtector());
        var settings = store.Load();
        edit(settings);
        store.Save(settings);
        return new JsonSettingsStore(path, new PassThroughSecretProtector()).Load();
    }

    [Fact]
    public void EveryProviderBaseUrlSurvivesASave()
    {
        var loaded = SaveAndLoad(settings =>
        {
            settings.OllamaBaseUrl = "http://localhost:11434";
            settings.NvidiaBaseUrl = "http://localhost:8000/v1";
            settings.OpenRouterBaseUrl = "https://openrouter.example/api/v1";
            settings.TokenRouterBaseUrl = "https://tokenrouter.example/v1";
            settings.DeepSeekBaseUrl = "https://deepseek.example";
            settings.ZhipuBaseUrl = "https://open.bigmodel.cn/api/paas/v4";
            settings.MiniMaxBaseUrl = "https://api.minimaxi.com/v1";
            settings.LlmApiBaseUrl = "https://llmapi.example";
        });

        Assert.Equal("http://localhost:11434", loaded.OllamaBaseUrl);
        Assert.Equal("http://localhost:8000/v1", loaded.NvidiaBaseUrl);
        Assert.Equal("https://openrouter.example/api/v1", loaded.OpenRouterBaseUrl);
        Assert.Equal("https://tokenrouter.example/v1", loaded.TokenRouterBaseUrl);
        Assert.Equal("https://deepseek.example", loaded.DeepSeekBaseUrl);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", loaded.ZhipuBaseUrl);
        Assert.Equal("https://api.minimaxi.com/v1", loaded.MiniMaxBaseUrl);
        Assert.Equal("https://llmapi.example", loaded.LlmApiBaseUrl);
    }

    [Fact]
    public void AHandWrittenProviderSurvivesASaveWithEveryFieldItCarries()
    {
        var loaded = SaveAndLoad(settings =>
        {
            settings.CustomProviders.Add(new CustomProviderSpec
            {
                Id = "my-relay",
                DisplayName = "My relay",
                BaseUrl = "https://relay.example/v1",
                ProtocolName = CustomProviderProtocols.Anthropic,
                RequiresApiKey = false,
            });
            settings.CustomProviders.Add(new CustomProviderSpec
            {
                Id = "second",
                DisplayName = "Second",
                BaseUrl = "https://two.example/v1",
            });
        });

        Assert.Equal(2, loaded.CustomProviders.Count);
        var first = loaded.CustomProviders[0];
        Assert.Equal("my-relay", first.Id);
        Assert.Equal("My relay", first.DisplayName);
        Assert.Equal("https://relay.example/v1", first.BaseUrl);
        Assert.Equal(CustomProviderProtocols.Anthropic, first.ProtocolName);
        // A protocol or a key requirement that resets on restart would send the next turn
        // to the wrong wire without anything on screen having changed.
        Assert.False(first.RequiresApiKey);

        // The order is the one the user added them in, and it is what the page lists.
        Assert.Equal("second", loaded.CustomProviders[1].Id);
        Assert.Equal(CustomProviderProtocols.OpenAi, loaded.CustomProviders[1].ProtocolName);
        Assert.True(loaded.CustomProviders[1].RequiresApiKey);
    }

    [Fact]
    public void AKeyStoredAgainstAHandWrittenProviderIsProtectedLikeAnyOther()
    {
        var loaded = SaveAndLoad(settings =>
        {
            settings.CustomProviders.Add(new CustomProviderSpec
            {
                Id = "my-relay",
                BaseUrl = "https://relay.example/v1",
            });
            settings.ApiKeySets["my-relay"] = ["sk-relay"];
        });

        Assert.Equal(["sk-relay"], loaded.GetApiKeys("my-relay"));
        // Keys are looked up case-insensitively everywhere, and the id is stored lowercased.
        Assert.Equal(["sk-relay"], loaded.GetApiKeys("MY-RELAY"));
    }

    /// <summary>
    /// The ChatGPT browser session stores which chat answers which conversation so a restart
    /// carries on in it. Everything above it can be right and the feature still dead if the store
    /// drops the field, which is the exact shape this file exists to catch.
    /// </summary>
    [Fact]
    public void TheChatGptChatsSurviveASave()
    {
        var loaded = SaveAndLoad(settings => settings.ChatGptChats =
        [
            new ChatGptChatEntry
            {
                Key = "0A1B",
                ConversationId = "conv-1",
                SentMessages = 3,
                DescribedTools = ["Read", "Edit"],
                UpdatedAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            },
        ]);

        var chat = Assert.Single(loaded.ChatGptChats);
        Assert.Equal("0A1B", chat.Key);
        Assert.Equal("conv-1", chat.ConversationId);
        Assert.Equal(3, chat.SentMessages);
        Assert.Equal(["Read", "Edit"], chat.DescribedTools);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero), chat.UpdatedAt);
    }

    [Fact]
    public void AFreshFileCarriesEachVendorsDocumentedRoot()
    {
        var settings = new JsonSettingsStore(
            Path.Combine(_temp.Path, "absent.json"), new PassThroughSecretProtector()).Load();

        Assert.Equal("https://openrouter.ai/api/v1", settings.OpenRouterBaseUrl);
        Assert.Equal("https://api.tokenrouter.io/v1", settings.TokenRouterBaseUrl);
        Assert.Equal("https://api.deepseek.com", settings.DeepSeekBaseUrl);
        Assert.Equal("https://api.z.ai/api/paas/v4", settings.ZhipuBaseUrl);
        Assert.Equal("https://api.minimax.io/v1", settings.MiniMaxBaseUrl);
        Assert.Empty(settings.CustomProviders);
    }

    // The tests above pin the settings somebody thought to pin. The two below pin the rest:
    // they walk AppSettings itself, give every property a value nothing defaults to, save and
    // load through the real store, and name whatever came back at its default. That closes the
    // class of bug — a property added to AppSettings and forgotten in Save — rather than the
    // instances of it found so far.

    /// <summary>
    /// The properties that deliberately do not come back verbatim, each with the reason.
    /// Everything else on <see cref="AppSettings"/> has to survive a round trip.
    /// </summary>
    private static readonly Dictionary<string, string> NotRoundTripped = new(StringComparer.Ordinal)
    {
        ["ApiKeys"] =
            "the legacy one-key-per-provider field. Load folds it into ApiKeySets and hands back "
            + "an empty dictionary, and Save stopped writing it on purpose, so a value that came "
            + "back unchanged would mean the migration had not run.",
        ["ApiKeySets"] =
            "written through ISecretProtector rather than verbatim, so what reaches disk is "
            + "ciphertext by design; its round trip is covered by "
            + nameof(AKeyStoredAgainstAHandWrittenProviderIsProtectedLikeAnyOther) + " above.",
    };

    /// <summary>
    /// Sample values for the property types the generic factory cannot invent one for. A type
    /// with no entry here fails the test rather than being skipped: a setting nobody can build
    /// a value for is exactly the one that would otherwise go unchecked.
    /// </summary>
    private static readonly Dictionary<Type, Func<object>> SamplesByType = new()
    {
        [typeof(List<CustomProviderSpec>)] = () => new List<CustomProviderSpec>
        {
            new()
            {
                Id = "round-trip",
                DisplayName = "Round trip",
                BaseUrl = "https://round-trip.example/v1",
                ProtocolName = CustomProviderProtocols.Anthropic,
                RequiresApiKey = false,
            },
        },
        [typeof(List<ModelInfo>)] = () => new List<ModelInfo>
        {
            new("round-trip", "round-trip-model", "Round trip model", 123_456, 1.25, 2.5),
        },
        [typeof(List<ChatGptChatEntry>)] = () => new List<ChatGptChatEntry>
        {
            new()
            {
                Key = "round-trip",
                ConversationId = "conv-round-trip",
                SentMessages = 7,
                DescribedTools = ["Read"],
                UpdatedAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            },
        },
    };

    [Fact]
    public void EverySettingSurvivesASave()
    {
        var path = Path.Combine(_temp.Path, "settings.json");
        var store = new JsonSettingsStore(path, new PassThroughSecretProtector());
        var settings = store.Load();
        var defaults = new AppSettings();

        var unbuildable = new List<string>();
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in RoundTrippedProperties())
        {
            if (NonDefaultValue(property, property.GetValue(settings)) is not { } value)
            {
                unbuildable.Add($"{property.Name} ({property.PropertyType})");
                continue;
            }

            property.SetValue(settings, value);
            saved[property.Name] = Describe(property, settings);
        }

        Assert.True(
            unbuildable.Count == 0,
            "This test can only check a setting it can invent a value for. Add a sample to "
            + $"{nameof(SamplesByType)} for:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", unbuildable));

        // A sample that happened to equal the default would pass whatever the store did with it.
        var indistinguishable = RoundTrippedProperties()
            .Where(p => saved[p.Name] == Describe(p, defaults))
            .Select(p => $"{p.Name} is set to its own default ({saved[p.Name]})")
            .ToList();
        Assert.True(
            indistinguishable.Count == 0,
            "A test value has to differ from the default to prove anything:"
            + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", indistinguishable));

        store.Save(settings);
        var loaded = new JsonSettingsStore(path, new PassThroughSecretProtector()).Load();

        var lost = new List<string>();
        foreach (var property in RoundTrippedProperties())
        {
            var after = Describe(property, loaded);
            if (after == saved[property.Name])
                continue;
            lost.Add(after == Describe(property, defaults)
                ? $"{property.Name} came back at its default ({after})"
                : $"{property.Name} came back as {after}, not the {saved[property.Name]} it was saved with");
        }

        Assert.True(
            lost.Count == 0,
            "JsonSettingsStore.Save serialises a hand-built copy of AppSettings, so a property "
            + "missing from that copy is written as its default and is back at the default on "
            + $"the next load. Add these to the copy:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", lost));
    }

    /// <summary>
    /// An exclusion naming a property that no longer exists would quietly take a renamed
    /// setting out of the sweep above, which is the bug this file exists to catch.
    /// </summary>
    [Fact]
    public void EverySettingExcludedFromThatSweepStillExists()
    {
        var names = AllProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, reason) in NotRoundTripped)
        {
            Assert.True(
                names.Contains(name),
                $"{nameof(NotRoundTripped)} excludes AppSettings.{name} — {reason} — but there is "
                + "no such property any more. Drop the entry, or point it at the new name.");
        }
    }

    private static IEnumerable<PropertyInfo> AllProperties() =>
        typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

    private static IEnumerable<PropertyInfo> RoundTrippedProperties() =>
        AllProperties().Where(p => !NotRoundTripped.ContainsKey(p.Name));

    /// <summary>The property's value as JSON, which is how two of them are compared.</summary>
    private static string Describe(PropertyInfo property, AppSettings settings) =>
        JsonSerializer.Serialize(property.GetValue(settings), property.PropertyType);

    /// <summary>
    /// A value for this property that its default is not, or null when no sample can be built —
    /// which the caller reports rather than skipping over.
    /// </summary>
    private static object? NonDefaultValue(PropertyInfo property, object? current)
    {
        if (SamplesByType.TryGetValue(property.PropertyType, out var sample))
            return sample();
        if (property.PropertyType == typeof(List<string>))
            return new List<string> { "round-trip-" + property.Name };
        if (property.PropertyType == typeof(Dictionary<string, string>))
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["round-trip"] = property.Name };
        if (property.PropertyType == typeof(Dictionary<string, int>))
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["round-trip"] = 4242 };
        if (property.PropertyType == typeof(Dictionary<string, Dictionary<string, string>>))
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            {
                ["round-trip-model"] = new(StringComparer.Ordinal) { ["power"] = "4" },
            };

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string))
            return "round-trip-" + property.Name;
        if (type == typeof(bool))
            return current is not true;
        if (type == typeof(int))
            return current is int i ? i + 1 : 1;
        if (type == typeof(long))
            return current is long l ? l + 1 : 1L;
        if (type == typeof(double))
            return current is double d ? d + 1 : 1d;
        return null;
    }
}
