using System.IO;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The browser owns this catalogue. These tests pin the small projection boundary so aliases and
/// future rows remain usable without teaching the desktop app a new list of ChatGPT model names.
/// </summary>
public sealed class ChatGptComposerDiscoveryTests
{
    [Fact]
    public void LatestRemainsTheSelectableKeyWhileTheResolvedPillExplainsWhatItMeans()
    {
        WithServices(services =>
        {
            var controls = new ChatGptComposerControls(
                "6 Pro",
                [new ChatGptPickerOption("Latest", "Latest", Selected: true)],
                []);

            var result = ChatGptComposerDiscovery.MergeModels(services, controls);

            Assert.True(result.Changed);
            var model = Assert.Single(services.Settings.Current.CustomModels, candidate =>
                candidate.ProviderId == ChatGptWebProvider.ProviderId);
            Assert.Equal(ChatGptWebProvider.DynamicModelPrefix + "Latest", model.ModelId);
            Assert.Equal("Latest · 6 Pro", model.DisplayName);
            Assert.Equal("Latest", ChatGptWebProvider.PickerLabel(model.ModelId));
            Assert.Equal("6 Pro", controls.CurrentModelLabel);
            Assert.Equal("Latest", Assert.Single(controls.Models).Label);
        });
    }

    [Theory]
    [InlineData("GPT-9 Quasar")]
    [InlineData("canary/model-2030")]
    [InlineData("未来モデル β")]
    public void ADiscoveredFutureModelRoundTripsWithoutAHardcodedMap(string rawPickerKey)
    {
        var modelId = ChatGptWebProvider.DynamicModelPrefix + rawPickerKey;

        Assert.Equal(rawPickerKey, ChatGptWebProvider.PickerLabel(modelId));
    }

    private static void WithServices(Action<AppServices> run)
    {
        var paths = ProfilePaths.Create("chatgpt-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var services = new AppServices(paths, headless: true);
            run(services);
        }
        finally
        {
            var target = Path.GetFullPath(paths.Root);
            var parent = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            if (Path.GetDirectoryName(target) == parent
                && Path.GetFileName(target).StartsWith("JarvisCode-chatgpt-discovery-", StringComparison.Ordinal)
                && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}
