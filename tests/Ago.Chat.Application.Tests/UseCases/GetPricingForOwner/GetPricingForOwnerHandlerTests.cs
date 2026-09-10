using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetPricingForOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetPricingForOwner;

/// <summary>
/// `25-20`: unlike its sibling owner reads (`ListSitesForOwnerHandler`, `GetSiteForOwnerHandler`,
/// neither of which has an Application-level unit test - both need a real database to say anything),
/// <see cref="GetPricingForOwnerHandler"/> touches no real I/O - `25-43`'s own
/// <see cref="FakePriceCatalogRepository"/> stands in for <c>IPriceCatalogRepository</c>, the same
/// in-memory-fake boundary `testing.md` draws for a plain unit test. That is exactly `testing.md`'s
/// own boundary for a plain unit test - no fake backed by a real database, no HTTP host - so this
/// class exists where its two siblings' equivalent behaviour instead lives only in
/// `Ago.Chat.Integration.Tests.OwnerPricingEndpointTests`.
/// </summary>
public sealed class GetPricingForOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    // `25-29`: `ago-business` decision `0012`'s own base-plus-marginal formula, not `0008`'s
    // superseded flat rate - this test now checks the three fields that state that formula
    // completely, plus the legacy `PricePerSeatRub` field kept only for wire compatibility (see that
    // field's own remarks on `OwnerSeatPricingDto` for why it reports the marginal rate).
    //
    // `25-43`: the two numbers now come from a fake IPriceCatalogRepository, not a BillingOptions
    // instance - GetPricingForOwnerHandler's own remarks explain why the wire shape this test asserts
    // against is unchanged even though the source moved.
    [Fact]
    public async Task HandleAsync_ReturnsTheRealSeatPricingFromThePriceCatalogAndDomainConstants()
    {
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, Now);
        var handler = new GetPricingForOwnerHandler(prices);

        var response = await handler.HandleAsync(CancellationToken.None);

        Assert.Equal(200m, response.SeatPricing.PricePerSeatRub);
        Assert.Equal(SubscriptionTierBands.BaseSeats, response.SeatPricing.BaseSeats);
        Assert.Equal(490m, response.SeatPricing.BaseSeatPriceRub);
        Assert.Equal(200m, response.SeatPricing.PricePerExtraSeatRub);
        Assert.Equal(BillingSubscription.PeriodLength.TotalDays, response.SeatPricing.BillingPeriodDays);
        Assert.Equal(SubscriptionTierBands.FreeSeatsIncluded, response.SeatPricing.FreeSeatsIncluded);
    }

    // `25-29`: one tier, not two - `ago-business` decision `0012` prices exactly one Business band
    // (2-5 seats), replacing this test's own previous two-tier ("Starter"/"Growth") assertion, which
    // proved `0008`'s superseded grid.
    [Fact]
    public async Task HandleAsync_ListsExactlyOneTier_MatchingSubscriptionTierBands()
    {
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 1m, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 1m, Now);
        var handler = new GetPricingForOwnerHandler(prices);

        var response = await handler.HandleAsync(CancellationToken.None);

        var starter = Assert.Single(response.SeatPricing.Tiers);
        Assert.Equal(SubscriptionTierBands.Starter, starter.Key);
        Assert.Equal(SubscriptionTierBands.MinSeats, starter.MinSeats);
        Assert.Equal(SubscriptionTierBands.MaxSeats, starter.MaxSeats);

        // Every seat count SubscriptionTierBands.TryResolveTier actually resolves must fall inside
        // the one tier row this handler reports - the two sources must never disagree about where the
        // boundary sits.
        for (var seats = SubscriptionTierBands.MinSeats; seats <= SubscriptionTierBands.MaxSeats; seats++)
        {
            SubscriptionTierBands.TryResolveTier(seats, out var expectedTier);
            Assert.Equal(expectedTier, starter.Key);
        }
    }

    // `25-20`'s own honest finding, pinned at the unit level too (Ago.Chat.Integration.Tests.
    // OwnerPricingEndpointTests proves it end to end over real HTTP): no billing option carries a
    // price anywhere in this codebase today, so this handler never fabricates a list - see the
    // handler's own remarks for why this is not read from a configuration section.
    [Fact]
    public async Task HandleAsync_ReturnsNoBillingOptions_NoMechanismExistsToPriceThemYet()
    {
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 1m, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 1m, Now);
        var handler = new GetPricingForOwnerHandler(prices);

        var response = await handler.HandleAsync(CancellationToken.None);

        Assert.Empty(response.BillingOptions);
    }

    // `25-43`: the second decision made visible on this exact screen - a registered key with nothing
    // published yet appears in PricedResources with a null amount, honestly, rather than being hidden
    // or fabricated. Both seat keys are seeded here (GetPricingForOwnerHandler throws otherwise - see
    // its own remarks) - PricedResourceKeys.All has exactly two entries today, both seeded, so this
    // test is written to remain correct the day a third key is registered with nothing published for
    // it, rather than asserting an exact list length that would break on that unrelated change.
    [Fact]
    public async Task HandleAsync_ListsEveryRegisteredKey_WithItsCurrentPriceOrNullIfUnpublished()
    {
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, Now);
        var handler = new GetPricingForOwnerHandler(prices);

        var response = await handler.HandleAsync(CancellationToken.None);

        Assert.Equal(PricedResourceKeys.All.Count, response.PricedResources.Count);
        var basePricedResource = Assert.Single(response.PricedResources, r => r.Key == SubscriptionTierBands.BaseSeatPriceKey.Value);
        Assert.Equal("v1", basePricedResource.CurrentVersion);
        Assert.Equal(490m, basePricedResource.CurrentAmountRub);
    }
}
