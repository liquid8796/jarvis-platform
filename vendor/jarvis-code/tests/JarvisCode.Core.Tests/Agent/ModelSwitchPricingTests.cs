using JarvisCode.Core.Hooks;
using JarvisCode.Core.Models;

namespace JarvisCode.Core.Tests.Agent;

public sealed class ModelSwitchPricingTests
{
    [Fact]
    public void Supported_configured_price_yields_a_labeled_estimate()
    {
        var change = Change(new ModelInfo("anthropic", "next", "Next", 200000, InputPricePerMTok: 3));
        Assert.Equal(0.375m, change.EstimatedCacheWriteUsd);
        Assert.Equal("configured-list-price", change.ToPayload()["pricing"]!.GetValue<string>());
        Assert.Equal(0.375m, change.ToPayload()["estimated_cache_write_usd"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unavailable_or_invalid_prices_do_not_become_zero_cost(double price)
    {
        var change = Change(new ModelInfo("anthropic", "next", "Next", 200000, InputPricePerMTok: price));
        Assert.Null(change.EstimatedCacheWriteUsd);
        Assert.Null(change.ToPayload()["estimated_cache_write_usd"]);
        Assert.Equal("unavailable", change.Pricing);
    }

    [Fact]
    public void Unknown_provider_cache_tariffs_and_missing_model_prices_remain_unknown()
    {
        Assert.Null(Change(new ModelInfo("custom", "next", "Next", 200000, InputPricePerMTok: 3)).EstimatedCacheWriteUsd);
        Assert.Null(Change(null).EstimatedCacheWriteUsd);
    }

    private static ModelSwitch Change(ModelInfo? model) => ModelSwitch.From("previous", "next", "next",
        ModelSwitchSource.Sdk, 100000, null, DateTimeOffset.UtcNow, targetModel: model)!;
}
