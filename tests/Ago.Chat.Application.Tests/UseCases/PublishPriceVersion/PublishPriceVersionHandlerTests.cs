using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.PublishPriceVersion;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.PublishPriceVersion;

/// <summary>
/// `25-101`: <see cref="PublishPriceVersionHandler"/>'s own first Application-level unit test - it had
/// none before this item. Its two decisions (`25-43`'s own first, "the owner only ever sets or changes
/// the figure for a key that already exists"; the concurrency-retry shape copied from
/// `PublishDocumentVersionHandler`) were previously proven only at the HTTP boundary
/// (<c>Ago.Chat.Integration.Tests.OwnerPricingEndpointTests</c>, against a real Postgres and Keycloak,
/// and only ever for the two seat-pricing keys) - real coverage, but the slower, heavier kind
/// `testing.md` reserves for what a fake genuinely cannot stand in for (authentication, authorization,
/// real concurrency). This file adds the faster, narrower layer for the handler's own logic, the
/// identical split <see cref="Ago.Chat.Application.Tests.UseCases.PublishDocumentVersion.PublishDocumentVersionHandlerTests"/>
/// already holds for its own sibling handler - and is also the first place this item's own new
/// <see cref="ChannelAddOnPricing.ChannelAddOnKey"/> is ever actually published against, not merely
/// declared in <see cref="PricedResourceKeys"/>.
/// </summary>
public sealed class PublishPriceVersionHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(PublishPriceVersionHandler Handler, FakePriceCatalogRepository Prices);

    private static Fixture CreateFixture(DateTimeOffset? now = null)
    {
        var prices = new FakePriceCatalogRepository();
        var handler = new PublishPriceVersionHandler(prices, new FakeIdGenerator(), new FakeClock(now ?? Now));
        return new Fixture(handler, prices);
    }

    /// <summary>The proof this item's own Done-when actually asks for: a platform owner can publish a
    /// real Rouble figure for `25-101`'s own new key, not merely that the key compiles into
    /// <see cref="PricedResourceKeys.All"/>.</summary>
    [Fact]
    public async Task HandleAsync_ForTheNewChannelAddOnKey_PublishesV1()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishPriceVersion.PublishPriceVersion(ChannelAddOnPricing.ChannelAddOnKey.Value, 100m),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);
        Assert.Equal(ChannelAddOnPricing.ChannelAddOnKey.Value, result.Value.Key);
        Assert.Equal("v1", result.Value.Version);
        Assert.Equal(1, result.Value.Sequence);
        Assert.Equal(100m, result.Value.AmountRub);
        Assert.Equal(Now, result.Value.PublishedAt);

        var current = await fixture.Prices.FindCurrentAsync(ChannelAddOnPricing.ChannelAddOnKey, CancellationToken.None);
        Assert.Equal(100m, current!.AmountRub);
    }

    [Fact]
    public async Task HandleAsync_ForAnAlreadyPublishedKey_PublishesTheNextVersion()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishPriceVersion.PublishPriceVersion(SubscriptionTierBands.BaseSeatPriceKey.Value, 490m),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishPriceVersion.PublishPriceVersion(SubscriptionTierBands.BaseSeatPriceKey.Value, 550m),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("v2", result.Value.Version);
        Assert.Equal(550m, result.Value.AmountRub);
    }

    /// <summary>`25-43`'s own first decision, proven at this layer too (its wire-level twin is
    /// <c>OwnerPricingEndpointTests.OwnerToken_CannotPublishAPriceForAnUnregisteredKey</c>): a caller
    /// can only ever move the figure for a key <see cref="PricedResourceKeys"/> already lists, never
    /// invent one from this call.</summary>
    [Fact]
    public async Task HandleAsync_ForAnUnregisteredKey_ReturnsPriceKeyUnknown_AndPublishesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishPriceVersion.PublishPriceVersion("not-a-real-key", 100m),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceKeyUnknown", result.Error!.Value.Code);
        Assert.Null(await fixture.Prices.GetByKeyAsync(new PriceKey("not-a-real-key"), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WithAMalformedKeyShape_ReturnsPriceInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishPriceVersion.PublishPriceVersion("Not A Valid Key!", 100m),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceInvalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithANegativeAmount_ReturnsPriceInvalid_AndPublishesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishPriceVersion.PublishPriceVersion(ChannelAddOnPricing.ChannelAddOnKey.Value, -1m),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceInvalid", result.Error!.Value.Code);
        Assert.Null(await fixture.Prices.FindCurrentAsync(ChannelAddOnPricing.ChannelAddOnKey, CancellationToken.None));
    }
}
